import * as path from 'node:path';

import {
  CommandPalettePathIdentity,
  CommandPaletteProjectTarget,
  CommandPaletteTarget,
  retainExactCommandPaletteTarget,
  sameCommandPalettePathIdentity
} from './commandPaletteTarget';
import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export interface ProjectManifestMutationProcessResult {
  exitCode: number;
  cancelled: boolean;
}

export interface ProjectManifestEditorSnapshot {
  filePath: string;
  bufferId: string;
  revision: string | number;
  text: string;
  isDirty: boolean;
}

export interface ProjectManifestDiskSnapshot {
  bytes: Uint8Array;
  text: string;
}

export interface ProjectManifestMutationClock {
  now(): number;
  wait(milliseconds: number): Promise<void>;
}

export interface ProjectManifestMutationDisposable {
  dispose(): void;
}

export type ProjectManifestDirtyPreflightChoice = 'saveAndContinue' | 'cancel';
export type ProjectManifestPreflightMismatchChoice = 'compare' | 'reload' | 'cancel';
export type ProjectManifestRecoveryChoice =
  | 'compare'
  | 'reload'
  | 'keepEditing'
  | undefined;

export type ProjectManifestMutationReportPresentation = 'default' | 'logOnly';

export interface ProjectManifestMutationContext {
  command: string;
  projectName: string;
  documentName?: string | undefined;
  manifestPath: string;
  reportPresentation?: ProjectManifestMutationReportPresentation | undefined;
}

export interface ProjectManifestProcessSummary {
  exitCode?: number | undefined;
  cancelled: boolean;
  threw: boolean;
}

export interface ProjectManifestComparisonContext
  extends ProjectManifestMutationContext {
  phase: 'preflight' | 'postMutation';
  buffer: ProjectManifestEditorSnapshot;
  disk: ProjectManifestDiskSnapshot;
  process: ProjectManifestProcessSummary;
}

export interface ProjectManifestReloadContext
  extends ProjectManifestComparisonContext {}

export type ProjectManifestMutationReportKind =
  | 'busy'
  | 'divergenceBlocked'
  | 'ambiguousBuffers'
  | 'preflightCancelled'
  | 'preflightFailed'
  | 'preflightTargetChanged'
  | 'preflightMismatch'
  | 'comparisonShown'
  | 'reloadCancelled'
  | 'reloadRefused'
  | 'reloadCompleted'
  | 'manifestUnchanged'
  | 'manifestChanged'
  | 'abnormalManifestChange'
  | 'manifestUntrusted'
  | 'coherenceComplete'
  | 'coherenceTimeout'
  | 'editorDivergence'
  | 'concurrentDiskChange'
  | 'keepEditingWarning'
  | 'manualRepairRequired'
  | 'divergenceCleared'
  | 'readOnlyDiskBasis';

export interface ProjectManifestMutationReport
  extends ProjectManifestMutationContext {
  kind: ProjectManifestMutationReportKind;
  runningCommand?: string | undefined;
  runningProjectName?: string | undefined;
  runningDocumentName?: string | undefined;
  runningManifestPath?: string | undefined;
  process?: ProjectManifestProcessSummary | undefined;
  detail?: string | undefined;
}

export interface ProjectManifestMutationPorts {
  resolvePathIdentity(filePath: string): Promise<CommandPalettePathIdentity>;
  readManifestBytes(manifestPath: string): Promise<Uint8Array>;
  decodeManifestBytes(bytes: Uint8Array): string;
  loadProjectTarget(
    manifestPath: string,
    bytes: Uint8Array
  ): Promise<CommandPaletteProjectTarget | undefined>;
  getOpenBuffers(
    manifestIdentity: CommandPalettePathIdentity
  ): Promise<readonly ProjectManifestEditorSnapshot[]>;
  observeBuffers(
    manifestIdentity: CommandPalettePathIdentity,
    listener: (buffers: readonly ProjectManifestEditorSnapshot[]) => void
  ): ProjectManifestMutationDisposable;
  saveBuffer(buffer: ProjectManifestEditorSnapshot): Promise<boolean>;
  chooseDirtyPreflight(
    context: ProjectManifestMutationContext,
    buffer: ProjectManifestEditorSnapshot
  ): Promise<ProjectManifestDirtyPreflightChoice>;
  choosePreflightMismatch(
    context: ProjectManifestComparisonContext
  ): Promise<ProjectManifestPreflightMismatchChoice>;
  chooseRecovery(
    context: ProjectManifestComparisonContext
  ): Promise<ProjectManifestRecoveryChoice>;
  showComparison(context: ProjectManifestComparisonContext): Promise<void>;
  confirmReload(context: ProjectManifestReloadContext): Promise<boolean>;
  revealAndFocus(buffer: ProjectManifestEditorSnapshot): Promise<void>;
  getActiveFileIdentity(): Promise<CommandPalettePathIdentity | undefined>;
  revertBuffer(buffer: ProjectManifestEditorSnapshot): Promise<void>;
  clock: ProjectManifestMutationClock;
  report(report: ProjectManifestMutationReport): void;
}

export interface ProjectManifestMutationRequest<
  TProcessResult extends ProjectManifestMutationProcessResult
> {
  command: string;
  target: CommandPaletteTarget;
  reportPresentation?: ProjectManifestMutationReportPresentation | undefined;
  run(): Promise<TProcessResult>;
}

export type ProjectManifestMutationRejectedReason =
  | 'busy'
  | 'divergence'
  | 'preflight';

export type ProjectManifestMutationCoherence =
  | 'notRequired'
  | 'coherent'
  | 'diverged'
  | 'untrusted';

export interface ProjectManifestMutationRunResult<
  TProcessResult extends ProjectManifestMutationProcessResult
> {
  status: 'completed' | 'rejected';
  reason?: ProjectManifestMutationRejectedReason | undefined;
  manifestOutcome?: 'unchanged' | 'changed' | 'untrusted' | undefined;
  coherence?: ProjectManifestMutationCoherence | undefined;
  processResult?: TProcessResult | undefined;
  processError?: unknown;
}

export interface ProjectManifestReadOnlyDiskBasisRequest {
  command: string;
  target: CommandPaletteTarget;
}

export interface ProjectManifestMutationCommandCoordinator {
  run<TProcessResult extends ProjectManifestMutationProcessResult>(
    request: ProjectManifestMutationRequest<TProcessResult>
  ): Promise<ProjectManifestMutationRunResult<TProcessResult>>;
  reportReadOnlyDiskBasis(
    request: ProjectManifestReadOnlyDiskBasisRequest
  ): Promise<boolean>;
}

interface BusyMutation {
  identity: CommandPalettePathIdentity;
  identityHistory: CommandPalettePathIdentity[];
  context: ProjectManifestMutationContext;
}

interface TrustedDiskState {
  kind: 'trusted';
  snapshot: ProjectManifestDiskSnapshot;
  project: CommandPaletteProjectTarget;
}

interface UntrustedDiskState {
  kind: 'untrusted';
}

type DiskState = TrustedDiskState | UntrustedDiskState;

interface MutationPreflight {
  target: CommandPaletteTarget;
  disk: ProjectManifestDiskSnapshot;
  baselineBuffer: ProjectManifestEditorSnapshot | undefined;
}

interface DivergenceState {
  context: ProjectManifestMutationContext;
  target: CommandPaletteTarget;
  manifestIdentity: CommandPalettePathIdentity;
  identityHistory: CommandPalettePathIdentity[];
  disk?: ProjectManifestDiskSnapshot | undefined;
  preservedBuffer?: ProjectManifestEditorSnapshot | undefined;
  process: ProjectManifestProcessSummary;
  phase: 'preflight' | 'postMutation';
  competing: boolean;
  recoveryAcknowledged: boolean;
  overwriteWarned: boolean;
  untrusted: boolean;
}

interface GuardedExecution<
  TProcessResult extends ProjectManifestMutationProcessResult
> {
  result: ProjectManifestMutationRunResult<TProcessResult>;
  divergence?: DivergenceState | undefined;
}

const NativeSynchronizationDeadlineMilliseconds = 2_000;

export class ProjectManifestMutationCoordinator
implements ProjectManifestMutationCommandCoordinator {
  private readonly busy: BusyMutation[] = [];
  private readonly divergences: DivergenceState[] = [];

  public constructor(private readonly ports: ProjectManifestMutationPorts) {}

  public async run<TProcessResult extends ProjectManifestMutationProcessResult>(
    request: ProjectManifestMutationRequest<TProcessResult>
  ): Promise<ProjectManifestMutationRunResult<TProcessResult>> {
    const manifestIdentity = normalizeIdentity(
      await this.ports.resolvePathIdentity(request.target.project.manifestPath)
    );
    const context = mutationContext(
      request.command,
      request.target,
      request.reportPresentation
    );
    const running = this.findBusy(manifestIdentity);
    if (running !== undefined) {
      this.report(context, 'busy', {
        runningCommand: running.context.command,
        runningProjectName: running.context.projectName,
        runningDocumentName: running.context.documentName,
        runningManifestPath: running.context.manifestPath
      });
      return { status: 'rejected', reason: 'busy' };
    }

    if (this.findDivergence(manifestIdentity) !== undefined) {
      const recovered = await this.recoverIdentity(manifestIdentity);
      if (!recovered) {
        this.report(context, 'divergenceBlocked');
        return { status: 'rejected', reason: 'divergence' };
      }
    }

    // Recovery contains awaits. Recheck both registries before reserving the
    // guard so two callers cannot both leave recovery and launch.
    const concurrent = this.findBusy(manifestIdentity);
    if (concurrent !== undefined) {
      this.report(context, 'busy', {
        runningCommand: concurrent.context.command,
        runningProjectName: concurrent.context.projectName,
        runningDocumentName: concurrent.context.documentName,
        runningManifestPath: concurrent.context.manifestPath
      });
      return { status: 'rejected', reason: 'busy' };
    }
    if (this.findDivergence(manifestIdentity) !== undefined) {
      this.report(context, 'divergenceBlocked');
      return { status: 'rejected', reason: 'divergence' };
    }

    const busy: BusyMutation = {
      identity: manifestIdentity,
      identityHistory: [manifestIdentity],
      context
    };
    this.busy.push(busy);
    let execution: GuardedExecution<TProcessResult>;
    try {
      execution = await this.executeGuarded(
        request,
        context,
        manifestIdentity
      );
    } finally {
      removeItem(this.busy, busy);
    }

    if (execution.divergence !== undefined) {
      this.setDivergence(execution.divergence);
      if (execution.divergence.phase === 'postMutation') {
        const recovered = await this.offerRecovery(execution.divergence, false);
        if (recovered && execution.result.status === 'completed') {
          execution.result.coherence = 'coherent';
        }
      }
    }
    return execution.result;
  }

  public async recover(
    request: ProjectManifestReadOnlyDiskBasisRequest
  ): Promise<boolean> {
    const identity = normalizeIdentity(
      await this.ports.resolvePathIdentity(request.target.project.manifestPath)
    );
    return this.recoverIdentity(identity);
  }

  public async reportReadOnlyDiskBasis(
    request: ProjectManifestReadOnlyDiskBasisRequest
  ): Promise<boolean> {
    const identity = normalizeIdentity(
      await this.ports.resolvePathIdentity(request.target.project.manifestPath)
    );
    const divergence = this.findDivergence(identity);
    if (divergence === undefined) {
      return false;
    }
    if (await this.tryClearDivergence(divergence)) {
      return false;
    }

    this.report(mutationContext(request.command, request.target), 'readOnlyDiskBasis');
    return true;
  }

  private async executeGuarded<
    TProcessResult extends ProjectManifestMutationProcessResult
  >(
    request: ProjectManifestMutationRequest<TProcessResult>,
    context: ProjectManifestMutationContext,
    manifestIdentity: CommandPalettePathIdentity
  ): Promise<GuardedExecution<TProcessResult>> {
    const preflight = await this.runPreflight(
      context,
      request.target,
      manifestIdentity
    );
    if (preflight.kind === 'failed') {
      return {
        result: { status: 'rejected', reason: 'preflight' },
        divergence: preflight.divergence
      };
    }

    const recorder = new BufferRevisionRecorder(
      preflight.value.baselineBuffer,
      () => this.ports.clock.now()
    );
    const subscription = this.ports.observeBuffers(manifestIdentity, (buffers) => {
      recorder.observe(buffers);
    });

    let processResult: TProcessResult | undefined;
    let processError: unknown;
    try {
      const stableBuffers = await this.ports.getOpenBuffers(manifestIdentity);
      recorder.observe(stableBuffers);
      if (!matchesPrelaunchBuffer(preflight.value.baselineBuffer, stableBuffers)) {
        this.report(context, 'preflightFailed', { detail: 'bufferChangedBeforeLaunch' });
        return { result: { status: 'rejected', reason: 'preflight' } };
      }

      const launchDisk = await this.readDiskState(request.target.project.manifestPath);
      const launchTarget = launchDisk.kind === 'trusted'
        ? retainExactCommandPaletteTarget(preflight.value.target, launchDisk.project)
        : undefined;
      const launchBuffers = await this.ports.getOpenBuffers(manifestIdentity);
      recorder.observe(launchBuffers);
      const launchBufferMatches = matchesPrelaunchBuffer(
        preflight.value.baselineBuffer,
        launchBuffers
      ) && (preflight.value.baselineBuffer === undefined ||
        launchDisk.kind === 'trusted' &&
        preflight.value.baselineBuffer.text === launchDisk.snapshot.text);
      if (launchDisk.kind !== 'trusted' ||
          launchTarget === undefined ||
          !bytesEqual(preflight.value.disk.bytes, launchDisk.snapshot.bytes) ||
          !launchBufferMatches) {
        this.report(context, 'preflightFailed', { detail: 'stateChangedBeforeLaunch' });
        return { result: { status: 'rejected', reason: 'preflight' } };
      }

      try {
        processResult = await request.run();
      } catch (error) {
        processError = error;
      }

      const postDisk = await this.readDiskState(request.target.project.manifestPath);
      const coherenceDeadline = this.ports.clock.now() +
        NativeSynchronizationDeadlineMilliseconds;
      recorder.observe(await this.ports.getOpenBuffers(manifestIdentity));
      const process = summarizeProcess(processResult, processError);
      if (postDisk.kind === 'untrusted') {
        const divergence = createDivergence({
          context,
          target: preflight.value.target,
          manifestIdentity,
          process,
          phase: 'postMutation',
          competing: true,
          untrusted: true,
          preservedBuffer: recorder.preservedCompetingBuffer()
        });
        this.report(context, 'manifestUntrusted', { process });
        return {
          result: {
            status: 'completed',
            manifestOutcome: 'untrusted',
            coherence: 'untrusted',
            processResult,
            processError
          },
          divergence
        };
      }

      if (bytesEqual(preflight.value.disk.bytes, postDisk.snapshot.bytes)) {
        this.report(context, 'manifestUnchanged', { process });
        return {
          result: {
            status: 'completed',
            manifestOutcome: 'unchanged',
            coherence: 'notRequired',
            processResult,
            processError
          }
        };
      }

      this.report(context, 'manifestChanged', { process });
      if (process.threw || process.cancelled || process.exitCode !== 0) {
        this.report(context, 'abnormalManifestChange', { process });
      }

      const coherence = await this.waitForCoherence(
        context,
        preflight.value.target,
        manifestIdentity,
        postDisk.snapshot,
        recorder,
        process,
        coherenceDeadline
      );
      if (coherence.kind === 'coherent') {
        this.report(context, 'coherenceComplete', { process });
        return {
          result: {
            status: 'completed',
            manifestOutcome: 'changed',
            coherence: 'coherent',
            processResult,
            processError
          }
        };
      }

      return {
        result: {
          status: 'completed',
          manifestOutcome: 'changed',
          coherence: coherence.divergence.untrusted ? 'untrusted' : 'diverged',
          processResult,
          processError
        },
        divergence: coherence.divergence
      };
    } finally {
      subscription.dispose();
    }
  }

  private async runPreflight(
    context: ProjectManifestMutationContext,
    selectedTarget: CommandPaletteTarget,
    manifestIdentity: CommandPalettePathIdentity
  ): Promise<
    | { kind: 'ready'; value: MutationPreflight }
    | { kind: 'failed'; divergence?: DivergenceState | undefined }
  > {
    for (;;) {
      const buffers = await this.ports.getOpenBuffers(manifestIdentity);
      if (buffers.length > 1) {
        this.report(context, 'ambiguousBuffers');
        return { kind: 'failed' };
      }

      const buffer = buffers[0];
      if (buffer?.isDirty === true) {
        const choice = await this.ports.chooseDirtyPreflight(
          context,
          cloneBuffer(buffer)
        );
        if (choice === 'cancel') {
          this.report(context, 'preflightCancelled');
          return { kind: 'failed' };
        }

        let saved = false;
        try {
          saved = await this.ports.saveBuffer(cloneBuffer(buffer));
        } catch {
          // The failure is reported below through the same fail-closed path.
        }
        if (!saved) {
          this.report(context, 'preflightFailed', { detail: 'saveFailed' });
          return { kind: 'failed' };
        }

        const afterSaveBuffers = await this.ports.getOpenBuffers(manifestIdentity);
        if (afterSaveBuffers.length !== 1 ||
            afterSaveBuffers[0]!.bufferId !== buffer.bufferId ||
            afterSaveBuffers[0]!.isDirty) {
          this.report(context, 'preflightFailed', { detail: 'saveDidNotStabilize' });
          return { kind: 'failed' };
        }
        const afterSave = cloneBuffer(afterSaveBuffers[0]!);
        const refreshed = await this.readAndRetainTarget(selectedTarget);
        if (refreshed === undefined) {
          this.report(context, 'preflightTargetChanged');
          return { kind: 'failed' };
        }
        const beforeLaunchBuffers = await this.ports.getOpenBuffers(manifestIdentity);
        if (!matchesSingleSnapshot(afterSave, beforeLaunchBuffers) ||
            refreshed.disk.text !== afterSave.text) {
          this.report(context, 'preflightFailed', { detail: 'revisionChangedAfterSave' });
          return { kind: 'failed' };
        }
        return {
          kind: 'ready',
          value: {
            target: refreshed.target,
            disk: refreshed.disk,
            baselineBuffer: afterSave
          }
        };
      }

      const refreshed = await this.readAndRetainTarget(selectedTarget);
      if (refreshed === undefined) {
        this.report(context, 'preflightTargetChanged');
        return { kind: 'failed' };
      }
      const stableBuffers = await this.ports.getOpenBuffers(manifestIdentity);
      if (buffer === undefined) {
        if (stableBuffers.length !== 0) {
          this.report(context, 'preflightFailed', { detail: 'bufferOpenedDuringPreflight' });
          return { kind: 'failed' };
        }
        return {
          kind: 'ready',
          value: {
            target: refreshed.target,
            disk: refreshed.disk,
            baselineBuffer: undefined
          }
        };
      }

      if (!matchesSingleSnapshot(buffer, stableBuffers)) {
        this.report(context, 'preflightFailed', { detail: 'revisionChangedDuringPreflight' });
        return { kind: 'failed' };
      }
      if (buffer.text === refreshed.disk.text) {
        return {
          kind: 'ready',
          value: {
            target: refreshed.target,
            disk: refreshed.disk,
            baselineBuffer: cloneBuffer(buffer)
          }
        };
      }

      this.report(context, 'preflightMismatch');
      const disk = cloneDisk(refreshed.disk);
      const preservedBuffer = cloneBuffer(buffer);
      for (;;) {
        const comparison = comparisonContext(
          context,
          'preflight',
          preservedBuffer,
          disk,
          { cancelled: false, threw: false }
        );
        const choice = await this.ports.choosePreflightMismatch(comparison);
        if (choice === 'compare') {
          await this.ports.showComparison(cloneComparison(comparison));
          this.report(context, 'comparisonShown');
          continue;
        }
        if (choice === 'reload') {
          const reloaded = await this.reloadFromSnapshot(
            manifestIdentity,
            comparison
          );
          if (reloaded) {
            break;
          }
          return {
            kind: 'failed',
            divergence: createDivergence({
              context,
              target: refreshed.target,
              manifestIdentity,
              disk,
              preservedBuffer,
              process: comparison.process,
              phase: 'preflight',
              competing: true,
              untrusted: false
            })
          };
        }

        this.report(context, 'preflightCancelled');
        return {
          kind: 'failed',
          divergence: createDivergence({
            context,
            target: refreshed.target,
            manifestIdentity,
            disk,
            preservedBuffer,
            process: comparison.process,
            phase: 'preflight',
            competing: true,
            untrusted: false
          })
        };
      }
      // A verified explicit reload restarts preflight from fresh disk state.
    }
  }

  private async readAndRetainTarget(
    selectedTarget: CommandPaletteTarget
  ): Promise<{
    target: CommandPaletteTarget;
    disk: ProjectManifestDiskSnapshot;
  } | undefined> {
    const disk = await this.readDiskState(selectedTarget.project.manifestPath);
    if (disk.kind === 'untrusted') {
      return undefined;
    }
    const target = retainExactCommandPaletteTarget(selectedTarget, disk.project);
    return target === undefined
      ? undefined
      : { target, disk: disk.snapshot };
  }

  private async readDiskState(manifestPath: string): Promise<DiskState> {
    try {
      const bytes = cloneBytes(await this.ports.readManifestBytes(manifestPath));
      const text = this.ports.decodeManifestBytes(bytes);
      const project = await this.ports.loadProjectTarget(manifestPath, cloneBytes(bytes));
      if (project === undefined) {
        return { kind: 'untrusted' };
      }
      return {
        kind: 'trusted',
        snapshot: { bytes, text },
        project
      };
    } catch {
      return { kind: 'untrusted' };
    }
  }

  private async waitForCoherence(
    context: ProjectManifestMutationContext,
    target: CommandPaletteTarget,
    manifestIdentity: CommandPalettePathIdentity,
    disk: ProjectManifestDiskSnapshot,
    recorder: BufferRevisionRecorder,
    process: ProjectManifestProcessSummary,
    deadline: number
  ): Promise<
    | { kind: 'coherent' }
    | { kind: 'diverged'; divergence: DivergenceState }
  > {
    for (;;) {
      const currentDisk = await this.readDiskState(target.project.manifestPath);
      if (currentDisk.kind === 'untrusted') {
        const divergence = createDivergence({
          context,
          target,
          manifestIdentity,
          disk,
          preservedBuffer: recorder.preservedCompetingBuffer(),
          process,
          phase: 'postMutation',
          competing: true,
          untrusted: true
        });
        this.report(context, 'manifestUntrusted', { process });
        return { kind: 'diverged', divergence };
      }
      if (!bytesEqual(currentDisk.snapshot.bytes, disk.bytes)) {
        const divergence = createDivergence({
          context,
          target,
          manifestIdentity,
          disk,
          preservedBuffer: recorder.preservedCompetingBuffer(),
          process,
          phase: 'postMutation',
          competing: true,
          untrusted: false
        });
        this.report(context, 'concurrentDiskChange', { process });
        return { kind: 'diverged', divergence };
      }

      const buffers = await this.ports.getOpenBuffers(manifestIdentity);
      recorder.observe(buffers);
      const classification = recorder.classify(disk.text, buffers, deadline);
      if (classification === 'coherent' || classification === 'noBuffer') {
        return { kind: 'coherent' };
      }
      if (classification === 'competing') {
        const divergence = createDivergence({
          context,
          target,
          manifestIdentity,
          disk,
          preservedBuffer: recorder.preservedCompetingBuffer(),
          process,
          phase: 'postMutation',
          competing: true,
          untrusted: false
        });
        this.report(context, 'editorDivergence', { process });
        return { kind: 'diverged', divergence };
      }

      const remaining = deadline - this.ports.clock.now();
      if (remaining <= 0) {
        const divergence = createDivergence({
          context,
          target,
          manifestIdentity,
          disk,
          preservedBuffer: recorder.currentOrBaselineBuffer(),
          process,
          phase: 'postMutation',
          competing: false,
          untrusted: false
        });
        this.report(context, 'coherenceTimeout', { process });
        return { kind: 'diverged', divergence };
      }

      await Promise.race([
        recorder.waitForObservation(),
        this.ports.clock.wait(remaining)
      ]);
    }
  }

  private async recoverIdentity(
    identity: CommandPalettePathIdentity
  ): Promise<boolean> {
    const divergence = this.findDivergence(identity);
    if (divergence === undefined) {
      return true;
    }
    if (await this.tryClearDivergence(divergence)) {
      return true;
    }
    return divergence.phase === 'preflight'
      ? this.offerPreflightRecovery(divergence)
      : this.offerRecovery(divergence);
  }

  private async offerRecovery(
    divergence: DivergenceState,
    allowPassiveClear = true
  ): Promise<boolean> {
    if (allowPassiveClear && await this.tryClearDivergence(divergence)) {
      return true;
    }
    if (divergence.untrusted || divergence.disk === undefined) {
      this.report(divergence.context, 'manualRepairRequired', {
        process: divergence.process
      });
      return false;
    }

    const buffers = await this.ports.getOpenBuffers(divergence.manifestIdentity);
    if (buffers.length !== 1) {
      if (buffers.length > 1) {
        this.report(divergence.context, 'ambiguousBuffers');
      }
      return false;
    }
    const buffer = cloneBuffer(buffers[0]!);
    const comparison = comparisonContext(
      divergence.context,
      divergence.phase,
      buffer,
      divergence.disk,
      divergence.process
    );
    const choice = await this.ports.chooseRecovery(comparison);
    if (choice === undefined) {
      return false;
    }
    if (choice === 'compare') {
      divergence.recoveryAcknowledged = true;
      await this.ports.showComparison(comparisonContext(
        divergence.context,
        divergence.phase,
        divergence.preservedBuffer ?? buffer,
        divergence.disk,
        divergence.process
      ));
      this.report(divergence.context, 'comparisonShown', {
        process: divergence.process
      });
      return this.tryClearDivergence(divergence);
    }
    if (choice === 'keepEditing') {
      divergence.overwriteWarned = true;
      this.report(divergence.context, 'keepEditingWarning', {
        process: divergence.process
      });
      return false;
    }

    if (await this.reloadFromSnapshot(divergence.manifestIdentity, comparison)) {
      this.removeDivergence(divergence);
      this.report(divergence.context, 'divergenceCleared', {
        process: divergence.process
      });
      return true;
    }
    return false;
  }

  private async offerPreflightRecovery(
    divergence: DivergenceState
  ): Promise<boolean> {
    if (divergence.untrusted || divergence.disk === undefined) {
      this.report(divergence.context, 'manualRepairRequired', {
        process: divergence.process
      });
      return false;
    }
    const buffers = await this.ports.getOpenBuffers(divergence.manifestIdentity);
    if (buffers.length !== 1) {
      return false;
    }
    const comparison = comparisonContext(
      divergence.context,
      'preflight',
      buffers[0]!,
      divergence.disk,
      divergence.process
    );
    for (;;) {
      const choice = await this.ports.choosePreflightMismatch(comparison);
      if (choice === 'compare') {
        await this.ports.showComparison(cloneComparison(comparison));
        this.report(divergence.context, 'comparisonShown');
        continue;
      }
      if (choice === 'cancel') {
        this.report(divergence.context, 'preflightCancelled');
        return false;
      }
      if (!await this.reloadFromSnapshot(divergence.manifestIdentity, comparison)) {
        return false;
      }
      this.removeDivergence(divergence);
      this.report(divergence.context, 'divergenceCleared');
      return true;
    }
  }

  private async tryClearDivergence(
    divergence: DivergenceState
  ): Promise<boolean> {
    const disk = await this.readDiskState(divergence.target.project.manifestPath);
    if (disk.kind === 'untrusted') {
      divergence.untrusted = true;
      return false;
    }

    const buffers = await this.ports.getOpenBuffers(divergence.manifestIdentity);
    if (buffers.length === 0) {
      this.removeDivergence(divergence);
      this.report(divergence.context, 'divergenceCleared', {
        process: divergence.process
      });
      return true;
    }
    if (buffers.length !== 1) {
      return false;
    }
    const buffer = buffers[0]!;
    const equalityProved = !buffer.isDirty && buffer.text === disk.snapshot.text;
    const immutableSnapshotPreserved = divergence.disk !== undefined &&
      bytesEqual(disk.snapshot.bytes, divergence.disk.bytes);
    const explicitCondition = divergence.untrusted ||
      divergence.phase === 'preflight' ||
      immutableSnapshotPreserved && (
        !divergence.competing || divergence.recoveryAcknowledged
      ) ||
      divergence.overwriteWarned;
    if (!equalityProved || !explicitCondition) {
      return false;
    }

    this.removeDivergence(divergence);
    this.report(divergence.context, 'divergenceCleared', {
      process: divergence.process
    });
    return true;
  }

  private async reloadFromSnapshot(
    manifestIdentity: CommandPalettePathIdentity,
    context: ProjectManifestReloadContext
  ): Promise<boolean> {
    if (!await this.ports.confirmReload(cloneComparison(context))) {
      this.report(context, 'reloadCancelled', { process: context.process });
      return false;
    }

    const expectedBuffer = cloneBuffer(context.buffer);
    if (!await this.reloadPrecheck(manifestIdentity, context.disk, expectedBuffer)) {
      this.report(context, 'reloadRefused', { process: context.process });
      return false;
    }

    try {
      await this.ports.revealAndFocus(cloneBuffer(expectedBuffer));
    } catch {
      this.report(context, 'reloadRefused', { process: context.process });
      return false;
    }
    if (!await this.reloadPrecheck(
      manifestIdentity,
      context.disk,
      expectedBuffer,
      true
    )) {
      this.report(context, 'reloadRefused', { process: context.process });
      return false;
    }

    try {
      await this.ports.revertBuffer(cloneBuffer(expectedBuffer));
    } catch {
      this.report(context, 'reloadRefused', { process: context.process });
      return false;
    }

    const disk = await this.readDiskState(context.manifestPath);
    const buffers = await this.ports.getOpenBuffers(manifestIdentity);
    const active = await this.ports.getActiveFileIdentity();
    const postBuffer = buffers.length === 1 ? buffers[0] : undefined;
    const stableBuffers = await this.ports.getOpenBuffers(manifestIdentity);
    const postcheck = disk.kind === 'trusted' &&
      bytesEqual(disk.snapshot.bytes, context.disk.bytes) &&
      postBuffer !== undefined &&
      postBuffer.bufferId === expectedBuffer.bufferId &&
      postBuffer.revision !== expectedBuffer.revision &&
      !postBuffer.isDirty &&
      postBuffer.text === context.disk.text &&
      matchesSingleSnapshot(postBuffer, stableBuffers) &&
      sameCommandPalettePathIdentity(manifestIdentity, active ?? impossibleIdentity());
    if (!postcheck) {
      this.report(context, 'reloadRefused', { process: context.process });
      return false;
    }

    this.report(context, 'reloadCompleted', { process: context.process });
    return true;
  }

  private async reloadPrecheck(
    manifestIdentity: CommandPalettePathIdentity,
    diskSnapshot: ProjectManifestDiskSnapshot,
    expectedBuffer: ProjectManifestEditorSnapshot,
    requireActive = false
  ): Promise<boolean> {
    const disk = await this.readDiskState(expectedBuffer.filePath);
    const buffers = await this.ports.getOpenBuffers(manifestIdentity);
    if (disk.kind !== 'trusted' ||
        !bytesEqual(disk.snapshot.bytes, diskSnapshot.bytes) ||
        !matchesSingleSnapshot(expectedBuffer, buffers)) {
      return false;
    }
    if (!requireActive) {
      return true;
    }
    const active = await this.ports.getActiveFileIdentity();
    return active !== undefined &&
      sameCommandPalettePathIdentity(manifestIdentity, active);
  }

  private report(
    context: ProjectManifestMutationContext,
    kind: ProjectManifestMutationReportKind,
    extras: Partial<Pick<
      ProjectManifestMutationReport,
      | 'runningCommand'
      | 'runningProjectName'
      | 'runningDocumentName'
      | 'runningManifestPath'
      | 'process'
      | 'detail'
    >> = {}
  ): void {
    this.ports.report({ ...context, kind, ...extras });
  }

  private findBusy(identity: CommandPalettePathIdentity): BusyMutation | undefined {
    const entry = this.busy.find((candidate) => candidate.identityHistory.some(
      (known) => sameManifestIdentity(known, identity)
    ));
    if (entry !== undefined) {
      rememberIdentity(entry.identityHistory, identity);
      entry.identity = identity;
    }
    return entry;
  }

  private findDivergence(
    identity: CommandPalettePathIdentity
  ): DivergenceState | undefined {
    const entry = this.divergences.find((candidate) =>
      candidate.identityHistory.some((known) => sameManifestIdentity(known, identity)));
    if (entry !== undefined) {
      rememberIdentity(entry.identityHistory, identity);
      entry.manifestIdentity = identity;
    }
    return entry;
  }

  private setDivergence(divergence: DivergenceState): void {
    const existing = this.findDivergence(divergence.manifestIdentity);
    if (existing !== undefined) {
      removeItem(this.divergences, existing);
    }
    this.divergences.push(divergence);
  }

  private removeDivergence(divergence: DivergenceState): void {
    removeItem(this.divergences, divergence);
  }
}

type ObservationClassification =
  | 'waiting'
  | 'coherent'
  | 'noBuffer'
  | 'competing';

class BufferRevisionRecorder {
  private readonly observations: Array<readonly ProjectManifestEditorSnapshot[]> = [];
  private readonly observationTimes: number[] = [];
  private readonly waiters = new Set<() => void>();
  private competingBuffer: ProjectManifestEditorSnapshot | undefined;

  public constructor(
    private readonly baseline: ProjectManifestEditorSnapshot | undefined,
    private readonly now: () => number
  ) {}

  public observe(buffers: readonly ProjectManifestEditorSnapshot[]): void {
    this.observations.push(buffers.map(cloneBuffer));
    this.observationTimes.push(this.now());
    for (const resolve of this.waiters) {
      resolve();
    }
    this.waiters.clear();
  }

  public waitForObservation(): Promise<void> {
    return new Promise((resolve) => this.waiters.add(resolve));
  }

  public classify(
    finalText: string,
    currentBuffers: readonly ProjectManifestEditorSnapshot[],
    deadline: number
  ): ObservationClassification {
    if (currentBuffers.length > 1) {
      this.rememberCompetingBuffer(currentBuffers[0]);
      return 'competing';
    }
    if (this.baseline === undefined) {
      const observedBuffer = this.observations.some((buffers) => buffers.length > 0);
      if (currentBuffers.length === 0 && !observedBuffer) {
        return 'noBuffer';
      }
      this.rememberCompetingBuffer(
        currentBuffers[0] ?? this.latestObservedBuffer()
      );
      return 'competing';
    }

    let previousText = this.baseline.text;
    let transitionedToFinal = false;
    let transitionedToFinalAt: number | undefined;
    let sawClose = false;
    for (let index = 0; index < this.observations.length; index++) {
      const buffers = this.observations[index]!;
      if (buffers.length === 0) {
        sawClose = true;
        continue;
      }
      if (buffers.length !== 1) {
        this.rememberCompetingBuffer(buffers[0]);
        return 'competing';
      }
      const buffer = buffers[0]!;
      if (buffer.bufferId !== this.baseline.bufferId || buffer.isDirty) {
        this.rememberCompetingBuffer(buffer);
        return 'competing';
      }
      if (sawClose) {
        this.rememberCompetingBuffer(buffer);
        return 'competing';
      }
      if (buffer.text === previousText) {
        continue;
      }
      if (buffer.text !== finalText || transitionedToFinal) {
        this.rememberCompetingBuffer(buffer);
        return 'competing';
      }
      transitionedToFinal = true;
      transitionedToFinalAt = this.observationTimes[index];
      previousText = buffer.text;
    }

    if (currentBuffers.length === 0) {
      return 'noBuffer';
    }

    const current = currentBuffers[0]!;
    if (current.bufferId !== this.baseline.bufferId || current.isDirty) {
      this.rememberCompetingBuffer(current);
      return 'competing';
    }
    if (current.text === finalText) {
      return transitionedToFinalAt === undefined || transitionedToFinalAt <= deadline
        ? 'coherent'
        : 'waiting';
    }
    if (current.text === this.baseline.text && !transitionedToFinal) {
      return 'waiting';
    }
    this.rememberCompetingBuffer(current);
    return 'competing';
  }

  public preservedCompetingBuffer(): ProjectManifestEditorSnapshot | undefined {
    if (this.competingBuffer !== undefined) {
      return cloneBuffer(this.competingBuffer);
    }
    return this.latestObservedBuffer() ?? (
      this.baseline === undefined ? undefined : cloneBuffer(this.baseline)
    );
  }

  public currentOrBaselineBuffer(): ProjectManifestEditorSnapshot | undefined {
    return this.preservedCompetingBuffer();
  }

  private latestObservedBuffer(): ProjectManifestEditorSnapshot | undefined {
    for (let index = this.observations.length - 1; index >= 0; index--) {
      const buffers = this.observations[index]!;
      if (buffers.length === 1) {
        return cloneBuffer(buffers[0]!);
      }
    }
    return undefined;
  }

  private rememberCompetingBuffer(
    buffer: ProjectManifestEditorSnapshot | undefined
  ): void {
    if (this.competingBuffer === undefined && buffer !== undefined) {
      this.competingBuffer = cloneBuffer(buffer);
    }
  }
}

function mutationContext(
  command: string,
  target: CommandPaletteTarget,
  reportPresentation?: ProjectManifestMutationReportPresentation
): ProjectManifestMutationContext {
  return {
    command,
    projectName: target.project.projectName,
    documentName: target.document?.name,
    manifestPath: target.project.manifestPath,
    ...(reportPresentation === undefined ? {} : { reportPresentation })
  };
}

function comparisonContext(
  context: ProjectManifestMutationContext,
  phase: 'preflight' | 'postMutation',
  buffer: ProjectManifestEditorSnapshot,
  disk: ProjectManifestDiskSnapshot,
  process: ProjectManifestProcessSummary
): ProjectManifestComparisonContext {
  return {
    ...context,
    phase,
    buffer: cloneBuffer(buffer),
    disk: cloneDisk(disk),
    process: { ...process }
  };
}

function cloneComparison(
  context: ProjectManifestComparisonContext
): ProjectManifestComparisonContext {
  return comparisonContext(
    context,
    context.phase,
    context.buffer,
    context.disk,
    context.process
  );
}

function createDivergence(
  value: Omit<
    DivergenceState,
    'identityHistory' | 'recoveryAcknowledged' | 'overwriteWarned'
  >
): DivergenceState {
  return {
    ...value,
    disk: value.disk === undefined ? undefined : cloneDisk(value.disk),
    preservedBuffer: value.preservedBuffer === undefined
      ? undefined
      : cloneBuffer(value.preservedBuffer),
    identityHistory: [value.manifestIdentity],
    recoveryAcknowledged: false,
    overwriteWarned: false
  };
}

function summarizeProcess<TProcessResult extends ProjectManifestMutationProcessResult>(
  processResult: TProcessResult | undefined,
  processError: unknown
): ProjectManifestProcessSummary {
  return {
    exitCode: processResult?.exitCode,
    cancelled: processResult?.cancelled ?? false,
    threw: processError !== undefined
  };
}

function matchesPrelaunchBuffer(
  baseline: ProjectManifestEditorSnapshot | undefined,
  buffers: readonly ProjectManifestEditorSnapshot[]
): boolean {
  return baseline === undefined
    ? buffers.length === 0
    : matchesSingleSnapshot(baseline, buffers);
}

function matchesSingleSnapshot(
  expected: ProjectManifestEditorSnapshot,
  buffers: readonly ProjectManifestEditorSnapshot[]
): boolean {
  if (buffers.length !== 1) {
    return false;
  }
  const actual = buffers[0]!;
  return actual.bufferId === expected.bufferId &&
    actual.revision === expected.revision &&
    actual.text === expected.text &&
    actual.isDirty === expected.isDirty;
}

function normalizeIdentity(
  identity: CommandPalettePathIdentity
): CommandPalettePathIdentity {
  return {
    canonicalPath: path.normalize(identity.canonicalPath),
    objectIdentity: identity.objectIdentity,
    kind: identity.kind
  };
}

function sameManifestIdentity(
  left: CommandPalettePathIdentity,
  right: CommandPalettePathIdentity
): boolean {
  return ordinalIgnoreCaseKey(path.normalize(left.canonicalPath)) ===
      ordinalIgnoreCaseKey(path.normalize(right.canonicalPath)) ||
    left.objectIdentity !== undefined &&
      right.objectIdentity !== undefined &&
      left.objectIdentity === right.objectIdentity;
}

function impossibleIdentity(): CommandPalettePathIdentity {
  return { canonicalPath: '\0' };
}

function cloneBuffer(
  buffer: ProjectManifestEditorSnapshot
): ProjectManifestEditorSnapshot {
  return { ...buffer };
}

function cloneDisk(
  disk: ProjectManifestDiskSnapshot
): ProjectManifestDiskSnapshot {
  return {
    bytes: cloneBytes(disk.bytes),
    text: disk.text
  };
}

function cloneBytes(bytes: Uint8Array): Uint8Array {
  return Uint8Array.from(bytes);
}

function bytesEqual(left: Uint8Array, right: Uint8Array): boolean {
  if (left.byteLength !== right.byteLength) {
    return false;
  }
  for (let index = 0; index < left.byteLength; index++) {
    if (left[index] !== right[index]) {
      return false;
    }
  }
  return true;
}

function removeItem<T>(items: T[], item: T): void {
  const index = items.indexOf(item);
  if (index >= 0) {
    items.splice(index, 1);
  }
}

function rememberIdentity(
  identities: CommandPalettePathIdentity[],
  identity: CommandPalettePathIdentity
): void {
  if (!identities.some((known) =>
    known.canonicalPath === identity.canonicalPath &&
    known.objectIdentity === identity.objectIdentity)) {
    identities.push(identity);
  }
}
