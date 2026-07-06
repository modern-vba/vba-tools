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
import {
  cloneHostDefinitions,
  cloneHostDefinitionsWithApplication,
  isHostDefinitionArray,
  mergeHostDefinitions
} from './hostDefinitionCatalog';
import { discoverHostDefinitionsFromTypeLibrary } from './hostTypeLibraryDiscovery';
import type { HostApplication, HostDefinition } from './hostDefinition';

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
export type HostCatalogTypeLibraryDiscovery = (hostApplication: HostApplication) => Promise<HostDefinition[]>;

export interface HostCatalogManagerOptions {
  platform?: NodeJS.Platform;
  cacheDirectory?: string;
  readCache?: HostCatalogCacheReader;
  writeCache?: HostCatalogCacheWriter;
  discoverFromCom?: HostCatalogComDiscovery;
  discoverFromTypeLibrary?: HostCatalogTypeLibraryDiscovery;
}

interface OfficeComDiscoverySpec {
  createScript: () => string;
  invalidCatalogMessage: string;
}

export class HostCatalogManager {
  private readonly definitionsByApplication = new Map<HostApplication, HostDefinition[]>();
  private readonly platform: NodeJS.Platform;
  private readonly cacheDirectory: string;
  private readonly readCache?: HostCatalogCacheReader;
  private readonly writeCache?: HostCatalogCacheWriter;
  private readonly discoverFromCom: HostCatalogComDiscovery;
  private readonly discoverFromTypeLibrary: HostCatalogTypeLibraryDiscovery;
  private readonly refreshAttempts = new Set<HostApplication>();
  private readonly refreshesInFlight = new Map<HostApplication, Promise<void>>();

  public constructor(options: HostCatalogManagerOptions = {}) {
    this.platform = options.platform ?? process.platform;
    this.cacheDirectory = options.cacheDirectory ?? getDefaultCacheDirectory();
    this.readCache = options.readCache;
    this.writeCache = options.writeCache;
    this.discoverFromCom = options.discoverFromCom ?? discoverOfficeComHostDefinitions;
    this.discoverFromTypeLibrary = options.discoverFromTypeLibrary ?? discoverHostDefinitionsFromTypeLibrary;

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
    const discovered_definitions = await this.discoverFromComSafely(hostApplication);
    const type_library_definitions = await this.discoverFromTypeLibrarySafely(hostApplication);
    if (discovered_definitions.length === 0 && type_library_definitions.length === 0) {
      return;
    }

    const base_definitions = discovered_definitions.length === 0
      ? this.getDefinitionsForApplication(hostApplication)
      : discovered_definitions;
    const definitions = cloneHostDefinitionsWithApplication(
      mergeHostDefinitions(base_definitions, type_library_definitions),
      hostApplication
    );
    this.definitionsByApplication.set(hostApplication, definitions);
    await this.writeCacheSafely(hostApplication, definitions);
  }

  private async discoverFromComSafely(
    hostApplication: HostApplication
  ): Promise<HostDefinition[]> {
    try {
      return await this.discoverFromCom(hostApplication);
    } catch {
      return [];
    }
  }

  private async discoverFromTypeLibrarySafely(
    hostApplication: HostApplication
  ): Promise<HostDefinition[]> {
    try {
      return await this.discoverFromTypeLibrary(hostApplication);
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

async function discoverOfficeComHostDefinitions(hostApplication: HostApplication): Promise<HostDefinition[]> {
  const spec = C_OFFICE_COM_DISCOVERY_SPECS[hostApplication];
  return executePowerShellHostCatalogScript(spec.createScript(), spec.invalidCatalogMessage);
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

const C_OFFICE_COM_DISCOVERY_SPECS: Record<HostApplication, OfficeComDiscoverySpec> = {
  excel: {
    createScript: createExcelComHostDiscoveryScript,
    invalidCatalogMessage: 'Excel COM discovery returned an invalid host catalog.'
  },
  word: {
    createScript: createWordComHostDiscoveryScript,
    invalidCatalogMessage: 'Word COM discovery returned an invalid host catalog.'
  },
  powerpoint: {
    createScript: createPowerPointComHostDiscoveryScript,
    invalidCatalogMessage: 'PowerPoint COM discovery returned an invalid host catalog.'
  },
  access: {
    createScript: createAccessComHostDiscoveryScript,
    invalidCatalogMessage: 'Access COM discovery returned an invalid host catalog.'
  }
};

function createExcelComHostDiscoveryScript(): string {
  return `
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
}

function createWordComHostDiscoveryScript(): string {
  return `
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
}

function createPowerPointComHostDiscoveryScript(): string {
  return `
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
}

function createAccessComHostDiscoveryScript(): string {
  return `
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
