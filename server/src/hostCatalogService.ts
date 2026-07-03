import { execFile } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';

import {
  C_SUPPORTED_HOST_APPLICATIONS,
  createHostApplicationSelection,
  getBundledHostDefinitionsForApplication,
  type HostApplicationSelectionOptions
} from './officeHostCatalog';
import { discoverHostSignaturesFromTypeLibrary } from './hostSignatureDiscovery';
import type { HostApplication, HostDefinition } from './vbaProject';

const execFileAsync = promisify(execFile);

export type HostCatalogCacheReader = (
  hostApplication: HostApplication,
  cachePath: string
) => HostDefinition[] | undefined;

export type HostCatalogCacheWriter = (
  hostApplication: HostApplication,
  cachePath: string,
  definitions: HostDefinition[]
) => void | Promise<void>;

export type HostCatalogComDiscovery = (hostApplication: HostApplication) => Promise<HostDefinition[]>;

export interface HostCatalogManagerOptions {
  platform?: NodeJS.Platform;
  cacheDirectory?: string;
  readCache?: HostCatalogCacheReader;
  writeCache?: HostCatalogCacheWriter;
  discoverFromCom?: HostCatalogComDiscovery;
  discoverSignaturesFromTypeLibrary?: HostCatalogComDiscovery;
}

export class HostCatalogManager {
  private readonly definitionsByApplication = new Map<HostApplication, HostDefinition[]>();
  private readonly platform: NodeJS.Platform;
  private readonly cacheDirectory: string;
  private readonly readCache?: HostCatalogCacheReader;
  private readonly writeCache?: HostCatalogCacheWriter;
  private readonly discoverFromCom: HostCatalogComDiscovery;
  private readonly discoverSignaturesFromTypeLibrary: HostCatalogComDiscovery;
  private readonly refreshAttempts = new Set<HostApplication>();
  private readonly refreshesInFlight = new Map<HostApplication, Promise<void>>();

  public constructor(options: HostCatalogManagerOptions = {}) {
    this.platform = options.platform ?? process.platform;
    this.cacheDirectory = options.cacheDirectory ?? getDefaultCacheDirectory();
    this.readCache = options.readCache;
    this.writeCache = options.writeCache;
    this.discoverFromCom = options.discoverFromCom ?? discoverOfficeComHostDefinitions;
    this.discoverSignaturesFromTypeLibrary = options.discoverSignaturesFromTypeLibrary
      ?? discoverHostSignaturesFromTypeLibrary;

    for (const host_application of C_SUPPORTED_HOST_APPLICATIONS) {
      this.definitionsByApplication.set(
        host_application,
        this.readCacheSafely(host_application) ?? getBundledHostDefinitionsForApplication(host_application)
      );
    }
  }

  public getDefinitions(options: HostApplicationSelectionOptions = {}): HostDefinition[] {
    const selection = createHostApplicationSelection(options);
    return selection.enabledHostApplications.flatMap((hostApplication) =>
      cloneHostDefinitions(this.getDefinitionsForApplication(hostApplication))
    );
  }

  public async refreshSelectedHostApplicationsFromComAsync(
    options: HostApplicationSelectionOptions = {}
  ): Promise<void> {
    if (this.platform !== 'win32') {
      return;
    }

    for (const host_application of createHostApplicationSelection(options).enabledHostApplications) {
      await this.refreshHostApplicationFromComAsync(host_application);
    }
  }

  public async refreshFromExcelComAsync(): Promise<void> {
    await this.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'excel' });
  }

  private async refreshHostApplicationFromComAsync(hostApplication: HostApplication): Promise<void> {
    const in_flight_refresh = this.refreshesInFlight.get(hostApplication);
    if (in_flight_refresh !== undefined) {
      await in_flight_refresh;
      return;
    }
    if (this.refreshAttempts.has(hostApplication)) {
      return;
    }

    this.refreshAttempts.add(hostApplication);
    const refresh = this.refreshHostApplicationFromComOnceAsync(hostApplication)
      .finally(() => {
        this.refreshesInFlight.delete(hostApplication);
      });
    this.refreshesInFlight.set(hostApplication, refresh);
    await refresh;
  }

  private async refreshHostApplicationFromComOnceAsync(hostApplication: HostApplication): Promise<void> {
    try {
      const discovered_definitions = await this.discoverFromCom(hostApplication);
      if (discovered_definitions.length === 0) {
        return;
      }

      const signature_definitions = await this.discoverSignaturesFromTypeLibrarySafely(hostApplication);
      const definitions = cloneHostDefinitionsWithApplication(
        mergeHostDefinitions(discovered_definitions, signature_definitions),
        hostApplication
      );
      this.definitionsByApplication.set(hostApplication, definitions);
      await this.writeCacheSafely(hostApplication, definitions);
    } catch {
      return;
    }
  }

  private async discoverSignaturesFromTypeLibrarySafely(
    hostApplication: HostApplication
  ): Promise<HostDefinition[]> {
    try {
      return await this.discoverSignaturesFromTypeLibrary(hostApplication);
    } catch {
      return [];
    }
  }

  private getDefinitionsForApplication(hostApplication: HostApplication): HostDefinition[] {
    return this.definitionsByApplication.get(hostApplication)
      ?? getBundledHostDefinitionsForApplication(hostApplication);
  }

  private readCacheSafely(hostApplication: HostApplication): HostDefinition[] | undefined {
    try {
      const cache_path = this.getCachePath(hostApplication);
      const definitions = this.readCache === undefined
        ? readHostCatalogCache(cache_path)
        : this.readCache(hostApplication, cache_path);
      return definitions === undefined
        ? undefined
        : cloneHostDefinitionsWithApplication(definitions, hostApplication);
    } catch {
      return undefined;
    }
  }

  private async writeCacheSafely(
    hostApplication: HostApplication,
    definitions: HostDefinition[]
  ): Promise<void> {
    try {
      const cache_path = this.getCachePath(hostApplication);
      if (this.writeCache === undefined) {
        writeHostCatalogCache(cache_path, definitions);
      } else {
        await this.writeCache(hostApplication, cache_path, cloneHostDefinitions(definitions));
      }
    } catch {
      return;
    }
  }

  private getCachePath(hostApplication: HostApplication): string {
    return path.join(this.cacheDirectory, `${hostApplication}.json`);
  }
}

export function createDefaultHostCatalogManager(): HostCatalogManager {
  return new HostCatalogManager();
}

function getDefaultCacheDirectory(): string {
  return path.join(os.homedir(), '.vba-language-server', 'host-catalogs');
}

function readHostCatalogCache(cachePath: string): HostDefinition[] | undefined {
  if (!fs.existsSync(cachePath)) {
    return undefined;
  }

  const parsed = JSON.parse(fs.readFileSync(cachePath, 'utf8')) as unknown;
  return isHostDefinitionArray(parsed) ? parsed : undefined;
}

function writeHostCatalogCache(cachePath: string, definitions: HostDefinition[]): void {
  fs.mkdirSync(path.dirname(cachePath), { recursive: true });
  fs.writeFileSync(cachePath, `${JSON.stringify(definitions, null, 2)}\n`, 'utf8');
}

function mergeHostDefinitions(
  baseDefinitions: HostDefinition[],
  enrichmentDefinitions: HostDefinition[]
): HostDefinition[] {
  const merged_definitions = baseDefinitions.map((definition) =>
    mergeHostDefinition(
      definition,
      enrichmentDefinitions.find((candidate) => sameName(candidate.name, definition.name))
    )
  );
  const base_names = new Set(baseDefinitions.map((definition) => definition.name.toLowerCase()));
  return [
    ...merged_definitions,
    ...enrichmentDefinitions.filter((definition) => !base_names.has(definition.name.toLowerCase()))
  ];
}

function mergeHostDefinition(
  baseDefinition: HostDefinition,
  enrichmentDefinition: HostDefinition | undefined
): HostDefinition {
  if (enrichmentDefinition === undefined) {
    return cloneHostDefinition(baseDefinition);
  }

  const merged_definition: HostDefinition = {
    ...baseDefinition
  };
  if (enrichmentDefinition.documentation !== undefined) {
    merged_definition.documentation = enrichmentDefinition.documentation;
  }
  if (enrichmentDefinition.typeName !== undefined) {
    merged_definition.typeName = enrichmentDefinition.typeName;
  }
  if (enrichmentDefinition.signature !== undefined) {
    merged_definition.signature = enrichmentDefinition.signature;
  }
  if (baseDefinition.members !== undefined || enrichmentDefinition.members !== undefined) {
    merged_definition.members = mergeHostDefinitions(
      baseDefinition.members ?? [],
      enrichmentDefinition.members ?? []
    );
  }

  return merged_definition;
}

async function discoverOfficeComHostDefinitions(hostApplication: HostApplication): Promise<HostDefinition[]> {
  switch (hostApplication) {
    case 'excel':
      return discoverExcelComHostDefinitions();
    case 'word':
      return discoverWordComHostDefinitions();
    case 'powerpoint':
      return discoverPowerPointComHostDefinitions();
    case 'access':
      return discoverAccessComHostDefinitions();
  }
}

const C_CONVERT_HOST_DEFINITION_SCRIPT = `
function Convert-HostDefinition([string]$Name, $Object, [string]$Documentation, [string]$Kind, [string]$MemberDocumentation) {
  $members = $Object |
    Get-Member -MemberType Method,Property |
    Sort-Object Name -Unique |
    ForEach-Object {
      $memberKind = if ($_.MemberType -eq 'Method') { 'function' } else { 'property' }
      @{ name = $_.Name; kind = $memberKind; documentation = $MemberDocumentation }
    }
  @{ name = $Name; kind = $Kind; documentation = $Documentation; members = @($members) }
}
`;

async function discoverExcelComHostDefinitions(): Promise<HostDefinition[]> {
  const script = `
$ErrorActionPreference = 'Stop'
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$workbook = $null
try {
  $workbook = $excel.Workbooks.Add()
  $worksheet = $workbook.Worksheets.Item(1)
  $range = $worksheet.Range('A1')
  ${C_CONVERT_HOST_DEFINITION_SCRIPT}
  @(
    Convert-HostDefinition 'Application' $excel 'Represents the installed Microsoft Excel application.' 'class' 'Excel COM member.'
    Convert-HostDefinition 'Workbook' $workbook 'Represents an Excel workbook from the installed Excel COM object model.' 'class' 'Excel COM member.'
    Convert-HostDefinition 'Worksheet' $worksheet 'Represents an Excel worksheet from the installed Excel COM object model.' 'class' 'Excel COM member.'
    Convert-HostDefinition 'Range' $range 'Represents an Excel range from the installed Excel COM object model.' 'class' 'Excel COM member.'
  ) | ConvertTo-Json -Depth 5 -Compress
} finally {
  if ($workbook -ne $null) {
    $workbook.Close($false)
  }
  $excel.Quit()
}
`;

  return executePowerShellHostCatalogScript(script, 'Excel COM discovery returned an invalid host catalog.');
}

async function discoverWordComHostDefinitions(): Promise<HostDefinition[]> {
  const script = `
$ErrorActionPreference = 'Stop'
$word = New-Object -ComObject Word.Application
$word.Visible = $false
$document = $null
try {
  $document = $word.Documents.Add()
  $range = $document.Range()
  $selection = $word.Selection
  ${C_CONVERT_HOST_DEFINITION_SCRIPT}
  @(
    Convert-HostDefinition 'Application' $word 'Represents the installed Microsoft Word application.' 'class' 'Word COM member.'
    Convert-HostDefinition 'Document' $document 'Represents a Word document from the installed Word COM object model.' 'class' 'Word COM member.'
    Convert-HostDefinition 'Range' $range 'Represents a Word range from the installed Word COM object model.' 'class' 'Word COM member.'
    Convert-HostDefinition 'Selection' $selection 'Represents the current Word selection from the installed Word COM object model.' 'class' 'Word COM member.'
  ) | ConvertTo-Json -Depth 5 -Compress
} finally {
  if ($document -ne $null) {
    $document.Close($false)
  }
  $word.Quit()
}
`;

  return executePowerShellHostCatalogScript(script, 'Word COM discovery returned an invalid host catalog.');
}

async function discoverPowerPointComHostDefinitions(): Promise<HostDefinition[]> {
  const script = `
$ErrorActionPreference = 'Stop'
$powerpoint = New-Object -ComObject PowerPoint.Application
$presentation = $null
try {
  $presentation = $powerpoint.Presentations.Add($true)
  $slide = $presentation.Slides.Add(1, 12)
  $shape = $slide.Shapes.AddShape(1, 0, 0, 100, 100)
  ${C_CONVERT_HOST_DEFINITION_SCRIPT}
  @(
    Convert-HostDefinition 'Application' $powerpoint 'Represents the installed Microsoft PowerPoint application.' 'class' 'PowerPoint COM member.'
    Convert-HostDefinition 'Presentation' $presentation 'Represents a PowerPoint presentation from the installed PowerPoint COM object model.' 'class' 'PowerPoint COM member.'
    Convert-HostDefinition 'Slide' $slide 'Represents a PowerPoint slide from the installed PowerPoint COM object model.' 'class' 'PowerPoint COM member.'
    Convert-HostDefinition 'Shape' $shape 'Represents a PowerPoint shape from the installed PowerPoint COM object model.' 'class' 'PowerPoint COM member.'
  ) | ConvertTo-Json -Depth 5 -Compress
} finally {
  if ($presentation -ne $null) {
    $presentation.Close()
  }
  $powerpoint.Quit()
}
`;

  return executePowerShellHostCatalogScript(script, 'PowerPoint COM discovery returned an invalid host catalog.');
}

async function discoverAccessComHostDefinitions(): Promise<HostDefinition[]> {
  const script = `
$ErrorActionPreference = 'Stop'
$access = New-Object -ComObject Access.Application
$databasePath = Join-Path ([System.IO.Path]::GetTempPath()) ("vba-language-server-" + [System.Guid]::NewGuid().ToString() + ".accdb")
$form = $null
$report = $null
try {
  $access.NewCurrentDatabase($databasePath)
  $form = $access.CreateForm()
  $report = $access.CreateReport()
  ${C_CONVERT_HOST_DEFINITION_SCRIPT}
  @(
    Convert-HostDefinition 'Application' $access 'Represents the installed Microsoft Access application.' 'class' 'Access COM member.'
    Convert-HostDefinition 'DoCmd' $access.DoCmd 'Represents the Access DoCmd object from the installed Access COM object model.' 'class' 'Access COM member.'
    Convert-HostDefinition 'Form' $form 'Represents an Access form from the installed Access COM object model.' 'class' 'Access COM member.'
    Convert-HostDefinition 'Report' $report 'Represents an Access report from the installed Access COM object model.' 'class' 'Access COM member.'
  ) | ConvertTo-Json -Depth 5 -Compress
} finally {
  try {
    if ($form -ne $null) {
      $access.DoCmd.Close(2, $form.Name, 2)
    }
  } catch {}
  try {
    if ($report -ne $null) {
      $access.DoCmd.Close(3, $report.Name, 2)
    }
  } catch {}
  try {
    $access.CloseCurrentDatabase()
  } catch {}
  $access.Quit()
  if (Test-Path -LiteralPath $databasePath) {
    Remove-Item -LiteralPath $databasePath -Force
  }
}
`;

  return executePowerShellHostCatalogScript(script, 'Access COM discovery returned an invalid host catalog.');
}

async function executePowerShellHostCatalogScript(
  script: string,
  invalidCatalogMessage: string
): Promise<HostDefinition[]> {
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script],
    {
      maxBuffer: 1024 * 1024 * 10,
      timeout: 30000,
      windowsHide: true
    }
  );
  const parsed = JSON.parse(stdout) as unknown;
  if (!isHostDefinitionArray(parsed)) {
    throw new Error(invalidCatalogMessage);
  }

  return parsed;
}

function cloneHostDefinitions(definitions: HostDefinition[]): HostDefinition[] {
  return definitions.map(cloneHostDefinition);
}

function cloneHostDefinition(definition: HostDefinition): HostDefinition {
  const clone: HostDefinition = { ...definition };
  if (definition.members !== undefined) {
    clone.members = definition.members.map(cloneHostDefinition);
  }

  return clone;
}

function cloneHostDefinitionsWithApplication(
  definitions: HostDefinition[],
  hostApplication: HostApplication
): HostDefinition[] {
  return definitions.map((definition) => cloneHostDefinitionWithApplication(definition, hostApplication));
}

function cloneHostDefinitionWithApplication(
  definition: HostDefinition,
  hostApplication: HostApplication
): HostDefinition {
  const clone: HostDefinition = {
    ...definition,
    hostApplication
  };
  if (definition.members !== undefined) {
    clone.members = definition.members.map((member) =>
      cloneHostDefinitionWithApplication(member, hostApplication)
    );
  }

  return clone;
}

function isHostDefinitionArray(value: unknown): value is HostDefinition[] {
  return Array.isArray(value) && value.every(isHostDefinition);
}

function isHostDefinition(value: unknown): value is HostDefinition {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<HostDefinition>;
  return typeof candidate.name === 'string'
    && (candidate.kind === undefined || isHostDefinitionKind(candidate.kind))
    && (candidate.hostApplication === undefined || isHostApplication(candidate.hostApplication))
    && (candidate.parentName === undefined || typeof candidate.parentName === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string')
    && (candidate.value === undefined || typeof candidate.value === 'string')
    && (candidate.members === undefined || isHostDefinitionArray(candidate.members));
}

function isHostDefinitionKind(value: unknown): boolean {
  return value === 'class'
    || value === 'property'
    || value === 'function'
    || value === 'enum'
    || value === 'enumMember'
    || value === 'constant';
}

function isHostApplication(value: unknown): value is HostApplication {
  return typeof value === 'string'
    && (C_SUPPORTED_HOST_APPLICATIONS as readonly string[]).includes(value);
}

function sameName(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}
