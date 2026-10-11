import assert from 'node:assert/strict';
import { realpath } from 'node:fs/promises';

export async function assertCanonicalProjectReceiptPath(
  receiptProject: unknown,
  requestedProjectRoot: string
): Promise<void> {
  assert.equal(receiptProject, await realpath(requestedProjectRoot));
}
