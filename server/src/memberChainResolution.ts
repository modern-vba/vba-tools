import {
  getUnqualifiedHostDefinitions,
  selectHostApplicationQualifiedDefinition,
  selectUnqualifiedHostDefinition,
  withHostMemberContext
} from './hostDefinitionCatalog';
import { resolveHostApplicationQualifier } from './hostDefinitionLookup';
import { containsPosition, type SourcePosition } from './sourceRange';
import {
  findSourceTypeModule,
  hasSourceQualifierName,
  resolveCurrentModuleDefinition,
  resolveLocalDefinition,
  resolveProjectPublicDefinition,
  resolveQualifiedModuleDefinition
} from './sourceDefinitionLookup';
import {
  findHostTypeDefinition,
  resolveTypeNameRef,
  typeRefForHostDefinition,
  typeRefForVbaDefinition
} from './typeResolution';
import { sameName } from './vbaNames';
import type { VbaProject } from './vbaProjectModel';
import type {
  MemberChainExpression,
  MemberChainSegment,
  NameResolutionResult,
  ResolvedChainSegment,
  TypeResolutionRef,
  VbaModule
} from './vbaSourceModel';
import { singleMatch, toVbaResolution } from './vbaResolution';
import { getCodeTextForStructure } from './vbaText';
import { getWithReceiverDeclarationAt } from './withReceiverSyntax';

export function resolveMemberChainReceiverType(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  chain: MemberChainExpression
): TypeResolutionRef | undefined {
  const resolved_segments = resolveMemberChain(project, currentModule, position, chain);
  return resolved_segments?.at(-1)?.typeRef;
}

export function resolveMemberChainTarget(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  chain: MemberChainExpression
): NameResolutionResult | undefined {
  const resolved_segments = resolveMemberChain(project, currentModule, position, chain);
  return resolved_segments?.at(-1)?.resolution;
}

export function resolveActiveWithReceiverType(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition
): TypeResolutionRef | undefined {
  const current_member = currentModule.moduleMembers.find((member) =>
    containsPosition(member.range, position)
  );
  if (current_member === undefined) {
    return undefined;
  }

  const receiver_stack: Array<TypeResolutionRef | undefined> = [];
  for (let line_index = current_member.range.start.line; line_index < position.line;) {
    const line = currentModule.lines[line_index] ?? '';
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '') {
      line_index += 1;
      continue;
    }

    if (/^End\s+With\b/i.test(structure_text)) {
      receiver_stack.pop();
      line_index += 1;
      continue;
    }

    if (/^With\b/i.test(structure_text)) {
      const receiver_declaration = getWithReceiverDeclarationAt(currentModule.lines, line_index);
      if (receiver_declaration === undefined) {
        line_index += 1;
        continue;
      }
      if (receiver_declaration.end.line >= position.line) {
        break;
      }

      const receiver_chain = receiver_declaration.chain;
      const receiver_type = receiver_chain === undefined
        ? undefined
        : resolveMemberChainReceiverTypeWithActiveReceiver(
          project,
          currentModule,
          receiver_declaration.end,
          receiver_chain,
          receiver_stack.at(-1)
        );
      receiver_stack.push(receiver_type);
      line_index = receiver_declaration.end.line + 1;
      continue;
    }

    line_index += 1;
  }

  return receiver_stack.at(-1);
}

function resolveMemberChainReceiverTypeWithActiveReceiver(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  chain: MemberChainExpression,
  activeWithReceiverType: TypeResolutionRef | undefined
): TypeResolutionRef | undefined {
  const resolved_segments = resolveMemberChain(project, currentModule, position, chain, {
    activeWithReceiverType,
    useActiveWithReceiverType: true
  });
  return resolved_segments?.at(-1)?.typeRef;
}

function resolveMemberChain(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  chain: MemberChainExpression,
  options: {
    activeWithReceiverType?: TypeResolutionRef;
    useActiveWithReceiverType?: boolean;
  } = {}
): ResolvedChainSegment[] | undefined {
  const resolved_segments: ResolvedChainSegment[] = [];
  const segments = chain.segments.slice(0, chain.targetSegmentIndex + 1);
  if (segments.length === 0) {
    return undefined;
  }

  let current_type_ref: TypeResolutionRef | undefined;
  let segment_index = 0;

  if (chain.usesWithReceiver === true) {
    current_type_ref = options.useActiveWithReceiverType === true
      ? options.activeWithReceiverType
      : resolveActiveWithReceiverType(project, currentModule, position);
    if (current_type_ref === undefined) {
      return undefined;
    }
  }

  if (chain.usesWithReceiver !== true && segments.length > 1) {
    const source_qualified_member = resolveQualifiedModuleDefinition(
      project,
      currentModule,
      segments[0].name,
      segments[1].name
    );
    if (source_qualified_member !== undefined) {
      current_type_ref = typeRefForVbaDefinition(project, currentModule, source_qualified_member, segments[1].hasCall, false);
      resolved_segments.push({ resolution: toVbaResolution(source_qualified_member), typeRef: current_type_ref });
      segment_index = 2;
    } else {
      const host_application = resolveHostApplicationQualifier(project, currentModule, segments[0].name);
      const host_root = host_application === undefined
        ? undefined
        : selectHostApplicationQualifiedDefinition(
          getUnqualifiedHostDefinitions(project.hostDefinitions),
          host_application,
          segments[1].name
        );
      if (host_root !== undefined) {
        current_type_ref = typeRefForHostDefinition(host_root, segments[1].hasCall);
        resolved_segments.push({ resolution: { source: 'host', definition: host_root }, typeRef: current_type_ref });
        segment_index = 2;
      }
    }
  }

  if (chain.usesWithReceiver !== true && segment_index === 0) {
    const root_segment = resolveRootChainSegment(project, currentModule, position, segments[0]);
    if (root_segment === undefined) {
      return undefined;
    }

    current_type_ref = root_segment.typeRef;
    resolved_segments.push(root_segment);
    segment_index = 1;
  }

  while (segment_index < segments.length) {
    if (current_type_ref === undefined) {
      return undefined;
    }

    const member_segment = resolveMemberOnType(project, currentModule, current_type_ref, segments[segment_index]);
    if (member_segment === undefined) {
      return undefined;
    }

    current_type_ref = member_segment.typeRef;
    resolved_segments.push(member_segment);
    segment_index += 1;
  }

  return resolved_segments;
}

function resolveRootChainSegment(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  segment: MemberChainSegment
): ResolvedChainSegment | undefined {
  if (sameName(segment.name, 'Me')) {
    if (currentModule.kind === 'standard') {
      return undefined;
    }

    return {
      typeRef: {
        source: 'vba',
        typeName: currentModule.identity,
        allowPrivate: true
      }
    };
  }

  if (!segment.hasCall) {
    const local_definition = resolveLocalDefinition(currentModule, position, segment.name);
    if (local_definition?.typeName !== undefined) {
      return {
        resolution: toVbaResolution(local_definition),
        typeRef: resolveTypeNameRef(project, currentModule, local_definition.typeName, false)
      };
    }
  }

  const current_module_definition = resolveCurrentModuleDefinition(currentModule, segment.name);
  if (current_module_definition !== undefined) {
    return {
      resolution: toVbaResolution(current_module_definition),
      typeRef: typeRefForVbaDefinition(project, currentModule, current_module_definition, segment.hasCall, true)
    };
  }

  const project_definition = resolveProjectPublicDefinition(project, currentModule, segment.name);
  if (project_definition !== undefined) {
    return {
      resolution: toVbaResolution(project_definition),
      typeRef: typeRefForVbaDefinition(project, currentModule, project_definition, segment.hasCall, false)
    };
  }

  if (hasSourceQualifierName(project, currentModule, position, segment.name)) {
    return undefined;
  }

  const host_definition = selectUnqualifiedHostDefinition(
    project.hostDefinitions.filter((definition) => sameName(definition.name, segment.name)),
    project.hostApplicationSelection.mainHostApplication
  );
  if (host_definition !== undefined) {
    return {
      resolution: { source: 'host', definition: host_definition },
      typeRef: typeRefForHostDefinition(host_definition, segment.hasCall)
    };
  }

  return undefined;
}

function resolveMemberOnType(
  project: VbaProject,
  currentModule: VbaModule,
  typeRef: TypeResolutionRef,
  segment: MemberChainSegment
): ResolvedChainSegment | undefined {
  if (typeRef.source === 'host') {
    const host_type = findHostTypeDefinition(project, currentModule, typeRef);
    if (host_type === undefined) {
      return undefined;
    }

    const host_member = singleMatch(host_type.members?.filter((definition) =>
      sameName(definition.name, segment.name)
    ) ?? []);
    return host_member === undefined
      ? undefined
      : {
          resolution: { source: 'host', definition: withHostMemberContext(host_type, host_member) },
          typeRef: typeRefForHostDefinition(host_member, segment.hasCall)
        };
  }

  const project_type = findSourceTypeModule(project, currentModule, typeRef.typeName);
  const project_member = singleMatch(project_type?.definitions
    .filter((definition) => typeRef.allowPrivate || definition.visibility === 'public')
    .filter((definition) => sameName(definition.name, segment.name)) ?? []);
  return project_member === undefined
    ? undefined
    : {
        resolution: toVbaResolution(project_member),
        typeRef: typeRefForVbaDefinition(project, currentModule, project_member, segment.hasCall, false)
      };
}
