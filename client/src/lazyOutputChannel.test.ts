import test from 'node:test';
import assert from 'node:assert/strict';

import { createLazyOutputChannel } from './lazyOutputChannel';

test('a lazy output channel stays uncreated until output is written', () => {
  let creations = 0;
  const lines: string[] = [];
  const channel = createLazyOutputChannel('VBA Tools', () => {
    creations += 1;
    return {
      name: 'VBA Tools',
      append: () => undefined,
      appendLine: (line) => lines.push(line),
      replace: () => undefined,
      clear: () => undefined,
      show: () => undefined,
      hide: () => undefined,
      dispose: () => undefined
    };
  });

  assert.equal(channel.name, 'VBA Tools');
  assert.equal(creations, 0);

  channel.clear();
  channel.hide();
  assert.equal(creations, 0);

  channel.appendLine('started');

  assert.equal(creations, 1);
  assert.deepEqual(lines, ['started']);
  channel.dispose();

  let disposedCreations = 0;
  const disposedChannel = createLazyOutputChannel('Disposed VBA Tools', () => {
    disposedCreations += 1;
    return {
      name: 'Disposed VBA Tools',
      append: () => undefined,
      appendLine: () => undefined,
      replace: () => undefined,
      clear: () => undefined,
      show: () => undefined,
      hide: () => undefined,
      dispose: () => undefined
    };
  });
  disposedChannel.dispose();
  disposedChannel.appendLine('late output');
  disposedChannel.show();

  assert.equal(disposedCreations, 0);
});
