import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test from 'node:test';
import { captureBuildInputProvenance } from './sourceProvenance';

test('measurement source provenance includes new source files and excludes generated outputs', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-preview-provenance-'));
  try {
    const source = path.join(root, 'tools', 'vba-language-server', 'src');
    await mkdir(path.join(source, 'bin'), { recursive: true });
    await writeFile(path.join(source, 'Existing.cs'), 'existing\n');
    const before = await captureBuildInputProvenance(root);
    await writeFile(path.join(source, 'bin', 'Generated.cs'), 'generated\n');
    assert.equal((await captureBuildInputProvenance(root)).treeSha256, before.treeSha256);
    await writeFile(path.join(source, 'NewRetainedAnalysis.cs'), 'untracked new source\n');
    const after = await captureBuildInputProvenance(root);
    assert.notEqual(after.treeSha256, before.treeSha256);
    assert.ok(after.files.some(file => file.path.endsWith('/NewRetainedAnalysis.cs')));
  } finally { await rm(root, { recursive: true, force: true }); }
});
