import test from 'node:test';
import assert from 'node:assert/strict';

import { IntrinsicHostEventCatalogStatusObserver } from './intrinsicHostEventCatalogStatus';

test('environment catalog status is hidden when healthy and warns when unavailable', () => {
  const views: unknown[] = [];
  const output: string[] = [];
  const observer = new IntrinsicHostEventCatalogStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: (line) => output.push(line)
  });

  observer.observe({
    kind: 'started',
    trigger: 'activation',
    revision: 0
  });
  observer.observe({
    kind: 'committed',
    trigger: 'activation',
    revision: 1,
    eventCount: 15
  });
  observer.observe({
    kind: 'unavailable',
    trigger: 'explicitRefresh',
    revision: 2,
    message: 'Excel unavailable',
    catalogRetained: true
  });

  assert.deepEqual(views, [
    {
      visible: true,
      text: '$(sync~spin) VBA UserForm Events',
      tooltip: 'Acquiring the environment UserForm Event catalog...',
      command: 'vbaTools.userFormEvents.showOutput'
    },
    {
      visible: false,
      text: '',
      tooltip: '',
      command: 'vbaTools.userFormEvents.showOutput'
    },
    {
      visible: true,
      text: '$(warning) VBA UserForm Events',
      tooltip: 'UserForm Event catalog refresh failed; the current catalog was retained: Excel unavailable',
      command: 'vbaTools.userFormEvents.showOutput'
    }
  ]);
  assert.equal(output.length, 3);
  assert.match(output[0], /^\[user-form-events\] /u);
});

test('cancelling a retry preserves pre-existing unavailable attention', () => {
  const views: Array<{ tooltip: string }> = [];
  const observer = new IntrinsicHostEventCatalogStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });
  observer.observe({
    kind: 'unavailable',
    trigger: 'activation',
    revision: 1,
    message: 'Excel unavailable'
  });
  observer.observe({
    kind: 'started',
    trigger: 'explicitRefresh',
    revision: 1
  });
  observer.observe({
    kind: 'cancelled',
    trigger: 'explicitRefresh',
    revision: 1
  });

  assert.equal(
    views.at(-1)?.tooltip,
    'UserForm Event catalog unavailable: Excel unavailable'
  );
});
