import { C_DEFAULT_MAIN_HOST_APPLICATION, type HostApplicationSelectionOptions } from './officeHostCatalog';
import type { HostApplication } from './hostDefinition';

export type HostApplicationConfigurationReader = (scopeUri: string) => unknown | Promise<unknown>;

export class HostApplicationConfigurationProvider {
  private readonly readConfiguration: HostApplicationConfigurationReader;

  public constructor(readConfiguration: HostApplicationConfigurationReader) {
    this.readConfiguration = readConfiguration;
  }

  public async getOptions(scopeUri: string): Promise<Required<HostApplicationSelectionOptions>> {
    return normalizeHostApplicationConfiguration(await this.readConfiguration(scopeUri));
  }
}

export function normalizeHostApplicationConfiguration(value: unknown): Required<HostApplicationSelectionOptions> {
  const configuration = isObject(value) ? value : {};
  const main_host_application = isHostApplication(configuration.mainHostApplication)
    ? configuration.mainHostApplication
    : C_DEFAULT_MAIN_HOST_APPLICATION;
  const additional_host_applications = Array.isArray(configuration.additionalHostApplications)
    ? configuration.additionalHostApplications.filter(isHostApplication)
    : [];

  return {
    mainHostApplication: main_host_application,
    additionalHostApplications: additional_host_applications
  };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isHostApplication(value: unknown): value is HostApplication {
  return value === 'excel'
    || value === 'word'
    || value === 'powerpoint'
    || value === 'access';
}
