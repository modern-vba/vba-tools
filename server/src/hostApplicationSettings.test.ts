import test from 'node:test';
import assert from 'node:assert/strict';

import { HostApplicationConfigurationProvider } from './hostApplicationSettings';

test('HostApplication configuration defaults to Excel for a resource', async () => {
  const provider = new HostApplicationConfigurationProvider(async () => undefined);

  assert.deepEqual(await provider.getOptions('file:///project/Book.bas'), {
    mainHostApplication: 'excel',
    additionalHostApplications: []
  });
});

test('HostApplication configuration is resolved per resource URI', async () => {
  const settings = new Map<string, unknown>([
    ['file:///excel/Book.bas', { mainHostApplication: 'excel', additionalHostApplications: [] }],
    ['file:///word/Document.bas', { mainHostApplication: 'word', additionalHostApplications: [] }]
  ]);
  const provider = new HostApplicationConfigurationProvider(async (scopeUri) => settings.get(scopeUri));

  assert.deepEqual(await provider.getOptions('file:///excel/Book.bas'), {
    mainHostApplication: 'excel',
    additionalHostApplications: []
  });
  assert.deepEqual(await provider.getOptions('file:///word/Document.bas'), {
    mainHostApplication: 'word',
    additionalHostApplications: []
  });
});

test('HostApplication configuration changes are visible without recreating the provider', async () => {
  const settings = new Map<string, unknown>([
    ['file:///project/Module.bas', { mainHostApplication: 'excel', additionalHostApplications: [] }]
  ]);
  const provider = new HostApplicationConfigurationProvider(async (scopeUri) => settings.get(scopeUri));

  assert.deepEqual(await provider.getOptions('file:///project/Module.bas'), {
    mainHostApplication: 'excel',
    additionalHostApplications: []
  });

  settings.set('file:///project/Module.bas', {
    mainHostApplication: 'word',
    additionalHostApplications: ['excel']
  });

  assert.deepEqual(await provider.getOptions('file:///project/Module.bas'), {
    mainHostApplication: 'word',
    additionalHostApplications: ['excel']
  });
});
