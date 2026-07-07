import type { HostApplication, HostDefinition } from './hostDefinition';
import {
  cloneHostDefinitionsWithApplication,
  mergeHostDefinitions
} from './hostDefinitionCatalog';

export type HostCatalogDiscoveryAdapter = (hostApplication: HostApplication) => Promise<HostDefinition[]>;

export interface HostCatalogRefreshAdapters {
  discoverFromCom: HostCatalogDiscoveryAdapter;
  discoverFromTypeLibrary: HostCatalogDiscoveryAdapter;
  getCurrentDefinitions: (hostApplication: HostApplication) => HostDefinition[];
}

export async function refreshHostApplicationCatalog(
  hostApplication: HostApplication,
  adapters: HostCatalogRefreshAdapters
): Promise<HostDefinition[] | undefined> {
  const discovered_definitions = await discoverSafely(adapters.discoverFromCom, hostApplication);
  const type_library_definitions = await discoverSafely(adapters.discoverFromTypeLibrary, hostApplication);
  if (discovered_definitions.length === 0 && type_library_definitions.length === 0) {
    return undefined;
  }

  const base_definitions = discovered_definitions.length === 0
    ? adapters.getCurrentDefinitions(hostApplication)
    : discovered_definitions;
  return cloneHostDefinitionsWithApplication(
    mergeHostDefinitions(base_definitions, type_library_definitions),
    hostApplication
  );
}

async function discoverSafely(
  adapter: HostCatalogDiscoveryAdapter,
  hostApplication: HostApplication
): Promise<HostDefinition[]> {
  try {
    return await adapter(hostApplication);
  } catch {
    return [];
  }
}
