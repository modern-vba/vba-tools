import { spawn } from 'node:child_process';
import * as path from 'node:path';
import { requestStdinCancellation, type StartVbaDevProcess } from '../devtoolCommand';

const sections = ['events', 'notifications', 'process', 'stdout', 'stderr', 'testOutput', 'outputChannel'] as const;
type DiagnosticSection = typeof sections[number];
const tailLimit = 2048;

/** Seven 2,048-character tails keep failure-only fixture evidence below 16,384 characters. */
export class IntegrationFailureDiagnostics {
  private phase = 'setup';
  private readonly tails = new Map<DiagnosticSection, { text: string; characters: number }>();

  // Match the production Node adapter, observing copies without delaying consumers.
  readonly startProcess: StartVbaDevProcess = (executablePath, args) => {
    const child = spawn(executablePath, [...args], { windowsHide: true });
    this.record('process', `start: ${path.basename(executablePath)} pid=${child.pid ?? 'unavailable'}; close not yet observed\n`);
    return {
      started: child.pid !== undefined,
      onStdout: listener => {
        child.stdout?.on('data', (chunk: Buffer) => {
          const value = chunk.toString('utf8');
          this.record('stdout', value);
          listener(value);
        });
      },
      onStderr: listener => {
        child.stderr?.on('data', (chunk: Buffer) => {
          const value = chunk.toString('utf8');
          this.record('stderr', value);
          listener(value);
        });
      },
      onSpawn: listener => { child.once('spawn', listener); },
      onExit: listener => { child.once('exit', listener); },
      onClose: listener => {
        child.once('close', (code, signal) => {
          this.record('process', `close: code=${code} signal=${signal}\n`);
          listener(code, signal);
        });
      },
      onError: listener => {
        child.once('error', error => {
          this.record('process', `error: ${error.message}\n`);
          listener(error);
        });
      },
      ...(args.some((arg, index) => arg === '--cancellation-transport' && args[index + 1] === 'stdin-v1')
        ? { requestCancellation: () => child.stdin === null
          ? Promise.reject(new Error('The companion process standard input is unavailable.'))
          : requestStdinCancellation(child.stdin) }
        : {}),
      kill: () => { child.kill(); }
    };
  };

  beginPhase(phase: string): void {
    this.phase = phase.slice(0, 128);
    this.tails.clear();
  }

  record(section: DiagnosticSection, value: string): void {
    const previous = this.tails.get(section);
    this.tails.set(section, {
      text: ((previous?.text ?? '') + value.slice(-tailLimit)).slice(-tailLimit),
      characters: (previous?.characters ?? 0) + value.length
    });
  }

  format(): string {
    return [
      '[Native Test build failure diagnostics]',
      `Phase: ${this.phase}`,
      'Only fixture command processes are observed; capability probes are not intercepted.',
      ...sections.map(section => {
        const tail = this.tails.get(section);
        const truncated = (tail?.characters ?? 0) > tailLimit ? '; truncated to last 2048 characters' : '';
        return `--- ${section} (${tail?.characters ?? 0} characters${truncated}) ---\n` +
          (tail?.text || '(none recorded)');
      }),
      '[/Native Test build failure diagnostics]'
    ].join('\n');
  }
}
