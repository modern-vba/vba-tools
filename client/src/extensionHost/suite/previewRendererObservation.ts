import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';

// Read-only CDP observation of this harness-owned VS Code renderer. It sends
// no mouse, keyboard, editor command, or semantic-token request.
export interface PreviewRendererObservation {
  readonly screenshotPath: string;
  readonly observedAt: number;
  readonly dom: {
    readonly capturedAt: number;
    readonly activeTab: string;
    readonly lines: readonly {
      readonly text: string;
      readonly spans: readonly { readonly text: string; readonly className: string; readonly color: string }[];
    }[];
  };
}

export async function capturePreviewRenderer(
  port: number,
  screenshotPath: string
): Promise<PreviewRendererObservation> {
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as {
    type: string; url: string; webSocketDebuggerUrl?: string;
  }[];
  const target = targets.find(candidate => candidate.type === 'page'
    && candidate.url.includes('workbench'));
  assert.ok(target?.webSocketDebuggerUrl, 'The test VS Code workbench renderer must be observable.');
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise<void>((resolve, reject) => {
    socket.addEventListener('open', () => resolve(), { once: true });
    socket.addEventListener('error', () => reject(new Error('Renderer connection failed.')), { once: true });
  });
  let nextId = 1;
  const pending = new Map<number, { resolve(value: unknown): void; reject(error: Error): void }>();
  socket.addEventListener('message', event => {
    const response = JSON.parse(String(event.data)) as {
      id?: number; result?: unknown; error?: unknown;
    };
    if (response.id === undefined) { return; }
    const request = pending.get(response.id);
    pending.delete(response.id);
    if (response.error !== undefined) {
      request?.reject(new Error(JSON.stringify(response.error)));
    } else { request?.resolve(response.result); }
  });
  const send = (method: string, params: object): Promise<unknown> => {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        pending.delete(id);
        reject(new Error(`Renderer observation timed out: ${method}`));
      }, 10_000);
      pending.set(id, {
        resolve: value => { clearTimeout(timeout); resolve(value); },
        reject: error => { clearTimeout(timeout); reject(error); }
      });
      socket.send(JSON.stringify({ id, method, params }));
    });
  };
  try {
    const observation = await send('Runtime.evaluate', {
      expression: `new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve({
        capturedAt: Date.now(),
        activeTab: document.querySelector('.tabs-container .tab.active')?.textContent,
        lines: Array.from(document.querySelectorAll('.monaco-editor .view-lines .view-line')).map(line => ({
          text: line.textContent,
          spans: Array.from(line.querySelectorAll('span')).filter(span => span.childElementCount === 0).map(span => ({
            text: span.textContent, className: span.className, color: getComputedStyle(span).color
          }))
        }))
      }))))`,
      awaitPromise: true, returnByValue: true
    }) as { result: { value?: PreviewRendererObservation['dom'] }; exceptionDetails?: unknown };
    assert.equal(observation.exceptionDetails, undefined);
    assert.ok(observation.result.value);
    const screenshot = await send('Page.captureScreenshot', { format: 'png' }) as { data: string };
    await writeFile(screenshotPath, Buffer.from(screenshot.data, 'base64'));
    return { screenshotPath, observedAt: Date.now(), dom: observation.result.value };
  } finally { socket.close(); }
}
