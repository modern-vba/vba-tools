import path from 'node:path';

import {
  createHostApplicationSelection,
  formatHostApplicationName,
  getBundledHostDefinitions,
  type HostApplicationSelection
} from './officeHostCatalog';

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
  detail?: string;
  insertText?: string;
  insertTextFormat?: 'snippet';
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
  | 'snippet'
  | 'type'
  | 'variable';

export type HostDefinitionKind = 'class' | 'property' | 'function' | 'enum' | 'enumMember';
export type HostApplication = 'excel' | 'word' | 'powerpoint' | 'access';

export interface CallableParameter {
  name: string;
  label?: string;
  documentation?: string;
  optional?: boolean;
  passingMode?: 'ByVal' | 'ByRef';
  isParamArray?: boolean;
  typeName?: string;
  defaultValue?: string;
}

export interface CallableSignature {
  label: string;
  parameters: CallableParameter[];
  returnTypeName?: string;
  documentation?: string;
}

export interface HostDefinition {
  name: string;
  kind?: HostDefinitionKind;
  hostApplication?: HostApplication;
  documentation?: string;
  typeName?: string;
  signature?: CallableSignature;
  members?: HostDefinition[];
}

export interface BuildVbaProjectOptions {
  hostDefinitions?: HostDefinition[];
  mainHostApplication?: HostApplication;
  additionalHostApplications?: HostApplication[];
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

export type SyntaxDiagnosticSeverity = 'error';
export type SyntaxDiagnosticCode =
  | 'syntax.invalidTrailingCommentContinuation'
  | 'syntax.invalidSourceCharacter'
  | 'syntax.malformedDateLiteral'
  | 'syntax.unterminatedDateLiteral'
  | 'syntax.unterminatedStringLiteral';

export interface SyntaxDiagnostic {
  code: SyntaxDiagnosticCode;
  message: string;
  range: SourceRange;
  severity: SyntaxDiagnosticSeverity;
  source: 'vba-language-server';
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
  signature?: CallableSignature;
  typeName?: string;
  optional?: boolean;
  passingMode?: 'ByVal' | 'ByRef';
  isParamArray?: boolean;
  defaultValue?: string;
}

interface MemberChainSegment {
  name: string;
  range: SourceRange;
  hasCall: boolean;
}

interface MemberChainExpression {
  segments: MemberChainSegment[];
  targetSegmentIndex: number;
  usesWithReceiver?: boolean;
}

interface MemberCompletionRequest {
  qualifier: string;
  prefix: string;
  receiverChain?: MemberChainExpression;
  usesWithReceiver?: boolean;
}

interface CallExpression {
  name: string;
  nameStart: number;
  activeParameter: number;
  chain?: MemberChainExpression;
}

interface LogicalSourceText {
  text: string;
  positions: SourcePosition[];
}

interface WithReceiverDeclaration {
  chain?: MemberChainExpression;
  end: SourcePosition;
}

interface WithReceiverSourceText extends LogicalSourceText {
  endLine: number;
  endCharacter: number;
  hasCommentContinuation: boolean;
}

type TypeResolutionRef =
  | {
      source: 'vba';
      typeName: string;
      allowPrivate: boolean;
    }
  | {
      source: 'host';
      typeName: string;
      hostApplication?: HostApplication;
    };

interface ResolvedChainSegment {
  resolution?: NameResolutionResult;
  typeRef?: TypeResolutionRef;
}

interface DocumentationComment {
  brief: string[];
  details: string[];
  params: string[];
  returns?: string;
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
  syntaxDiagnostics: SyntaxDiagnostic[];
}

export interface VbaProject {
  modules: VbaModule[];
  hostDefinitions: HostDefinition[];
  hostApplicationSelection: HostApplicationSelection;
}

const C_IDENTIFIER_PATTERN = /[A-Za-z_][A-Za-z0-9_]*/;
const C_TYPE_NAME_PATTERN = /[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?/;

export function buildVbaProject(
  files: VbaProjectFile[],
  options: BuildVbaProjectOptions = {}
): VbaProject {
  const modules = files
    .filter((file) => isVbaSourceUri(file.uri))
    .map((file) => parseModule(file));

  const host_application_selection = createHostApplicationSelection(options);

  return {
    modules,
    hostDefinitions: options.hostDefinitions ?? getBundledHostDefinitions(host_application_selection),
    hostApplicationSelection: host_application_selection
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
      hostDefinitions: project.hostDefinitions,
      hostApplicationSelection: project.hostApplicationSelection
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

  const end_statement_completion = getEndStatementCompletionAt(current_module, request.position);
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
    .filter((definition) => isUnqualifiedHostCompletionDefinition(project, definition))
    .map((definition) => ({
      label: definition.name,
      kind: completionKindForHostDefinition(definition),
      detail: getHostDefinitionDetail(definition)
    }));

  return uniqueCompletionEntries([
    ...(end_statement_completion === undefined ? [] : [end_statement_completion]),
    ...project_candidates,
    ...host_candidates
  ]);
}

function isUnqualifiedHostCompletionDefinition(project: VbaProject, definition: HostDefinition): boolean {
  const matches = project.hostDefinitions.filter((candidate) => sameName(candidate.name, definition.name));
  return selectUnqualifiedHostDefinition(project, matches) === definition;
}

function getRootHostCompletions(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string,
  prefix: string
): CompletionEntry[] | undefined {
  const host_application = resolveHostApplicationQualifier(project, currentModule, qualifier);
  if (host_application === undefined) {
    return undefined;
  }

  return project.hostDefinitions
    .filter((definition) => definition.hostApplication === host_application)
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .map((definition) => ({
      label: definition.name,
      kind: completionKindForHostDefinition(definition),
      detail: getHostDefinitionDetail(definition)
    }));
}

function getTypedMemberCompletions(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  request: MemberCompletionRequest
): CompletionEntry[] {
  const root_host_completions = getRootHostCompletions(project, currentModule, request.qualifier, request.prefix);
  if (root_host_completions !== undefined) {
    return root_host_completions;
  }

  const host_definition = resolveHostQualifiedPath(project, currentModule, request.qualifier);
  if (host_definition?.members !== undefined) {
    const prefix = request.prefix.toLowerCase();
    return host_definition.members
      .filter((member) => prefix === '' || member.name.toLowerCase().startsWith(prefix))
      .map((member) => ({
        label: member.name,
        kind: completionKindForHostDefinition(member),
        detail: getHostDefinitionDetail(member)
      }));
  }

  if (request.receiverChain !== undefined) {
    const type_ref = resolveMemberChainReceiverType(project, currentModule, position, request.receiverChain);
    if (type_ref !== undefined) {
      return completionEntriesForResolvedMembers(
        getMembersForResolvedType(project, currentModule, type_ref),
        request.prefix
      );
    }
  }

  if (request.usesWithReceiver === true) {
    const type_ref = resolveActiveWithReceiverType(project, currentModule, position);
    if (type_ref !== undefined) {
      return completionEntriesForResolvedMembers(
        getMembersForResolvedType(project, currentModule, type_ref),
        request.prefix
      );
    }

    return [];
  }

  const type_name = findTypeNameForExpression(project, currentModule, position, request.qualifier);
  if (type_name === undefined) {
    return [];
  }

  return completionEntriesForResolvedMembers(
    getMembersForType(project, currentModule, type_name),
    request.prefix
  );
}

function completionEntriesForResolvedMembers(
  members: { name: string; kind: CompletionEntryKind; detail?: string }[],
  prefix: string
): CompletionEntry[] {
  const normalized_prefix = prefix.toLowerCase();
  return members
    .filter((member) => normalized_prefix === '' || member.name.toLowerCase().startsWith(normalized_prefix))
    .map((member) => ({
      label: member.name,
      kind: member.kind,
      detail: member.detail
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

export function getSyntaxDiagnostics(project: VbaProject, uri: string): SyntaxDiagnostic[] {
  return findModule(project, uri)?.syntaxDiagnostics ?? [];
}

export function getHover(project: VbaProject, request: CompletionRequest): HoverResult | undefined {
  const resolution = resolveName(project, request);
  if (resolution === undefined) {
    return undefined;
  }

  if (resolution.source === 'host') {
    return resolution.definition.documentation === undefined
      ? undefined
      : { contents: renderHostDefinitionHover(resolution.definition) };
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

  const resolution = call_expression.chain === undefined
    ? resolveName(project, {
      uri: request.uri,
      position: {
        line: request.position.line,
        character: call_expression.nameStart
      }
    })
    : resolveMemberChainTarget(project, current_module, request.position, call_expression.chain);
  if (resolution === undefined) {
    return undefined;
  }

  if (resolution.source === 'host') {
    return getHostSignatureHelp(resolution.definition, call_expression.activeParameter);
  }

  const definition = findDefinitionByLocation(project, resolution.definition);
  return definition?.signature === undefined
    ? undefined
    : getSourceSignatureHelp(project, definition, call_expression.activeParameter);
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
  const host_definition = selectUnqualifiedHostDefinition(project, host_matches);
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

function resolveHostQualifiedDefinition(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string,
  member: string
): HostDefinition | undefined {
  const host_path_definition = resolveHostQualifiedPath(project, currentModule, qualifier);
  if (host_path_definition !== undefined) {
    return singleMatch(host_path_definition.members?.filter((definition) =>
      sameName(definition.name, member)
    ) ?? []);
  }

  const host_application = resolveHostApplicationQualifier(project, currentModule, qualifier);
  if (host_application === undefined) {
    return undefined;
  }

  return singleMatch(project.hostDefinitions.filter((definition) =>
    definition.hostApplication === host_application && sameName(definition.name, member)
  ));
}

function resolveHostQualifiedPath(
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

function resolveHostApplicationQualifier(
  project: VbaProject,
  currentModule: VbaModule,
  qualifier: string
): HostApplication | undefined {
  if (qualifier.includes('.')) {
    return undefined;
  }

  const source_module = project.modules.find((module) =>
    module.folderUri.toLowerCase() === currentModule.folderUri.toLowerCase()
      && sameName(module.identity, qualifier)
  );
  if (source_module !== undefined) {
    return undefined;
  }

  return project.hostApplicationSelection.enabledHostApplications.find((hostApplication) =>
    sameName(hostApplication, qualifier) || sameName(formatHostApplicationName(hostApplication), qualifier)
  );
}

function selectUnqualifiedHostDefinition(
  project: VbaProject,
  matches: HostDefinition[]
): HostDefinition | undefined {
  if (matches.length === 1) {
    return matches[0];
  }

  const main_host_matches = matches.filter((definition) =>
    definition.hostApplication === project.hostApplicationSelection.mainHostApplication
  );
  return singleMatch(main_host_matches);
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
      project,
      project.hostDefinitions.filter((definition) => sameName(definition.name, type_name))
    );
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
  const syntax_diagnostics = collectSyntaxDiagnostics(lines, code_start_line);
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
    moduleMembers: parsed_members.moduleMembers,
    syntaxDiagnostics: syntax_diagnostics
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

function collectSyntaxDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    diagnostics.push(...collectLexicalSyntaxDiagnostics(line, line_index));

    const invalid_trailing_comment_range = getInvalidTrailingCommentContinuationRange(line, line_index);
    if (invalid_trailing_comment_range !== undefined) {
      diagnostics.push({
        code: 'syntax.invalidTrailingCommentContinuation',
        message: 'Code line-continuation marker cannot be followed by a comment.',
        range: invalid_trailing_comment_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }
  }

  return diagnostics;
}

function collectLexicalSyntaxDiagnostics(line: string, lineIndex: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let character_index = 0;

  while (character_index < line.length) {
    const character = line[character_index];
    if (character === "'" || isRemCommentStart(line, character_index)) {
      break;
    }

    if (character === '"') {
      const string_end = getStringLiteralEnd(line, character_index);
      if (string_end === undefined) {
        diagnostics.push({
          code: 'syntax.unterminatedStringLiteral',
          message: 'String literal is missing a closing double quote.',
          range: {
            start: { line: lineIndex, character: character_index },
            end: { line: lineIndex, character: line.length }
          },
          severity: 'error',
          source: 'vba-language-server'
        });
        break;
      }

      character_index = string_end;
      continue;
    }

    if (character === '#') {
      if (shouldSkipHashCharacter(line, character_index)) {
        character_index += 1;
        continue;
      }

      const closing_index = line.indexOf('#', character_index + 1);
      if (closing_index === -1) {
        diagnostics.push({
          code: 'syntax.unterminatedDateLiteral',
          message: 'Date literal is missing a closing # delimiter.',
          range: {
            start: { line: lineIndex, character: character_index },
            end: { line: lineIndex, character: line.length }
          },
          severity: 'error',
          source: 'vba-language-server'
        });
        break;
      }

      if (!isValidDateLiteralText(line.slice(character_index + 1, closing_index).trim())) {
        diagnostics.push({
          code: 'syntax.malformedDateLiteral',
          message: 'Date literal is malformed.',
          range: {
            start: { line: lineIndex, character: character_index },
            end: { line: lineIndex, character: closing_index + 1 }
          },
          severity: 'error',
          source: 'vba-language-server'
        });
      }

      character_index = closing_index + 1;
      continue;
    }

    if (!isValidSourceCharacter(character)) {
      diagnostics.push({
        code: 'syntax.invalidSourceCharacter',
        message: 'Character cannot begin a supported VBA token.',
        range: {
          start: { line: lineIndex, character: character_index },
          end: { line: lineIndex, character: character_index + 1 }
        },
        severity: 'error',
        source: 'vba-language-server'
      });
    }

    character_index += 1;
  }

  return diagnostics;
}

function getStringLiteralEnd(line: string, startCharacter: number): number | undefined {
  let character_index = startCharacter + 1;
  while (character_index < line.length) {
    if (line[character_index] !== '"') {
      character_index += 1;
      continue;
    }

    if (line[character_index + 1] === '"') {
      character_index += 2;
      continue;
    }

    return character_index + 1;
  }

  return undefined;
}

function shouldSkipHashCharacter(line: string, characterIndex: number): boolean {
  const before = line.slice(0, characterIndex).trimEnd();
  if (before === '' && /^#\s*(?:If|ElseIf|Else|End\s+If|Const)\b/i.test(line.slice(characterIndex))) {
    return true;
  }

  const previous_character = findPreviousNonWhitespace(line, characterIndex - 1);
  return previous_character !== undefined && isIdentifierPart(line[previous_character]);
}

function isValidDateLiteralText(text: string): boolean {
  if (text.length === 0 || !/[0-9]/.test(text) || /[^0-9A-Za-z\s/:.,-]/.test(text)) {
    return false;
  }

  if (!Number.isNaN(Date.parse(text))) {
    return true;
  }

  return /^\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AP]M)?$/i.test(text);
}

function isValidSourceCharacter(character: string): boolean {
  return /\s/.test(character)
    || /[A-Za-z0-9_]/.test(character)
    || character.charCodeAt(0) > 127
    || '()[]:.,;!?+-*/\\^=&<>$%@'.includes(character);
}

function isRemCommentStart(line: string, characterIndex: number): boolean {
  if (!/^Rem\b/i.test(line.slice(characterIndex))) {
    return false;
  }

  const before = line.slice(0, characterIndex).trimEnd();
  return before === '' || before.endsWith(':');
}

function getInvalidTrailingCommentContinuationRange(line: string, lineIndex: number): SourceRange | undefined {
  const code_end = getCodeEndCharacter(line);
  if (code_end >= line.length) {
    return undefined;
  }

  const marker_index = findPreviousNonWhitespace(line, code_end - 1);
  if (
    marker_index === undefined
    || line[marker_index] !== '_'
    || marker_index === 0
    || !/\s/.test(line[marker_index - 1])
  ) {
    return undefined;
  }

  return {
    start: { line: lineIndex, character: marker_index },
    end: { line: lineIndex, character: line.length }
  };
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
      new RegExp(`^\\s*(?:(?:Public|Private|Dim)\\s+)?WithEvents\\s+(${C_IDENTIFIER_PATTERN.source})\\s+As\\s+(${C_TYPE_NAME_PATTERN.source})\\b`, 'i').exec(line);
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
      const type_match = new RegExp(`\\bAs\\s+(${C_TYPE_NAME_PATTERN.source})\\b`, 'i').exec(trimmed_segment);
      const passing_mode_match = /\b(ByVal|ByRef)\b/i.exec(trimmed_segment);
      const default_value_match = /=\s*(.+)\s*$/.exec(trimmed_segment);
      definitions.push({
        name,
        kind: 'parameter',
        visibility: 'local',
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        typeName: type_match?.[1],
        optional: /\bOptional\b/i.test(trimmed_segment),
        passingMode: passing_mode_match === null
          ? undefined
          : canonicalPassingMode(passing_mode_match[1]),
        isParamArray: /\bParamArray\b/i.test(trimmed_segment),
        defaultValue: default_value_match?.[1].trim()
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
): CallableSignature {
  const parameters = parameterDefinitions.map((parameter) => ({
    name: parameter.name,
    label: formatCallableParameterLabel(parameter),
    optional: parameter.optional,
    passingMode: parameter.passingMode,
    isParamArray: parameter.isParamArray,
    typeName: parameter.typeName,
    defaultValue: parameter.defaultValue
  }));
  const return_type_name = parseReturnTypeName(line);
  const return_suffix = return_type_name === undefined ? '' : ` As ${return_type_name}`;

  return {
    label: `${name}(${parameters.map((parameter) => parameter.label ?? parameter.name).join(', ')})${return_suffix}`,
    parameters,
    returnTypeName: return_type_name
  };
}

function canonicalPassingMode(value: string): 'ByVal' | 'ByRef' {
  return value.toLowerCase() === 'byval' ? 'ByVal' : 'ByRef';
}

function formatCallableParameterLabel(parameter: VbaDefinition): string {
  const modifiers = [
    parameter.isParamArray === true ? 'ParamArray' : undefined,
    parameter.optional === true ? 'Optional' : undefined
  ].filter((modifier) => modifier !== undefined);

  return [...modifiers, parameter.name].join(' ');
}

function parseReturnTypeName(line: string): string | undefined {
  const return_match = new RegExp(`\\)\\s+As\\s+(${C_TYPE_NAME_PATTERN.source})\\b`, 'i').exec(line);
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
      new RegExp(`^\\s*Dim\\s+(${C_IDENTIFIER_PATTERN.source})\\b(?:\\s+As\\s+(${C_TYPE_NAME_PATTERN.source}))?`, 'i').exec(line);
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
): MemberCompletionRequest | undefined {
  const line = lines[position.line] ?? '';
  const effective_character = Math.min(position.character, line.length);
  if (!isCodePosition(line, effective_character)) {
    return undefined;
  }

  const prefix = getIdentifierPrefix(lines, position);
  const prefix_start = effective_character - prefix.length;
  const dot_index = findPreviousNonWhitespace(line, prefix_start - 1);
  if (dot_index === undefined || line[dot_index] !== '.') {
    return undefined;
  }

  const continued_receiver_chain = parseContinuedMemberChainEndingBefore(lines, position.line, dot_index);
  const receiver_chain = continued_receiver_chain ?? parseMemberChainEndingAt(line, position.line, dot_index);
  if (receiver_chain === undefined) {
    const leading_dot = findPreviousNonWhitespace(line, dot_index - 1) === undefined;
    return leading_dot
      ? {
          qualifier: '',
          prefix,
          usesWithReceiver: true
        }
      : undefined;
  }

  const qualifier_start = receiver_chain.segments[0].range.start.line === position.line
    ? receiver_chain.segments[0].range.start.character
    : dot_index;
  return {
    qualifier: line.slice(qualifier_start, dot_index).trim(),
    prefix,
    receiverChain: receiver_chain
  };
}

function parseContinuedMemberChainEndingBefore(
  lines: string[],
  lineIndex: number,
  endCharacter: number
): MemberChainExpression | undefined {
  const logical_source = getContinuedSourceTextEndingBefore(lines, lineIndex, endCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  return parseMemberChainEndingBeforeSource(logical_source, logical_source.text.length);
}

function getContinuedSourceTextEndingBefore(
  lines: string[],
  lineIndex: number,
  endCharacter: number
): LogicalSourceText | undefined {
  let start_line = lineIndex;
  while (start_line > 0 && getCodeContinuationMarkerStart(lines[start_line - 1] ?? '') !== undefined) {
    start_line -= 1;
  }

  if (start_line === lineIndex) {
    return undefined;
  }

  const text_parts: string[] = [];
  const positions: SourcePosition[] = [];
  for (let current_line_index = start_line; current_line_index <= lineIndex; current_line_index += 1) {
    const line = lines[current_line_index] ?? '';
    const line_end = current_line_index === lineIndex
      ? Math.min(endCharacter, line.length)
      : getCodeContinuationMarkerStart(line);
    if (line_end === undefined) {
      return undefined;
    }

    text_parts.push(line.slice(0, line_end));
    for (let character = 0; character < line_end; character += 1) {
      positions.push({ line: current_line_index, character });
    }
  }

  return {
    text: text_parts.join(''),
    positions
  };
}

function getLogicalSourceRange(
  source: LogicalSourceText,
  start: number,
  end: number
): SourceRange {
  const start_position = source.positions[start];
  const last_position = source.positions[end - 1];
  return {
    start: start_position,
    end: {
      line: last_position.line,
      character: last_position.character + 1
    }
  };
}

function getCodeContinuationMarkerStart(line: string): number | undefined {
  const code_end = getCodeEndCharacter(line);
  if (code_end < line.length) {
    return undefined;
  }

  const marker_index = findPreviousNonWhitespace(line, code_end - 1);
  if (
    marker_index === undefined
    || line[marker_index] !== '_'
    || marker_index === 0
    || !/\s/.test(line[marker_index - 1])
  ) {
    return undefined;
  }

  return marker_index;
}

function parseMemberChainEndingAt(
  line: string,
  lineIndex: number,
  endCharacter: number
): MemberChainExpression | undefined {
  return parseMemberChainEndingBefore(line, lineIndex, endCharacter);
}

function parseMemberChainEndingBefore(
  line: string,
  lineIndex: number,
  endCharacter: number
): MemberChainExpression | undefined {
  const expression_end = findPreviousNonWhitespace(line, endCharacter - 1);
  if (expression_end === undefined) {
    return undefined;
  }

  const end_index = expression_end + 1;
  const candidates: Array<{ segments: MemberChainSegment[]; endIndex: number }> = [];
  for (const range of getIdentifierRangesInCode(line, lineIndex)) {
    if (range.start.character >= end_index) {
      continue;
    }

    const candidate = parseMemberChainFrom(line, lineIndex, range.start.character, end_index);
    if (candidate !== undefined && candidate.endIndex === end_index) {
      candidates.push(candidate);
    }
  }

  const selected = candidates.sort((left, right) =>
    right.segments.length - left.segments.length
      || left.segments[0].range.start.character - right.segments[0].range.start.character
  )[0];
  return selected === undefined
    ? undefined
    : {
        segments: selected.segments,
        targetSegmentIndex: selected.segments.length - 1,
        usesWithReceiver: isLeadingDotChain(line, selected.segments[0].range.start.character)
      };
}

function isLeadingDotChain(line: string, firstSegmentStart: number): boolean {
  const dot_index = findPreviousNonWhitespace(line, firstSegmentStart - 1);
  return dot_index !== undefined
    && line[dot_index] === '.'
    && findPreviousNonWhitespace(line, dot_index - 1) === undefined;
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

function parseMemberChainFrom(
  line: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number,
  getRange: (start: number, end: number) => SourceRange = (start, end) => ({
    start: { line: lineIndex, character: start },
    end: { line: lineIndex, character: end }
  })
): { segments: MemberChainSegment[]; endIndex: number } | undefined {
  const segments: MemberChainSegment[] = [];
  let character_index = startCharacter;

  while (character_index < endCharacter) {
    character_index = skipWhitespace(line, character_index, endCharacter);
    const identifier = readIdentifierAt(line, character_index);
    if (identifier === undefined) {
      return undefined;
    }

    character_index = identifier.end;
    character_index = skipWhitespace(line, character_index, endCharacter);
    let has_call = false;
    if (character_index < endCharacter && line[character_index] === '(') {
      const close_paren = findMatchingParen(line, character_index, endCharacter);
      if (close_paren === undefined) {
        return undefined;
      }

      has_call = true;
      character_index = close_paren + 1;
      character_index = skipWhitespace(line, character_index, endCharacter);
    }

    segments.push({
      name: identifier.name,
      range: getRange(identifier.start, identifier.end),
      hasCall: has_call
    });

    if (character_index >= endCharacter || line[character_index] !== '.') {
      break;
    }

    character_index += 1;
  }

  const end_index = skipWhitespace(line, character_index, endCharacter);
  return segments.length === 0 ? undefined : { segments, endIndex: end_index };
}

function readIdentifierAt(
  line: string,
  startCharacter: number
): { name: string; start: number; end: number } | undefined {
  if (!isIdentifierStart(line[startCharacter] ?? '')) {
    return undefined;
  }

  let character_index = startCharacter + 1;
  while (character_index < line.length && isIdentifierPart(line[character_index])) {
    character_index += 1;
  }

  return {
    name: line.slice(startCharacter, character_index),
    start: startCharacter,
    end: character_index
  };
}

function findMatchingParen(
  line: string,
  openParen: number,
  endCharacter: number
): number | undefined {
  let depth = 0;
  let character_index = openParen;
  let is_in_string = false;

  while (character_index < endCharacter) {
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
      return undefined;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }
    if (character === '(') {
      depth += 1;
    } else if (character === ')') {
      depth -= 1;
      if (depth === 0) {
        return character_index;
      }
    }

    character_index += 1;
  }

  return undefined;
}

function findPreviousNonWhitespace(line: string, startCharacter: number): number | undefined {
  for (let character_index = startCharacter; character_index >= 0; character_index -= 1) {
    if (!/\s/.test(line[character_index])) {
      return character_index;
    }
  }

  return undefined;
}

function skipWhitespace(line: string, startCharacter: number, endCharacter: number): number {
  let character_index = startCharacter;
  while (character_index < endCharacter && /\s/.test(line[character_index])) {
    character_index += 1;
  }

  return character_index;
}

function isCodePosition(line: string, character: number): boolean {
  let character_index = 0;
  let is_in_string = false;

  while (character_index < Math.min(character, line.length)) {
    const current_character = line[character_index];
    if (is_in_string) {
      if (current_character === '"') {
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

    if (current_character === "'") {
      return false;
    }
    if (current_character === '"') {
      is_in_string = true;
    }

    character_index += 1;
  }

  return !is_in_string;
}

function getEndStatementCompletionAt(
  module: VbaModule,
  position: SourcePosition
): CompletionEntry | undefined {
  if (position.line < module.codeStartLine) {
    return undefined;
  }

  const line = module.lines[position.line] ?? '';
  if (position.character !== line.length) {
    return undefined;
  }

  const structure_text = getCodeTextForStructure(line).trim();
  const closer = getEndStatementCloser(structure_text);
  if (closer === undefined || hasFollowingCloser(module.lines, position.line + 1, closer)) {
    return undefined;
  }

  const base_indent = /^\s*/.exec(line)?.[0] ?? '';
  const body_indent = `${base_indent}    `;
  return {
    label: `Insert ${closer}`,
    kind: 'snippet',
    insertText: `\n${body_indent}$0\n${base_indent}${closer}`,
    insertTextFormat: 'snippet'
  };
}

function getEndStatementCloser(text: string): string | undefined {
  if (/^(?:(?:Public|Private|Friend)\s+)?Sub\b/i.test(text)) {
    return 'End Sub';
  }
  if (/^(?:(?:Public|Private|Friend)\s+)?Function\b/i.test(text)) {
    return 'End Function';
  }
  if (/^(?:(?:Public|Private|Friend)\s+)?Property\s+(?:Get|Let|Set)\b/i.test(text)) {
    return 'End Property';
  }
  if (/^If\b.*\bThen\s*$/i.test(text)) {
    return 'End If';
  }
  if (/^For\b/i.test(text)) {
    return 'Next';
  }
  if (/^Do\b/i.test(text)) {
    return 'Loop';
  }
  if (/^While\b/i.test(text)) {
    return 'Wend';
  }
  if (/^Select\s+Case\b/i.test(text)) {
    return 'End Select';
  }
  if (/^With\b/i.test(text)) {
    return 'End With';
  }
  if (/^(?:(?:Public|Private)\s+)?Enum\b/i.test(text)) {
    return 'End Enum';
  }
  if (/^(?:(?:Public|Private)\s+)?Type\b/i.test(text)) {
    return 'End Type';
  }

  return undefined;
}

function hasFollowingCloser(lines: string[], startLine: number, closer: string): boolean {
  const closer_pattern = new RegExp(`^\\s*${escapeRegExp(closer).replace(/\s+/g, '\\s+')}\\b`, 'i');
  for (let line_index = startLine; line_index < lines.length; line_index += 1) {
    if (closer_pattern.test(getCodeTextForStructure(lines[line_index]).trim())) {
      return true;
    }
  }

  return false;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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

function resolveMemberChainReceiverType(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  chain: MemberChainExpression
): TypeResolutionRef | undefined {
  const resolved_segments = resolveMemberChain(project, currentModule, position, chain);
  return resolved_segments?.at(-1)?.typeRef;
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

function resolveMemberChainTarget(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  chain: MemberChainExpression
): NameResolutionResult | undefined {
  const resolved_segments = resolveMemberChain(project, currentModule, position, chain);
  return resolved_segments?.at(-1)?.resolution;
}

function resolveActiveWithReceiverType(
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

function getWithReceiverDeclarationAt(lines: string[], lineIndex: number): WithReceiverDeclaration | undefined {
  const line = lines[lineIndex] ?? '';
  const code_text = getCodeTextForStructure(line);
  const with_match = /^\s*With\b/i.exec(code_text);
  if (with_match === null) {
    return undefined;
  }

  const first_line_end = getCodeContinuationMarkerStart(line) ?? getCodeEndCharacter(line);
  const receiver_source = getWithReceiverSourceText(lines, lineIndex, with_match[0].length);
  if (receiver_source === undefined) {
    return {
      end: { line: lineIndex, character: first_line_end }
    };
  }
  if (receiver_source.hasCommentContinuation) {
    return {
      end: {
        line: receiver_source.endLine,
        character: receiver_source.endCharacter
      }
    };
  }

  const receiver_chain = getWithReceiverChainFromSource(receiver_source);
  return {
    chain: receiver_chain,
    end: {
      line: receiver_source.endLine,
      character: receiver_source.endCharacter
    }
  };
}

function getWithReceiverSourceText(
  lines: string[],
  lineIndex: number,
  receiverStart: number
): WithReceiverSourceText | undefined {
  const text_parts: string[] = [];
  const positions: SourcePosition[] = [];
  let has_comment_continuation = false;

  for (let current_line_index = lineIndex; current_line_index < lines.length; current_line_index += 1) {
    const line = lines[current_line_index] ?? '';
    const line_start = current_line_index === lineIndex ? receiverStart : 0;
    const continuation_marker = getCodeContinuationMarkerStart(line);
    const line_end = continuation_marker ?? getCodeEndCharacter(line);
    has_comment_continuation = has_comment_continuation || hasCommentContinuationMarker(line);

    text_parts.push(line.slice(line_start, line_end));
    for (let character = line_start; character < line_end; character += 1) {
      positions.push({ line: current_line_index, character });
    }

    if (continuation_marker === undefined) {
      return {
        text: text_parts.join(''),
        positions,
        endLine: current_line_index,
        endCharacter: line_end,
        hasCommentContinuation: has_comment_continuation
      };
    }
  }

  return undefined;
}

function hasCommentContinuationMarker(line: string): boolean {
  const code_end = getCodeEndCharacter(line);
  if (code_end >= line.length) {
    return false;
  }

  const marker_index = findPreviousNonWhitespace(line, line.length - 1);
  return marker_index !== undefined
    && marker_index > code_end
    && line[marker_index] === '_'
    && marker_index > 0
    && /\s/.test(line[marker_index - 1]);
}

function getWithReceiverChainFromSource(
  source: LogicalSourceText
): MemberChainExpression | undefined {
  const expression_end = findPreviousNonWhitespace(source.text, source.text.length - 1);
  if (expression_end === undefined) {
    return undefined;
  }
  if (source.text[expression_end] === '.') {
    return undefined;
  }

  const code_end = expression_end + 1;
  let receiver_start = skipWhitespace(source.text, 0, code_end);
  let uses_with_receiver = false;
  if (source.text[receiver_start] === '.') {
    uses_with_receiver = true;
    receiver_start = skipWhitespace(source.text, receiver_start + 1, code_end);
  }

  const receiver_chain = parseMemberChainFrom(
    source.text,
    source.positions[receiver_start]?.line ?? 0,
    receiver_start,
    code_end,
    (start, end) => getLogicalSourceRange(source, start, end)
  );
  if (receiver_chain === undefined || receiver_chain.endIndex !== code_end) {
    return undefined;
  }

  return {
    segments: receiver_chain.segments,
    targetSegmentIndex: receiver_chain.segments.length - 1,
    usesWithReceiver: uses_with_receiver
  };
}

function getCodeEndCharacter(line: string): number {
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
      return character_index;
    }
    if (character === '"') {
      is_in_string = true;
    }

    character_index += 1;
  }

  return line.length;
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
        : singleMatch(project.hostDefinitions.filter((definition) =>
          definition.hostApplication === host_application && sameName(definition.name, segments[1].name)
        ));
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

  const current_module_definition = singleMatch(
    currentModule.definitions
      .filter((definition) => sameName(definition.name, segment.name))
  );
  if (current_module_definition !== undefined) {
    return {
      resolution: toVbaResolution(current_module_definition),
      typeRef: typeRefForVbaDefinition(project, currentModule, current_module_definition, segment.hasCall, true)
    };
  }

  const project_definition = singleMatch(
    project.modules
      .filter((module) => module.folderUri.toLowerCase() === currentModule.folderUri.toLowerCase())
      .filter((module) => !sameUri(module.uri, currentModule.uri))
      .flatMap((module) => module.definitions)
      .filter((definition) => definition.visibility === 'public')
      .filter((definition) => sameName(definition.name, segment.name))
  );
  if (project_definition !== undefined) {
    return {
      resolution: toVbaResolution(project_definition),
      typeRef: typeRefForVbaDefinition(project, currentModule, project_definition, segment.hasCall, false)
    };
  }

  const host_definition = selectUnqualifiedHostDefinition(
    project,
    project.hostDefinitions.filter((definition) => sameName(definition.name, segment.name))
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
    const host_member = singleMatch(host_type?.members?.filter((definition) =>
      sameName(definition.name, segment.name)
    ) ?? []);
    return host_member === undefined
      ? undefined
      : {
          resolution: { source: 'host', definition: host_member },
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

function typeRefForVbaDefinition(
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

function typeRefForHostDefinition(
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

function resolveTypeNameRef(
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
    project,
    project.hostDefinitions.filter((definition) => sameName(definition.name, typeName))
  );
  return unqualified_host_type === undefined
    ? undefined
    : {
        source: 'host',
        typeName: unqualified_host_type.name,
        hostApplication: unqualified_host_type.hostApplication
      };
}

function findSourceTypeModule(
  project: VbaProject,
  currentModule: VbaModule,
  typeName: string
): VbaModule | undefined {
  if (typeName.includes('.')) {
    return undefined;
  }

  return project.modules.find((module) =>
    module.folderUri.toLowerCase() === currentModule.folderUri.toLowerCase()
      && sameName(module.identity, typeName)
  );
}

function findHostTypeDefinition(
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
    project,
    project.hostDefinitions.filter((definition) => sameName(definition.name, typeRef.typeName))
  );
}

function getMembersForType(
  project: VbaProject,
  currentModule: VbaModule,
  typeName: string
): { name: string; kind: CompletionEntryKind; detail?: string }[] {
  const type_ref = resolveTypeNameRef(project, currentModule, typeName, false);
  return type_ref === undefined ? [] : getMembersForResolvedType(project, currentModule, type_ref);
}

function getMembersForResolvedType(
  project: VbaProject,
  currentModule: VbaModule,
  typeRef: TypeResolutionRef
): { name: string; kind: CompletionEntryKind; detail?: string }[] {
  if (typeRef.source === 'host') {
    const host_type = findHostTypeDefinition(project, currentModule, typeRef);
    if (host_type?.members === undefined) {
      return [];
    }

    return host_type.members.map((member) => ({
      name: member.name,
      kind: completionKindForHostDefinition(member),
      detail: getHostDefinitionDetail(member)
    }));
  }

  const project_type = findSourceTypeModule(project, currentModule, typeRef.typeName);
  if (project_type !== undefined) {
    return project_type.definitions
      .filter((definition) => typeRef.allowPrivate || definition.visibility === 'public')
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

function getCallExpressionAt(
  lines: string[],
  position: SourcePosition
): CallExpression | undefined {
  const line = lines[position.line] ?? '';
  const effective_character = Math.min(position.character, line.length);
  const open_paren = findActiveCallOpenParen(line, effective_character);
  if (open_paren === undefined) {
    return getContinuedCallExpressionAt(lines, position, effective_character);
  }

  const chain = parseContinuedMemberChainEndingBefore(lines, position.line, open_paren)
    ?? parseMemberChainEndingBefore(line, position.line, open_paren);
  const target_segment = chain?.segments.at(-1);
  if (target_segment === undefined) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(line.slice(open_paren + 1, effective_character)),
    chain
  };
}

function getContinuedCallExpressionAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): CallExpression | undefined {
  const logical_source = getContinuedSourceTextEndingBefore(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  const open_paren = findActiveCallOpenParen(logical_source.text, logical_source.text.length);
  if (open_paren === undefined) {
    return undefined;
  }

  const chain = parseMemberChainEndingBeforeSource(logical_source, open_paren);
  const target_segment = chain?.segments.at(-1);
  if (target_segment === undefined) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(logical_source.text.slice(open_paren + 1)),
    chain
  };
}

function parseMemberChainEndingBeforeSource(
  source: LogicalSourceText,
  endCharacter: number
): MemberChainExpression | undefined {
  const expression_end = findPreviousNonWhitespace(source.text, endCharacter - 1);
  if (expression_end === undefined) {
    return undefined;
  }

  const end_index = expression_end + 1;
  const candidates: Array<{ segments: MemberChainSegment[]; endIndex: number; startIndex: number }> = [];
  for (const range of getIdentifierRangesInCode(source.text, source.positions[0]?.line ?? 0)) {
    if (range.start.character >= end_index) {
      continue;
    }

    const candidate = parseMemberChainFrom(
      source.text,
      source.positions[range.start.character]?.line ?? 0,
      range.start.character,
      end_index,
      (start, end) => getLogicalSourceRange(source, start, end)
    );
    if (candidate !== undefined && candidate.endIndex === end_index) {
      candidates.push({
        ...candidate,
        startIndex: range.start.character
      });
    }
  }

  const selected = candidates.sort((left, right) =>
    right.segments.length - left.segments.length
      || left.startIndex - right.startIndex
  )[0];
  return selected === undefined
    ? undefined
    : {
        segments: selected.segments,
        targetSegmentIndex: selected.segments.length - 1,
        usesWithReceiver: isLeadingDotChain(source.text, selected.startIndex)
      };
}

function findActiveCallOpenParen(line: string, positionCharacter: number): number | undefined {
  const open_parens: number[] = [];
  let character_index = 0;
  let is_in_string = false;

  while (character_index < positionCharacter) {
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
    if (character === '(') {
      open_parens.push(character_index);
    } else if (character === ')') {
      open_parens.pop();
    }

    character_index += 1;
  }

  return open_parens.at(-1);
}

function countTopLevelCommas(text: string): number {
  let count = 0;
  let depth = 0;
  let character_index = 0;
  let is_in_string = false;

  while (character_index < text.length) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
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
    if (character === '(') {
      depth += 1;
    } else if (character === ')') {
      depth = Math.max(0, depth - 1);
    } else if (character === ',' && depth === 0) {
      count += 1;
    }

    character_index += 1;
  }

  return count;
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

function getSourceSignatureHelp(
  project: VbaProject,
  definition: VbaDefinition,
  activeParameter: number
): SignatureHelpResult | undefined {
  if (definition.signature === undefined) {
    return undefined;
  }

  const documentation = findDocumentationForDefinition(project, definition);
  const parameter_docs = getParameterDocumentation(documentation);
  return toSignatureHelpResult(
    definition.signature,
    activeParameter,
    renderSignatureDocumentation(documentation),
    (parameter) => parameter_docs.get(parameter.name.toLowerCase()) ?? renderSourceCallableParameterMetadata(parameter)
  );
}

function getHostSignatureHelp(
  definition: HostDefinition,
  activeParameter: number
): SignatureHelpResult | undefined {
  if (definition.signature === undefined) {
    return undefined;
  }

  return toSignatureHelpResult(
    definition.signature,
    activeParameter,
    definition.signature.documentation ?? definition.documentation,
    (parameter) => parameter.documentation ?? renderCallableParameterMetadata(parameter)
  );
}

function toSignatureHelpResult(
  signature: CallableSignature,
  activeParameter: number,
  documentation: string | undefined,
  getParameterDocumentation: (parameter: CallableParameter) => string | undefined
): SignatureHelpResult {
  return {
    label: signature.label,
    activeParameter: Math.min(activeParameter, Math.max(signature.parameters.length - 1, 0)),
    documentation,
    parameters: signature.parameters.map((parameter) => ({
      label: parameter.label ?? parameter.name,
      documentation: getParameterDocumentation(parameter)
    }))
  };
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

function renderHostDefinitionHover(definition: HostDefinition): string {
  const detail = getHostDefinitionDetail(definition);
  return detail === undefined
    ? definition.documentation ?? ''
    : [detail, definition.documentation].filter((section) => section !== undefined && section !== '').join('\n\n');
}

function getHostDefinitionDetail(definition: HostDefinition): string | undefined {
  if (definition.hostApplication === undefined) {
    return undefined;
  }

  return `${formatHostApplicationName(definition.hostApplication)}.${definition.name}`;
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

function renderCallableParameterMetadata(parameter: CallableParameter): string | undefined {
  const sections = [
    parameter.typeName,
    parameter.optional === true ? 'Optional.' : undefined,
    parameter.defaultValue === undefined ? undefined : `Default: ${parameter.defaultValue}.`
  ].filter((section) => section !== undefined && section !== '');

  return sections.length === 0 ? undefined : sections.join(' ');
}

function renderSourceCallableParameterMetadata(parameter: CallableParameter): string | undefined {
  if (parameter.optional !== true
    && parameter.isParamArray !== true
    && parameter.defaultValue === undefined) {
    return undefined;
  }

  return renderCallableParameterMetadata(parameter);
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
