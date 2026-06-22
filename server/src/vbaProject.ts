import path from 'node:path';

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
}

export interface CompletionRequest {
  uri: string;
  position: SourcePosition;
}

export interface HostDefinition {
  name: string;
}

export interface BuildVbaProjectOptions {
  hostDefinitions?: HostDefinition[];
}

export interface DefinitionLocation {
  uri: string;
  range: SourceRange;
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

interface WithEventsDeclaration {
  name: string;
  typeName: string;
}

interface VbaModule {
  uri: string;
  folderUri: string;
  identity: string;
  lines: string[];
  definitions: VbaDefinition[];
  procedureScopes: ProcedureScope[];
  withEventsDeclarations: WithEventsDeclaration[];
  implements: string[];
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
    hostDefinitions: options.hostDefinitions ?? []
  };
}

export function getCompletions(project: VbaProject, request: CompletionRequest): CompletionEntry[] {
  const current_module = findModule(project, request.uri);
  if (current_module === undefined) {
    return [];
  }

  const prefix = getIdentifierPrefix(current_module.lines, request.position).toLowerCase();
  const candidates = project.modules
    .filter((module) => module.folderUri.toLowerCase() === current_module.folderUri.toLowerCase())
    .flatMap((module) => module.definitions)
    .filter((definition) => definition.visibility === 'public')
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .map((definition) => ({ label: definition.name }));

  return candidates;
}

export function getModuleIdentities(project: VbaProject): string[] {
  return project.modules.map((module) => module.identity);
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
  const definition_location = getDefinition(project, request);
  if (definition_location === undefined) {
    return undefined;
  }

  const definition = findDefinitionByLocation(project, definition_location);
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

    return undefined;
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
  const identity = parseModuleIdentity(lines) ?? fallbackModuleIdentity(file.uri);
  const parsed_members = parseModuleMembers(file.uri, lines, getCodeStartLine(file.uri, lines));

  return {
    uri: file.uri,
    folderUri: getFolderUri(file.uri),
    identity,
    lines,
    definitions: parsed_members.definitions,
    procedureScopes: parsed_members.procedureScopes,
    withEventsDeclarations: parsed_members.withEventsDeclarations,
    implements: parsed_members.implements
  };
}

function parseModuleIdentity(lines: string[]): string | undefined {
  for (const line of lines) {
    const match = /^\s*Attribute\s+VB_Name\s*=\s*"([^"]+)"/i.exec(line);
    if (match !== null) {
      return match[1];
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
} {
  const definitions: VbaDefinition[] = [];
  const procedureScopes: ProcedureScope[] = [];
  const withEventsDeclarations: WithEventsDeclaration[] = [];
  const implementedInterfaces: string[] = [];

  for (let line_index = start_line; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const implements_match = /^\s*Implements\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (implements_match !== null) {
      implementedInterfaces.push(implements_match[1]);
      continue;
    }

    const with_events_match =
      /^\s*(?:(?:Public|Private|Dim)\s+)?WithEvents\s+([A-Za-z_][A-Za-z0-9_]*)\s+As\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (with_events_match !== null) {
      withEventsDeclarations.push({
        name: with_events_match[1],
        typeName: with_events_match[2]
      });
      continue;
    }

    const event_match = /^\s*(?:(Public|Private)\s+)?Event\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (event_match !== null) {
      const visibility = (event_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = event_match[2];
      const name_start = line.indexOf(name);
      definitions.push({
        name,
        kind: 'event',
        visibility,
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        documentation: parseDocumentationComment(lines, line_index)
      });
      continue;
    }

    const enum_match = /^\s*(?:(Public|Private)\s+)?Enum\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (enum_match !== null) {
      const visibility = (enum_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = enum_match[2];
      const name_start = line.indexOf(name);
      definitions.push({
        name,
        kind: 'enum',
        visibility,
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        documentation: parseDocumentationComment(lines, line_index)
      });

      const end_line_index = findBlockEndLine(lines, line_index + 1, 'enum');
      definitions.push(...parseEnumMemberDefinitions(uri, lines, line_index + 1, end_line_index, visibility));
      line_index = end_line_index;
      continue;
    }

    const type_match = /^\s*(?:(Public|Private)\s+)?Type\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
    if (type_match !== null) {
      const visibility = (type_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = type_match[2];
      const name_start = line.indexOf(name);
      const end_line_index = findBlockEndLine(lines, line_index + 1, 'type');
      definitions.push({
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
    definitions.push({
      name,
      kind: procedure_kind,
      visibility,
      uri,
      range: {
        start: { line: line_index, character: name_start },
        end: { line: line_index, character: name_start + name.length }
      },
      documentation: parseDocumentationComment(lines, line_index),
      signature: buildSignatureInfo(line, name, parameter_definitions)
    });

    const end_line_index = findProcedureEndLine(lines, line_index + 1, end_keyword);

    procedureScopes.push({
      range: {
        start: { line: line_index, character: 0 },
        end: { line: end_line_index, character: lines[end_line_index]?.length ?? 0 }
      },
      definitions: [
        ...parameter_definitions,
        ...parseProcedureDefinitions(uri, lines, line_index + 1, end_line_index)
      ]
    });
    line_index = end_line_index;
  }

  return {
    definitions,
    procedureScopes,
    withEventsDeclarations,
    implements: implementedInterfaces
  };
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
    const match = /^(?:(?:Optional|ByVal|ByRef|ParamArray)\s+)*([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(segment.trimStart());
    if (match !== null) {
      const name = match[1];
      const name_start = line.indexOf(name, segment_start);
      definitions.push({
        name,
        kind: 'parameter',
        visibility: 'local',
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        }
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
  const return_match = /\)\s+As\s+(.+?)\s*$/i.exec(line);
  const parameters = parameterDefinitions.map((parameter) => parameter.name);
  const return_suffix = return_match === null ? '' : ` As ${return_match[1].trim()}`;

  return {
    label: `${name}(${parameters.join(', ')})${return_suffix}`,
    parameters
  };
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
    const local_match = /^\s*Dim\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line);
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
      }
    });
  }

  return definitions;
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

function singleMatch<T>(items: T[]): T | undefined {
  return items.length === 1 ? items[0] : undefined;
}

function findDefinitionByLocation(
  project: VbaProject,
  location: DefinitionLocation
): VbaDefinition | undefined {
  return project.modules
    .flatMap((module) => module.definitions)
    .find((definition) =>
      sameUri(definition.uri, location.uri)
        && comparePosition(definition.range.start, location.range.start) === 0
        && comparePosition(definition.range.end, location.range.end) === 0
    );
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

function comparePosition(left: SourcePosition, right: SourcePosition): number {
  if (left.line !== right.line) {
    return left.line - right.line;
  }

  return left.character - right.character;
}
