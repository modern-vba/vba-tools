import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';

import { HostCatalogManager } from './hostCatalogService';
import type { HostApplication, HostDefinition } from './vbaProject';

test('host catalog manager uses isolated cache files per HostApplication', () => {
  const cache_directory = path.join('cache-root', 'host-catalogs');
  const cache_paths = new Map<HostApplication, string>();
  const manager = new HostCatalogManager({
    platform: 'linux',
    cacheDirectory: cache_directory,
    readCache: (hostApplication, cachePath) => {
      cache_paths.set(hostApplication, cachePath);
      return [{ name: `Cached${hostApplication}` }];
    }
  });

  assert.deepEqual(
    manager.getDefinitions({
      mainHostApplication: 'excel',
      additionalHostApplications: ['word', 'powerpoint', 'access']
    }).map((definition) => ({ name: definition.name, hostApplication: definition.hostApplication })),
    [
      { name: 'Cachedexcel', hostApplication: 'excel' },
      { name: 'Cachedword', hostApplication: 'word' },
      { name: 'Cachedpowerpoint', hostApplication: 'powerpoint' },
      { name: 'Cachedaccess', hostApplication: 'access' }
    ]
  );
  assert.deepEqual(Object.fromEntries(cache_paths), {
    excel: path.join(cache_directory, 'excel.json'),
    word: path.join(cache_directory, 'word.json'),
    powerpoint: path.join(cache_directory, 'powerpoint.json'),
    access: path.join(cache_directory, 'access.json')
  });
});

test('COM refresh runs only for selected HostApplications on Windows', async () => {
  const discovery_calls: HostApplication[] = [];
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: () => undefined,
    discoverFromCom: async (hostApplication) => {
      discovery_calls.push(hostApplication);
      return [{ name: `Com${hostApplication}` }];
    },
    discoverFromTypeLibrary: async () => []
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({
    mainHostApplication: 'word',
    additionalHostApplications: ['access']
  });

  assert.deepEqual(discovery_calls, ['word', 'access']);
  assert.deepEqual(
    manager.getDefinitions({
      mainHostApplication: 'word',
      additionalHostApplications: ['access']
    }).map((definition) => ({ name: definition.name, hostApplication: definition.hostApplication })),
    [
      { name: 'Comword', hostApplication: 'word' },
      { name: 'Comaccess', hostApplication: 'access' }
    ]
  );
  assert.equal(
    manager.getDefinitions({ mainHostApplication: 'excel' }).some((definition) => definition.name === 'Comexcel'),
    false
  );
});

test('selected COM refresh keeps cached definitions available while discovery is pending', async () => {
  let resolve_discovery: (definitions: HostDefinition[]) => void = () => {};
  const pending_discovery = new Promise<HostDefinition[]>((resolve) => {
    resolve_discovery = resolve;
  });
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: (hostApplication) =>
      hostApplication === 'excel' ? [{ name: 'CachedExcel' }] : undefined,
    discoverFromCom: async () => pending_discovery,
    discoverFromTypeLibrary: async () => []
  });

  const refresh = manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'excel' });

  assert.deepEqual(
    manager.getDefinitions({ mainHostApplication: 'excel' }),
    [{ name: 'CachedExcel', hostApplication: 'excel' }]
  );

  resolve_discovery([{ name: 'ComExcel' }]);
  await refresh;

  assert.deepEqual(
    manager.getDefinitions({ mainHostApplication: 'excel' }),
    [{ name: 'ComExcel', hostApplication: 'excel' }]
  );
});

test('successful selected COM refresh writes only the selected host cache', async () => {
  const cache_directory = path.join('cache-root', 'host-catalogs');
  const writes: Array<{
    hostApplication: HostApplication;
    cachePath: string;
    definitions: HostDefinition[];
  }> = [];
  const manager = new HostCatalogManager({
    platform: 'win32',
    cacheDirectory: cache_directory,
    readCache: (hostApplication) =>
      hostApplication === 'excel' ? [{ name: 'CachedExcel' }] : undefined,
    writeCache: (hostApplication, cachePath, definitions) => {
      writes.push({ hostApplication, cachePath, definitions });
    },
    discoverFromCom: async (hostApplication) => [{ name: `Discovered${hostApplication}` }],
    discoverFromTypeLibrary: async () => []
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'word' });

  assert.deepEqual(writes, [
    {
      hostApplication: 'word',
      cachePath: path.join(cache_directory, 'word.json'),
      definitions: [{ name: 'Discoveredword', hostApplication: 'word' }]
    }
  ]);
  assert.deepEqual(
    manager.getDefinitions({ mainHostApplication: 'word' }),
    [{ name: 'Discoveredword', hostApplication: 'word' }]
  );
  assert.deepEqual(
    manager.getDefinitions({ mainHostApplication: 'excel' }),
    [{ name: 'CachedExcel', hostApplication: 'excel' }]
  );
});

test('selected refresh enriches host catalogs with Type Library metadata', async () => {
  let written_definitions: HostDefinition[] | undefined;
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: () => undefined,
    writeCache: (_hostApplication, _cachePath, definitions) => {
      written_definitions = definitions;
    },
    discoverFromCom: async () => [
      {
        name: 'Range',
        kind: 'class',
        members: [
          { name: 'Find', kind: 'function' },
          { name: 'Clear', kind: 'function' }
        ]
      }
    ],
    discoverFromTypeLibrary: async () => [
      {
        name: 'Range',
        kind: 'class',
        members: [
          {
            name: 'Find',
            kind: 'function',
            typeName: 'Range',
            signature: {
              label: 'Find(What) As Range',
              returnTypeName: 'Range',
              parameters: [{ name: 'What', typeName: 'Variant' }]
            }
          }
        ]
      },
      {
        name: 'XlDirection',
        kind: 'enum',
        members: [
          { name: 'xlUp', kind: 'enumMember', value: '-4162' }
        ]
      },
      {
        name: 'xlCalculationManual',
        kind: 'constant',
        value: '-4135'
      }
    ]
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'excel' });

  assert.deepEqual(manager.getDefinitions({ mainHostApplication: 'excel' }), [
    {
      name: 'Range',
      kind: 'class',
      hostApplication: 'excel',
      members: [
        {
          name: 'Find',
          kind: 'function',
          hostApplication: 'excel',
          typeName: 'Range',
          signature: {
            label: 'Find(What) As Range',
            returnTypeName: 'Range',
            parameters: [{ name: 'What', typeName: 'Variant' }]
          }
        },
        { name: 'Clear', kind: 'function', hostApplication: 'excel' }
      ]
    },
    {
      name: 'XlDirection',
      kind: 'enum',
      hostApplication: 'excel',
      members: [
        { name: 'xlUp', kind: 'enumMember', value: '-4162', hostApplication: 'excel' }
      ]
    },
    {
      name: 'xlCalculationManual',
      kind: 'constant',
      value: '-4135',
      hostApplication: 'excel'
    }
  ]);
  assert.deepEqual(written_definitions, manager.getDefinitions({ mainHostApplication: 'excel' }));
});

test('selected refresh caches Type Library metadata when COM discovery is empty', async () => {
  let written_definitions: HostDefinition[] | undefined;
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: () => [],
    writeCache: (_hostApplication, _cachePath, definitions) => {
      written_definitions = definitions;
    },
    discoverFromCom: async () => [],
    discoverFromTypeLibrary: async () => [
      {
        name: 'XlDirection',
        kind: 'enum',
        members: [
          { name: 'xlUp', kind: 'enumMember', value: '-4162' }
        ]
      },
      {
        name: 'xlCalculationManual',
        kind: 'constant',
        value: '-4135'
      }
    ]
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'excel' });

  assert.deepEqual(manager.getDefinitions({ mainHostApplication: 'excel' }), [
    {
      name: 'XlDirection',
      kind: 'enum',
      hostApplication: 'excel',
      members: [
        { name: 'xlUp', kind: 'enumMember', value: '-4162', hostApplication: 'excel' }
      ]
    },
    {
      name: 'xlCalculationManual',
      kind: 'constant',
      value: '-4135',
      hostApplication: 'excel'
    }
  ]);
  assert.deepEqual(written_definitions, manager.getDefinitions({ mainHostApplication: 'excel' }));
});

test('selected refresh preserves cached object members when only Type Library metadata is discovered', async () => {
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: (hostApplication) =>
      hostApplication === 'excel'
        ? [
            {
              name: 'Range',
              kind: 'class',
              members: [
                { name: 'Clear', kind: 'function' }
              ]
            }
          ]
        : undefined,
    discoverFromCom: async () => [],
    discoverFromTypeLibrary: async () => [
      {
        name: 'XlDirection',
        kind: 'enum',
        members: [
          { name: 'xlUp', kind: 'enumMember', value: '-4162' }
        ]
      }
    ]
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'excel' });

  assert.deepEqual(manager.getDefinitions({ mainHostApplication: 'excel' }), [
    {
      name: 'Range',
      kind: 'class',
      hostApplication: 'excel',
      members: [
        { name: 'Clear', kind: 'function', hostApplication: 'excel' }
      ]
    },
    {
      name: 'XlDirection',
      kind: 'enum',
      hostApplication: 'excel',
      members: [
        { name: 'xlUp', kind: 'enumMember', value: '-4162', hostApplication: 'excel' }
      ]
    }
  ]);
});

test('COM refresh is skipped outside Windows and preserves bundled fallback', async () => {
  const discovery_calls: HostApplication[] = [];
  const manager = new HostCatalogManager({
    platform: 'linux',
    readCache: () => undefined,
    discoverFromCom: async (hostApplication) => {
      discovery_calls.push(hostApplication);
      return [{ name: `Com${hostApplication}` }];
    }
  });

  const definitions_before_refresh = manager.getDefinitions({
    mainHostApplication: 'powerpoint',
    additionalHostApplications: ['access']
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({
    mainHostApplication: 'powerpoint',
    additionalHostApplications: ['access']
  });

  assert.deepEqual(discovery_calls, []);
  assert.deepEqual(
    manager.getDefinitions({
      mainHostApplication: 'powerpoint',
      additionalHostApplications: ['access']
    }),
    definitions_before_refresh
  );
  assert.ok(definitions_before_refresh.some((definition) => definition.hostApplication === 'powerpoint'));
  assert.ok(definitions_before_refresh.some((definition) => definition.hostApplication === 'access'));
});

test('COM refresh failure preserves cached definitions without surfacing an error', async () => {
  let write_called = false;
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: (hostApplication) =>
      hostApplication === 'word' ? [{ name: 'CachedWord' }] : undefined,
    writeCache: () => {
      write_called = true;
    },
    discoverFromCom: async () => {
      throw new Error('Office is not installed');
    },
    discoverFromTypeLibrary: async () => []
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'word' });

  assert.equal(write_called, false);
  assert.deepEqual(
    manager.getDefinitions({ mainHostApplication: 'word' }),
    [{ name: 'CachedWord', hostApplication: 'word' }]
  );
});

test('Type Library discovery failure preserves COM-discovered definitions without surfacing an error', async () => {
  let write_called = false;
  const manager = new HostCatalogManager({
    platform: 'win32',
    readCache: () => undefined,
    writeCache: () => {
      write_called = true;
    },
    discoverFromCom: async () => [
      {
        name: 'Range',
        kind: 'class',
        members: [
          { name: 'Clear', kind: 'function' }
        ]
      }
    ],
    discoverFromTypeLibrary: async () => {
      throw new Error('Type library is unavailable');
    }
  });

  await manager.refreshSelectedHostApplicationsFromComAsync({ mainHostApplication: 'excel' });

  assert.equal(write_called, true);
  assert.deepEqual(manager.getDefinitions({ mainHostApplication: 'excel' }), [
    {
      name: 'Range',
      kind: 'class',
      hostApplication: 'excel',
      members: [
        { name: 'Clear', kind: 'function', hostApplication: 'excel' }
      ]
    }
  ]);
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
