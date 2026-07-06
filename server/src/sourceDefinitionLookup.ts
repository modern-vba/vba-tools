import { containsPosition, type SourcePosition } from './sourceRange';
import { sameName, unqualifiedTypeName } from './vbaNames';
import { sameUri } from './vbaUris';
import type { VbaProject } from './vbaProjectModel';
import type {
  VbaDefinition,
  VbaModule
} from './vbaSourceModel';
import { singleMatch } from './vbaResolution';

export function resolveLocalDefinition(
  module: VbaModule,
  position: SourcePosition,
  identifier: string
): VbaDefinition | undefined {
  const procedure_scope = module.procedureScopes.find((scope) => containsPosition(scope.range, position));
  if (procedure_scope === undefined) {
    return undefined;
  }

  return procedure_scope.definitions.find((definition) => sameName(definition.name, identifier));
}

export function resolveCurrentModuleDefinition(
  module: VbaModule,
  identifier: string
): VbaDefinition | undefined {
  return singleMatch(module.definitions.filter((definition) => sameName(definition.name, identifier)));
}

export function resolveCurrentModuleEventDefinition(
  module: VbaModule,
  identifier: string
): VbaDefinition | undefined {
  return singleMatch(module.definitions
    .filter((definition) => definition.kind === 'event')
    .filter((definition) => sameName(definition.name, identifier)));
}

export function resolveProjectPublicDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  identifier: string
): VbaDefinition | undefined {
  return singleMatch(getProjectPublicDefinitionMatches(project, currentModule, identifier));
}

export function hasAmbiguousCurrentModuleDefinition(module: VbaModule, identifier: string): boolean {
  return module.definitions.filter((definition) => sameName(definition.name, identifier)).length > 1;
}

export function resolveWithEventsHandlerDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  identifier: string
): VbaDefinition | undefined {
  for (const declaration of currentModule.withEventsDeclarations) {
    const handler_prefix = `${declaration.name}_`;
    if (!identifier.toLowerCase().startsWith(handler_prefix.toLowerCase())) {
      continue;
    }

    const event_name = identifier.slice(handler_prefix.length);
    if (event_name === '') {
      continue;
    }

    const declared_type_name = unqualifiedTypeName(declaration.typeName);
    const event_source_module = project.modules.find((module) =>
      sameUri(module.folderUri, currentModule.folderUri)
        && sameName(module.identity, declared_type_name)
    );
    if (event_source_module === undefined) {
      continue;
    }

    const matches = event_source_module.definitions
      .filter((definition) => definition.kind === 'event')
      .filter((definition) => definition.visibility === 'public')
      .filter((definition) => sameName(definition.name, event_name));

    const event_definition = singleMatch(matches);
    if (event_definition !== undefined) {
      return event_definition;
    }
  }

  return undefined;
}

export function isWithEventsHandlerName(module: VbaModule, identifier: string): boolean {
  return module.withEventsDeclarations.some((declaration) =>
    identifier.toLowerCase().startsWith(`${declaration.name}_`.toLowerCase())
  );
}

export function resolveQualifiedModuleDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string,
  member: string
): VbaDefinition | undefined {
  const qualified_module = project.modules.find((module) =>
    sameUri(module.folderUri, currentModule.folderUri)
      && sameName(module.identity, qualifier)
  );
  if (qualified_module === undefined) {
    return undefined;
  }

  const matches = qualified_module.definitions
    .filter((definition) => sameName(definition.name, member))
    .filter((definition) => sameUri(qualified_module.uri, currentModule.uri) || definition.visibility === 'public');

  return singleMatch(matches);
}

export function hasSourceQualifierName(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  name: string
): boolean {
  if (resolveLocalDefinition(currentModule, position, name) !== undefined) {
    return true;
  }

  if (currentModule.definitions.some((definition) => sameName(definition.name, name))) {
    return true;
  }

  return project.modules
    .filter((module) => sameUri(module.folderUri, currentModule.folderUri))
    .some((module) =>
      sameName(module.identity, name)
        || module.definitions
          .filter((definition) => sameUri(module.uri, currentModule.uri) || definition.visibility === 'public')
          .some((definition) => sameName(definition.name, name))
    );
}

export function findSourceTypeModule(
  project: VbaProject,
  currentModule: VbaModule,
  typeName: string
): VbaModule | undefined {
  if (typeName.includes('.')) {
    return undefined;
  }

  return project.modules.find((module) =>
    sameUri(module.folderUri, currentModule.folderUri)
      && sameName(module.identity, typeName)
  );
}

function getProjectPublicDefinitionMatches(
  project: VbaProject,
  currentModule: VbaModule,
  identifier: string
): VbaDefinition[] {
  return project.modules
    .filter((module) => sameUri(module.folderUri, currentModule.folderUri))
    .filter((module) => !sameUri(module.uri, currentModule.uri))
    .flatMap((module) => module.definitions)
    .filter((definition) => definition.visibility === 'public')
    .filter((definition) => sameName(definition.name, identifier));
}
