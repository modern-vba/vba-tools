import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  FirstRunDoctorPromptState,
  runDoctorCommand,
  promptForFirstRunDoctor
} from './doctorCommand';
import { VbaDevCompatibilityError } from './devtool';

test('Doctor command validates the CLI and invokes doctor with an explicit project root', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const notifications: string[] = [];
  const diagnosticRefreshes: Array<{ scopeKey: string; output: string }> = [];

  const result = await runDoctorCommand({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: path.join('D:', 'tools', 'vba-dev.exe'),
    activeFilePath: path.join(projectRoot, 'src', 'Book1', 'Module1.bas'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    capabilitiesProcess: async (file, args) => {
      calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            doctor: { outputSchemaVersion: '1.0' }
          },
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    },
    startProcess: (file, args) => {
      calls.push({ file, args });
      return {
        onStdout: (listener) => listener('[FAIL] Project manifest: missing\n'),
        onStderr: () => undefined,
        onExit: (listener) => listener(1, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    diagnosticReporter: {
      refresh: (scopeKey, value) => {
        diagnosticRefreshes.push({ scopeKey, output: value });
        return [];
      }
    },
    showErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    },
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {
        doctor: '1.0'
      }
    }
  });

  assert.ok(result);
  assert.equal(result.projectRoot, projectRoot);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['doctor', '--project', projectRoot]
  ]);
  assert.match(output.join(''), /\[FAIL\] Project manifest/);
  assert.match(notifications[0], /Doctor found blocking issues/);
  assert.deepEqual(diagnosticRefreshes, [
    {
      scopeKey: `project:${projectRoot}`,
      output: '[FAIL] Project manifest: missing\n'
    }
  ]);
});

test('Doctor reports configured and effective vba-dev paths after bundled fallback', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const configuredPath = path.join('D:', 'old', 'vba-dev.exe');
  const effectivePath = path.join('C:', 'extension', 'bin', 'vba-dev.exe');
  const output: string[] = [];
  const capabilities = {
    toolVersion: '0.1.0',
    contractVersion: '1.0',
    commands: {
      doctor: { outputSchemaVersion: '1.0' }
    },
    debugAdapter: {
      protocolVersion: '1.0',
      transport: 'stdio',
      command: 'debug-adapter'
    }
  };

  await runDoctorCommand({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => ({
        configuredPath,
        bundledPath: effectivePath,
        executablePath: effectivePath,
        source: 'bundled',
        configuredFailure: 'contractVersion 0.9 is incompatible',
        capabilities
      })
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    startProcess: (file) => {
      assert.equal(file, effectivePath);
      return {
        onStdout: (listener) => listener('[PASS] Project manifest\n'),
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  });

  assert.match(output.join(''), /vba-dev executable fallback:/);
  assert.match(output.join(''), new RegExp(`Configured: ${escapeRegExp(configuredPath)}`));
  assert.match(output.join(''), new RegExp(`Effective: ${escapeRegExp(effectivePath)}`));
});

test('Doctor stops without another notification when companion resolution failure was already reported', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  let processStarts = 0;
  const notifications: string[] = [];

  const result = await runDoctorCommand({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => {
        throw new VbaDevCompatibilityError('no compatible vba-dev', true);
      }
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    startProcess: () => {
      processStarts += 1;
      throw new Error('Doctor must not start');
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    }
  });

  assert.equal(result, undefined);
  assert.equal(processStarts, 0);
  assert.deepEqual(notifications, []);
});

test('First-run doctor prompt can run doctor once for the workspace', async () => {
  const state = new MemoryPromptState();
  let doctorRuns = 0;

  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => 'Run Doctor',
    runDoctor: async () => {
      doctorRuns += 1;
    }
  });
  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => {
      throw new Error('prompt should be suppressed after the first prompt');
    },
    runDoctor: async () => {
      doctorRuns += 1;
    }
  });

  assert.equal(doctorRuns, 1);
});

test('First-run doctor prompt supports a workspace do-not-ask-again choice', async () => {
  const state = new MemoryPromptState();
  let prompts = 0;

  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => {
      prompts += 1;
      return "Don't Ask Again";
    },
    runDoctor: async () => {
      throw new Error('doctor should not run when the user suppresses the prompt');
    }
  });
  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => {
      prompts += 1;
      return 'Run Doctor';
    },
    runDoctor: async () => undefined
  });

  assert.equal(prompts, 1);
  assert.equal(state.get(FirstRunDoctorPromptState.Suppress), true);
});

class MemoryPromptState {
  private readonly values = new Map<string, unknown>();

  public get<T>(key: string): T | undefined {
    return this.values.get(key) as T | undefined;
  }

  public async update(key: string, value: unknown): Promise<void> {
    this.values.set(key, value);
  }
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
