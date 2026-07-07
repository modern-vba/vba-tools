import {
  getUnqualifiedHostDefinitions,
  selectUnqualifiedHostDefinition
} from './hostDefinitionCatalog';
import {
  resolveHostQualifiedDefinition,
  resolveHostQualifiedPath
} from './hostDefinitionLookup';
import {
  parseContinuedMemberChainEndingBefore,
  parseMemberChainEndingBefore
} from './memberChainSyntax';
import { resolveMemberChainTarget } from './memberChainResolution';
import type { SourcePosition } from './sourceRange';
import {
  hasAmbiguousCurrentModuleDefinition,
  isWithEventsHandlerName,
  resolveCurrentModuleDefinition,
  resolveLocalDefinition,
  resolveProjectPublicDefinition,
  resolveQualifiedModuleDefinition,
  resolveWithEventsHandlerDefinition
} from './sourceDefinitionLookup';
import type { VbaProject } from './vbaProjectModel';
import type {
  MemberChainExpression,
  NameResolutionResult,
  VbaModule
} from './vbaSourceModel';
import { singleMatch, toVbaResolution } from './vbaResolution';
import { C_IDENTIFIER_PATTERN } from './vbaText';
import { getIdentifierAt, getIdentifierRangesInCode } from './vbaIdentifierSource';
import { sameName } from './vbaNames';
import { sameRange } from './sourceRange';
import { sameUri } from './vbaUris';
import { shouldSuppressNameResolutionAt } from './syntaxAnalysis';

export interface NameResolutionRequest {
  uri: string;
  position: SourcePosition;
}

export function resolveName(
  project: VbaProject,
  request: NameResolutionRequest
): NameResolutionResult | undefined {
  const current_module = findModule(project, request.uri);
  if (current_module === undefined) {
    return undefined;
  }
  if (shouldSuppressNameResolutionAt(current_module, request.position)) {
    return undefined;
  }

  const identifier = getIdentifierAt(current_module.lines, request.position);
  if (identifier === undefined) {
    return undefined;
  }

  const member_chain = getMemberChainExpressionAt(current_module.lines, request.position);
  if (
    member_chain !== undefined
    && (member_chain.segments.length > 1 || member_chain.usesWithReceiver === true)
  ) {
    return resolveMemberChainTarget(project, current_module, request.position, member_chain);
  }

  const qualified_reference = getQualifiedReferenceAt(current_module.lines, request.position);
  if (qualified_reference !== undefined) {
    const qualified_definition = resolveQualifiedModuleDefinition(
      project,
      current_module,
      qualified_reference.qualifier,
      qualified_reference.member
    );
    if (qualified_definition !== undefined) {
      return toVbaResolution(qualified_definition);
    }

    const host_qualified_definition = resolveHostQualifiedDefinition(
      project,
      current_module,
      qualified_reference.qualifier,
      qualified_reference.member
    );
    if (host_qualified_definition !== undefined) {
      return {
        source: 'host',
        definition: host_qualified_definition
      };
    }

    return resolveTypedMemberDefinition(
      project,
      current_module,
      request.position,
      qualified_reference.qualifier,
      qualified_reference.member
    );
  }

  const local_definition = resolveLocalDefinition(current_module, request.position, identifier);
  if (local_definition !== undefined) {
    return toVbaResolution(local_definition);
  }

  const event_handler_definition = resolveWithEventsHandlerDefinition(project, current_module, identifier);
  if (event_handler_definition !== undefined) {
    return toVbaResolution(event_handler_definition);
  }
  if (isWithEventsHandlerName(current_module, identifier)) {
    return undefined;
  }

  const current_module_definition = resolveCurrentModuleDefinition(current_module, identifier);
  if (current_module_definition !== undefined) {
    return toVbaResolution(current_module_definition);
  }
  if (hasAmbiguousCurrentModuleDefinition(current_module, identifier)) {
    return undefined;
  }

  const project_definition = resolveProjectPublicDefinition(project, current_module, identifier);
  if (project_definition !== undefined) {
    return toVbaResolution(project_definition);
  }

  const host_matches = getUnqualifiedHostDefinitions(project.hostDefinitions).filter((definition) =>
    sameName(definition.name, identifier)
  );
  const host_definition = selectUnqualifiedHostDefinition(
    host_matches,
    project.hostApplicationSelection.mainHostApplication
  );
  if (host_definition !== undefined) {
    return {
      source: 'host',
      definition: host_definition
    };
  }

  return undefined;
}

function resolveTypedMemberDefinition(
  project: VbaProject,
  current_module: VbaModule,
  position: SourcePosition,
  qualifier: string,
  member: string
): NameResolutionResult | undefined {
  const type_name = findTypeNameForExpression(project, current_module, position, qualifier);
  if (type_name === undefined) {
    return undefined;
  }

  const host_type = resolveHostQualifiedPath(project, current_module, type_name)
    ?? selectUnqualifiedHostDefinition(
      project.hostDefinitions.filter((definition) => sameName(definition.name, type_name)),
      project.hostApplicationSelection.mainHostApplication
    );
  const host_member = singleMatch(host_type?.members?.filter((definition) => sameName(definition.name, member)) ?? []);
  if (host_member !== undefined) {
    return {
      source: 'host',
      definition: host_member
    };
  }

  const project_type = project.modules.find((module) =>
    sameUri(module.folderUri, current_module.folderUri)
      && sameName(module.identity, type_name)
  );
  const project_member = singleMatch(project_type?.definitions
    .filter((definition) => definition.visibility === 'public')
    .filter((definition) => sameName(definition.name, member)) ?? []);
  return project_member === undefined ? undefined : toVbaResolution(project_member);
}

export function findTypeNameForExpression(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  expression: string
): string | undefined {
  const trimmed_expression = expression.trim();
  const call_match = /^([A-Za-z_][A-Za-z0-9_]*)\s*\([^()]*\)$/i.exec(trimmed_expression);
  const identifier_match = /^([A-Za-z_][A-Za-z0-9_]*)$/i.exec(trimmed_expression);
  const identifier = call_match?.[1] ?? identifier_match?.[1];
  if (identifier === undefined) {
    return undefined;
  }

  if (call_match === null) {
    const local_type_name = resolveLocalDefinition(currentModule, position, identifier)?.typeName;
    if (local_type_name !== undefined) {
      return local_type_name;
    }
  }

  const current_module_type_name = singleMatch(
    currentModule.definitions
      .filter((definition) => sameName(definition.name, identifier))
      .filter((definition) => definition.typeName !== undefined)
  )?.typeName;
  if (current_module_type_name !== undefined) {
    return current_module_type_name;
  }

  const project_type_name = singleMatch(
    project.modules
      .filter((module) => sameUri(module.folderUri, currentModule.folderUri))
      .filter((module) => !sameUri(module.uri, currentModule.uri))
      .flatMap((module) => module.definitions)
      .filter((definition) => definition.visibility === 'public')
      .filter((definition) => sameName(definition.name, identifier))
      .filter((definition) => definition.typeName !== undefined)
  )?.typeName;
  if (project_type_name !== undefined) {
    return project_type_name;
  }

  return undefined;
}

function getMemberChainExpressionAt(
  lines: string[],
  position: SourcePosition
): MemberChainExpression | undefined {
  const line = lines[position.line] ?? '';
  const identifier_range = getIdentifierRangesInCode(line, position.line).find((range) =>
    position.character >= range.start.character && position.character <= range.end.character
  );
  if (identifier_range === undefined) {
    return undefined;
  }

  const chain = parseContinuedMemberChainEndingBefore(lines, position.line, identifier_range.end.character)
    ?? parseMemberChainEndingBefore(line, position.line, identifier_range.end.character);
  if (chain === undefined) {
    return undefined;
  }

  const target_segment_index = chain.segments.findIndex((segment) =>
    sameRange(segment.range, identifier_range)
  );
  return target_segment_index === -1
    ? undefined
    : {
        segments: chain.segments,
        targetSegmentIndex: target_segment_index,
        usesWithReceiver: chain.usesWithReceiver
      };
}

function getQualifiedReferenceAt(
  lines: string[],
  position: SourcePosition
): { qualifier: string; member: string } | undefined {
  const line = lines[position.line] ?? '';
  const identifier_pattern = new RegExp(C_IDENTIFIER_PATTERN.source, 'g');

  for (const match of line.matchAll(identifier_pattern)) {
    const start = match.index;
    const end = start + match[0].length;
    if (position.character < start || position.character > end) {
      continue;
    }

    const qualifier_match = /([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\.\s*$/.exec(line.slice(0, start));
    if (qualifier_match === null) {
      return undefined;
    }

    return {
      qualifier: qualifier_match[1],
      member: match[0]
    };
  }

  return undefined;
}

function findModule(project: VbaProject, uri: string): VbaModule | undefined {
  return project.modules.find((module) => sameUri(module.uri, uri));
}
