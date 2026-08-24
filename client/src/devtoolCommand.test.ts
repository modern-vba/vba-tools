import test from 'node:test';
import assert from 'node:assert/strict';
import { Writable } from 'node:stream';

import {
  requestStdinCancellation,
  runVbaDevCommand
} from './devtoolCommand';

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

test('a companion command reports a spawn error instead of remaining pending', async () => {
  const lines: string[] = [];
  const running = runVbaDevCommand({
    executablePath: 'missing-vba-dev.exe',
    args: ['doctor'],
    outputChannel: {
      append: (value) => lines.push(value),
      appendLine: (value) => lines.push(`${value}\n`),
      show: () => undefined
    },
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: () => undefined,
      onSpawn: () => undefined,
      onExit: () => undefined,
      onClose: () => undefined,
      onError: (listener) => {
        setTimeout(() => listener(new Error('spawn ENOENT')), 0);
      },
      kill: () => undefined
    })
  });
  const outcome = await Promise.race([
    running.then((result) => ({ kind: 'result' as const, result })),
    new Promise<{ kind: 'timeout' }>((resolve) => {
      setTimeout(() => resolve({ kind: 'timeout' }), 25);
    })
  ]);

  assert.equal(outcome.kind, 'result');
  if (outcome.kind !== 'result') {
    return;
  }
  assert.equal(outcome.result.exitCode, 1);
  assert.equal(outcome.result.cancelled, false);
  assert.match(outcome.result.stderr, /spawn ENOENT/);
  assert.match(lines.join(''), /spawn ENOENT/);
});

test('a pid-known process error before the spawn event still waits for close', async () => {
  const output: string[] = [];
  let errorListener: ((error: Error) => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let cancelListener: (() => void) | undefined;
  let settled = false;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['doctor'],
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
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
      started: true,
      onStdout: () => undefined,
      onStderr: () => undefined,
      onSpawn: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      onError: (listener) => {
        errorListener = listener;
      },
      kill: () => {
        errorListener?.(new Error('kill EPERM'));
      }
    })
  });
  void running.then(() => {
    settled = true;
  });

  cancelListener?.();
  await new Promise<void>((resolve) => setTimeout(resolve, 0));

  assert.equal(settled, false);
  assert.match(output.join(''), /process error: kill EPERM/);
  assert.doesNotMatch(output.join(''), /failed to start/);
  closeListener?.(1, null);
  const result = await running;
  assert.equal(result.cancelled, true);
  assert.match(result.stderr, /kill EPERM/);
});

test('a companion command waits for close before returning trailing output', async () => {
  let stdoutListener: ((value: string) => void) | undefined;
  const result = await runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['doctor'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    startProcess: () => ({
      onStdout: (listener) => {
        stdoutListener = listener;
      },
      onStderr: () => undefined,
      onExit: (listener) => {
        setTimeout(() => listener(0, null), 0);
      },
      onClose: (listener) => {
        setTimeout(() => {
          stdoutListener?.('{"complete":true}\n');
          listener(0, null);
        }, 1);
      },
      kill: () => undefined
    })
  });

  assert.equal(result.exitCode, 0);
  assert.equal(result.stdout, '{"complete":true}\n');
});

test('a companion command drains trailing stderr between exit and close', async () => {
  let stderrListener: ((value: string) => void) | undefined;
  const result = await runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: (listener) => {
        stderrListener = listener;
      },
      onExit: (listener) => {
        setTimeout(() => listener(1, null), 0);
      },
      onClose: (listener) => {
        setTimeout(() => {
          stderrListener?.('cleanup failed\n');
          listener(1, null);
        }, 1);
      },
      kill: () => undefined
    })
  });

  assert.equal(result.exitCode, 1);
  assert.equal(result.stderr, 'cleanup failed\n');
});

test('an already-cancelled companion command does not start its executable', async () => {
  let starts = 0;
  const result = await runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['doctor'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationToken: {
      isCancellationRequested: true,
      onCancellationRequested: () => ({ dispose: () => undefined })
    },
    startProcess: () => {
      starts += 1;
      throw new Error('an already-cancelled command must not start');
    }
  });

  assert.equal(starts, 0);
  assert.equal(result.cancelled, true);
  assert.match(result.message, /cancelled/i);
});

test('a cancellation delivered during subscription cannot outrun close registration', async () => {
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  const running = runVbaDevCommand({
    executablePath: 'vba-debug-adapter.exe',
    args: ['doctor', '--format', 'json'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        listener();
        return { dispose: () => undefined };
      }
    },
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => {
        closeListener?.(1, null);
      },
      kill: () => undefined
    })
  });

  const outcome = await Promise.race([
    running.then((result) => ({ kind: 'result' as const, result })),
    new Promise<{ kind: 'timeout' }>((resolve) => {
      setTimeout(() => resolve({ kind: 'timeout' }), 25);
    })
  ]);

  assert.equal(outcome.kind, 'result');
  if (outcome.kind === 'result') {
    assert.equal(outcome.result.cancelled, false);
    assert.equal(outcome.result.cancellationRequested, true);
  }
});

test('stdin-v1 cancellation requests cooperation without killing the process', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let cancellationRequests = 0;
  let kills = 0;
  const running = runVbaDevCommand({
    executablePath: 'vba-debug-adapter.exe',
    args: ['doctor', '--format', 'json'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => {
        cancellationRequests += 1;
      },
      kill: () => {
        kills += 1;
      }
    })
  });

  cancelListener?.();

  assert.equal(cancellationRequests, 1);
  assert.equal(kills, 0);
  closeListener?.(1, null);
  const result = await running;
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequested, true);
  assert.equal(result.cancellationRequestDelivered, true);
});

test('repeated local cancellation sends the stdin-v1 request only once', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let cancellationRequests = 0;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => {
        cancellationRequests += 1;
      },
      kill: () => undefined
    })
  });

  cancelListener?.();
  cancelListener?.();

  assert.equal(cancellationRequests, 1);
  closeListener?.(130, null);
  await running;
});

test('stdin-v1 cancellation reports that the extension is waiting for vba-dev', async () => {
  const progress: string[] = [];
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    reportCancellationProgress: (message) => progress.push(message),
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => undefined,
      kill: () => undefined
    })
  });

  cancelListener?.();

  assert.deepEqual(progress, [
    'Cancellation requested; waiting for vba-dev to finish.'
  ]);
  closeListener?.(130, null);
  await running;
});

test('a successful terminal outcome remains authoritative after a stdin cancellation request', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => undefined,
      kill: () => undefined
    })
  });

  cancelListener?.();
  closeListener?.(0, null);
  const result = await running;

  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
});

test('exit 130 is authoritative cancellation without a local request', async () => {
  const result = await runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => listener(130, null),
      requestCancellation: async () => undefined,
      kill: () => undefined
    })
  });

  assert.equal(result.exitCode, 130);
  assert.equal(result.cancelled, true);
  assert.equal(result.cancellationRequested, false);
  assert.equal(result.cancellationRequestDelivered, undefined);
});

test('abnormal close remains an authoritative failure after a stdin cancellation request', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => undefined,
      kill: () => undefined
    })
  });

  cancelListener?.();
  closeListener?.(null, 'SIGABRT');
  const result = await running;

  assert.equal(result.exitCode, 1);
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequested, true);
});

test('the Node process bridge writes the exact stdin-v1 cancellation frame', async () => {
  let cancelListener: (() => void) | undefined;
  let signalReady: (() => void) | undefined;
  const ready = new Promise<void>((resolve) => {
    signalReady = resolve;
  });
  const script = [
    "process.stdout.write('ready\\n');",
    'const chunks = [];',
    "process.stdin.on('data', chunk => chunks.push(chunk));",
    "process.stdin.on('end', () => process.stdout.write(Buffer.concat(chunks).toString('hex')));"
  ].join('');
  const running = runVbaDevCommand({
    executablePath: process.execPath,
    args: ['-e', script],
    outputChannel: {
      append: (value) => {
        if (value.includes('ready')) {
          signalReady?.();
        }
      },
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    }
  });

  await ready;
  cancelListener?.();
  const result = await running;

  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequested, true);
  assert.match(result.stdout, /ready\r?\n63616e63656c0a$/);
});

test('stdin-v1 rejects an end callback error and consumes the later error event', async () => {
  let writes = 0;
  const stdin = new Writable({
    write: (_chunk, _encoding, callback) => {
      writes += 1;
      callback(new Error('write EPIPE'));
    }
  });

  await assert.rejects(
    requestStdinCancellation(stdin),
    /write EPIPE/
  );
  await new Promise<void>((resolve) => setImmediate(resolve));

  assert.equal(writes, 1);
});

test('a failed stdin-v1 write is reported while the process remains authoritative', async () => {
  const output: string[] = [];
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let kills = 0;
  let settled = false;
  const running = runVbaDevCommand({
    executablePath: 'vba-debug-adapter.exe',
    args: ['doctor', '--format', 'json'],
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => {
        throw new Error('write EPIPE');
      },
      kill: () => {
        kills += 1;
      }
    })
  });
  void running.then(() => {
    settled = true;
  });

  cancelListener?.();
  await new Promise<void>((resolve) => setTimeout(resolve, 0));

  assert.match(output.join(''), /cancellation request could not be delivered/);
  assert.equal(kills, 0);
  assert.equal(settled, false);
  closeListener?.(1, null);
  const result = await running;
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequested, true);
  assert.equal(result.cancellationRequestDelivered, false);
});

test('EPIPE detail updates progress while terminal success remains authoritative', async () => {
  const output: string[] = [];
  const progress: string[] = [];
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    reportCancellationProgress: (message) => progress.push(message),
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => {
        throw new Error('write EPIPE');
      },
      kill: () => undefined
    })
  });

  cancelListener?.();
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.match(output.join(''), /write EPIPE/);
  assert.deepEqual(progress, [
    'Cancellation requested; waiting for vba-dev to finish.',
    'Cancellation request could not be delivered; waiting for vba-dev to finish.'
  ]);

  closeListener?.(0, null);
  const result = await running;
  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequestDelivered, false);
  assert.equal(result.cancellationRequestError, 'write EPIPE');
});

test('child close remains authoritative when stdin cancellation delivery never settles', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: () => new Promise<void>(() => undefined),
      kill: () => undefined
    })
  });

  cancelListener?.();
  closeListener?.(0, null);
  const outcome = await Promise.race([
    running.then((result) => ({ kind: 'result' as const, result })),
    new Promise<{ kind: 'timeout' }>((resolve) => {
      setTimeout(() => resolve({ kind: 'timeout' }), 25);
    })
  ]);

  assert.equal(outcome.kind, 'result');
  if (outcome.kind === 'result') {
    assert.equal(outcome.result.exitCode, 0);
    assert.equal(outcome.result.cancelled, false);
    assert.equal(outcome.result.cancellationRequestDelivered, false);
  }
});

test('an ordinary stdin-v1 command escalates only after its cooperative grace period', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let kills = 0;
  let settled = false;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    forceKillAfterCancellationMilliseconds: 0,
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => undefined,
      kill: () => {
        kills += 1;
      }
    })
  });
  void running.then(() => {
    settled = true;
  });

  cancelListener?.();
  assert.equal(kills, 0);
  await new Promise<void>((resolve) => setTimeout(resolve, 10));
  assert.equal(kills, 1);
  assert.equal(settled, false);

  closeListener?.(null, 'SIGTERM');
  const result = await running;
  assert.equal(result.exitCode, 1);
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequested, true);
});

test('authoritative close cancels the cooperative grace escalation timer', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let kills = 0;
  const running = runVbaDevCommand({
    executablePath: 'vba-dev.exe',
    args: ['build', '--cancellation-transport', 'stdin-v1'],
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationTransport: 'stdin-v1',
    forceKillAfterCancellationMilliseconds: 10,
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
      onExit: () => undefined,
      onClose: (listener) => {
        closeListener = listener;
      },
      requestCancellation: async () => undefined,
      kill: () => {
        kills += 1;
      }
    })
  });

  cancelListener?.();
  closeListener?.(0, null);
  const result = await running;
  await new Promise<void>((resolve) => setTimeout(resolve, 20));

  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
  assert.equal(kills, 0);
});
