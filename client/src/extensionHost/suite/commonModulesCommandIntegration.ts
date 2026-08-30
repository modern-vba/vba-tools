import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, stat, unlink, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { window } from 'vscode';

import {
  CommonModulesCommandOptions,
  runCommonModulesAddCommand,
  runCommonModulesUpdateCommand
} from '../../commonModulesCommand';
import {
  CommandPaletteTarget,
  resolveCommandPaletteProjectTargetFromManifestText
} from '../../commandPaletteTarget';
import { resolveCommandPalettePathIdentity } from '../../commandPaletteTargetAdapter';
import { StartVbaDevProcess } from '../../devtoolCommand';
import { createHarness } from './projectManifestMutationIntegration';

interface CommonModulesFixture {
  root: string;
  projectRoot: string;
  manifestPath: string;
  repositoryPath: string;
  packageModulePaths: readonly string[];
  sourceModulePaths: readonly string[];
  target: CommandPaletteTarget;
}

interface CapturedCommandOutput {
  lines: string[];
  shows: boolean[];
  dispose(): void;
  channel: CommonModulesCommandOptions['outputChannel'];
}

interface RecordedProcessController {
  readonly rawStdout: string[];
  readonly startProcess: StartVbaDevProcess;
  transformNextStdout?: ((stdout: string) => string) | undefined;
}

export async function runCommonModulesCommandIntegrationTests(): Promise<void> {
  await runTest(
    'packaged CommonModules CLI Add and no-op Update remain one-shot through the real coordinator',
    async () => {
      const fixture = await createFixture();
      const harness = createHarness({ decodeManifestBytes: decodeProjectManifestBytes });
      const commandOutput = createCapturedCommandOutput();
      const processInvocations: Array<{
        executablePath: string;
        args: readonly string[];
      }> = [];
      const processController = createRecordedProcessController(processInvocations);
      const informationMessages: string[] = [];
      const warningMessages: string[] = [];
      const warningActions: string[] = [];
      const errorMessages: string[] = [];
      const explicitOutputShows: string[] = [];
      const extensionRoot = path.resolve(__dirname, '..', '..', '..', '..');
      const executablePath = path.join(
        extensionRoot,
        'bin',
        'vba-dev',
        'win-x64',
        'vba-dev.exe'
      );
      const options: CommonModulesCommandOptions = {
        extensionRoot,
        configuredDevToolPath: executablePath,
        activeFilePath: fixture.manifestPath,
        workspaceRoots: [fixture.root],
        fileExists,
        findProjectManifests: async () => [fixture.manifestPath],
        chooseProject: async (candidates) => candidates[0],
        resolveCommandPaletteTarget: async (scope) => scope === 'project'
          ? { project: fixture.target.project }
          : fixture.target,
        projectManifestMutationCoordinator: harness.coordinator,
        outputChannel: commandOutput.channel,
        startProcess: processController.startProcess,
        showErrorMessage: async (message) => {
          errorMessages.push(message);
        },
        showInformationMessage: async (message) => {
          informationMessages.push(message);
        },
        showWarningMessage: async (message, action) => {
          warningMessages.push(message);
          warningActions.push(action);
          return undefined;
        },
        showOutput: () => {
          explicitOutputShows.push('show');
        }
      };

      try {
        assert.equal(await fileExists(executablePath), true);

        const add = await runCommonModulesAddCommand(options, ['Feature']);

        assert.ok(add);
        assert.equal(add.exitCode, 0);
        assert.equal(add.cancelled, false);
        const manifestAfterAdd = decodeProjectManifestBytes(
          await readFile(fixture.manifestPath)
        );
        assert.ok(add.commonModulesMutation, JSON.stringify({
          informationMessages,
          warningMessages,
          errorMessages,
          coordinatorReports: harness.reports,
          commandOutput: commandOutput.lines,
          processInvocations,
          manifestAfterAdd
        }, undefined, 2));
        assert.equal(add.commonModulesMutation.operation, 'add');
        assert.equal(add.commonModulesMutation?.document, 'Book1');
        assert.deepEqual(
          add.commonModulesMutation?.documents[0]?.modules.map((module) => ({
            name: module.name,
            requested: module.requested,
            status: module.status,
            changes: module.changes.map((change) => change.kind)
          })),
          [
            {
              name: 'Base',
              requested: false,
              status: 'changed',
              changes: ['installed']
            },
            {
              name: 'Feature',
              requested: true,
              status: 'changed',
              changes: ['installed']
            }
          ]
        );
        assert.deepEqual(informationMessages, [
          'CommonModules for Book1: 2 changed, 0 unchanged, 0 references added.'
        ]);
        assert.deepEqual(warningMessages, []);
        assert.deepEqual(warningActions, []);
        assert.deepEqual(errorMessages, []);
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestChanged]')));
        assert.deepEqual(
          await Promise.all(fixture.sourceModulePaths.map(fileExists)),
          [true, true]
        );
        assert.ok(manifestAfterAdd.indexOf('"name": "Base"') <
          manifestAfterAdd.indexOf('"name": "Feature"'));
        assert.match(manifestAfterAdd, /"name": "Feature"/u);
        assertNoAutomaticOutputReveal(harness.outputShows, commandOutput.shows, explicitOutputShows);
        assertNoListInvocation(processInvocations);

        informationMessages.length = 0;
        warningMessages.length = 0;
        warningActions.length = 0;
        harness.reports.length = 0;

        const update = await runCommonModulesUpdateCommand(options);

        assert.ok(update);
        assert.equal(update.exitCode, 0);
        assert.equal(update.cancelled, false);
        assert.equal(update.commonModulesMutation?.operation, 'update');
        assert.equal(update.commonModulesMutation?.document, null);
        assert.deepEqual(
          update.commonModulesMutation?.documents[0]?.modules.map((module) => ({
            name: module.name,
            status: module.status,
            changes: module.changes.map((change) => change.kind)
          })),
          [
            { name: 'Base', status: 'unchanged', changes: [] },
            { name: 'Feature', status: 'unchanged', changes: [] }
          ]
        );
        assert.deepEqual(informationMessages, [
          'CommonModules update for CommonModulesProject: 0 changed, 2 unchanged, 0 references added.'
        ]);
        assert.deepEqual(warningMessages, []);
        assert.deepEqual(warningActions, []);
        assert.deepEqual(errorMessages, []);
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestUnchanged]')));
        assertNoAutomaticOutputReveal(harness.outputShows, commandOutput.shows, explicitOutputShows);
        assertNoListInvocation(processInvocations);
        informationMessages.length = 0;
        warningMessages.length = 0;
        warningActions.length = 0;
        harness.reports.length = 0;
        await writePackageManifest(fixture.repositoryPath, [
          'Unrelated.bas\toptional\t\t[]'
        ]);
        await writeModule(
          path.join(fixture.repositoryPath, 'Unrelated.bas'),
          'Unrelated'
        );
        await Promise.all(fixture.packageModulePaths.map((modulePath) =>
          unlink(modulePath)));

        const orphaned = await runCommonModulesUpdateCommand(options);

        assert.ok(orphaned?.commonModulesMutation, JSON.stringify({
          orphaned,
          informationMessages,
          warningMessages,
          warningActions,
          errorMessages,
          coordinatorReports: harness.reports,
          commandOutput: commandOutput.lines.slice(-4),
          processInvocations
        }, undefined, 2));
        assert.equal(orphaned.exitCode, 0);
        assert.deepEqual(
          orphaned.commonModulesMutation.documents[0]?.modules.map((module) => ({
            name: module.name,
            orphaned: module.orphaned,
            status: module.status,
            changes: module.changes.map((change) => change.kind)
          })),
          [
            {
              name: 'Base',
              orphaned: true,
              status: 'changed',
              changes: ['orphanedChanged']
            },
            {
              name: 'Feature',
              orphaned: true,
              status: 'changed',
              changes: ['orphanedChanged']
            }
          ]
        );
        assert.deepEqual(
          orphaned.commonModulesMutation.warnings.map((warning) => warning.code),
          ['orphanedCommonModulesRetained']
        );
        assert.deepEqual(informationMessages, []);
        assert.deepEqual(warningMessages, [
          'CommonModules update for CommonModulesProject: 2 changed, 0 unchanged, ' +
            '0 references added. 1 warning.'
        ]);
        assert.deepEqual(warningActions, ['Show Output']);
        assert.deepEqual(errorMessages, []);
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestChanged]')));
        assertNoAutomaticOutputReveal(harness.outputShows, commandOutput.shows, explicitOutputShows);
        assertNoListInvocation(processInvocations);

        informationMessages.length = 0;
        warningMessages.length = 0;
        warningActions.length = 0;
        harness.reports.length = 0;
        const manifestBeforeUntrusted = await readFile(fixture.manifestPath);
        const mismatchedProject = path.join(fixture.root, 'MismatchedProject');
        processController.transformNextStdout = (stdout) => {
          try {
            const receipt = JSON.parse(stdout) as Record<string, unknown>;
            return JSON.stringify({ ...receipt, project: mismatchedProject });
          } catch {
            return '{"malformed":true}';
          }
        };

        const untrusted = await runCommonModulesUpdateCommand(options);

        assert.ok(untrusted);
        assert.equal(untrusted.exitCode, 0);
        assert.equal(untrusted.cancelled, false);
        assert.equal(untrusted.commonModulesMutation, undefined);
        assert.deepEqual(await readFile(fixture.manifestPath), manifestBeforeUntrusted);
        const rawUntrustedReceipt = JSON.parse(
          processController.rawStdout.at(-1)!
        ) as Record<string, unknown>;
        assert.equal(rawUntrustedReceipt.schemaVersion, '1.0');
        assert.equal(rawUntrustedReceipt.operation, 'update');
        assert.equal(rawUntrustedReceipt.project, fixture.projectRoot);
        assert.equal(rawUntrustedReceipt.complete, true);
        assert.ok(await resolveCommandPaletteProjectTargetFromManifestText(
          fixture.manifestPath,
          decodeProjectManifestBytes(manifestBeforeUntrusted),
          resolveCommandPalettePathIdentity
        ));
        assert.deepEqual(informationMessages, []);
        assert.deepEqual(warningMessages, [
          'CommonModules Update completed with an untrusted result; the project manifest may ' +
            'already have committed. Inspect the manifest and VBA Tools Output before retrying.'
        ]);
        assert.deepEqual(warningActions, ['Show Output']);
        assert.deepEqual(errorMessages, []);
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestUnchanged]')));
        assertNoAutomaticOutputReveal(harness.outputShows, commandOutput.shows, explicitOutputShows);
        assertNoListInvocation(processInvocations);
        assert.deepEqual(
          processInvocations.map((invocation) => invocation.args.slice(0, 2)),
          [
            ['common-module', 'add'],
            ['common-module', 'update'],
            ['common-module', 'update'],
            ['common-module', 'update']
          ]
        );
        assert.equal(processController.rawStdout.length, 4);
        assert.deepEqual(harness.prompts, []);
      } finally {
        commandOutput.dispose();
        harness.dispose();
      }
    }
  );
}

async function createFixture(): Promise<CommonModulesFixture> {
  const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_MUTATION_FIXTURE_ROOT;
  assert.ok(fixtureRoot, 'Manifest mutation fixture root was not configured.');
  const root = await mkdtemp(path.join(fixtureRoot, 'common-modules-case-'));
  const projectRoot = path.join(root, 'CommonModulesProject');
  const repositoryPath = path.join(root, 'common_modules_repo');
  const sourceRoot = path.join(projectRoot, 'src', 'Book1');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const packageModulePaths = [
    path.join(repositoryPath, 'Base.bas'),
    path.join(repositoryPath, 'Feature.bas')
  ];
  const sourceModulePaths = [
    path.join(sourceRoot, 'common-modules', 'Base.bas'),
    path.join(sourceRoot, 'common-modules', 'Feature.bas')
  ];
  await mkdir(sourceRoot, { recursive: true });
  await mkdir(repositoryPath, { recursive: true });
  await mkdir(path.join(projectRoot, 'bin'), { recursive: true });
  await mkdir(path.join(projectRoot, 'publish'), { recursive: true });

  const manifest = `${JSON.stringify({
    schemaVersion: 1,
    projectName: 'CommonModulesProject',
    primaryDocument: 'Book1',
    commonModulesRepository: '../common_modules_repo',
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
  }, undefined, 2)}\n`;
  await writeFile(manifestPath, manifest, 'utf8');
  await writePackageManifest(repositoryPath, [
    'Base.bas\truntime-baseline\t\t[]',
    'Feature.bas\toptional\tBase.bas\t[]'
  ]);
  await writeModule(packageModulePaths[0]!, 'Base');
  await writeModule(packageModulePaths[1]!, 'Feature');

  const project = await resolveCommandPaletteProjectTargetFromManifestText(
    manifestPath,
    manifest,
    resolveCommandPalettePathIdentity
  );
  assert.ok(project);
  const document = project.documents[0];
  assert.ok(document);
  return {
    root,
    projectRoot,
    manifestPath,
    repositoryPath,
    packageModulePaths,
    sourceModulePaths,
    target: { project, document }
  };
}

async function writePackageManifest(
  repositoryPath: string,
  rows: readonly string[]
): Promise<void> {
  const packageManifest = [
    'ModuleFile\tCategories\tDependencies\tRequiredReferences',
    ...rows,
    ''
  ].join('\r\n');
  await writeFile(
    path.join(repositoryPath, 'common-modules-manifest.tsv'),
    Buffer.concat([
      Buffer.from([0xff, 0xfe]),
      Buffer.from(packageManifest, 'utf16le')
    ])
  );
}

async function writeModule(modulePath: string, moduleName: string): Promise<void> {
  await writeFile(
    modulePath,
    Buffer.from([
      `Attribute VB_Name = "${moduleName}"`,
      'Option Explicit',
      '',
      `Public Sub Run${moduleName}()`,
      'End Sub',
      ''
    ].join('\r\n'), 'ascii')
  );
}

function createCapturedCommandOutput(): CapturedCommandOutput {
  const lines: string[] = [];
  const shows: boolean[] = [];
  const output = window.createOutputChannel('VBA Tools CommonModules CLI Integration');
  return {
    lines,
    shows,
    channel: {
      append: (value) => {
        lines.push(value);
        output.append(value);
      },
      appendLine: (value) => {
        lines.push(`${value}\n`);
        output.appendLine(value);
      },
      show: (preserveFocus) => {
        shows.push(preserveFocus ?? false);
        output.show(preserveFocus);
      }
    },
    dispose: () => output.dispose()
  };
}

function createRecordedProcessController(
  invocations: Array<{ executablePath: string; args: readonly string[] }>
): RecordedProcessController {
  const controller: RecordedProcessController = {
    rawStdout: [],
    startProcess: (executablePath, args) => {
      const transformStdout = controller.transformNextStdout;
      controller.transformNextStdout = undefined;
      let stdout = '';
      let stdoutListener: ((value: string) => void) | undefined;
      const child = spawn(executablePath, [...args], { windowsHide: true });
      child.stdout.on('data', (chunk: Buffer) => {
        stdout += chunk.toString('utf8');
      });
      invocations.push({ executablePath, args: [...args] });
      return {
        started: child.pid !== undefined,
        onStdout: (listener) => {
          stdoutListener = listener;
        },
        onStderr: (listener) => {
          child.stderr.on('data', (chunk: Buffer) => listener(chunk.toString('utf8')));
        },
        onSpawn: (listener) => {
          child.once('spawn', listener);
        },
        onExit: (listener) => {
          child.once('exit', listener);
        },
        onClose: (listener) => {
          child.once('close', (exitCode, signal) => {
            controller.rawStdout.push(stdout);
            stdoutListener?.(transformStdout?.(stdout) ?? stdout);
            listener(exitCode, signal);
          });
        },
        onError: (listener) => {
          child.once('error', listener);
        },
        kill: () => {
          child.kill();
        }
      };
    }
  };
  return controller;
}

async function fileExists(candidate: string): Promise<boolean> {
  try {
    await stat(candidate);
    return true;
  } catch {
    return false;
  }
}

function decodeProjectManifestBytes(bytes: Uint8Array): string {
  const buffer = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (buffer.length >= 2 && buffer[0] === 0xff && buffer[1] === 0xfe) {
    return buffer.subarray(2).toString('utf16le');
  }
  if (
    buffer.length >= 3
    && buffer[0] === 0xef
    && buffer[1] === 0xbb
    && buffer[2] === 0xbf
  ) {
    return buffer.subarray(3).toString('utf8');
  }
  return buffer.toString('utf8');
}

function assertNoAutomaticOutputReveal(
  coordinatorShows: readonly boolean[],
  commandShows: readonly boolean[],
  explicitShows: readonly string[]
): void {
  assert.deepEqual(coordinatorShows, []);
  assert.deepEqual(commandShows, []);
  assert.deepEqual(explicitShows, []);
}

function assertNoListInvocation(
  invocations: readonly { args: readonly string[] }[]
): void {
  assert.equal(invocations.some((invocation) =>
    invocation.args[0] === 'common-module'
    && invocation.args[1] === 'list'), false);
}

async function runTest(name: string, body: () => Promise<void>): Promise<void> {
  const startedAt = Date.now();
  try {
    await body();
    console.log(`PASS ${name} (${Date.now() - startedAt} ms)`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    throw error;
  }
}
