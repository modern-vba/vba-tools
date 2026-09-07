import { createHash } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import * as path from 'node:path';

export async function captureBuildInputProvenance(root: string) {
  const directories = [
    'tools/vba-language-server/src', 'tools/vba-syntax/src',
    'tools/vba-project-metadata/src', 'tools/vba-protocol-framing/src',
    'tools/vba-dev/src', 'client/src'
  ];
  const excluded = new Set(['bin', 'obj', 'out', 'node_modules', '.git']);
  const files = new Set<string>();
  const enumerate = async (relative: string): Promise<void> => {
    let entries;
    try { entries = await readdir(path.join(root, relative), { withFileTypes: true }); }
    catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') { return; }
      throw error;
    }
    for (const entry of entries) {
      const next = `${relative}/${entry.name}`;
      if (entry.isDirectory()) {
        if (!excluded.has(entry.name)) { await enumerate(next); }
      } else if (entry.isFile()) { files.add(next); }
    }
  };
  for (const directory of directories) { await enumerate(directory); }
  const parents = new Set(['', 'client']);
  for (const directory of directories) {
    const parts = directory.split('/');
    for (let length = 1; length < parts.length; length++) {
      parents.add(parts.slice(0, length).join('/'));
    }
  }
  for (const parent of parents) {
    let entries;
    try { entries = await readdir(path.join(root, parent), { withFileTypes: true }); }
    catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') { continue; }
      throw error;
    }
    for (const entry of entries) {
      if (entry.isFile() && (/\.(?:props|targets)$/iu.test(entry.name)
          || /^(?:global|package(?:-lock)?|tsconfig(?:\.[^.]+)?)\.json$/iu.test(entry.name)
          || /^nuget\.config$/iu.test(entry.name))) {
        files.add(parent === '' ? entry.name : `${parent}/${entry.name}`);
      }
    }
  }
  const evidence = await Promise.all([...files].sort().map(async relative => ({
    path: relative,
    sha256: createHash('sha256').update(await readFile(path.join(root, relative))).digest('hex')
  })));
  return {
    algorithm: 'SHA-256 of sorted relative-path NUL file-SHA-256 lines, UTF-8',
    treeSha256: createHash('sha256').update(evidence
      .map(file => `${file.path}\0${file.sha256}\n`).join(''), 'utf8').digest('hex'),
    files: evidence
  };
}
