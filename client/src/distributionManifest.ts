import * as path from 'node:path';
import { readFileSync } from 'node:fs';

export const distributionManifestFileName = 'distribution-manifest.json';

export interface DistributionManifest {
  manifestVersion: 1;
  runtimes: {
    vbaDev: DistributionRuntimeManifest;
    vbaLanguageServer: DistributionRuntimeManifest;
  };
  vsix: {
    requiredFiles: readonly string[];
    excludedSourcePrefixes: readonly string[];
    excludedFiles: readonly string[];
    excludedFileSuffixes: readonly string[];
    forbiddenRuntimeSidecarSuffixes: readonly string[];
  };
}

export interface DistributionRuntimeManifest {
  label: string;
  executablePath: string;
  projectPath: string;
  assemblyName: string;
  runtimeIdentifier: string;
  selfContained: boolean;
  publishSingleFile: boolean;
  smokeCommand: readonly string[];
  contractPath?: string | undefined;
  versionOutputPrefix?: string | undefined;
}

export class DistributionManifestError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'DistributionManifestError';
  }
}

export function loadDistributionManifest(extensionRoot: string): DistributionManifest {
  const manifestPath = path.join(extensionRoot, distributionManifestFileName);
  let parsed: unknown;
  try {
    parsed = JSON.parse(readFileSync(manifestPath, 'utf8')) as unknown;
  } catch (error) {
    throw new DistributionManifestError(
      `Distribution manifest could not be read from '${manifestPath}': ${String(error)}`
    );
  }

  if (!isDistributionManifest(parsed)) {
    throw new DistributionManifestError(
      `Distribution manifest at '${manifestPath}' must include runtime executable paths and VSIX rules.`
    );
  }

  return parsed;
}

export function resolveBundledRuntimePath(
  extensionRoot: string,
  runtime: keyof DistributionManifest['runtimes']
): string {
  return path.join(extensionRoot, loadDistributionManifest(extensionRoot).runtimes[runtime].executablePath);
}

function isDistributionManifest(value: unknown): value is DistributionManifest {
  if (!isRecord(value) || value.manifestVersion !== 1 || !isRecord(value.runtimes) || !isRecord(value.vsix)) {
    return false;
  }

  return isRuntimeManifest(value.runtimes.vbaDev) &&
    isRuntimeManifest(value.runtimes.vbaLanguageServer) &&
    isStringArray(value.vsix.requiredFiles) &&
    isStringArray(value.vsix.excludedSourcePrefixes) &&
    isStringArray(value.vsix.excludedFiles) &&
    isStringArray(value.vsix.excludedFileSuffixes) &&
    isStringArray(value.vsix.forbiddenRuntimeSidecarSuffixes);
}

function isRuntimeManifest(value: unknown): value is DistributionRuntimeManifest {
  return isRecord(value) &&
    typeof value.label === 'string' &&
    typeof value.executablePath === 'string' &&
    typeof value.projectPath === 'string' &&
    typeof value.assemblyName === 'string' &&
    typeof value.runtimeIdentifier === 'string' &&
    typeof value.selfContained === 'boolean' &&
    typeof value.publishSingleFile === 'boolean' &&
    isStringArray(value.smokeCommand) &&
    (value.contractPath === undefined || typeof value.contractPath === 'string') &&
    (value.versionOutputPrefix === undefined || typeof value.versionOutputPrefix === 'string');
}

function isStringArray(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
