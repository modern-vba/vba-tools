import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { Buffer } from 'node:buffer';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import { runTests } from '@vscode/test-electron';
import {
  createExtensionHostLaunchArgs,
  createExtensionHostRuntimeSelection
} from './configuration';

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
  const runtime = createExtensionHostRuntimeSelection(process.env);
  const userDataPath = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-extension-host-'
  ));
  const fixtureRoot = await createDebugConfigurationFixture();

  try {
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      vscodeExecutablePath: runtime.vscodeExecutablePath,
      version: runtime.version,
      launchArgs: createExtensionHostLaunchArgs(userDataPath, fixtureRoot),
      extensionTestsEnv: {
        VBA_TOOLS_EXTENSION_HOST_TEST: '1',
        VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT: fixtureRoot
      }
    });
  } finally {
    await rm(userDataPath, { recursive: true, force: true });
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

async function createDebugConfigurationFixture(): Promise<string> {
  const fixtureRoot = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-debug-fixture-'
  ));
  const sourceSetPath = path.join(fixtureRoot, 'src', 'Book1');
  const outsidePath = path.join(fixtureRoot, 'outside');
  await mkdir(sourceSetPath, { recursive: true });
  await mkdir(outsidePath, { recursive: true });
  await writeFile(path.join(fixtureRoot, 'vba-project.json'), JSON.stringify({
    schemaVersion: 1,
    projectName: 'DebugFixture',
    primaryDocument: 'Book1',
    documents: {
      Book1: {
        kind: 'excel',
        sourcePath: 'src/Book1',
        templatePath: 'src/Book1/Book1.xlsm',
        binPath: 'bin/Book1.xlsm',
        publishPath: 'publish/Book1.xlsm'
      }
    }
  }, undefined, 2), 'utf8');
  await writeFile(path.join(sourceSetPath, 'DebugModule.bas'), [
    'Attribute VB_Name = "DebugModule"',
    'Option Explicit',
    '',
    'Public Sub DebugMe()',
    '    Debug.Print "saved"',
    'End Sub',
    ''
  ].join('\r\n'), 'utf8');
  await writeFile(
    path.join(sourceSetPath, 'EncodedModule.bas'),
    createCp932DebugSource()
  );
  await writeFile(path.join(outsidePath, 'Outside.bas'), [
    'Attribute VB_Name = "Outside"',
    'Option Explicit',
    ''
  ].join('\r\n'), 'utf8');
  return fixtureRoot;
}

function createCp932DebugSource(): Uint8Array {
  return Uint8Array.from([
    ...Buffer.from([
      'Attribute VB_Name = "EncodedModule"',
      'Option Explicit',
      '',
      'Public Sub EncodedTarget()',
      '    Debug.Print "'
    ].join('\r\n'), 'ascii'),
    0x93, 0xfa,
    0x96, 0x7b,
    0x8c, 0xea,
    ...Buffer.from(['"', 'End Sub', ''].join('\r\n'), 'ascii')
  ]);
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
