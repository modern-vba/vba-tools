import test from 'node:test';
import assert from 'node:assert/strict';
import { runVbaDevCommand } from './devtoolCommand';
import { runResolvedVbaDevProjectCommandInvocation } from './devtoolRuntime';
import { loadRequiredVbaDevContract } from './devtool';
import * as path from 'node:path';

test('packaged contract rejects providers without source Build, Export, or confirmation', () => {
  const contract = loadRequiredVbaDevContract(path.resolve(__dirname, '..', '..'));
  for (const feature of ['build.sourceWorkbook', 'export.sourceWorkbook', 'invocation.stdinWorkbookConfirmation']) {
    assert.equal(contract.featureVersions?.[feature], '1.0');
  }
});

test('Build confirms workbook replacement once and returns consent to the same invocation', async () => {
  const requestId = '0123456789abcdef0123456789abcdef';
  const message = 'Replace VBA and save other unsaved edits in C:\\Project\\Book.xlsm?';
  const replies: unknown[] = [];
  const prompts: string[] = [];
  const options = {
    executablePath: 'vba-dev.exe',
    args: ['build'],
    outputChannel: { append: () => {}, appendLine: () => {}, show: () => {} },
    confirmWorkbookChanges: async (warning: string) => {
      prompts.push(warning);
      return true;
    },
    startProcess: () => ({
      onStdout: () => {},
      onStderr: (listener: (value: string) => void) => {
        const frame = JSON.stringify({
          type: 'workbookConfirmation', schemaVersion: '1.0', requestId, message
        }) + '\n';
        listener(frame.slice(0, 19));
        listener(frame.slice(19));
        listener(frame);
      },
      respondToWorkbookConfirmation: async (id: string, approved: boolean) => {
        replies.push({ id, approved });
      },
      onExit: (listener: (code: number, signal: null) => void) => {
        setImmediate(() => listener(0, null));
      },
      kill: () => {}
    })
  };

  const result = await runVbaDevCommand(options);

  assert.equal(result.exitCode, 0);
  assert.deepEqual(prompts, [message]);
  assert.deepEqual(replies, [{ id: requestId, approved: true }]);
});

test('Publish never gains a workbook replacement prompt', async () => {
  let prompts = 0;
  const options = {
    executablePath: 'vba-dev.exe',
    args: ['publish'],
    outputChannel: { append: () => {}, appendLine: () => {}, show: () => {} },
    confirmWorkbookChanges: async () => { prompts++; return true; },
    startProcess: () => ({
      onStdout: () => {},
      onStderr: (listener: (value: string) => void) => listener(JSON.stringify({
        type: 'workbookConfirmation', schemaVersion: '1.0',
        requestId: '0123456789abcdef0123456789abcdef', message: 'Unexpected prompt'
      }) + '\n'),
      respondToWorkbookConfirmation: async () => {},
      onExit: (listener: (code: number, signal: null) => void) => {
        setImmediate(() => listener(0, null));
      },
      kill: () => {}
    })
  };

  await runVbaDevCommand(options);

  assert.equal(prompts, 0);
});

test('source Build ignores malformed or unsupported confirmation requests', async () => {
  let prompts = 0;
  const requestId = '0123456789abcdef0123456789abcdef';
  const frames = [
    'not JSON',
    JSON.stringify({ type: 'workbookConfirmation', schemaVersion: '2.0', requestId, message: 'Future' }),
    JSON.stringify({ type: 'workbookConfirmation', schemaVersion: '1.0', requestId: 'wrong', message: 'Bad id' }),
    JSON.stringify({ type: 'workbookConfirmation', schemaVersion: '1.0', requestId, message: '' })
  ].join('\n') + '\n';
  await runVbaDevCommand({
    executablePath: 'vba-dev.exe', args: ['build'],
    outputChannel: { append: () => {}, appendLine: () => {}, show: () => {} },
    confirmWorkbookChanges: async () => { prompts++; return true; },
    startProcess: () => ({
      onStdout: () => {}, onStderr: listener => listener(frames),
      onExit: listener => { setImmediate(() => listener(1, null)); },
      respondToWorkbookConfirmation: async () => { throw new Error('Malformed request was accepted'); },
      kill: () => {}
    })
  });
  assert.equal(prompts, 0);
});

test('approval after source Build cancellation cannot reach the child', async () => {
  let cancel: (() => void) | undefined;
  let approve: ((value: boolean) => void) | undefined;
  let replies = 0;
  let deliveries = 0;
  const pendingApproval = new Promise<boolean>(resolve => { approve = resolve; });
  const result = runVbaDevCommand({
    executablePath: 'vba-dev.exe', args: ['build'], cancellationTransport: 'stdin-v1',
    outputChannel: { append: () => {}, appendLine: () => {}, show: () => {} },
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: listener => { cancel = listener; return { dispose: () => {} }; }
    },
    confirmWorkbookChanges: () => pendingApproval,
    startProcess: () => ({
      onStdout: () => {}, onStderr: listener => listener(JSON.stringify({
        type: 'workbookConfirmation', schemaVersion: '1.0',
        requestId: '0123456789abcdef0123456789abcdef', message: 'Replace code?'
      }) + '\n'),
      onExit: listener => { setImmediate(() => { cancel?.(); approve?.(true); listener(130, null); }); },
      respondToWorkbookConfirmation: async () => { replies++; },
      requestCancellation: async () => { deliveries++; },
      kill: () => { throw new Error('Must not kill source Build during recovery'); }
    })
  });
  const completed = await result;
  assert.equal(completed.cancelled, true);
  assert.equal(replies, 0);
  assert.equal(deliveries, 1);
});

test('managed source Build explicitly enables its UI confirmation without replaying Build', async () => {
  const calls: readonly string[][] = [];
  const replies: boolean[] = [];
  let prompts = 0;
  const options = {
    extensionRoot: 'C:\\Extension',
    outputChannel: { append: () => {}, appendLine: () => {}, show: () => {} },
    confirmWorkbookChanges: async () => { prompts++; return false; },
    startProcess: (_file: string, args: readonly string[]) => {
      (calls as string[][]).push([...args]);
      return {
        onStdout: () => {},
        onStderr: (listener: (value: string) => void) => listener(JSON.stringify({
          type: 'workbookConfirmation', schemaVersion: '1.0',
          requestId: '0123456789abcdef0123456789abcdef', message: 'Replace live code?'
        }) + '\n'),
        respondToWorkbookConfirmation: async (_id: string, approved: boolean) => { replies.push(approved); },
        onExit: (listener: (code: number, signal: null) => void) => {
          setImmediate(() => listener(1, null));
        },
        kill: () => { throw new Error('Source Build must unwind safely rather than be force killed.'); }
      };
    }
  };

  await runResolvedVbaDevProjectCommandInvocation(options, 'vba-dev.exe', {
    projectRoot: 'C:\\Project', argsBeforeProject: ['build']
  }, {
    toolVersion: '0.1.0', contractVersion: '1.0', commands: { build: { outputSchemaVersion: '3.0' } },
    featureVersions: { 'invocation.stdinCancellation': '1.0', 'invocation.stdinWorkbookConfirmation': '1.0',
      'build.sourceWorkbook': '1.0' }
  });

  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0], ['build', '--project', 'C:\\Project', '--interactive', 'true',
    '--cancellation-transport', 'stdin-v1']);
  assert.equal(prompts, 1);
  assert.deepEqual(replies, [false]);
});

test('managed source Build without a UI callback explicitly refuses interactive changes', async () => {
  let startedArgs: readonly string[] = [];
  await runResolvedVbaDevProjectCommandInvocation({
    extensionRoot: 'C:\\Extension',
    outputChannel: { append: () => {}, appendLine: () => {}, show: () => {} },
    startProcess: (_file, args) => {
      startedArgs = args;
      return { onStdout: () => {}, onStderr: () => {},
        onExit: listener => { setImmediate(() => listener(1, null)); }, kill: () => {} };
    }
  }, 'vba-dev.exe', { projectRoot: 'C:\\Project', argsBeforeProject: ['build'] }, {
    toolVersion: 'test', contractVersion: '1.0', commands: { build: { outputSchemaVersion: '3.0' } },
    featureVersions: { 'invocation.stdinCancellation': '1.0', 'invocation.stdinWorkbookConfirmation': '1.0',
      'build.sourceWorkbook': '1.0' }
  });
  assert.deepEqual(startedArgs, ['build', '--project', 'C:\\Project', '--interactive', 'false',
    '--cancellation-transport', 'stdin-v1']);
});
