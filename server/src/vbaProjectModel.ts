import type { HostDefinition } from './hostDefinition';
import type { HostApplicationSelection } from './officeHostCatalog';
import type { VbaModule } from './vbaSourceModel';

export interface VbaProject {
  modules: VbaModule[];
  hostDefinitions: HostDefinition[];
  hostApplicationSelection: HostApplicationSelection;
}
