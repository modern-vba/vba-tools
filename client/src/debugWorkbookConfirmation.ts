import * as path from 'node:path';
import { windowsPathKey } from './windowsPathIdentity';

export interface DebugWorkbookConfirmationOptions {
  readonly sessionId: string;
  readonly generationId: number;
  readonly workbookPath: string;
  readonly customRequest: (command: string, argumentsValue: Record<string, unknown>) => PromiseLike<unknown> | unknown;
  readonly confirmReplacement: (warning: string) => Promise<boolean>;
}

/** One debug generation's authority to answer a source-workbook replacement request. */
export class DebugWorkbookConfirmationBinding {
  private requestClaimed = false;
  private disposed = false;

  constructor(private readonly options: DebugWorkbookConfirmationOptions) {
    if (!isHexId(options.sessionId)) {
      throw new TypeError('Debug workbook confirmation requires an exact sessionId.');
    }
    if (!Number.isInteger(options.generationId)
        || options.generationId < 0 || options.generationId > 0x7fffffff) {
      throw new RangeError('Debug workbook confirmation generationId must be a nonnegative Int32.');
    }
    if (!isCanonicalWindowsWorkbookPath(options.workbookPath)) {
      throw new TypeError('Debug workbook confirmation requires a canonical Windows workbook path.');
    }
  }

  async handleEvent(originSessionId: string, message: unknown): Promise<void> {
    if (this.disposed || originSessionId !== this.options.sessionId || !isRecord(message)
        || message.type !== 'event' || message.event !== 'vba/workbookConfirmation'
        || !isRecord(message.body)) return;
    const body = message.body;
    if (body.schemaVersion !== '1.0' || !isHexId(body.requestId)
        || body.sessionId !== this.options.sessionId
        || body.generationId !== this.options.generationId
        || !isCanonicalWindowsWorkbookPath(body.workbookPath)
        || windowsPathKey(body.workbookPath) !== windowsPathKey(this.options.workbookPath)
        || typeof body.message !== 'string' || body.message.trim().length === 0) return;
    if (this.requestClaimed) return;
    this.requestClaimed = true;

    const warning = `Debug will replace live VBA code in ${this.options.workbookPath}. `
      + `The workbook will not be saved automatically.\n\n${body.message}`;
    let accepted = false;
    try {
      accepted = await this.options.confirmReplacement(warning) === true;
    } catch {
      // If the confirmation UI fails, the debug adapter still receives a refusal.
    }
    if (this.disposed) return;
    await this.options.customRequest('vba/workbookConfirmationResult', {
      requestId: body.requestId,
      sessionId: this.options.sessionId,
      generationId: this.options.generationId,
      accepted
    });
  }

  dispose(): void { this.disposed = true; }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isHexId(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{32}$/.test(value);
}

function isCanonicalWindowsWorkbookPath(value: unknown): value is string {
  return typeof value === 'string' && path.win32.isAbsolute(value)
    && !value.startsWith('\\\\?\\') && !value.startsWith('\\\\.\\')
    && path.win32.normalize(value) === value;
}
