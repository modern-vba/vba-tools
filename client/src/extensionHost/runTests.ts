import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { Buffer } from 'node:buffer';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import {
  downloadAndUnzipVSCode,
  runTests
} from '@vscode/test-electron';
import {
  createExtensionHostLaunchArgs,
  createExtensionHostRuntimeSelection
} from './configuration';
import { runRestrictedModeExtensionHostTests } from './restrictedModeExtensionHost';

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
  const hostEventCatalogTestsPath = path.resolve(
    __dirname,
    'suite',
    'intrinsicHostEventCatalogIndex.js'
  );
  const runtime = createExtensionHostRuntimeSelection(process.env);
  const userDataPath = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-extension-host-'
  ));
  const fixtureRoot = await createDebugConfigurationFixture();
  const mutationFixtureRoot = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-manifest-mutation-fixture-'
  ));
  const hostEventCatalogUserDataPath = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-host-event-catalog-user-data-'
  ));
  const untrustedHostEventCatalogUserDataPath = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-untrusted-host-event-catalog-user-data-'
  ));
  const hostEventCatalogFixtureRoot = await createHostEventCatalogFixture();

  try {
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath: hostEventCatalogTestsPath,
      vscodeExecutablePath: runtime.vscodeExecutablePath,
      version: runtime.version,
      launchArgs: createExtensionHostLaunchArgs(
        hostEventCatalogUserDataPath,
        hostEventCatalogFixtureRoot
      ),
      extensionTestsEnv: {
        VBA_TOOLS_EXTENSION_HOST_TEST: '1',
        VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT: hostEventCatalogFixtureRoot,
        VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted'
      }
    });
    const restrictedModeVscodeExecutablePath = runtime.vscodeExecutablePath ??
      await downloadAndUnzipVSCode({
        version: runtime.version,
        extensionDevelopmentPath
      });
    await runRestrictedModeExtensionHostTests({
      extensionDevelopmentPath,
      extensionTestsPath: hostEventCatalogTestsPath,
      vscodeExecutablePath: restrictedModeVscodeExecutablePath,
      userDataPath: untrustedHostEventCatalogUserDataPath,
      workspacePath: hostEventCatalogFixtureRoot,
      extensionTestsEnvironment: {
        VBA_TOOLS_EXTENSION_HOST_TEST: '1',
        VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT: hostEventCatalogFixtureRoot,
        VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'actual-untrusted'
      }
    });
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      vscodeExecutablePath: runtime.vscodeExecutablePath,
      version: runtime.version,
      launchArgs: createExtensionHostLaunchArgs(userDataPath, fixtureRoot),
      extensionTestsEnv: {
        VBA_TOOLS_EXTENSION_HOST_TEST: '1',
        VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT: fixtureRoot,
        VBA_TOOLS_EXTENSION_HOST_MUTATION_FIXTURE_ROOT: mutationFixtureRoot,
        VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted'
      }
    });
  } finally {
    await rm(userDataPath, { recursive: true, force: true });
    await rm(fixtureRoot, { recursive: true, force: true });
    await rm(mutationFixtureRoot, { recursive: true, force: true });
    await rm(hostEventCatalogUserDataPath, { recursive: true, force: true });
    await rm(untrustedHostEventCatalogUserDataPath, { recursive: true, force: true });
    await rm(hostEventCatalogFixtureRoot, { recursive: true, force: true });
  }
}

async function createHostEventCatalogFixture(): Promise<string> {
  const fixtureRoot = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-host-event-catalog-fixture-'
  ));
  const documents: Record<string, {
    kind: string;
    sourcePath: string;
    templatePath: string;
    binPath: string;
    publishPath: string;
    commonModules: never[];
    references: never[];
  }> = {};
  for (let index = 1; index <= 15; index += 1) {
    const document = `Book${String(index).padStart(2, '0')}`;
    const sourcePath = path.join(fixtureRoot, 'src', document);
    const templatePath = path.join(fixtureRoot, 'templates', `${document}.xlsm`);
    await mkdir(sourcePath, { recursive: true });
    await mkdir(path.dirname(templatePath), { recursive: true });
    await writeFile(path.join(sourcePath, 'ProbeForm.frm'), [
      'VERSION 5.00',
      'Begin VB.UserForm ProbeForm',
      'End',
      'Attribute VB_Name = "ProbeForm"',
      '',
      'Private Sub Probe()',
      'End Sub',
      '',
      'Private Sub UserForm_',
      ''
    ].join('\r\n'), 'utf8');
    await writeFile(
      templatePath,
      Buffer.from(`sentinel-${document}`, 'utf8')
    );
    documents[document] = {
      kind: 'excel',
      sourcePath: `src/${document}`,
      templatePath: `templates/${document}.xlsm`,
      binPath: `bin/${document}.xlsm`,
      publishPath: `publish/${document}.xlsm`,
      commonModules: [],
      references: []
    };
  }
  await writeFile(path.join(fixtureRoot, 'vba-project.json'), JSON.stringify({
    schemaVersion: 1,
    projectName: 'HostEventCatalogFixture',
    primaryDocument: 'Book01',
    documents
  }, undefined, 2), 'utf8');
  return fixtureRoot;
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
        publishPath: 'publish/Book1.xlsm',
        commonModules: [],
        references: []
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
  await writeFile(path.join(outsidePath, 'InvoiceModule.bas'), [
    'Attribute VB_Name = "InvoiceModule"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    'End Sub',
    '',
    'Public Sub Invoke()',
    '    InvoiceModule.Run',
    'End Sub',
    ''
  ].join('\r\n'), 'utf8');
  await writeFile(path.join(outsidePath, 'ApplicationModule.bas'), [
    'Attribute VB_Name = "ApplicationModule"',
    'Option Explicit',
    ''
  ].join('\r\n'), 'utf8');
  await writeFile(path.join(outsidePath, 'Dialog.frm'), [
    'VERSION 5.00',
    'Begin VB.UserForm dIaLoG',
    '   OleObjectBlob = "DIALOG.FRX":0000',
    'End',
    'Attribute VB_Name = "dIaLoG"',
    ''
  ].join('\r\n'), 'utf8');
  await writeFile(
    path.join(outsidePath, 'DIALOG.FRX'),
    Uint8Array.from([0x00, 0x01, 0x02, 0x03])
  );
  await writeFile(path.join(outsidePath, 'MixedCaseForm.frm'), [
    'VERSION 5.00',
    'Begin VB.UserForm mIxEdCaSeFoRm',
    '   OleObjectBlob = "mixedcaseform.frx":0000',
    'End',
    'Attribute VB_Name = "mIxEdCaSeFoRm"',
    ''
  ].join('\r\n'), 'utf8');
  await writeFile(
    path.join(outsidePath, 'mixedcaseform.frx'),
    Uint8Array.from([0x04, 0x05, 0x06, 0x07])
  );
  await writeFile(path.join(outsidePath, 'StandaloneForm.frm'), [
    'VERSION 5.00',
    'Begin VB.UserForm StandaloneForm',
    'End',
    'Attribute VB_Name = "StandaloneForm"',
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
