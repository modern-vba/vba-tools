import test from 'node:test';
import assert from 'node:assert/strict';

import { runVbaDevCommand } from './devtoolCommand';

test('VbaDev command output streams to the provided output channel', async () => {
  const lines: string[] = [];
  const result = await runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['doctor', '--project', 'C:\\Project'],
    outputChannel: {
      append: (value) => lines.push(value),
      appendLine: (value) => lines.push(`${value}\n`),
      show: () => undefined
    },
    startProcess: () => ({
      onStdout: (listener) => listener('doctor output\n'),
      onStderr: (listener) => listener(''),
      onExit: (listener) => listener(0, null),
      kill: () => undefined
    })
  });

  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
  assert.match(lines.join(''), /doctor output/);
});

test('VbaDev command cancellation kills the spawned process and reports cancellation', async () => {
  let killed = false;
  let cancelListener: (() => void) | undefined;

  const resultPromise = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['doctor'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: () => undefined,
      onExit: (listener) => {
        setTimeout(() => listener(null, 'SIGTERM'), 0);
      },
      kill: () => {
        killed = true;
      }
    })
  });

  cancelListener?.();
  const result = await resultPromise;

  assert.equal(killed, true);
  assert.equal(result.cancelled, true);
  assert.match(result.message, /cancelled/i);
});
