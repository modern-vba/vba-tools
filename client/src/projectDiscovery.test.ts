import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  discoverWorkbookBackedProject,
  findNearestProjectManifest
} from './projectDiscovery';

test('ProjectManifest discovery walks upward from an active VBA source file', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const activeFilePath = path.join(projectRoot, 'src', 'Book1', 'Module1.bas');
  const existingFiles = new Set([path.join(projectRoot, 'project.json')]);

  assert.equal(
    await findNearestProjectManifest(activeFilePath, async (candidate) => existingFiles.has(candidate)),
    path.join(projectRoot, 'project.json')
  );
});

test('ProjectManifest discovery treats project.json itself as the manifest', async () => {
  const manifestPath = path.join('C:', 'work', 'BookProject', 'project.json');

  assert.equal(
    await findNearestProjectManifest(manifestPath, async (candidate) => candidate === manifestPath),
    manifestPath
  );
});

test('WorkbookBackedProject selection uses workspace candidates when the active file is ambiguous', async () => {
  const firstRoot = path.join('C:', 'work', 'First');
  const secondRoot = path.join('C:', 'work', 'Second');
  const selected = await discoverWorkbookBackedProject({
    workspaceRoots: [path.join('C:', 'work')],
    fileExists: async () => false,
    findProjectManifests: async () => [
      path.join(firstRoot, 'project.json'),
      path.join(secondRoot, 'project.json')
    ],
    chooseProject: async (candidates) => {
      assert.deepEqual(candidates.map((candidate) => candidate.projectRoot), [firstRoot, secondRoot]);
      return candidates[1];
    }
  });

  assert.equal(selected?.projectRoot, secondRoot);
  assert.equal(selected?.manifestPath, path.join(secondRoot, 'project.json'));
});
