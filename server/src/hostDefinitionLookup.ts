import type {
  HostApplication,
  HostDefinition
} from './hostDefinition';
import {
  getUnqualifiedHostDefinitions,
  selectHostApplicationQualifiedDefinition,
  selectUnqualifiedHostDefinition,
  withHostMemberContext
} from './hostDefinitionCatalog';
import { formatHostApplicationName } from './officeHostCatalog';
import { type SourcePosition } from './sourceRange';
import { hasSourceQualifierName } from './sourceDefinitionLookup';
import { sameName } from './vbaNames';
import { sameUri } from './vbaUris';
import type { VbaProject } from './vbaProjectModel';
import type { VbaModule } from './vbaSourceModel';
import { singleMatch } from './vbaResolution';

export function resolveHostQualifiedDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string,
  member: string
): HostDefinition | undefined {
  const host_path_definition = resolveHostQualifiedPath(project, currentModule, qualifier);
  if (host_path_definition !== undefined) {
    const host_member = singleMatch(host_path_definition.members?.filter((definition) =>
      sameName(definition.name, member)
    ) ?? []);
    return host_member === undefined ? undefined : withHostMemberContext(host_path_definition, host_member);
  }

  const host_application = resolveHostApplicationQualifier(project, currentModule, qualifier);
  if (host_application === undefined) {
    return undefined;
  }

  return selectHostApplicationQualifiedDefinition(
    getUnqualifiedHostDefinitions(project.hostDefinitions),
    host_application,
    member
  );
}

export function resolveHostQualifiedPath(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string
): HostDefinition | undefined {
  const parts = qualifier.split('.');
  if (parts.length !== 2) {
    return undefined;
  }

  const host_application = resolveHostApplicationQualifier(project, currentModule, parts[0]);
  if (host_application === undefined) {
    return undefined;
  }

  return singleMatch(project.hostDefinitions.filter((definition) =>
    definition.hostApplication === host_application && sameName(definition.name, parts[1])
  ));
}

export function resolveUnqualifiedHostEnumQualifier(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  qualifier: string
): HostDefinition | undefined {
  if (qualifier.includes('.') || hasSourceQualifierName(project, currentModule, position, qualifier)) {
    return undefined;
  }

  const host_definition = selectUnqualifiedHostDefinition(
    project.hostDefinitions.filter((definition) =>
      definition.kind === 'enum' && sameName(definition.name, qualifier)
    ),
    project.hostApplicationSelection.mainHostApplication
  );
  return host_definition?.kind === 'enum' ? host_definition : undefined;
}

export function resolveHostApplicationQualifier(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string
): HostApplication | undefined {
  if (qualifier.includes('.')) {
    return undefined;
  }

  const source_module = project.modules.find((module) =>
    sameUri(module.folderUri, currentModule.folderUri)
      && sameName(module.identity, qualifier)
  );
  if (source_module !== undefined) {
    return undefined;
  }

  return project.hostApplicationSelection.enabledHostApplications.find((hostApplication) =>
    sameName(hostApplication, qualifier) || sameName(formatHostApplicationName(hostApplication), qualifier)
  );
}
