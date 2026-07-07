import { sameRange, type SourceRange } from './sourceRange';
import { sameName } from './vbaNames';
import { sameUri } from './vbaUris';
import type { VbaProject } from './vbaProjectModel';
import type {
  DefinitionLocation,
  VbaDefinition,
  VbaModule
} from './vbaSourceModel';

export function findModule(project: VbaProject, uri: string): VbaModule | undefined {
  return project.modules.find((module) => sameUri(module.uri, uri));
}

export function getModuleIdentities(project: VbaProject): string[] {
  return project.modules.map((module) => module.identity);
}

export function getModuleMemberRanges(project: VbaProject, uri: string): SourceRange[] {
  return findModule(project, uri)?.moduleMembers.map((member) => member.range) ?? [];
}

export function getTypeFields(project: VbaProject, typeName: string): { name: string; range: SourceRange }[] {
  const type_definition = project.modules
    .flatMap((module) => module.definitions)
    .find((definition) => definition.kind === 'type' && sameName(definition.name, typeName));

  return type_definition?.children?.map((field) => ({
    name: field.name,
    range: field.range
  })) ?? [];
}

export function getAllVbaDefinitions(project: VbaProject): VbaDefinition[] {
  return project.modules.flatMap((module) => [
    ...module.definitions,
    ...module.definitions.flatMap((definition) => definition.children ?? []),
    ...module.procedureScopes.flatMap((scope) => scope.definitions)
  ]);
}

export function sameDefinitionLocation(left: DefinitionLocation, right: DefinitionLocation): boolean {
  return sameUri(left.uri, right.uri) && sameRange(left.range, right.range);
}

export function findDefinitionByLocation(
  project: VbaProject,
  location: DefinitionLocation
): VbaDefinition | undefined {
  return getAllVbaDefinitions(project)
    .find((definition) =>
      sameUri(definition.uri, location.uri)
      && sameRange(definition.range, location.range)
    );
}
