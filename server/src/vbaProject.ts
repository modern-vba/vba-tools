import path from 'node:path';

import { getBundledExcelHostDefinitions } from './excelHostCatalog';

export interface SourcePosition {
  line: number;
  character: number;
}

export interface SourceRange {
  start: SourcePosition;
  end: SourcePosition;
}

export interface VbaProjectFile {
  uri: string;
  text: string;
}

export interface CompletionEntry {
  label: string;
  kind: CompletionEntryKind;
}

export interface CompletionRequest {
  uri: string;
  position: SourcePosition;
}

export type CompletionEntryKind =
  | 'class'
  | 'enum'
  | 'enumMember'
  | 'event'
  | 'function'
  | 'namespace'
  | 'parameter'
  | 'property'
  | 'type'
  | 'variable';

export type HostDefinitionKind = 'class' | 'property' | 'function' | 'enum' | 'enumMember';

export interface HostDefinition {
  name: string;
  kind?: HostDefinitionKind;
  documentation?: string;
  members?: HostDefinition[];
}

export interface BuildVbaProjectOptions {
  hostDefinitions?: HostDefinition[];
}

export interface DefinitionLocation {
  uri: string;
  range: SourceRange;
}

export interface RenameEdit {
  uri: string;
  range: SourceRange;
  newText: string;
}

export interface TextChange {
  range: SourceRange;
  text: string;
}

export interface VbaFormattingOptions {
  tabSize: number;
  insertSpaces: boolean;
}

export type VbaProjectUpdateStrategy = 'moduleMember' | 'fullRebuild';

export interface VbaProjectUpdateResult {
  project: VbaProject;
  strategy: VbaProjectUpdateStrategy;
}

export interface HoverResult {
  contents: string;
}

export interface SignatureHelpResult {
  label: string;
  activeParameter: number;
  documentation?: string;
  parameters: Array<{
    label: string;
    documentation?: string;
  }>;
}

export type SemanticTokenType =
  | 'namespace'
  | 'class'
  | 'function'
  | 'property'
  | 'variable'
  | 'parameter'
  | 'enum'
  | 'enumMember'
  | 'type'
  | 'event';

export interface VbaSemanticToken {
  range: SourceRange;
  tokenType: SemanticTokenType;
}

export const VBA_SEMANTIC_TOKEN_TYPES: SemanticTokenType[] = [
  'namespace',
  'class',
  'function',
  'property',
  'variable',
  'parameter',
  'enum',
  'enumMember',
  'type',
  'event'
];

export type NameResolutionResult =
  | {
      source: 'vba';
      definition: DefinitionLocation;
    }
  | {
      source: 'host';
      definition: HostDefinition;
    };

interface VbaDefinition {
  name: string;
  kind:
    | 'function'
    | 'sub'
    | 'property'
    | 'enum'
    | 'enumMember'
    | 'type'
    | 'typeField'
    | 'event'
    | 'local'
    | 'parameter';
  visibility: 'public' | 'private' | 'local';
  uri: string;
  range: SourceRange;
  children?: VbaDefinition[];
  documentation?: DocumentationComment;
  signature?: SignatureInfo;
  typeName?: string;
}

interface DocumentationComment {
  brief: string[];
  details: string[];
  params: string[];
  returns?: string;
}

interface SignatureInfo {
  label: string;
  parameters: string[];
}

interface ProcedureScope {
  range: SourceRange;
  definitions: VbaDefinition[];
}

interface ModuleMember {
  range: SourceRange;
  definitions: VbaDefinition[];
  procedureScopes: ProcedureScope[];
  withEventsDeclarations: WithEventsDeclaration[];
  implements: string[];
}

interface WithEventsDeclaration {
  name: string;
  typeName: string;
}

type VbaModuleKind = 'standard' | 'class' | 'form';

interface VbaModule {
  uri: string;
  folderUri: string;
  identity: string;
  identityRange?: SourceRange;
  kind: VbaModuleKind;
  codeStartLine: number;
  lines: string[];
  definitions: VbaDefinition[];
  procedureScopes: ProcedureScope[];
  withEventsDeclarations: WithEventsDeclaration[];
  implements: string[];
  moduleMembers: ModuleMember[];
}

export interface VbaProject {
  modules: VbaModule[];
  hostDefinitions: HostDefinition[];
}

const C_IDENTIFIER_PATTERN = /[A-Za-z_][A-Za-z0-9_]*/;

export function buildVbaProject(
  files: VbaProjectFile[],
  options: BuildVbaProjectOptions = {}
): VbaProject {
  const modules = files
    .filter((file) => isVbaSourceUri(file.uri))
    .map((file) => parseModule(file));

  return {
    modules,
    hostDefinitions: options.hostDefinitions ?? getBundledExcelHostDefinitions()
  };
}

export function updateVbaProjectFile(
  project: VbaProject,
  uri: string,
  change: TextChange
): VbaProjectUpdateResult {
  const current_module = findModule(project, uri);
  if (current_module === undefined) {
    return {
      project,
      strategy: 'fullRebuild'
    };
  }

  const containing_member = current_module.moduleMembers.find((member) =>
    containsRange(member.range, change.range)
  );
  const text = applyTextChange(current_module.lines, change);
  const updated_module = parseModule({ uri, text });
  const can_replace_member = containing_member !== undefined
    && updated_module.moduleMembers.some((member) =>
      member.range.start.line === containing_member.range.start.line
    );

  return {
    project: {
      modules: project.modules.map((module) =>
        sameUri(module.uri, uri) ? updated_module : module
      ),
      hostDefinitions: project.hostDefinitions
    },
    strategy: can_replace_member ? 'moduleMember' : 'fullRebuild'
  };
}

export function getCompletions(project: VbaProject, request: CompletionRequest): CompletionEntry[] {
  const current_module = findModule(project, request.uri);
  if (current_module === undefined) {
    return [];
  }

  const member_completion = getMemberCompletionAt(current_module.lines, request.position);
  if (member_completion !== undefined) {
    return getTypedMemberCompletions(project, current_module, request.position, member_completion);
  }

  const prefix = getIdentifierPrefix(current_module.lines, request.position).toLowerCase();
  const project_candidates = project.modules
    .filter((module) => module.folderUri.toLowerCase() === current_module.folderUri.toLowerCase())
    .flatMap((module) => module.definitions)
    .filter((definition) => definition.visibility === 'public')
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .map((definition) => ({
      label: definition.name,
      kind: completionKindForVbaDefinition(definition)
    }));
  const host_candidates = project.hostDefinitions
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .map((definition) => ({
      label: definition.name,
      kind: completionKindForHostDefinition(definition)
    }));

  return uniqueCompletionEntries([...project_candidates, ...host_candidates]);
}

function getTypedMemberCompletions(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  request: { qualifier: string; prefix: string }
): CompletionEntry[] {
  const type_name = findTypeNameForExpression(project, currentModule, position, request.qualifier);
  if (type_name === undefined) {
    return [];
  }

  const prefix = request.prefix.toLowerCase();
  return getMembersForType(project, currentModule, type_name)
    .filter((member) => prefix === '' || member.name.toLowerCase().startsWith(prefix))
    .map((member) => ({
      label: member.name,
      kind: member.kind
    }));
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

export function getHover(project: VbaProject, request: CompletionRequest): HoverResult | undefined {
  const resolution = resolveName(project, request);
  if (resolution === undefined) {
    return undefined;
  }

  if (resolution.source === 'host') {
    return resolution.definition.documentation === undefined
      ? undefined
      : { contents: resolution.definition.documentation };
  }

  const definition = findDefinitionByLocation(project, resolution.definition);
  const documentation = definition === undefined
    ? undefined
    : findDocumentationForDefinition(project, definition);
  if (documentation === undefined) {
    return undefined;
  }

  return {
    contents: renderDocumentationComment(documentation)
  };
}

export function getSemanticTokens(project: VbaProject, uri: string): VbaSemanticToken[] {
  const current_module = findModule(project, uri);
  if (current_module === undefined) {
    return [];
  }

  const tokens: VbaSemanticToken[] = [];
  if (current_module.identityRange !== undefined) {
    tokens.push({
      range: current_module.identityRange,
      tokenType: current_module.kind === 'standard' ? 'namespace' : 'class'
    });
  }

  for (let line_index = current_module.codeStartLine; line_index < current_module.lines.length; line_index += 1) {
    for (const range of getIdentifierRangesInCode(current_module.lines[line_index], line_index)) {
      const resolution = resolveName(project, {
        uri,
        position: range.start
      });
      if (resolution === undefined) {
        continue;
      }

      const token_type = resolution.source === 'host'
        ? semanticTokenTypeForHostDefinition(resolution.definition)
        : semanticTokenTypeForVbaLocation(project, resolution.definition);
      if (token_type === undefined) {
        continue;
      }

      tokens.push({
        range,
        tokenType: token_type
      });
    }
  }

  return uniqueSemanticTokens(tokens).sort(compareSemanticTokens);
}

export function getDocumentFormattingEdits(
  project: VbaProject,
  uri: string,
  options: VbaFormattingOptions
): TextChange[] {
  const current_module = findModule(project, uri);
  if (current_module === undefined) {
    return [];
  }

  const formatted_text = formatModuleText(project, current_module, options);
  const original_text = current_module.lines.join('\n');
  if (formatted_text === original_text) {
    return [];
  }

  return [
    {
      range: {
        start: { line: 0, character: 0 },
        end: {
          line: Math.max(current_module.lines.length - 1, 0),
          character: current_module.lines[current_module.lines.length - 1]?.length ?? 0
        }
      },
      text: formatted_text
    }
  ];
}

function uniqueCompletionEntries(entries: CompletionEntry[]): CompletionEntry[] {
  const seen_names = new Set<string>();
  const unique_entries: CompletionEntry[] = [];

  for (const entry of entries) {
    const key = entry.label.toLowerCase();
    if (seen_names.has(key)) {
      continue;
    }

    seen_names.add(key);
    unique_entries.push(entry);
  }

  return unique_entries;
}

export function getSignatureHelp(
  project: VbaProject,
  request: CompletionRequest
): SignatureHelpResult | undefined {
  const current_module = findModule(project, request.uri);
  if (current_module === undefined) {
    return undefined;
  }

  const call_expression = getCallExpressionAt(current_module.lines, request.position);
  if (call_expression === undefined) {
    return undefined;
  }

  const resolution = resolveName(project, {
    uri: request.uri,
    position: {
      line: request.position.line,
      character: call_expression.nameStart
    }
  });
  if (resolution?.source !== 'vba') {
    return undefined;
  }

  const definition = findDefinitionByLocation(project, resolution.definition);
  if (definition?.signature === undefined) {
    return undefined;
  }

  const documentation = findDocumentationForDefinition(project, definition);
  const parameter_docs = getParameterDocumentation(documentation);
  return {
    label: definition.signature.label,
    activeParameter: Math.min(call_expression.activeParameter, Math.max(definition.signature.parameters.length - 1, 0)),
    documentation: renderSignatureDocumentation(documentation),
    parameters: definition.signature.parameters.map((parameter) => ({
      label: parameter,
      documentation: parameter_docs.get(parameter.toLowerCase())
    }))
  };
}

export function getDefinition(
  project: VbaProject,
  request: CompletionRequest
): DefinitionLocation | undefined {
  const resolution = resolveName(project, request);
  return resolution?.source === 'vba' ? resolution.definition : undefined;
}

export function getRenameTarget(
  project: VbaProject,
  request: CompletionRequest
): DefinitionLocation | undefined {
  const resolution = resolveName(project, request);
  return resolution?.source === 'vba' ? resolution.definition : undefined;
}

export function getRenameEdits(
  project: VbaProject,
  request: CompletionRequest,
  newName: string
): RenameEdit[] {
  if (!isIdentifierName(newName)) {
    return [];
  }

  const target = getRenameTarget(project, request);
  if (target === undefined) {
    return [];
  }

  const target_module = findModule(project, target.uri);
  if (target_module === undefined) {
    return [];
  }

  const edits: RenameEdit[] = [];
  for (const module of project.modules.filter((candidate) =>
    candidate.folderUri.toLowerCase() === target_module.folderUri.toLowerCase()
  )) {
    for (let line_index = 0; line_index < module.lines.length; line_index += 1) {
      for (const range of getIdentifierRangesInCode(module.lines[line_index], line_index)) {
        const resolution = resolveName(project, {
          uri: module.uri,
          position: range.start
        });
        if (resolution?.source === 'vba' && sameDefinitionLocation(resolution.definition, target)) {
          edits.push({
            uri: module.uri,
            range,
            newText: newName
          });
        }
      }
    }
  }

  return edits;
}

export function resolveName(
  project: VbaProject,
  request: CompletionRequest
): NameResolutionResult | undefined {
  const current_module = findModule(project, request.uri);
  if (current_module === undefined) {
    return undefined;
  }

  const identifier = getIdentifierAt(current_module.lines, request.position);
  if (identifier === undefined) {
    return undefined;
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

  const current_module_matches = current_module.definitions.filter((definition) =>
    sameName(definition.name, identifier)
  );
  const current_module_definition = singleMatch(current_module_matches);
  if (current_module_definition !== undefined) {
    return toVbaResolution(current_module_definition);
  }
  if (current_module_matches.length > 1) {
    return undefined;
  }

  const project_matches = project.modules
    .filter((module) => module.folderUri.toLowerCase() === current_module.folderUri.toLowerCase())
    .filter((module) => !sameUri(module.uri, current_module.uri))
    .flatMap((module) => module.definitions)
    .filter((definition) => definition.visibility === 'public')
    .filter((definition) => definition.name.toLowerCase() === identifier.toLowerCase());

  const project_definition = singleMatch(project_matches);
  if (project_definition !== undefined) {
    return toVbaResolution(project_definition);
  }

  const host_matches = project.hostDefinitions.filter((definition) => sameName(definition.name, identifier));
  const host_definition = singleMatch(host_matches);
  if (host_definition !== undefined) {
    return {
      source: 'host',
      definition: host_definition
    };
  }

  return undefined;
}

function isWithEventsHandlerName(module: VbaModule, identifier: string): boolean {
  return module.withEventsDeclarations.some((declaration) =>
    identifier.toLowerCase().startsWith(`${declaration.name}_`.toLowerCase())
  );
}

function resolveWithEventsHandlerDefinition(
  project: VbaProject,
  current_module: VbaModule,
  identifier: string
): VbaDefinition | undefined {
  for (const declaration of current_module.withEventsDeclarations) {
    const handler_prefix = `${declaration.name}_`;
    if (!identifier.toLowerCase().startsWith(handler_prefix.toLowerCase())) {
      continue;
    }

    const event_name = identifier.slice(handler_prefix.length);
    if (event_name === '') {
      continue;
    }

    const event_source_module = project.modules.find((module) =>
      module.folderUri.toLowerCase() === current_module.folderUri.toLowerCase()
        && sameName(module.identity, declaration.typeName)
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

function resolveQualifiedModuleDefinition(
  project: VbaProject,
  current_module: VbaModule,
  qualifier: string,
  member: string
): VbaDefinition | undefined {
  const qualified_module = project.modules.find((module) =>
    module.folderUri.toLowerCase() === current_module.folderUri.toLowerCase()
      && sameName(module.identity, qualifier)
  );
  if (qualified_module === undefined) {
    return undefined;
  }

  const matches = qualified_module.definitions
    .filter((definition) => sameName(definition.name, member))
    .filter((definition) => sameUri(qualified_module.uri, current_module.uri) || definition.visibility === 'public');

  return singleMatch(matches);
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

  const host_type = project.hostDefinitions.find((definition) => sameName(definition.name, type_name));
  const host_member = singleMatch(host_type?.members?.filter((definition) => sameName(definition.name, member)) ?? []);
  if (host_member !== undefined) {
    return {
      source: 'host',
      definition: host_member
    };
  }

  const project_type = project.modules.find((module) =>
    module.folderUri.toLowerCase() === current_module.folderUri.toLowerCase()
      && sameName(module.identity, type_name)
  );
  const project_member = singleMatch(project_type?.definitions
    .filter((definition) => definition.visibility === 'public')
    .filter((definition) => sameName(definition.name, member)) ?? []);
  return project_member === undefined ? undefined : toVbaResolution(project_member);
}

function resolveLocalDefinition(
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

function parseModule(file: VbaProjectFile): VbaModule {
  const lines = file.text.split(/\r?\n/);
  const parsed_identity = parseModuleIdentity(lines);
  const identity = parsed_identity?.name ?? fallbackModuleIdentity(file.uri);
  const code_start_line = getCodeStartLine(file.uri, lines);
  const parsed_members = parseModuleMembers(file.uri, lines, code_start_line);

  return {
    uri: file.uri,
    folderUri: getFolderUri(file.uri),
    identity,
    identityRange: parsed_identity?.range,
    kind: getModuleKind(file.uri),
    codeStartLine: code_start_line,
    lines,
    definitions: parsed_members.definitions,
    procedureScopes: parsed_members.procedureScopes,
    withEventsDeclarations: parsed_members.withEventsDeclarations,
    implements: parsed_members.implements,
    moduleMembers: parsed_members.moduleMembers
  };
}

function parseModuleIdentity(lines: string[]): { name: string; range: SourceRange } | undefined {
  for (let line_index = 0; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const match = /^\s*Attribute\s+VB_Name\s*=\s*"([^"]+)"/i.exec(line);
    if (match !== null) {
      const name = match[1];
      const name_start = line.indexOf('"') + 1;
      return {
        name,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        }
      };
    }
  }

  return undefined;
}

function parseDocumentationComment(lines: string[], member_line: number): DocumentationComment | undefined {
  const comment_lines: string[] = [];

  for (let line_index = member_line - 1; line_index >= 0; line_index -= 1) {
    const match = /^\s*'\*\s?(.*)$/.exec(lines[line_index]);
    if (match === null) {
      break;
    }

    comment_lines.unshift(match[1]);
  }

  if (comment_lines.length === 0) {
    return undefined;
  }

  const documentation: DocumentationComment = {
    brief: [],
    details: [],
    params: []
  };
  let current_section: 'brief' | 'details' | undefined = 'brief';

  for (const line of comment_lines) {
    const brief_match = /^@brief\s*(.*)$/i.exec(line);
    if (brief_match !== null) {
      documentation.brief.push(brief_match[1].trim());
      current_section = 'brief';
      continue;
    }

    const details_match = /^@details\s*(.*)$/i.exec(line);
    if (details_match !== null) {
      documentation.details.push(details_match[1].trim());
      current_section = 'details';
      continue;
    }

    const param_match = /^@param\s+(.+)$/i.exec(line);
    if (param_match !== null) {
      documentation.params.push(param_match[1].trim());
      current_section = undefined;
      continue;
    }

    const return_match = /^@returns?\s+(.+)$/i.exec(line);
    if (return_match !== null) {
      documentation.returns = return_match[1].trim();
      current_section = undefined;
      continue;
    }

    if (line.trim() === '') {
      continue;
    }

    if (current_section === 'details') {
      documentation.details.push(line.trim());
    } else {
      documentation.brief.push(line.trim());
    }
  }

  return documentation;
}

function parseModuleMembers(
  uri: string,
  lines: string[],
  start_line: number
): {
  definitions: VbaDefinition[];
  procedureScopes: ProcedureScope[];
  withEventsDeclarations: WithEventsDeclaration[];
  implements: string[];
  moduleMembers: ModuleMember[];
} {
  const definitions: VbaDefinition[] = [];
  const procedureScopes: ProcedureScope[] = [];
  const withEventsDeclarations: WithEventsDeclaration[] = [];
  const implementedInterfaces: string[] = [];
  const moduleMembers: ModuleMember[] = [];

  for (let line_index = start_line; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const implements_match = /^\s*Implements\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (implements_match !== null) {
      const implemented_interface = implements_match[1];
      implementedInterfaces.push(implemented_interface);
      moduleMembers.push({
        range: createModuleMemberRange(lines, line_index, line_index),
        definitions: [],
        procedureScopes: [],
        withEventsDeclarations: [],
        implements: [implemented_interface]
      });
      continue;
    }

    const with_events_match =
      /^\s*(?:(?:Public|Private|Dim)\s+)?WithEvents\s+([A-Za-z_][A-Za-z0-9_]*)\s+As\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (with_events_match !== null) {
      const declaration = {
        name: with_events_match[1],
        typeName: with_events_match[2]
      };
      withEventsDeclarations.push(declaration);
      moduleMembers.push({
        range: createModuleMemberRange(lines, line_index, line_index),
        definitions: [],
        procedureScopes: [],
        withEventsDeclarations: [declaration],
        implements: []
      });
      continue;
    }

    const event_match = /^\s*(?:(Public|Private)\s+)?Event\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (event_match !== null) {
      const visibility = (event_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = event_match[2];
      const name_start = line.indexOf(name);
      const definition: VbaDefinition = {
        name,
        kind: 'event',
        visibility,
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        documentation: parseDocumentationComment(lines, line_index)
      };
      definitions.push(definition);
      moduleMembers.push({
        range: createModuleMemberRange(lines, line_index, line_index),
        definitions: [definition],
        procedureScopes: [],
        withEventsDeclarations: [],
        implements: []
      });
      continue;
    }

    const enum_match = /^\s*(?:(Public|Private)\s+)?Enum\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (enum_match !== null) {
      const visibility = (enum_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = enum_match[2];
      const name_start = line.indexOf(name);
      const enum_definition: VbaDefinition = {
        name,
        kind: 'enum',
        visibility,
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        documentation: parseDocumentationComment(lines, line_index)
      };

      const end_line_index = findBlockEndLine(lines, line_index + 1, 'enum');
      const enum_member_definitions = parseEnumMemberDefinitions(uri, lines, line_index + 1, end_line_index, visibility);
      const member_definitions = [enum_definition, ...enum_member_definitions];
      definitions.push(...member_definitions);
      moduleMembers.push({
        range: createModuleMemberRange(lines, line_index, end_line_index),
        definitions: member_definitions,
        procedureScopes: [],
        withEventsDeclarations: [],
        implements: []
      });
      line_index = end_line_index;
      continue;
    }

    const type_match = /^\s*(?:(Public|Private)\s+)?Type\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (type_match !== null) {
      const visibility = (type_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = type_match[2];
      const name_start = line.indexOf(name);
      const end_line_index = findBlockEndLine(lines, line_index + 1, 'type');
      const definition: VbaDefinition = {
        name,
        kind: 'type',
        visibility,
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        documentation: parseDocumentationComment(lines, line_index),
        children: parseTypeFieldDefinitions(uri, lines, line_index + 1, end_line_index, visibility)
      };
      definitions.push(definition);
      moduleMembers.push({
        range: createModuleMemberRange(lines, line_index, end_line_index),
        definitions: [definition],
        procedureScopes: [],
        withEventsDeclarations: [],
        implements: []
      });
      line_index = end_line_index;
      continue;
    }

    const procedure_match =
      /^\s*(?:(Public|Private)\s+)?(?:(Sub|Function)|Property\s+(Get|Let|Set))\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(([^)]*)\))?/i.exec(line);
    if (procedure_match === null) {
      continue;
    }

    const visibility = (procedure_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
    const procedure_kind = procedure_match[2] === undefined
      ? 'property'
      : procedure_match[2].toLowerCase() as 'sub' | 'function';
    const end_keyword = procedure_kind === 'property' ? 'property' : procedure_kind;
    const name = procedure_match[4];
    const name_start = line.indexOf(name);
    const parameter_start = line.indexOf('(') + 1;
    const parameter_definitions = procedure_match[5] === undefined
      ? []
      : parseParameterDefinitions(uri, line, line_index, procedure_match[5], parameter_start);
    const definition: VbaDefinition = {
      name,
      kind: procedure_kind,
      visibility,
      uri,
      range: {
        start: { line: line_index, character: name_start },
        end: { line: line_index, character: name_start + name.length }
      },
      documentation: parseDocumentationComment(lines, line_index),
      signature: buildSignatureInfo(line, name, parameter_definitions),
      typeName: parseReturnTypeName(line)
    };
    definitions.push(definition);

    const end_line_index = findProcedureEndLine(lines, line_index + 1, end_keyword);

    const procedure_scope: ProcedureScope = {
      range: {
        start: { line: line_index, character: 0 },
        end: { line: end_line_index, character: lines[end_line_index]?.length ?? 0 }
      },
      definitions: [
        ...parameter_definitions,
        ...parseProcedureDefinitions(uri, lines, line_index + 1, end_line_index)
      ]
    };
    procedureScopes.push(procedure_scope);
    moduleMembers.push({
      range: createModuleMemberRange(lines, line_index, end_line_index),
      definitions: [definition],
      procedureScopes: [procedure_scope],
      withEventsDeclarations: [],
      implements: []
    });
    line_index = end_line_index;
  }

  return {
    definitions,
    procedureScopes,
    withEventsDeclarations,
    implements: implementedInterfaces,
    moduleMembers
  };
}

function createModuleMemberRange(lines: string[], startLine: number, endLine: number): SourceRange {
  return {
    start: { line: startLine, character: 0 },
    end: { line: endLine, character: lines[endLine]?.length ?? 0 }
  };
}

function applyTextChange(lines: string[], change: TextChange): string {
  const text = lines.join('\n');
  const start_offset = getTextOffset(lines, change.range.start);
  const end_offset = getTextOffset(lines, change.range.end);

  return `${text.slice(0, start_offset)}${change.text}${text.slice(end_offset)}`;
}

function getTextOffset(lines: string[], position: SourcePosition): number {
  let offset = 0;
  for (let line_index = 0; line_index < position.line; line_index += 1) {
    offset += (lines[line_index]?.length ?? 0) + 1;
  }

  return offset + position.character;
}

function parseEnumMemberDefinitions(
  uri: string,
  lines: string[],
  start_line: number,
  end_line: number,
  visibility: 'public' | 'private'
): VbaDefinition[] {
  const definitions: VbaDefinition[] = [];

  for (let line_index = start_line; line_index < end_line; line_index += 1) {
    const line = lines[line_index];
    const member_match = /^\s*([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (member_match === null) {
      continue;
    }

    const name = member_match[1];
    const name_start = line.indexOf(name);
    definitions.push({
      name,
      kind: 'enumMember',
      visibility,
      uri,
      range: {
        start: { line: line_index, character: name_start },
        end: { line: line_index, character: name_start + name.length }
      }
    });
  }

  return definitions;
}

function parseTypeFieldDefinitions(
  uri: string,
  lines: string[],
  start_line: number,
  end_line: number,
  visibility: 'public' | 'private'
): VbaDefinition[] {
  const definitions: VbaDefinition[] = [];

  for (let line_index = start_line; line_index < end_line; line_index += 1) {
    const line = lines[line_index];
    const field_match = /^\s*([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (field_match === null) {
      continue;
    }

    const name = field_match[1];
    const name_start = line.indexOf(name);
    definitions.push({
      name,
      kind: 'typeField',
      visibility,
      uri,
      range: {
        start: { line: line_index, character: name_start },
        end: { line: line_index, character: name_start + name.length }
      }
    });
  }

  return definitions;
}

function findBlockEndLine(lines: string[], start_line: number, block_kind: 'enum' | 'type'): number {
  for (let line_index = start_line; line_index < lines.length; line_index += 1) {
    if (new RegExp(`^\\s*End\\s+${block_kind}\\b`, 'i').test(lines[line_index])) {
      return line_index;
    }
  }

  return Math.max(start_line - 1, 0);
}

function getCodeStartLine(uri: string, lines: string[]): number {
  if (!/\.frm$/i.test(uriPathname(uri))) {
    return 0;
  }

  const attribute_line = lines.findIndex((line) => /^\s*Attribute\s+VB_Name\s*=/i.test(line));
  return attribute_line === -1 ? 0 : attribute_line;
}

function parseParameterDefinitions(
  uri: string,
  line: string,
  line_index: number,
  parameter_text: string,
  parameter_start: number
): VbaDefinition[] {
  const definitions: VbaDefinition[] = [];
  let search_offset = 0;

  for (const segment of parameter_text.split(',')) {
    const segment_start = parameter_start + search_offset;
    const trimmed_segment = segment.trimStart();
    const match = /^(?:(?:Optional|ByVal|ByRef|ParamArray)\s+)*([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(trimmed_segment);
    if (match !== null) {
      const name = match[1];
      const name_start = line.indexOf(name, segment_start);
      const type_match = /\bAs\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(trimmed_segment);
      definitions.push({
        name,
        kind: 'parameter',
        visibility: 'local',
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        typeName: type_match?.[1]
      });
    }

    search_offset += segment.length + 1;
  }

  return definitions;
}

function buildSignatureInfo(
  line: string,
  name: string,
  parameterDefinitions: VbaDefinition[]
): SignatureInfo {
  const parameters = parameterDefinitions.map((parameter) => parameter.name);
  const return_type_name = parseReturnTypeName(line);
  const return_suffix = return_type_name === undefined ? '' : ` As ${return_type_name}`;

  return {
    label: `${name}(${parameters.join(', ')})${return_suffix}`,
    parameters
  };
}

function parseReturnTypeName(line: string): string | undefined {
  const return_match = /\)\s+As\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
  return return_match?.[1];
}

function findProcedureEndLine(
  lines: string[],
  start_line: number,
  procedure_kind: 'sub' | 'function' | 'property'
): number {
  for (let line_index = start_line; line_index < lines.length; line_index += 1) {
    if (new RegExp(`^\\s*End\\s+${procedure_kind}\\b`, 'i').test(lines[line_index])) {
      return line_index;
    }
  }

  return Math.max(start_line - 1, 0);
}

function parseProcedureDefinitions(
  uri: string,
  lines: string[],
  start_line: number,
  end_line: number
): VbaDefinition[] {
  const definitions: VbaDefinition[] = [];

  for (let line_index = start_line; line_index < end_line; line_index += 1) {
    const line = lines[line_index];
    const local_match =
      /^\s*Dim\s+([A-Za-z_][A-Za-z0-9_]*)\b(?:\s+As\s+([A-Za-z_][A-Za-z0-9_]*))?/i.exec(line);
    if (local_match === null) {
      continue;
    }

    const name = local_match[1];
    const name_start = line.indexOf(name);
    definitions.push({
      name,
      kind: 'local',
      visibility: 'local',
      uri,
      range: {
        start: { line: line_index, character: name_start },
        end: { line: line_index, character: name_start + name.length }
      },
      typeName: local_match[2]
    });
  }

  return definitions;
}

function getMemberCompletionAt(
  lines: string[],
  position: SourcePosition
): { qualifier: string; prefix: string } | undefined {
  const line = lines[position.line] ?? '';
  const text_before_position = line.slice(0, position.character);
  const match = /([A-Za-z_][A-Za-z0-9_]*(?:\s*\([^()]*\))?)\.\s*([A-Za-z_][A-Za-z0-9_]*)?$/i.exec(text_before_position);
  if (match === null) {
    return undefined;
  }

  return {
    qualifier: match[1],
    prefix: match[2] ?? ''
  };
}

function findTypeNameForExpression(
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
      .filter((module) => module.folderUri.toLowerCase() === currentModule.folderUri.toLowerCase())
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

function getMembersForType(
  project: VbaProject,
  currentModule: VbaModule,
  typeName: string
): { name: string; kind: CompletionEntryKind }[] {
  const host_type = project.hostDefinitions.find((definition) => sameName(definition.name, typeName));
  if (host_type?.members !== undefined) {
    return host_type.members.map((member) => ({
      name: member.name,
      kind: completionKindForHostDefinition(member)
    }));
  }

  const project_type = project.modules.find((module) =>
    module.folderUri.toLowerCase() === currentModule.folderUri.toLowerCase()
      && sameName(module.identity, typeName)
  );
  if (project_type !== undefined) {
    return project_type.definitions
      .filter((definition) => definition.visibility === 'public')
      .map((definition) => ({
        name: definition.name,
        kind: completionKindForVbaDefinition(definition)
      }));
  }

  return [];
}

const C_LANGUAGE_VOCABULARY = new Map<string, string>([
  ['and', 'And'],
  ['as', 'As'],
  ['attribute', 'Attribute'],
  ['base', 'Base'],
  ['byref', 'ByRef'],
  ['byval', 'ByVal'],
  ['byte', 'Byte'],
  ['boolean', 'Boolean'],
  ['call', 'Call'],
  ['case', 'Case'],
  ['const', 'Const'],
  ['currency', 'Currency'],
  ['date', 'Date'],
  ['decimal', 'Decimal'],
  ['dim', 'Dim'],
  ['do', 'Do'],
  ['double', 'Double'],
  ['each', 'Each'],
  ['else', 'Else'],
  ['elseif', 'ElseIf'],
  ['empty', 'Empty'],
  ['end', 'End'],
  ['enum', 'Enum'],
  ['event', 'Event'],
  ['exit', 'Exit'],
  ['explicit', 'Explicit'],
  ['false', 'False'],
  ['for', 'For'],
  ['friend', 'Friend'],
  ['function', 'Function'],
  ['get', 'Get'],
  ['if', 'If'],
  ['implements', 'Implements'],
  ['in', 'In'],
  ['integer', 'Integer'],
  ['let', 'Let'],
  ['long', 'Long'],
  ['longlong', 'LongLong'],
  ['longptr', 'LongPtr'],
  ['loop', 'Loop'],
  ['mod', 'Mod'],
  ['module', 'Module'],
  ['new', 'New'],
  ['next', 'Next'],
  ['not', 'Not'],
  ['nothing', 'Nothing'],
  ['null', 'Null'],
  ['object', 'Object'],
  ['option', 'Option'],
  ['optional', 'Optional'],
  ['or', 'Or'],
  ['paramarray', 'ParamArray'],
  ['private', 'Private'],
  ['property', 'Property'],
  ['ptrsafe', 'PtrSafe'],
  ['public', 'Public'],
  ['raiseevent', 'RaiseEvent'],
  ['select', 'Select'],
  ['set', 'Set'],
  ['single', 'Single'],
  ['static', 'Static'],
  ['string', 'String'],
  ['sub', 'Sub'],
  ['then', 'Then'],
  ['true', 'True'],
  ['type', 'Type'],
  ['variant', 'Variant'],
  ['vb_name', 'VB_Name'],
  ['wend', 'Wend'],
  ['while', 'While'],
  ['with', 'With'],
  ['xor', 'Xor']
]);

function formatModuleText(
  project: VbaProject,
  module: VbaModule,
  options: VbaFormattingOptions
): string {
  const should_indent = hasBalancedFormattingBlocks(module);
  const indent_text = options.insertSpaces ? ' '.repeat(options.tabSize) : '\t';
  let block_depth = 0;

  return module.lines.map((line, line_index) => {
    if (line_index < module.codeStartLine) {
      return line;
    }

    if (line.trim() === '') {
      return '';
    }

    const cased_line = formatLineCasing(project, module, line, line_index);
    if (isHeaderLine(cased_line)) {
      return cased_line.trimStart();
    }

    if (!should_indent) {
      return cased_line;
    }

    const structure_text = getCodeTextForStructure(cased_line).trim();
    if (isClosingBlockLine(structure_text)) {
      block_depth = Math.max(block_depth - 1, 0);
    }

    const line_depth = isMidBlockLine(structure_text)
      ? Math.max(block_depth - 1, 0)
      : block_depth;
    const formatted_line = `${indent_text.repeat(line_depth)}${cased_line.trimStart()}`;

    if (isOpeningBlockLine(structure_text)) {
      block_depth += 1;
    }

    return formatted_line;
  }).join('\n');
}

function formatLineCasing(project: VbaProject, module: VbaModule, line: string, lineIndex: number): string {
  const ranges = getIdentifierRangesInCode(line, lineIndex).reverse();
  let formatted_line = line;

  for (const range of ranges) {
    const original_text = line.slice(range.start.character, range.end.character);
    const language_text = C_LANGUAGE_VOCABULARY.get(original_text.toLowerCase());
    if (language_text !== undefined) {
      formatted_line = replaceRangeText(formatted_line, range, language_text);
      continue;
    }

    if (isDeclarationRange(project, module.uri, range)) {
      continue;
    }

    const resolution = resolveName(project, {
      uri: module.uri,
      position: range.start
    });
    const resolved_text = resolution === undefined
      ? undefined
      : resolution.source === 'host'
        ? resolution.definition.name
        : getDefinitionText(project, resolution.definition);
    if (resolved_text !== undefined) {
      formatted_line = replaceRangeText(formatted_line, range, resolved_text);
    }
  }

  return formatted_line;
}

function replaceRangeText(line: string, range: SourceRange, text: string): string {
  return `${line.slice(0, range.start.character)}${text}${line.slice(range.end.character)}`;
}

function isDeclarationRange(project: VbaProject, uri: string, range: SourceRange): boolean {
  return getAllVbaDefinitions(project).some((definition) =>
    sameUri(definition.uri, uri) && sameRange(definition.range, range)
  );
}

function getDefinitionText(project: VbaProject, location: DefinitionLocation): string | undefined {
  const module = findModule(project, location.uri);
  if (module === undefined || location.range.start.line !== location.range.end.line) {
    return undefined;
  }

  const line = module.lines[location.range.start.line] ?? '';
  return line.slice(location.range.start.character, location.range.end.character);
}

function hasBalancedFormattingBlocks(module: VbaModule): boolean {
  let depth = 0;

  for (let line_index = module.codeStartLine; line_index < module.lines.length; line_index += 1) {
    const structure_text = getCodeTextForStructure(module.lines[line_index]).trim();
    if (structure_text === '' || isCommentOnlyLine(module.lines[line_index]) || isHeaderLine(structure_text)) {
      continue;
    }

    if (isClosingBlockLine(structure_text)) {
      depth -= 1;
      if (depth < 0) {
        return false;
      }
    }
    if (isOpeningBlockLine(structure_text)) {
      depth += 1;
    }
  }

  return depth === 0;
}

function getCodeTextForStructure(line: string): string {
  let result = '';
  let character_index = 0;
  let is_in_string = false;

  while (character_index < line.length) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
        } else {
          is_in_string = false;
          character_index += 1;
        }
      } else {
        character_index += 1;
      }
      continue;
    }

    if (character === "'") {
      break;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }

    result += character;
    character_index += 1;
  }

  return result;
}

function isHeaderLine(line: string): boolean {
  return /^\s*(?:Attribute|Option)\b/i.test(line);
}

function isCommentOnlyLine(line: string): boolean {
  return /^\s*'/.test(line);
}

function isClosingBlockLine(text: string): boolean {
  return /^End\s+(?:Sub|Function|Property|If|Select|With|Enum|Type)\b/i.test(text)
    || /^Next\b/i.test(text)
    || /^Loop\b/i.test(text)
    || /^Wend\b/i.test(text);
}

function isMidBlockLine(text: string): boolean {
  return /^ElseIf\b.*\bThen\b/i.test(text)
    || /^Else\b/i.test(text)
    || /^Case\b/i.test(text);
}

function isOpeningBlockLine(text: string): boolean {
  if (/^ElseIf\b/i.test(text) || /^End\b/i.test(text)) {
    return false;
  }

  return /^(?:(?:Public|Private|Friend)\s+)?(?:Sub|Function|Property\s+(?:Get|Let|Set))\b/i.test(text)
    || /^If\b.*\bThen\s*$/i.test(text)
    || /^For\b/i.test(text)
    || /^Do\b/i.test(text)
    || /^While\b/i.test(text)
    || /^Select\s+Case\b/i.test(text)
    || /^With\b/i.test(text)
    || /^(?:(?:Public|Private)\s+)?Enum\b/i.test(text)
    || /^(?:(?:Public|Private)\s+)?Type\b/i.test(text);
}

function findModule(project: VbaProject, uri: string): VbaModule | undefined {
  return project.modules.find((module) => sameUri(module.uri, uri));
}

function getIdentifierPrefix(lines: string[], position: SourcePosition): string {
  const line = lines[position.line] ?? '';
  const text_before_position = line.slice(0, position.character);
  const match = new RegExp(`${C_IDENTIFIER_PATTERN.source}$`).exec(text_before_position);

  return match?.[0] ?? '';
}

function getIdentifierAt(lines: string[], position: SourcePosition): string | undefined {
  const line = lines[position.line] ?? '';
  const identifier_pattern = new RegExp(C_IDENTIFIER_PATTERN.source, 'g');

  for (const match of line.matchAll(identifier_pattern)) {
    const start = match.index;
    const end = start + match[0].length;
    if (position.character >= start && position.character <= end) {
      return match[0];
    }
  }

  return undefined;
}

function getIdentifierRangesInCode(line: string, lineIndex: number): SourceRange[] {
  const ranges: SourceRange[] = [];
  let character_index = 0;
  let is_in_string = false;

  while (character_index < line.length) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
        } else {
          is_in_string = false;
          character_index += 1;
        }
      } else {
        character_index += 1;
      }
      continue;
    }

    if (character === "'") {
      break;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }

    if (isIdentifierStart(character)) {
      const start = character_index;
      character_index += 1;
      while (character_index < line.length && isIdentifierPart(line[character_index])) {
        character_index += 1;
      }
      ranges.push({
        start: { line: lineIndex, character: start },
        end: { line: lineIndex, character: character_index }
      });
      continue;
    }

    character_index += 1;
  }

  return ranges;
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

    const qualifier_match = /([A-Za-z_][A-Za-z0-9_]*)\.\s*$/.exec(line.slice(0, start));
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

function getCallExpressionAt(
  lines: string[],
  position: SourcePosition
): { name: string; nameStart: number; activeParameter: number } | undefined {
  const line = lines[position.line] ?? '';
  const text_before_position = line.slice(0, position.character);
  const open_paren = text_before_position.lastIndexOf('(');
  if (open_paren === -1) {
    return undefined;
  }

  const before_paren = text_before_position.slice(0, open_paren);
  const match = /(?:\bCall\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\.)?([A-Za-z_][A-Za-z0-9_]*)\s*$/i.exec(before_paren);
  if (match === null) {
    return undefined;
  }

  const name = match[1];
  return {
    name,
    nameStart: before_paren.lastIndexOf(name),
    activeParameter: countCommas(text_before_position.slice(open_paren + 1))
  };
}

function countCommas(text: string): number {
  return [...text].filter((character) => character === ',').length;
}

function isVbaSourceUri(uri: string): boolean {
  return /\.(bas|cls|frm)$/i.test(uriPathname(uri));
}

function fallbackModuleIdentity(uri: string): string {
  const parsed_path = uriPathname(uri);
  const file_name = path.posix.basename(parsed_path);
  const extension = path.posix.extname(file_name);

  return file_name.slice(0, file_name.length - extension.length);
}

function getFolderUri(uri: string): string {
  const parsed_path = uriPathname(uri);
  const folder_path = path.posix.dirname(parsed_path);

  return `file://${folder_path}`;
}

function uriPathname(uri: string): string {
  if (uri.startsWith('file://')) {
    return new URL(uri).pathname;
  }

  return uri;
}

function sameUri(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function sameName(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function sameDefinitionLocation(left: DefinitionLocation, right: DefinitionLocation): boolean {
  return sameUri(left.uri, right.uri)
    && sameRange(left.range, right.range);
}

function sameRange(left: SourceRange, right: SourceRange): boolean {
  return comparePosition(left.start, right.start) === 0
    && comparePosition(left.end, right.end) === 0;
}

function isIdentifierName(value: string): boolean {
  return new RegExp(`^${C_IDENTIFIER_PATTERN.source}$`).test(value);
}

function isIdentifierStart(character: string): boolean {
  return /[A-Za-z_]/.test(character);
}

function isIdentifierPart(character: string): boolean {
  return /[A-Za-z0-9_]/.test(character);
}

function singleMatch<T>(items: T[]): T | undefined {
  return items.length === 1 ? items[0] : undefined;
}

function findDefinitionByLocation(
  project: VbaProject,
  location: DefinitionLocation
): VbaDefinition | undefined {
  return getAllVbaDefinitions(project)
    .find((definition) =>
      sameUri(definition.uri, location.uri)
        && comparePosition(definition.range.start, location.range.start) === 0
        && comparePosition(definition.range.end, location.range.end) === 0
    );
}

function getAllVbaDefinitions(project: VbaProject): VbaDefinition[] {
  return project.modules.flatMap((module) => [
    ...module.definitions,
    ...module.definitions.flatMap((definition) => definition.children ?? []),
    ...module.procedureScopes.flatMap((scope) => scope.definitions)
  ]);
}

function findDocumentationForDefinition(
  project: VbaProject,
  definition: VbaDefinition
): DocumentationComment | undefined {
  if (definition.documentation !== undefined) {
    return definition.documentation;
  }

  const owner_module = findModule(project, definition.uri);
  if (owner_module === undefined) {
    return undefined;
  }

  for (const interface_name of owner_module.implements) {
    const handler_prefix = `${interface_name}_`;
    if (!definition.name.toLowerCase().startsWith(handler_prefix.toLowerCase())) {
      continue;
    }

    const member_name = definition.name.slice(handler_prefix.length);
    const interface_module = project.modules.find((module) =>
      module.folderUri.toLowerCase() === owner_module.folderUri.toLowerCase()
        && sameName(module.identity, interface_name)
    );
    const interface_definition = interface_module?.definitions.find((candidate) =>
      sameName(candidate.name, member_name)
    );
    if (interface_definition?.documentation !== undefined) {
      return interface_definition.documentation;
    }
  }

  return undefined;
}

function renderDocumentationComment(documentation: DocumentationComment): string {
  const sections: string[] = [];
  const brief = documentation.brief.join(' ').trim();
  const details = documentation.details.join(' ').trim();

  if (brief !== '') {
    sections.push(brief);
  }
  if (details !== '') {
    sections.push(details);
  }
  if (documentation.params.length > 0 || documentation.returns !== undefined) {
    const tags = [
      ...documentation.params.map((param) => `@param ${param}`),
      ...(documentation.returns === undefined ? [] : [`@return ${documentation.returns}`])
    ];
    sections.push(tags.join('\n'));
  }

  return sections.join('\n\n');
}

function renderSignatureDocumentation(documentation: DocumentationComment | undefined): string | undefined {
  if (documentation === undefined) {
    return undefined;
  }

  const sections: string[] = [];
  const brief = documentation.brief.join(' ').trim();
  if (brief !== '') {
    sections.push(brief);
  }
  if (documentation.returns !== undefined) {
    sections.push(`@return ${documentation.returns}`);
  }

  return sections.length === 0 ? undefined : sections.join('\n\n');
}

function getParameterDocumentation(documentation: DocumentationComment | undefined): Map<string, string> {
  const result = new Map<string, string>();
  if (documentation === undefined) {
    return result;
  }

  for (const parameter of documentation.params) {
    const match = /^([A-Za-z_][A-Za-z0-9_]*)\s+(.+)$/.exec(parameter);
    if (match !== null) {
      result.set(match[1].toLowerCase(), match[2]);
    }
  }

  return result;
}

function toDefinitionLocation(definition: VbaDefinition): DefinitionLocation {
  return {
    uri: definition.uri,
    range: definition.range
  };
}

function toVbaResolution(definition: VbaDefinition): NameResolutionResult {
  return {
    source: 'vba',
    definition: toDefinitionLocation(definition)
  };
}

function containsPosition(range: SourceRange, position: SourcePosition): boolean {
  return comparePosition(range.start, position) <= 0 && comparePosition(position, range.end) <= 0;
}

function containsRange(outerRange: SourceRange, innerRange: SourceRange): boolean {
  return containsPosition(outerRange, innerRange.start) && containsPosition(outerRange, innerRange.end);
}

function comparePosition(left: SourcePosition, right: SourcePosition): number {
  if (left.line !== right.line) {
    return left.line - right.line;
  }

  return left.character - right.character;
}

function getModuleKind(uri: string): VbaModuleKind {
  if (/\.bas$/i.test(uriPathname(uri))) {
    return 'standard';
  }
  if (/\.frm$/i.test(uriPathname(uri))) {
    return 'form';
  }

  return 'class';
}

function completionKindForVbaDefinition(definition: VbaDefinition): CompletionEntryKind {
  const token_type = semanticTokenTypeForVbaDefinition(definition);
  return token_type === undefined ? 'variable' : token_type;
}

function completionKindForHostDefinition(definition: HostDefinition): CompletionEntryKind {
  return semanticTokenTypeForHostDefinition(definition);
}

function semanticTokenTypeForVbaLocation(
  project: VbaProject,
  location: DefinitionLocation
): SemanticTokenType | undefined {
  const definition = findDefinitionByLocation(project, location);
  return definition === undefined ? undefined : semanticTokenTypeForVbaDefinition(definition);
}

function semanticTokenTypeForVbaDefinition(definition: VbaDefinition): SemanticTokenType | undefined {
  switch (definition.kind) {
    case 'sub':
    case 'function':
      return 'function';
    case 'property':
    case 'typeField':
      return 'property';
    case 'local':
      return 'variable';
    case 'parameter':
      return 'parameter';
    case 'enum':
      return 'enum';
    case 'enumMember':
      return 'enumMember';
    case 'type':
      return 'type';
    case 'event':
      return 'event';
    default:
      return undefined;
  }
}

function semanticTokenTypeForHostDefinition(definition: HostDefinition): SemanticTokenType {
  switch (definition.kind) {
    case 'function':
      return 'function';
    case 'property':
      return 'property';
    case 'enum':
      return 'enum';
    case 'enumMember':
      return 'enumMember';
    case 'class':
      return 'class';
    default:
      return definition.members === undefined ? 'property' : 'class';
  }
}

function uniqueSemanticTokens(tokens: VbaSemanticToken[]): VbaSemanticToken[] {
  const seen_ranges = new Set<string>();
  const unique_tokens: VbaSemanticToken[] = [];

  for (const token of tokens) {
    const key = [
      token.range.start.line,
      token.range.start.character,
      token.range.end.line,
      token.range.end.character,
      token.tokenType
    ].join(':');
    if (seen_ranges.has(key)) {
      continue;
    }

    seen_ranges.add(key);
    unique_tokens.push(token);
  }

  return unique_tokens;
}

function compareSemanticTokens(left: VbaSemanticToken, right: VbaSemanticToken): number {
  return comparePosition(left.range.start, right.range.start)
    || comparePosition(left.range.end, right.range.end)
    || left.tokenType.localeCompare(right.tokenType);
}
