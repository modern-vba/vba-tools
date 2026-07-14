import * as path from 'node:path';

export interface WorkbookBackedProjectCandidate {
  projectRoot: string;
  manifestPath: string;
}

export interface WorkbookBackedProjectDiscoveryOptions {
  activeFilePath?: string | undefined;
  workspaceRoots: readonly string[];
  fileExists: (filePath: string) => Promise<boolean>;
  findProjectManifests: (workspaceRoots: readonly string[]) => Promise<readonly string[]>;
  chooseProject: (
    candidates: readonly WorkbookBackedProjectCandidate[]
  ) => Promise<WorkbookBackedProjectCandidate | undefined>;
}

const ProjectManifestFileName = 'vba-project.json';

export async function findNearestProjectManifest(
  activeFilePath: string,
  fileExists: (filePath: string) => Promise<boolean>
): Promise<string | undefined> {
  let directory = path.basename(activeFilePath).toLowerCase() === ProjectManifestFileName
    ? path.dirname(activeFilePath)
    : path.dirname(activeFilePath);

  while (true) {
    const candidate = path.join(directory, ProjectManifestFileName);
    if (await fileExists(candidate)) {
      return candidate;
    }

    const parent = path.dirname(directory);
    if (parent === directory) {
      return undefined;
    }

    directory = parent;
  }
}

export async function discoverWorkbookBackedProject(
  options: WorkbookBackedProjectDiscoveryOptions
): Promise<WorkbookBackedProjectCandidate | undefined> {
  if (options.activeFilePath) {
    const manifestPath = await findNearestProjectManifest(options.activeFilePath, options.fileExists);
    if (manifestPath) {
      return toCandidate(manifestPath);
    }
  }

  const candidates = uniqueManifestPaths(await options.findProjectManifests(options.workspaceRoots))
    .map(toCandidate);

  if (candidates.length === 0) {
    return undefined;
  }

  if (candidates.length === 1) {
    return candidates[0];
  }

  return options.chooseProject(candidates);
}

function toCandidate(manifestPath: string): WorkbookBackedProjectCandidate {
  return {
    manifestPath,
    projectRoot: path.dirname(manifestPath)
  };
}

function uniqueManifestPaths(manifestPaths: readonly string[]): string[] {
  return [...new Set(manifestPaths.map((manifestPath) => path.normalize(manifestPath)))];
}
