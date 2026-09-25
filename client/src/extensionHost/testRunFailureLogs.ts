import { cp, lstat, mkdir, mkdtemp } from 'node:fs/promises';
import * as path from 'node:path';

export interface ExtensionHostLogProfile {
  readonly name: string;
  readonly userDataPath: string;
}

const diagnosticRunName = /^run-\d{8}T\d{9}Z-[0-9a-f]{16}$/;

export function resolveExtensionHostFailureLogRoot(
  extensionDevelopmentPath: string,
  env: NodeJS.ProcessEnv = process.env
): string {
  const runRoot = env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT;
  if (runRoot && path.isAbsolute(runRoot)
      && !path.win32.normalize(runRoot).startsWith('\\\\')
      && diagnosticRunName.test(path.basename(runRoot))) {
    return path.join(runRoot, 'extension-host');
  }
  return path.join(extensionDevelopmentPath, '.tmp', 'extension-host-failures');
}

export async function saveExtensionHostFailureLogs(
  profiles: readonly ExtensionHostLogProfile[],
  evidenceRoot: string
): Promise<string> {
  await ensureUnlinkedEvidenceDirectory(evidenceRoot);
  const destination = await mkdtemp(path.join(evidenceRoot, 'run-'));
  const failures: Error[] = [];
  for (const profile of profiles) {
    try {
      const logs = path.join(profile.userDataPath, 'logs');
      const exists = await lstat(logs).catch((error: NodeJS.ErrnoException) => {
        if (error.code === 'ENOENT') { return undefined; }
        throw error;
      });
      if (!exists) { continue; }
      await cp(logs, path.join(destination, profile.name),
        {
          recursive: true,
          force: false,
          errorOnExist: true,
          filter: async source => {
            const entry = await lstat(source);
            return entry.isDirectory() || entry.isFile();
          }
        });
    } catch (error) {
      failures.push(new Error(`Could not capture Extension Host logs: ${profile.name}`, {
        cause: error
      }));
    }
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, `Extension Host log snapshot is incomplete: ${destination}`);
  }
  return destination;
}

async function ensureUnlinkedEvidenceDirectory(directory: string): Promise<void> {
  const fullPath = path.resolve(directory);
  if (process.platform === 'win32' && path.win32.normalize(fullPath).startsWith('\\\\')) {
    throw new Error('Refusing linked diagnostic evidence directory: network or device path');
  }
  const root = path.parse(fullPath).root;
  let current = root;
  for (const component of fullPath.slice(root.length).split(path.sep).filter(Boolean)) {
    current = path.join(current, component);
    const entry = await lstat(current).catch(async (error: NodeJS.ErrnoException) => {
      if (error.code !== 'ENOENT') { throw error; }
      await mkdir(current);
      return lstat(current);
    });
    if (!entry.isDirectory() || entry.isSymbolicLink()) {
      throw new Error(`Refusing linked diagnostic evidence directory: ${current}`);
    }
  }
}
