import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, realpath, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test from 'node:test';

import { assertCanonicalProjectReceiptPath } from './commonModulesReceiptAssertions';

test('CommonModules receipt assertion accepts a canonical path through a directory alias', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-receipt-path-'));
  try {
    const projectRoot = path.join(root, 'CommonModulesProject');
    const aliasRoot = path.join(root, 'alias');
    await mkdir(projectRoot);
    await symlink(projectRoot, aliasRoot, process.platform === 'win32' ? 'junction' : 'dir');
    const canonicalProject = await realpath(projectRoot);
    assert.notEqual(canonicalProject, aliasRoot);
    await assertCanonicalProjectReceiptPath(canonicalProject, aliasRoot);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('CommonModules receipt assertion accepts Windows short ancestor paths', {
  skip: process.platform !== 'win32'
}, async (context) => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-receipt-path-'));
  try {
    const projectRoot = path.join(root, 'CommonModulesProject');
    await mkdir(projectRoot);
    const shortPath = spawnSync(process.env.ComSpec ?? 'cmd.exe', [
      '/d', '/c', 'for %I in ("%VBA_TOOLS_SHORT_PATH_FIXTURE%") do @echo %~sI'
    ], {
      encoding: 'utf8',
      windowsHide: true,
      windowsVerbatimArguments: true,
      env: { ...process.env, VBA_TOOLS_SHORT_PATH_FIXTURE: projectRoot }
    });
    assert.equal(shortPath.status, 0, shortPath.stderr);
    const requestedProject = shortPath.stdout.trim();
    const canonicalProject = await realpath(projectRoot);
    if (requestedProject === canonicalProject) {
      context.skip('This fixture volume exposes no 8.3 alias; directory-alias coverage still applies.');
      return;
    }
    assert.equal(await realpath(requestedProject), canonicalProject);
    await assertCanonicalProjectReceiptPath(canonicalProject, requestedProject);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('CommonModules receipt assertion rejects a different project and noncanonical output', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-receipt-path-'));
  try {
    const projectRoot = path.join(root, 'CommonModulesProject');
    const otherProject = path.join(root, 'OtherProject');
    const aliasRoot = path.join(root, 'alias');
    await mkdir(projectRoot);
    await mkdir(otherProject);
    await symlink(projectRoot, aliasRoot, process.platform === 'win32' ? 'junction' : 'dir');
    await assertCanonicalProjectReceiptPath(await realpath(projectRoot), projectRoot);
    await assert.rejects(assertCanonicalProjectReceiptPath(await realpath(otherProject), projectRoot));
    await assert.rejects(assertCanonicalProjectReceiptPath(aliasRoot, projectRoot));
    await assert.rejects(assertCanonicalProjectReceiptPath(undefined, projectRoot));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
