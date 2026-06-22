import { execFile } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';

import {
  createHostApplicationSelection,
  getBundledHostDefinitionsForApplication,
  type HostApplicationSelectionOptions
} from './officeHostCatalog';
import type { HostDefinition } from './vbaProject';

const execFileAsync = promisify(execFile);

export interface HostCatalogManagerOptions {
  platform?: NodeJS.Platform;
  cachePath?: string;
  readCache?: () => HostDefinition[] | undefined;
  writeCache?: (definitions: HostDefinition[]) => void | Promise<void>;
  discoverFromCom?: () => Promise<HostDefinition[]>;
}

export class HostCatalogManager {
  private definitions: HostDefinition[];
  private readonly platform: NodeJS.Platform;
  private readonly cachePath: string;
  private readonly readCache?: () => HostDefinition[] | undefined;
  private readonly writeCache?: (definitions: HostDefinition[]) => void | Promise<void>;
  private readonly discoverFromCom: () => Promise<HostDefinition[]>;

  public constructor(options: HostCatalogManagerOptions = {}) {
    this.platform = options.platform ?? process.platform;
    this.cachePath = options.cachePath ?? getDefaultCachePath();
    this.readCache = options.readCache;
    this.writeCache = options.writeCache;
    this.discoverFromCom = options.discoverFromCom ?? discoverExcelComHostDefinitions;
    this.definitions = cloneHostDefinitions(this.readCacheSafely() ?? getBundledHostDefinitionsForApplication('excel'));
  }

  public getDefinitions(options: HostApplicationSelectionOptions = {}): HostDefinition[] {
    const selection = createHostApplicationSelection(options);
    return selection.enabledHostApplications.flatMap((hostApplication) =>
      hostApplication === 'excel'
        ? cloneHostDefinitions(this.definitions)
        : getBundledHostDefinitionsForApplication(hostApplication)
    );
  }

  public async refreshFromExcelComAsync(): Promise<void> {
    if (this.platform !== 'win32') {
      return;
    }

    try {
      const discovered_definitions = await this.discoverFromCom();
      if (discovered_definitions.length === 0) {
        return;
      }

      this.definitions = cloneHostDefinitions(discovered_definitions);
      await this.writeCacheSafely(discovered_definitions);
    } catch {
      return;
    }
  }

  private readCacheSafely(): HostDefinition[] | undefined {
    try {
      const definitions = this.readCache === undefined
        ? readHostCatalogCache(this.cachePath)
        : this.readCache();
      return definitions === undefined ? undefined : cloneHostDefinitions(definitions);
    } catch {
      return undefined;
    }
  }

  private async writeCacheSafely(definitions: HostDefinition[]): Promise<void> {
    try {
      if (this.writeCache === undefined) {
        writeHostCatalogCache(this.cachePath, definitions);
      } else {
        await this.writeCache(cloneHostDefinitions(definitions));
      }
    } catch {
      return;
    }
  }
}

export function createDefaultHostCatalogManager(): HostCatalogManager {
  return new HostCatalogManager();
}

function getDefaultCachePath(): string {
  return path.join(os.homedir(), '.vba-language-server', 'excel-host-catalog.json');
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
  function Convert-HostDefinition([string]$Name, $Object, [string]$Documentation, [string]$Kind) {
    $members = $Object |
      Get-Member -MemberType Method,Property |
      Sort-Object Name -Unique |
      ForEach-Object {
        $memberKind = if ($_.MemberType -eq 'Method') { 'function' } else { 'property' }
        @{ name = $_.Name; kind = $memberKind; documentation = 'Excel COM member.' }
      }
    @{ name = $Name; kind = $Kind; documentation = $Documentation; members = @($members) }
  }
  @(
    Convert-HostDefinition 'Application' $excel 'Represents the installed Microsoft Excel application.' 'class'
    Convert-HostDefinition 'Workbook' $workbook 'Represents an Excel workbook from the installed Excel COM object model.' 'class'
    Convert-HostDefinition 'Worksheet' $worksheet 'Represents an Excel worksheet from the installed Excel COM object model.' 'class'
    Convert-HostDefinition 'Range' $range 'Represents an Excel range from the installed Excel COM object model.' 'class'
  ) | ConvertTo-Json -Depth 5 -Compress
} finally {
  if ($workbook -ne $null) {
    $workbook.Close($false)
  }
  $excel.Quit()
}
`;

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
    throw new Error('Excel COM discovery returned an invalid host catalog.');
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
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string')
    && (candidate.members === undefined || isHostDefinitionArray(candidate.members));
}

function isHostDefinitionKind(value: unknown): boolean {
  return value === 'class'
    || value === 'property'
    || value === 'function'
    || value === 'enum'
    || value === 'enumMember';
}
