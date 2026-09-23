import { cp, lstat, mkdir, mkdtemp } from 'node:fs/promises';
import * as path from 'node:path';

export interface ExtensionHostLogProfile {
  readonly name: string;
  readonly userDataPath: string;
}

export async function saveExtensionHostFailureLogs(
  profiles: readonly ExtensionHostLogProfile[],
  evidenceRoot: string
): Promise<string> {
  await mkdir(evidenceRoot, { recursive: true });
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
