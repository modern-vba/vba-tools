import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { waitForReferenceCatalogSettlement } from './previewSchedulerEvidence';

export async function collectFreshPreviewOracle(
  executable: string,
  sources: ReadonlyMap<string, string>,
  resultDirectory: string
): Promise<Map<string, { readonly sourceSha256: string; readonly data: readonly number[] }>> {
  await mkdir(resultDirectory, { recursive: true });
  const child = spawn(executable, [], {
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
    env: {
      ...process.env,
      VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY: path.join(resultDirectory, 'scheduler'),
      VBA_TOOLS_REFERENCE_CATALOG_CACHE_DIR: path.join(resultDirectory, 'reference-catalog')
    }
  });
  const pending = new Map<number, { resolve(value: unknown): void; reject(error: Error): void }>();
  let nextId = 1;
  let buffer = Buffer.alloc(0);
  let stderr = '';
  const send = (message: object) => {
    const body = Buffer.from(JSON.stringify(message), 'utf8');
    child.stdin.write(Buffer.concat([
      Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body
    ]));
  };
  child.stderr.on('data', chunk => { stderr += String(chunk); });
  child.stdout.on('data', (chunk: Buffer) => {
    buffer = Buffer.concat([buffer, chunk]);
    for (;;) {
      const headerEnd = buffer.indexOf('\r\n\r\n');
      if (headerEnd < 0) { break; }
      const length = Number(/Content-Length:\s*(\d+)/iu.exec(
        buffer.subarray(0, headerEnd).toString('ascii'))?.[1]);
      if (!Number.isSafeInteger(length) || length < 0) {
        throw new Error('Invalid oracle LSP framing.');
      }
      if (buffer.length < headerEnd + 4 + length) { break; }
      const message = JSON.parse(buffer.subarray(headerEnd + 4,
        headerEnd + 4 + length).toString('utf8')) as {
          id?: number; method?: string; result?: unknown; error?: unknown;
        };
      buffer = buffer.subarray(headerEnd + 4 + length);
      if (message.method !== undefined && message.id !== undefined) {
        send({ jsonrpc: '2.0', id: message.id, result: null });
      } else if (message.id !== undefined) {
        const request = pending.get(message.id);
        pending.delete(message.id);
        if (message.error !== undefined) {
          request?.reject(new Error(JSON.stringify(message.error)));
        } else { request?.resolve(message.result); }
      }
    }
  });
  child.on('error', error => {
    for (const request of pending.values()) { request.reject(error); }
    pending.clear();
  });
  child.on('exit', code => {
    for (const request of pending.values()) {
      request.reject(new Error(`Oracle exited (${code}): ${stderr}`));
    }
    pending.clear();
  });
  const request = (method: string, params: unknown): Promise<unknown> => {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        pending.delete(id);
        reject(new Error(`Oracle timed out: ${method}`));
      }, 90_000);
      pending.set(id, {
        resolve: value => { clearTimeout(timeout); resolve(value); },
        reject: error => { clearTimeout(timeout); reject(error); }
      });
      send({ jsonrpc: '2.0', id, method, params });
    });
  };
  try {
    await request('initialize', {
      processId: process.pid, rootUri: null, capabilities: {},
      clientInfo: { name: 'VBA Tools independent fresh token oracle' }
    });
    send({ jsonrpc: '2.0', method: 'initialized', params: {} });
    const result = new Map<string, { sourceSha256: string; data: readonly number[] }>();
    for (const [uri, text] of sources) {
      send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: {
        textDocument: { uri, languageId: 'vba', version: 1, text }
      } });
      await request('textDocument/semanticTokens/full', {
        textDocument: { uri }
      });
    }
    await waitForReferenceCatalogSettlement(path.join(resultDirectory, 'scheduler'));
    for (const [uri, text] of sources) {
      const tokens = await request('textDocument/semanticTokens/full', {
        textDocument: { uri }
      }) as { data?: unknown };
      assert.ok(Array.isArray(tokens?.data));
      assert.ok(tokens.data.every(value => typeof value === 'number'));
      result.set(uri, { sourceSha256: createHash('sha256').update(text, 'utf8').digest('hex'),
        data: tokens.data as number[] });
    }
    await writeFile(path.join(resultDirectory, 'tokens.json'),
      JSON.stringify(Object.fromEntries(result), undefined, 2) + '\n');
    await request('shutdown', null);
    send({ jsonrpc: '2.0', method: 'exit' });
    return result;
  } finally {
    child.stdin.end();
    child.kill();
    await writeFile(path.join(resultDirectory, 'stderr.log'), stderr);
  }
}
