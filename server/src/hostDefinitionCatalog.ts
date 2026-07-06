import type {
  CallableParameter,
  CallableSignature,
  HostApplication,
  HostDefinition
} from './hostDefinition';
import { sameName } from './vbaNames';

export const C_SUPPORTED_HOST_APPLICATIONS: readonly HostApplication[] = [
  'excel',
  'word',
  'powerpoint',
  'access'
];

export function cloneHostDefinitions(definitions: HostDefinition[]): HostDefinition[] {
  return definitions.map(cloneHostDefinition);
}

export function cloneHostDefinition(definition: HostDefinition): HostDefinition {
  const clone: HostDefinition = { ...definition };
  if (definition.members !== undefined) {
    clone.members = definition.members.map(cloneHostDefinition);
  }

  return clone;
}

export function cloneHostDefinitionsWithApplication(
  definitions: HostDefinition[],
  hostApplication: HostApplication
): HostDefinition[] {
  return definitions.map((definition) => cloneHostDefinitionWithApplication(definition, hostApplication));
}

export function cloneHostDefinitionWithApplication(
  definition: HostDefinition,
  hostApplication: HostApplication
): HostDefinition {
  const clone: HostDefinition = {
    ...definition,
    hostApplication
  };
  if (definition.members !== undefined) {
    clone.members = definition.members.map((member) =>
      cloneHostDefinitionWithApplication(member, hostApplication)
    );
  }

  return clone;
}

export function mergeHostDefinitions(
  baseDefinitions: HostDefinition[],
  enrichmentDefinitions: HostDefinition[]
): HostDefinition[] {
  const merged_definitions = baseDefinitions.map((definition) =>
    mergeHostDefinition(
      definition,
      enrichmentDefinitions.find((candidate) => sameHostDefinitionName(candidate.name, definition.name))
    )
  );
  const base_names = new Set(baseDefinitions.map((definition) => definition.name.toLowerCase()));
  return [
    ...merged_definitions,
    ...enrichmentDefinitions.filter((definition) => !base_names.has(definition.name.toLowerCase()))
  ];
}

export function getUnqualifiedHostDefinitions(hostDefinitions: HostDefinition[]): HostDefinition[] {
  return hostDefinitions.flatMap((definition) => [
    definition,
    ...getUnqualifiedHostEnumMembers(definition)
  ]);
}

export function selectUnqualifiedHostDefinition(
  matches: HostDefinition[],
  mainHostApplication: HostApplication
): HostDefinition | undefined {
  if (matches.length === 1) {
    return matches[0];
  }

  const main_host_matches = matches.filter((definition) =>
    definition.hostApplication === mainHostApplication
  );
  return singleMatch(main_host_matches);
}

export function selectHostApplicationQualifiedDefinition(
  definitions: HostDefinition[],
  hostApplication: HostApplication,
  name: string
): HostDefinition | undefined {
  return singleMatch(definitions.filter((definition) =>
    definition.hostApplication === hostApplication && sameHostDefinitionName(definition.name, name)
  ));
}

export function withHostMemberContext(parent: HostDefinition, member: HostDefinition): HostDefinition {
  if (parent.kind !== 'enum') {
    return member;
  }

  return {
    ...member,
    hostApplication: member.hostApplication ?? parent.hostApplication,
    parentName: member.parentName ?? parent.name
  };
}

export function stripNullProperties(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(stripNullProperties);
  }

  if (typeof value !== 'object' || value === null) {
    return value;
  }

  return Object.fromEntries(
    Object.entries(value)
      .filter(([, entry_value]) => entry_value !== null)
      .map(([key, entry_value]) => [key, stripNullProperties(entry_value)])
  );
}

export function isHostDefinitionArray(value: unknown): value is HostDefinition[] {
  return Array.isArray(value) && value.every(isHostDefinition);
}

export function isHostDefinition(value: unknown): value is HostDefinition {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<HostDefinition>;
  return typeof candidate.name === 'string'
    && (candidate.kind === undefined || isHostDefinitionKind(candidate.kind))
    && (candidate.hostApplication === undefined || isHostApplication(candidate.hostApplication))
    && (candidate.parentName === undefined || typeof candidate.parentName === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string')
    && (candidate.value === undefined || typeof candidate.value === 'string')
    && (candidate.typeName === undefined || typeof candidate.typeName === 'string')
    && (candidate.signature === undefined || isCallableSignature(candidate.signature))
    && (candidate.members === undefined || isHostDefinitionArray(candidate.members));
}

export function isHostApplication(value: unknown): value is HostApplication {
  return typeof value === 'string'
    && (C_SUPPORTED_HOST_APPLICATIONS as readonly string[]).includes(value);
}

export function sameHostDefinitionName(left: string, right: string): boolean {
  return sameName(left, right);
}

function mergeHostDefinition(
  baseDefinition: HostDefinition,
  enrichmentDefinition: HostDefinition | undefined
): HostDefinition {
  if (enrichmentDefinition === undefined) {
    return cloneHostDefinition(baseDefinition);
  }

  const merged_definition: HostDefinition = {
    ...baseDefinition
  };
  if (enrichmentDefinition.documentation !== undefined) {
    merged_definition.documentation = enrichmentDefinition.documentation;
  }
  if (enrichmentDefinition.typeName !== undefined) {
    merged_definition.typeName = enrichmentDefinition.typeName;
  }
  if (enrichmentDefinition.signature !== undefined) {
    merged_definition.signature = enrichmentDefinition.signature;
  }
  if (baseDefinition.members !== undefined || enrichmentDefinition.members !== undefined) {
    merged_definition.members = mergeHostDefinitions(
      baseDefinition.members ?? [],
      enrichmentDefinition.members ?? []
    );
  }

  return merged_definition;
}

function getUnqualifiedHostEnumMembers(definition: HostDefinition): HostDefinition[] {
  if (definition.kind !== 'enum') {
    return [];
  }

  return (definition.members ?? [])
    .filter((member) => member.kind === 'enumMember' || member.kind === 'constant')
    .map((member) => withHostMemberContext(definition, member));
}

function isCallableSignature(value: unknown): value is CallableSignature {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<CallableSignature>;
  return typeof candidate.label === 'string'
    && Array.isArray(candidate.parameters)
    && candidate.parameters.every(isCallableParameter)
    && (candidate.returnTypeName === undefined || typeof candidate.returnTypeName === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string');
}

function isCallableParameter(value: unknown): value is CallableParameter {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<CallableParameter>;
  return typeof candidate.name === 'string'
    && (candidate.label === undefined || typeof candidate.label === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string')
    && (candidate.optional === undefined || typeof candidate.optional === 'boolean')
    && (candidate.passingMode === undefined || candidate.passingMode === 'ByVal' || candidate.passingMode === 'ByRef')
    && (candidate.isParamArray === undefined || typeof candidate.isParamArray === 'boolean')
    && (candidate.typeName === undefined || typeof candidate.typeName === 'string')
    && (candidate.defaultValue === undefined || typeof candidate.defaultValue === 'string');
}

function isHostDefinitionKind(value: unknown): boolean {
  return value === 'class'
    || value === 'property'
    || value === 'function'
    || value === 'enum'
    || value === 'enumMember'
    || value === 'constant';
}

function singleMatch<T>(items: T[]): T | undefined {
  return items.length === 1 ? items[0] : undefined;
}
