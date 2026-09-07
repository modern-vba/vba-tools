import { createHash } from 'node:crypto';

export interface PreviewTokenResponse {
  readonly uri: string;
  readonly version: number;
  readonly responseVersion: number;
  readonly sourceSha256: string;
  readonly startedAt: number;
  readonly completedAt: number;
  readonly data: readonly number[];
}

interface ObservedDocument {
  readonly uri: { toString(): string };
  readonly version: number;
  getText(): string;
}

export function createPreviewSemanticTokensProbe() {
  const responses: PreviewTokenResponse[] = [];
  return {
    responses,
    async observe<T extends { readonly data: Uint32Array } | null | undefined>(
      document: ObservedDocument,
      next: () => PromiseLike<T> | T,
      isCancellationRequested: () => boolean = () => false
    ): Promise<T> {
      const startedAt = Date.now();
      const version = document.version;
      const sourceSha256 = createHash('sha256')
        .update(document.getText(), 'utf8').digest('hex');
      const result = await next();
      const completedAt = Date.now();
      if (result !== undefined && result !== null && !isCancellationRequested()) {
        responses.push({
          uri: document.uri.toString(), version,
          responseVersion: document.version, sourceSha256,
          startedAt, completedAt, data: Array.from(result.data)
        });
      }
      return result;
    }
  };
}

// This observer does not request tokens or alter normal provider scheduling.
// It exists only in explicitly enabled extension-host measurement processes.
export const previewSemanticTokensProbe =
  process.env.VBA_TOOLS_PREVIEW_PERFORMANCE === '1'
    ? createPreviewSemanticTokensProbe()
    : undefined;
