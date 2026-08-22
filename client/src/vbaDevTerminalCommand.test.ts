import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { VbaDevSessionResolver } from './devtool';
import {
  createVbaDevTerminalEnvironment,
  openVbaDevTerminal,
  selectTerminalCwd,
  VbaDevTerminalOptionsLike
} from './vbaDevTerminalCommand';

test('Open vba-dev Terminal prepends the resolved CLI directory to PATH and uses the active workspace root', async () => {
  const extensionRoot = path.join('C:', 'extensions', 'vba-tools');
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  const workspaceRoot = path.join('C:', 'work', 'BookProject');
  const parentWorkspaceRoot = path.dirname(workspaceRoot);
  const processEnv = { Path: path.join('C:', 'Windows') };
  const processCalls: Array<{ file: string; args: readonly string[] }> = [];
  const terminalOptions: VbaDevTerminalOptionsLike[] = [];
  const terminalShows: Array<boolean | undefined> = [];

  const result = await openVbaDevTerminal({
    extensionRoot,
    configuredDevToolPath: executablePath,
    activeFilePath: path.join(workspaceRoot, 'src', 'Module1.bas'),
    workspaceRoots: [parentWorkspaceRoot, workspaceRoot],
    processEnv,
    capabilitiesProcess: async (file, args) => {
      processCalls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {},
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    },
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {}
    },
    chooseWorkspaceRoot: async () => {
      throw new Error('workspace root should not be prompted for an active workspace file');
    },
    createTerminal: (options) => {
      terminalOptions.push(options);
      return {
        show: (preserveFocus) => terminalShows.push(preserveFocus)
      };
    },
    showErrorMessage: async () => undefined
  });

  assert.ok(result);
  assert.equal(result.executablePath, executablePath);
  assert.equal(result.executableDirectory, path.dirname(executablePath));
  assert.equal(result.cwd, workspaceRoot);
  assert.deepEqual(processCalls, [
    {
      file: executablePath,
      args: ['capabilities', '--format', 'json']
    }
  ]);
  assert.deepEqual(terminalShows, [undefined]);
  assert.deepEqual(terminalOptions, [
    {
      name: 'vba-dev',
      cwd: workspaceRoot,
      env: {
        Path: `${path.dirname(executablePath)}${path.delimiter}${processEnv.Path}`
      }
    }
  ]);
});

test('Open vba-dev Terminal uses the selected workspace root when multiple roots are otherwise ambiguous', async () => {
  const firstWorkspaceRoot = path.join('C:', 'work', 'First');
  const secondWorkspaceRoot = path.join('C:', 'work', 'Second');
  const selectedRoots: Array<readonly string[]> = [];

  const cwdSelection = await selectTerminalCwd({
    workspaceRoots: [firstWorkspaceRoot, secondWorkspaceRoot],
    chooseWorkspaceRoot: async (workspaceRoots) => {
      selectedRoots.push(workspaceRoots);
      return secondWorkspaceRoot;
    }
  });

  assert.deepEqual(cwdSelection, {
    cwd: secondWorkspaceRoot,
    cancelled: false
  });
  assert.deepEqual(selectedRoots, [[firstWorkspaceRoot, secondWorkspaceRoot]]);
});

test('Open vba-dev Terminal does not resolve the CLI or create a terminal when workspace selection is cancelled', async () => {
  const terminalOptions: VbaDevTerminalOptionsLike[] = [];

  const result = await openVbaDevTerminal({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    workspaceRoots: [path.join('C:', 'work', 'First'), path.join('C:', 'work', 'Second')],
    chooseWorkspaceRoot: async () => undefined,
    capabilitiesProcess: async () => {
      throw new Error('capabilities should not be checked after cancellation');
    },
    createTerminal: (options) => {
      terminalOptions.push(options);
      return {
        show: () => undefined
      };
    },
    showErrorMessage: async () => undefined
  });

  assert.equal(result, undefined);
  assert.deepEqual(terminalOptions, []);
});

test('Open vba-dev Terminal reports incompatible CLI resolution without creating a terminal', async () => {
  const errors: string[] = [];
  const terminalOptions: VbaDevTerminalOptionsLike[] = [];
  const executablePath = path.join('D:', 'tools', 'old-vba-dev.exe');

  const result = await openVbaDevTerminal({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: executablePath,
    workspaceRoots: [],
    capabilitiesProcess: async () => ({
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: '0.9',
        commands: {}
      }),
      stderr: ''
    }),
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {}
    },
    chooseWorkspaceRoot: async () => undefined,
    createTerminal: (options) => {
      terminalOptions.push(options);
      return {
        show: () => undefined
      };
    },
    showErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    }
  });

  assert.equal(result, undefined);
  assert.deepEqual(terminalOptions, []);
  assert.match(errors[0], /Could not open vba-dev terminal/);
  assert.match(errors[0], /contractVersion 0\.9/);
});

test('vba-dev Terminal environment preserves PATH casing when Path is unavailable', () => {
  assert.deepEqual(
    createVbaDevTerminalEnvironment(path.join('D:', 'tools'), {
      PATH: path.join('C:', 'Windows')
    }),
    {
      PATH: `${path.join('D:', 'tools')}${path.delimiter}${path.join('C:', 'Windows')}`
    }
  );
});

test('Open vba-dev Terminal reuses the session-pinned executable', async () => {
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  let capabilityCalls = 0;
  const vbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    configuredPath: executablePath,
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {}
    },
    runProcess: async () => {
      capabilityCalls += 1;
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {},
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    }
  });
  const terminalPaths: string[] = [];
  const options = {
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver,
    workspaceRoots: [] as readonly string[],
    processEnv: { Path: path.join('C:', 'Windows') },
    chooseWorkspaceRoot: async () => undefined,
    createTerminal: (terminalOptions: VbaDevTerminalOptionsLike) => {
      terminalPaths.push(terminalOptions.env.Path ?? '');
      return { show: () => undefined };
    },
    showErrorMessage: async () => undefined
  };

  const first = await openVbaDevTerminal(options);
  const second = await openVbaDevTerminal(options);

  assert.equal(first?.executablePath, executablePath);
  assert.equal(second?.executablePath, executablePath);
  assert.equal(capabilityCalls, 1);
  assert.equal(terminalPaths.length, 2);
  assert.ok(terminalPaths.every((value) => value.startsWith(path.dirname(executablePath))));
});

test('Open vba-dev Terminal does not duplicate a reported total resolution failure', async () => {
  const notices: unknown[] = [];
  const errors: string[] = [];
  let terminalCreates = 0;
  const vbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    configuredPath: path.join('D:', 'missing', 'vba-dev.exe'),
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {}
    },
    runProcess: async () => {
      throw new Error('unavailable');
    },
    reportNotice: (notice) => notices.push(notice)
  });

  const result = await openVbaDevTerminal({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver,
    workspaceRoots: [],
    chooseWorkspaceRoot: async () => undefined,
    createTerminal: () => {
      terminalCreates += 1;
      return { show: () => undefined };
    },
    showErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    }
  });

  assert.equal(result, undefined);
  assert.equal(notices.length, 1);
  assert.deepEqual(errors, []);
  assert.equal(terminalCreates, 0);
});
