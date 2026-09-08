import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { decodeProjectManifestBytes } from './projectManifestBytes';
import { loadCommandPaletteProjectTarget } from './commandPaletteTarget';

const fixture: { cases: Array<{ name: string; base64: string; accepted: boolean; text?: string }> } =
  JSON.parse(readFileSync(path.resolve(__dirname, '../../fixtures/project-manifest-encoding/cases.json'), 'utf8'));

for (const entry of fixture.cases) {
  test(`ProjectManifest byte admission: ${entry.name}`, async () => {
    const bytes = Buffer.from(entry.base64, 'base64');
    if (entry.accepted) {
      assert.equal(decodeProjectManifestBytes(bytes), entry.text);
    } else {
      assert.throws(() => decodeProjectManifestBytes(bytes));
      for (const valid of fixture.cases.filter((candidate) => candidate.accepted)) {
        assert.equal(decodeProjectManifestBytes(Buffer.from(valid.base64, 'base64')), valid.text);
      }
    }
    const target = await loadCommandPaletteProjectTarget(path.resolve('project/vba-project.json'), {
      readTextFile: async () => decodeProjectManifestBytes(bytes),
      resolvePathIdentity: async (canonicalPath) => ({ canonicalPath })
    });
    assert.equal(target !== undefined, entry.accepted);
  });
}
