import type { HostDefinition } from './hostDefinition';
import { selectUnqualifiedHostDefinition } from './hostDefinitionCatalog';
import { resolveHostQualifiedPath } from './hostDefinitionLookup';
import { findSourceTypeModule } from './sourceDefinitionLookup';
import { sameName } from './vbaNames';
import type { VbaProject } from './vbaProjectModel';
import type {
  TypeResolutionRef,
  VbaDefinition,
  VbaModule
} from './vbaSourceModel';
import { singleMatch } from './vbaResolution';

export function typeRefForVbaDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  definition: VbaDefinition,
  hasCall: boolean,
  allowPrivate: boolean
): TypeResolutionRef | undefined {
  if (hasCall && definition.signature?.returnTypeName !== undefined) {
    return resolveTypeNameRef(project, currentModule, definition.signature.returnTypeName, allowPrivate);
  }
  if (definition.typeName !== undefined) {
    return resolveTypeNameRef(project, currentModule, definition.typeName, allowPrivate);
  }

  return undefined;
}

export function typeRefForHostDefinition(
  definition: HostDefinition,
  hasCall: boolean
): TypeResolutionRef | undefined {
  const type_name = hasCall
    ? definition.signature?.returnTypeName ?? definition.typeName
    : definition.typeName ?? (definition.members === undefined ? undefined : definition.name);
  return type_name === undefined
    ? undefined
    : {
        source: 'host',
        typeName: type_name,
        hostApplication: definition.hostApplication
      };
}

export function resolveTypeNameRef(
  project: VbaProject,
  currentModule: VbaModule,
  typeName: string,
  allowPrivate: boolean
): TypeResolutionRef | undefined {
  const host_type = resolveHostQualifiedPath(project, currentModule, typeName);
  if (host_type !== undefined) {
    return {
      source: 'host',
      typeName: host_type.name,
      hostApplication: host_type.hostApplication
    };
  }

  const source_type = findSourceTypeModule(project, currentModule, typeName);
  if (source_type !== undefined) {
    return {
      source: 'vba',
      typeName: source_type.identity,
      allowPrivate
    };
  }

  const unqualified_host_type = selectUnqualifiedHostDefinition(
    project.hostDefinitions.filter((definition) => sameName(definition.name, typeName)),
    project.hostApplicationSelection.mainHostApplication
  );
  return unqualified_host_type === undefined
    ? undefined
    : {
        source: 'host',
        typeName: unqualified_host_type.name,
        hostApplication: unqualified_host_type.hostApplication
      };
}

export function findHostTypeDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  typeRef: Extract<TypeResolutionRef, { source: 'host' }>
): HostDefinition | undefined {
  const host_qualified_type = resolveHostQualifiedPath(project, currentModule, typeRef.typeName);
  if (host_qualified_type !== undefined) {
    return host_qualified_type;
  }

  if (typeRef.hostApplication !== undefined) {
    return singleMatch(project.hostDefinitions.filter((definition) =>
      definition.hostApplication === typeRef.hostApplication && sameName(definition.name, typeRef.typeName)
    ));
  }

  return selectUnqualifiedHostDefinition(
    project.hostDefinitions.filter((definition) => sameName(definition.name, typeRef.typeName)),
    project.hostApplicationSelection.mainHostApplication
  );
}
