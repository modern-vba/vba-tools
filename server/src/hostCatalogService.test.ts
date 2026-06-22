import test from 'node:test';
import assert from 'node:assert/strict';

import { HostCatalogManager } from './hostCatalogService';
import type { HostDefinition } from './vbaProject';

test('cached host catalog is available immediately and COM refresh takes precedence asynchronously', async () => {
  const cached_definitions: HostDefinition[] = [
    { name: 'CachedApplication', documentation: 'cached metadata' }
  ];
  const com_definitions: HostDefinition[] = [
    { name: 'ComApplication', documentation: 'COM metadata' }
  ];
  let written_cache: HostDefinition[] | undefined;

  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: () => cached_definitions,
    writeCache: (definitions) => {
      written_cache = definitions;
    },
    discoverFromCom: async () => com_definitions
  });

  assert.deepEqual(manager.getDefinitions(), cached_definitions);

  const refresh = manager.refreshFromExcelComAsync();

  assert.deepEqual(manager.getDefinitions(), cached_definitions);

  await refresh;

  assert.deepEqual(manager.getDefinitions(), com_definitions);
  assert.deepEqual(written_cache, com_definitions);
});

test('COM refresh is skipped outside Windows and preserves bundled fallback', async () => {
  let com_discovery_called = false;
  const manager = new HostCatalogManager({
    platform: 'linux',
    readCache: () => undefined,
    discoverFromCom: async () => {
      com_discovery_called = true;
      return [{ name: 'ComApplication' }];
    }
  });

  const definitions_before_refresh = manager.getDefinitions();

  await manager.refreshFromExcelComAsync();

  assert.equal(com_discovery_called, false);
  assert.deepEqual(manager.getDefinitions(), definitions_before_refresh);
  assert.ok(manager.getDefinitions().some((definition) => definition.name === 'Application'));
});

test('COM refresh failure preserves cached definitions without breaking completion data', async () => {
  const cached_definitions: HostDefinition[] = [
    { name: 'CachedRange', documentation: 'cached metadata' }
  ];
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: () => cached_definitions,
    discoverFromCom: async () => {
      throw new Error('Excel is not installed');
    }
  });

  await manager.refreshFromExcelComAsync();

  assert.deepEqual(manager.getDefinitions(), cached_definitions);
});

test('host catalog manager resolves definitions for the selected HostApplication', () => {
  const manager = new HostCatalogManager({
    platform: 'linux',
    readCache: () => undefined
  });

  assert.deepEqual(
    manager.getDefinitions({ mainHostApplication: 'word' }).map((definition) => definition.name),
    ['Application', 'Document', 'Range', 'Selection']
  );
});

test('host catalog manager reflects HostApplication selection changes without recreation', () => {
  const manager = new HostCatalogManager({
    platform: 'linux',
    readCache: () => undefined
  });

  assert.equal(
    manager.getDefinitions({ additionalHostApplications: ['word'] }).some((definition) => definition.name === 'Document'),
    true
  );
  assert.equal(
    manager.getDefinitions({ additionalHostApplications: [] }).some((definition) => definition.name === 'Document'),
    false
  );
});
