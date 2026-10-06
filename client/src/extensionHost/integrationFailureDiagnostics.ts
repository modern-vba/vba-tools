import { spawn, type ChildProcessWithoutNullStreams, type SpawnOptionsWithoutStdio } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { appendFileSync } from 'node:fs';
import * as path from 'node:path';
import { requestStdinCancellation, type StartVbaDevProcess } from '../devtoolCommand';
import { captureSnapshotInputEvidence } from './snapshotInputEvidence';

const sections = ['events', 'notifications', 'process', 'stdout', 'stderr', 'testOutput', 'outputChannel'] as const;
type DiagnosticSection = typeof sections[number];
const tailLimit = 2048;
type NativeTestBuildDumpTarget = 'invalid' | 'corrected';

export interface NativeTestBuildDumpOptions {
  readonly dumpRoot: string;
  readonly target: NativeTestBuildDumpTarget;
}

type SpawnProcess = (executablePath: string, args: string[], options: SpawnOptionsWithoutStdio) => ChildProcessWithoutNullStreams;

/** Seven 2,048-character tails keep failure-only fixture evidence below 16,384 characters. */
export class IntegrationFailureDiagnostics {
  private phase = 'setup';
  private readonly tails = new Map<DiagnosticSection, { text: string; characters: number }>();
  private crashDumpArmed = false;

  constructor(
    private readonly dumpOptions?: NativeTestBuildDumpOptions,
    private readonly spawnProcess: SpawnProcess = spawn
  ) {
    if (dumpOptions !== undefined && (!path.isAbsolute(dumpOptions.dumpRoot)
        || (process.platform === 'win32' && path.win32.normalize(dumpOptions.dumpRoot).startsWith('\\\\')))) {
      throw new Error('The native Test build dump root must be an absolute local directory.');
    }
  }

  // Match the production Node adapter, observing copies without delaying consumers.
  readonly startProcess: StartVbaDevProcess = (executablePath, args) => {
    const targetPhase = this.dumpOptions?.target === 'invalid'
      ? 'Explorer invalid unsaved source' : 'Explorer corrected unsaved source';
    const captureDump = this.dumpOptions !== undefined && !this.crashDumpArmed
      && this.phase === targetPhase && path.win32.basename(executablePath).toLowerCase() === 'vba-dev.exe'
      && args[0] === 'test' && args.includes('--source-snapshot');
    if (captureDump) this.crashDumpArmed = true;
    const invocationId = captureDump ? randomUUID() : undefined;
    const dumpName = invocationId === undefined ? undefined
      : path.join(this.dumpOptions!.dumpRoot, `vba-dev-${invocationId}-%p.dmp`);
    const metadataPath = invocationId === undefined ? undefined
      : path.join(this.dumpOptions!.dumpRoot, `vba-dev-${invocationId}.jsonl`);
    const snapshotArg = args.indexOf('--source-snapshot');
    const sourceSnapshot = snapshotArg >= 0 ? args[snapshotArg + 1] : undefined;
    const snapshotEvidence = captureDump ? captureSnapshotInputEvidence(sourceSnapshot) : undefined;
    const child = this.spawnProcess(executablePath, [...args], dumpName === undefined
      ? { windowsHide: true }
      : { windowsHide: true, env: { ...process.env,
        DOTNET_DbgEnableMiniDump: '1', DOTNET_DbgMiniDumpType: '4', DOTNET_DbgMiniDumpName: dumpName } });
    if (metadataPath !== undefined) {
      this.appendMetadata(metadataPath, { event: 'start', phase: this.phase, executablePath,
        args, sourceSnapshot, snapshotEvidence, pid: child.pid ?? null, dumpName });
      child.once('close', (exitCode, signal) => {
        this.appendMetadata(metadataPath, { event: 'close', exitCode, signal });
      });
    }
    this.record('process', `start: ${JSON.stringify({ executablePath, args, pid: child.pid ?? null,
      dumpName: dumpName ?? null })}; close not yet observed\n`);
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

  private appendMetadata(file: string, record: object): void {
    try {
      appendFileSync(file, JSON.stringify(record) + '\n', 'utf8');
    } catch (error) {
      this.record('process', `local dump metadata unavailable: ${String(error)}\n`);
    }
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
