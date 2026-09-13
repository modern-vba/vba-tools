import { spawn } from 'node:child_process';

// The published adapter is a valid capabilities provider without reference list.
// Wait for that specific rejection: EOF or --version alone cannot prove that the
// language server's asynchronous capability admission loaded and completed.
export function verifyLanguageServerCapabilityRejection(file, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd, windowsHide: true });
    let pending = Buffer.alloc(0);
    let stderr = '';
    let phase = 'initialize';
    let failure;
    let cleanupTimer;
    const timer = setTimeout(() => fail(new Error('Language-server capability smoke timed out.')), 15000);

    function fail(error) {
      if (failure) return;
      failure = error;
      child.kill();
      cleanupTimer = setTimeout(() => reject(new Error(
        `${failure.message} The owned language-server process did not close after termination.`
      )), 5000);
    }

    function send(message) {
      if (failure) return;
      const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', ...message }), 'utf8');
      child.stdin.write(Buffer.concat([
        Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body
      ]));
    }

    function receive(message) {
      if (failure) return;
      if (message.error) throw new Error(`Language-server smoke request failed: ${JSON.stringify(message.error)}`);
      if (phase === 'initialize' && message.id === 1) {
        if (!message.result?.capabilities) throw new Error('Language server did not initialize.');
        phase = 'admission';
        send({ method: 'initialized', params: {} });
      } else if (phase === 'admission' && message.method === 'window/logMessage') {
        const warning = message.params?.message ?? '';
        if (!warning.includes('registry-only discovery remains available')) return;
        if (!warning.includes('does not report reference list output schema 1.0')) {
          throw new Error(`The published admission dependency did not reject the expected capability: ${warning}`);
        }
        phase = 'completion';
        const uri = 'file:///C:/vba-tools-capability-smoke/Module1.bas';
        send({ method: 'textDocument/didOpen', params: { textDocument: {
          uri, languageId: 'vba', version: 1, text: 'Public Sub Run()\nEnd Sub\n'
        } } });
        send({ id: 2, method: 'textDocument/completion', params: {
          textDocument: { uri }, position: { line: 0, character: 0 }
        } });
      } else if (phase === 'completion' && message.id === 2) {
        if (!Object.hasOwn(message, 'result')) throw new Error('No completion result after capability rejection.');
        phase = 'shutdown';
        send({ id: 3, method: 'shutdown', params: null });
      } else if (phase === 'shutdown' && message.id === 3) {
        if (message.result !== null) throw new Error('Language server did not acknowledge shutdown.');
        phase = 'exit';
        send({ method: 'exit', params: null });
        child.stdin.end();
      }
    }

    child.stdout.on('data', chunk => {
      if (failure) return;
      pending = Buffer.concat([pending, chunk]);
      try {
        while (true) {
          const headerEnd = pending.indexOf('\r\n\r\n');
          if (headerEnd < 0) return;
          const length = Number(pending.subarray(0, headerEnd).toString('ascii')
            .match(/Content-Length: (\d+)/i)?.[1]);
          if (!Number.isSafeInteger(length) || length <= 0) throw new Error('Invalid LSP smoke response framing.');
          const end = headerEnd + 4 + length;
          if (pending.length < end) return;
          const message = JSON.parse(pending.subarray(headerEnd + 4, end).toString('utf8'));
          pending = pending.subarray(end);
          receive(message);
        }
      } catch (error) { fail(error); }
    });
    child.stderr.on('data', chunk => { stderr += chunk.toString('utf8'); });
    child.on('error', fail);
    child.stdin.on('error', fail);
    child.stdout.on('error', fail);
    child.stderr.on('error', fail);
    child.on('close', (code, signal) => {
      clearTimeout(timer);
      clearTimeout(cleanupTimer);
      if (failure) { reject(failure); return; }
      if (phase !== 'exit' || code !== 0 || signal !== null || pending.length !== 0) {
        reject(new Error(`Language-server capability smoke stopped during ${phase} (exit ${code}, signal ${signal}). ${stderr}`));
        return;
      }
      resolve();
    });
    send({ id: 1, method: 'initialize', params: { processId: null, rootUri: null, capabilities: {} } });
  });
}
