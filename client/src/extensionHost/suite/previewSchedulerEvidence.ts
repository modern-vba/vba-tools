import { readFile, readdir, stat } from 'node:fs/promises';
import * as path from 'node:path';
import { isReferencePreparationSettled } from '../../previewPerformanceEvidence';

export async function waitForReferenceCatalogSettlement(directory: string): Promise<void> {
  const deadline = Date.now() + 90_000;
  while (Date.now() < deadline) {
    const files = (await readdir(directory)).filter(file =>
      file.includes('-vba_referenceCatalog') && !file.endsWith('.tmp'));
    const evidence = await Promise.all(files.map(async fileName => ({
      fileName, recordedAt: (await stat(path.join(directory, fileName))).mtimeMs
    })));
    if (isReferencePreparationSettled(evidence, Date.now())) { return; }
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  throw new Error('Reference preparation did not settle before warm measurement.');
}

export async function readPreviewSchedulerEvidence(directory: string) {
  return Promise.all((await readdir(directory)).filter(file =>
    /\.(?:admitted|captured|completed)$/u.test(file)).map(async file => {
      const filePath = path.join(directory, file);
      const [content, metadata] = await Promise.all([
        readFile(filePath, 'utf8'), stat(filePath)
      ]);
      return { path: filePath, recordedAt: metadata.mtimeMs,
        fields: Object.fromEntries(content.split(/\r?\n/u).filter(Boolean).map(line => {
          const separator = line.indexOf('=');
          return [line.slice(0, separator), line.slice(separator + 1)];
        })) };
    }));
}
