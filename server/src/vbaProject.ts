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
  | 'constant'
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

export type HostDefinitionKind = 'class' | 'property' | 'function' | 'enum' | 'enumMember' | 'constant';
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
  parentName?: string;
  documentation?: string;
  value?: string;
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
  | 'syntax.invalidContinuationMarkerSpacing'
  | 'syntax.invalidContinuationMarkerText'
  | 'syntax.incompleteContinuation'
  | 'syntax.missingContinuationMarker'
  | 'syntax.invalidSourceCharacter'
  | 'syntax.invalidStatementSeparator'
  | 'syntax.malformedCall'
  | 'syntax.malformedCallableDeclaration'
  | 'syntax.malformedConditionalCompilation'
  | 'syntax.malformedDeclaration'
  | 'syntax.malformedDeclarationBlock'
  | 'syntax.malformedBlockStructure'
  | 'syntax.malformedControlFlow'
  | 'syntax.malformedExpression'
  | 'syntax.malformedMemberAccess'
  | 'syntax.malformedStatement'
  | 'syntax.malformedAttribute'
  | 'syntax.malformedDateLiteral'
  | 'syntax.malformedOption'
  | 'syntax.misplacedHeaderStatement'
  | 'syntax.unexpectedToken'
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
  if (
    isInMalformedExpressionRegion(current_module, request.position)
    || isInMalformedMemberAccessRegion(current_module, request.position)
  ) {
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
  const host_definitions = getUnqualifiedHostDefinitions(project);
  const host_candidates = host_definitions
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .filter((definition) => isUnqualifiedHostCompletionDefinition(project, definition, host_definitions))
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

function isUnqualifiedHostCompletionDefinition(
  project: VbaProject,
  definition: HostDefinition,
  hostDefinitions: HostDefinition[] = getUnqualifiedHostDefinitions(project)
): boolean {
  const matches = hostDefinitions.filter((candidate) => sameName(candidate.name, definition.name));
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

  return getUnqualifiedHostDefinitions(project)
    .filter((definition) => definition.hostApplication === host_application)
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .filter((definition, _index, definitions) =>
      selectHostApplicationQualifiedDefinition(definitions, host_application, definition.name) === definition
    )
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
  if (isInMalformedMemberAccessRegion(current_module, request.position)) {
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
  if (
    isInMalformedExpressionRegion(current_module, request.position)
    || isInMalformedMemberAccessRegion(current_module, request.position)
  ) {
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

  const host_matches = getUnqualifiedHostDefinitions(project).filter((definition) => sameName(definition.name, identifier));
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
  if (host_path_definition !== undefined && host_path_definition.kind !== 'enum') {
    return singleMatch(host_path_definition.members?.filter((definition) =>
      sameName(definition.name, member)
    ) ?? []);
  }

  const host_application = resolveHostApplicationQualifier(project, currentModule, qualifier);
  if (host_application === undefined) {
    return undefined;
  }

  return selectHostApplicationQualifiedDefinition(
    getUnqualifiedHostDefinitions(project),
    host_application,
    member
  );
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

function selectHostApplicationQualifiedDefinition(
  definitions: HostDefinition[],
  hostApplication: HostApplication,
  name: string
): HostDefinition | undefined {
  return singleMatch(definitions.filter((definition) =>
    definition.hostApplication === hostApplication && sameName(definition.name, name)
  ));
}

function getUnqualifiedHostDefinitions(project: VbaProject): HostDefinition[] {
  return project.hostDefinitions.flatMap((definition) => [
    definition,
    ...getUnqualifiedHostEnumMembers(definition)
  ]);
}

function getUnqualifiedHostEnumMembers(definition: HostDefinition): HostDefinition[] {
  if (definition.kind !== 'enum') {
    return [];
  }

  return (definition.members ?? [])
    .filter((member) => member.kind === 'enumMember' || member.kind === 'constant')
    .map((member) => ({
      ...member,
      hostApplication: member.hostApplication ?? definition.hostApplication,
      parentName: member.parentName ?? definition.name
    }));
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
  const pre_expression_diagnostics = [
    ...collectHeaderSyntaxDiagnostics(lines, codeStartLine),
    ...collectConditionalCompilationDiagnostics(lines, codeStartLine),
    ...collectPhysicalLineContinuationDiagnostics(lines, codeStartLine),
    ...collectDeclarationBlockDiagnostics(lines, codeStartLine),
    ...collectControlFlowDiagnostics(lines, codeStartLine)
  ];
  const expression_skip_lines = new Set(pre_expression_diagnostics.map((diagnostic) => diagnostic.range.start.line));
  const expression_diagnostics = collectExpressionDiagnostics(lines, codeStartLine, expression_skip_lines);
  const call_skip_lines = new Set([
    ...pre_expression_diagnostics.map((diagnostic) => diagnostic.range.start.line),
    ...expression_diagnostics.map((diagnostic) => diagnostic.range.start.line)
  ]);
  const call_diagnostics = collectCallSyntaxDiagnostics(lines, codeStartLine, call_skip_lines);
  const member_access_skip_lines = new Set([
    ...pre_expression_diagnostics.map((diagnostic) => diagnostic.range.start.line),
    ...expression_diagnostics.map((diagnostic) => diagnostic.range.start.line),
    ...call_diagnostics.map((diagnostic) => diagnostic.range.start.line)
  ]);
  const member_access_diagnostics = collectMemberAccessDiagnostics(
    lines,
    codeStartLine,
    member_access_skip_lines
  );
  const statement_specific_skip_lines = new Set([
    ...pre_expression_diagnostics.map((diagnostic) => diagnostic.range.start.line),
    ...expression_diagnostics.map((diagnostic) => diagnostic.range.start.line),
    ...call_diagnostics.map((diagnostic) => diagnostic.range.start.line),
    ...member_access_diagnostics.map((diagnostic) => diagnostic.range.start.line)
  ]);
  const diagnostics = [
    ...pre_expression_diagnostics,
    ...expression_diagnostics,
    ...call_diagnostics,
    ...member_access_diagnostics,
    ...collectStatementSpecificDiagnostics(lines, codeStartLine, statement_specific_skip_lines),
    ...collectBlockStructureDiagnostics(lines, codeStartLine)
  ];
  const header_diagnostic_lines = new Set(diagnostics.map((diagnostic) => diagnostic.range.start.line));
  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    if (header_diagnostic_lines.has(line_index)) {
      continue;
    }

    const line = lines[line_index];
    const lexical_diagnostics = collectLexicalSyntaxDiagnostics(line, line_index);
    diagnostics.push(...lexical_diagnostics);

    const callable_diagnostics = lexical_diagnostics.length === 0
      ? collectCallableDeclarationDiagnostics(line, line_index)
      : [];
    diagnostics.push(...callable_diagnostics);

    const declaration_diagnostics = lexical_diagnostics.length === 0 && callable_diagnostics.length === 0
      ? collectDeclarationDiagnostics(line, line_index)
      : [];
    diagnostics.push(...declaration_diagnostics);

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

    if (
      lexical_diagnostics.length === 0
      && callable_diagnostics.length === 0
      && declaration_diagnostics.length === 0
      && invalid_trailing_comment_range === undefined
    ) {
      diagnostics.push(...collectStatementBoundaryDiagnostics(line, line_index));
    }
  }

  return diagnostics;
}

function collectHeaderSyntaxDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const first_code_member_line = findFirstCodeMemberLine(lines, codeStartLine);

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const malformed_attribute_range = getMalformedAttributeRange(line, line_index);
    if (malformed_attribute_range !== undefined) {
      diagnostics.push({
        code: 'syntax.malformedAttribute',
        message: 'Attribute statement is malformed.',
        range: malformed_attribute_range,
        severity: 'error',
        source: 'vba-language-server'
      });
      continue;
    }

    const malformed_option = getMalformedOptionDiagnostic(line, line_index);
    if (malformed_option !== undefined) {
      diagnostics.push(malformed_option);
      continue;
    }

    if (
      first_code_member_line !== undefined
      && line_index > first_code_member_line
      && isMisplaceableHeaderStatement(line)
    ) {
      diagnostics.push({
        code: 'syntax.misplacedHeaderStatement',
        message: 'Module header statement must appear before code members.',
        range: getTrimmedLineRange(line, line_index),
        severity: 'error',
        source: 'vba-language-server'
      });
    }
  }

  return diagnostics;
}

interface ConditionalCompilationBlock {
  lineIndex: number;
  startCharacter: number;
  endCharacter: number;
}

function collectConditionalCompilationDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const stack: ConditionalCompilationBlock[] = [];

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const directive_start = skipWhitespace(line, 0, code_end);
    if (directive_start >= code_end || line[directive_start] !== '#') {
      continue;
    }

    const keyword_start = skipWhitespace(line, directive_start + 1, code_end);
    const keyword = readIdentifierTokenAt(line, keyword_start, code_end);
    if (keyword === undefined) {
      continue;
    }

    const directive_end = trimEndIndex(line, code_end);
    if (keyword.lowerText === 'const') {
      diagnostics.push(...collectConstDirectiveDiagnostics(line, line_index, keyword.end, directive_end));
      continue;
    }

    if (keyword.lowerText === 'if') {
      const then_start = findKeywordOutsideLiterals(line, 'then', keyword.end, directive_end);
      if (then_start === undefined) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          '#If directive must include Then.',
          line_index,
          directive_start,
          directive_end
        ));
        continue;
      }

      diagnostics.push(...collectConditionalCompilationExpressionDiagnostics(
        line,
        line_index,
        keyword.end,
        then_start,
        '#If directive is missing an expression.',
        then_start,
        then_start + 'Then'.length
      ));
      stack.push({
        lineIndex: line_index,
        startCharacter: directive_start,
        endCharacter: keyword.end
      });
      continue;
    }

    if (keyword.lowerText === 'elseif') {
      const then_start = findKeywordOutsideLiterals(line, 'then', keyword.end, directive_end);
      if (stack.length === 0) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          'Unexpected #ElseIf without matching #If.',
          line_index,
          directive_start,
          directive_end
        ));
        continue;
      }
      if (then_start === undefined) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          '#ElseIf directive must include Then.',
          line_index,
          directive_start,
          directive_end
        ));
        continue;
      }

      diagnostics.push(...collectConditionalCompilationExpressionDiagnostics(
        line,
        line_index,
        keyword.end,
        then_start,
        '#ElseIf directive is missing an expression.',
        then_start,
        then_start + 'Then'.length
      ));
      continue;
    }

    if (keyword.lowerText === 'else') {
      if (stack.length === 0) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          'Unexpected #Else without matching #If.',
          line_index,
          directive_start,
          directive_end
        ));
        continue;
      }

      const trailing_start = skipWhitespace(line, keyword.end, directive_end);
      if (trailing_start < directive_end) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          '#Else directive cannot include trailing text.',
          line_index,
          trailing_start,
          directive_end
        ));
      }
      continue;
    }

    if (keyword.lowerText === 'end') {
      const second_keyword_start = skipWhitespace(line, keyword.end, directive_end);
      const second_keyword = readIdentifierTokenAt(line, second_keyword_start, directive_end);
      if (second_keyword?.lowerText !== 'if') {
        continue;
      }

      const trailing_start = skipWhitespace(line, second_keyword.end, directive_end);
      if (trailing_start < directive_end) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          '#End If directive cannot include trailing text.',
          line_index,
          trailing_start,
          directive_end
        ));
        continue;
      }

      if (stack.length === 0) {
        diagnostics.push(createMalformedConditionalCompilationDiagnostic(
          'Unexpected #End If without matching #If.',
          line_index,
          directive_start,
          directive_end
        ));
        continue;
      }

      stack.pop();
    }
  }

  for (const block of stack) {
    diagnostics.push(createMalformedConditionalCompilationDiagnostic(
      '#If directive is missing #End If.',
      block.lineIndex,
      block.startCharacter,
      block.endCharacter
    ));
  }

  return diagnostics;
}

function collectConstDirectiveDiagnostics(
  line: string,
  lineIndex: number,
  valueStartCharacter: number,
  directiveEnd: number
): SyntaxDiagnostic[] {
  const name_start = skipWhitespace(line, valueStartCharacter, directiveEnd);
  const name = readIdentifierTokenAt(line, name_start, directiveEnd);
  if (name === undefined) {
    const equals_index = findTopLevelEquals(line, name_start, directiveEnd);
    const diagnostic_start = equals_index ?? name_start;
    const diagnostic_end = equals_index === undefined ? directiveEnd : equals_index + 1;
    return [createMalformedConditionalCompilationDiagnostic(
      '#Const directive is missing a name.',
      lineIndex,
      diagnostic_start,
      diagnostic_end
    )];
  }

  const equals_index = findTopLevelEquals(line, name.end, directiveEnd);
  if (equals_index === undefined) {
    return [createMalformedConditionalCompilationDiagnostic(
      '#Const directive is missing a value.',
      lineIndex,
      name.end,
      directiveEnd
    )];
  }

  const expression_start = equals_index + 1;
  return collectConditionalCompilationExpressionDiagnostics(
    line,
    lineIndex,
    expression_start,
    directiveEnd,
    '#Const directive is missing a value.',
    equals_index,
    equals_index + 1
  );
}

function collectConditionalCompilationExpressionDiagnostics(
  line: string,
  lineIndex: number,
  expressionStart: number,
  expressionEnd: number,
  missingExpressionMessage: string,
  missingExpressionStart: number,
  missingExpressionEnd: number
): SyntaxDiagnostic[] {
  const trimmed_start = skipWhitespace(line, expressionStart, expressionEnd);
  const trimmed_end = trimEndIndex(line, expressionEnd);
  if (trimmed_start >= trimmed_end) {
    return [createMalformedConditionalCompilationDiagnostic(
      missingExpressionMessage,
      lineIndex,
      missingExpressionStart,
      missingExpressionEnd
    )];
  }

  return collectMalformedExpressionDiagnostics(line, lineIndex, trimmed_start, trimmed_end)
    .map((diagnostic) => ({
      ...diagnostic,
      code: 'syntax.malformedConditionalCompilation' as const,
      message: conditionalCompilationExpressionMessage(diagnostic.message)
    }));
}

function conditionalCompilationExpressionMessage(message: string): string {
  if (message.startsWith('Expression ')) {
    return `Conditional compilation expression ${message.slice('Expression '.length)}`;
  }
  if (message.startsWith('Parenthesized expression ')) {
    return `Conditional compilation parenthesized expression ${message.slice('Parenthesized expression '.length)}`;
  }

  return 'Conditional compilation expression is malformed.';
}

function createMalformedConditionalCompilationDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedConditionalCompilation',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

function getMalformedAttributeRange(line: string, lineIndex: number): SourceRange | undefined {
  if (!/^\s*Attribute\b/i.test(line)) {
    return undefined;
  }

  const code_end = getCodeEndCharacter(line);
  const code_text = line.slice(0, code_end).trimEnd();
  const valid_attribute =
    /^\s*Attribute\s+[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\s*=\s*(?:"(?:""|[^"])*"|True|False|-?\d+(?:\.\d+)?)\s*$/i.exec(code_text);
  if (valid_attribute !== null) {
    return undefined;
  }

  const equals_index = line.indexOf('=');
  if (equals_index !== -1) {
    const value_start = skipWhitespace(line, equals_index + 1, code_end);
    return {
      start: { line: lineIndex, character: value_start },
      end: { line: lineIndex, character: code_end }
    };
  }

  return getTrimmedLineRange(line, lineIndex);
}

function getMalformedOptionDiagnostic(line: string, lineIndex: number): SyntaxDiagnostic | undefined {
  if (!/^\s*Option\b/i.test(line)) {
    return undefined;
  }

  const code_end = getCodeEndCharacter(line);
  const code_text = line.slice(0, code_end).trimEnd();
  if (/^\s*Option\s+Explicit\b/i.test(code_text)) {
    return undefined;
  }
  if (/^\s*Option\s+Private\s+Module\s*$/i.test(code_text)) {
    return undefined;
  }

  const base_match = /^\s*Option\s+Base\s+(\S+)/i.exec(code_text);
  if (base_match !== null) {
    if (/^\s*Option\s+Base\s+[01]\s*$/i.test(code_text)) {
      return undefined;
    }

    const value_start = line.indexOf(base_match[1], base_match.index);
    return {
      code: 'syntax.malformedOption',
      message: 'Option Base must be 0 or 1.',
      range: {
        start: { line: lineIndex, character: value_start },
        end: { line: lineIndex, character: code_end }
      },
      severity: 'error',
      source: 'vba-language-server'
    };
  }

  const compare_match = /^\s*Option\s+Compare\s+(\S+)/i.exec(code_text);
  if (compare_match !== null) {
    if (/^\s*Option\s+Compare\s+(?:Binary|Text|Database)\s*$/i.test(code_text)) {
      return undefined;
    }

    const value_start = line.indexOf(compare_match[1], compare_match.index);
    return {
      code: 'syntax.malformedOption',
      message: 'Option Compare must be Binary, Text, or Database.',
      range: {
        start: { line: lineIndex, character: value_start },
        end: { line: lineIndex, character: code_end }
      },
      severity: 'error',
      source: 'vba-language-server'
    };
  }

  if (/^\s*Option\s+Private\b/i.test(code_text)) {
    return {
      code: 'syntax.malformedOption',
      message: 'Option Private must be followed by Module.',
      range: getTrimmedLineRange(line, lineIndex),
      severity: 'error',
      source: 'vba-language-server'
    };
  }

  return {
    code: 'syntax.malformedOption',
    message: 'Option statement is malformed.',
    range: getTrimmedLineRange(line, lineIndex),
    severity: 'error',
    source: 'vba-language-server'
  };
}

function findFirstCodeMemberLine(lines: string[], codeStartLine: number): number | undefined {
  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const structure_text = getCodeTextForStructure(line).trim();
    if (
      structure_text === ''
      || isCommentOnlyLine(line)
      || isHeaderStatementLine(line)
      || /^VERSION\b/i.test(structure_text)
    ) {
      continue;
    }

    return line_index;
  }

  return undefined;
}

function isHeaderStatementLine(line: string): boolean {
  return /^\s*(?:Attribute|Option)\b/i.test(line);
}

function isMisplaceableHeaderStatement(line: string): boolean {
  return /^\s*Option\b/i.test(line) || /^\s*Attribute\s+VB_Name\b/i.test(line);
}

function getTrimmedLineRange(line: string, lineIndex: number): SourceRange {
  const start = line.search(/\S/);
  const end = line.trimEnd().length;
  return {
    start: { line: lineIndex, character: start === -1 ? 0 : start },
    end: { line: lineIndex, character: end }
  };
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

interface CallableDeclarationHead {
  kind: 'sub' | 'function' | 'property' | 'event' | 'declare';
  propertyKind?: 'Get' | 'Let' | 'Set';
  headEnd: number;
}

interface CallableDeclarationToken {
  text: string;
  lowerText: string;
  start: number;
  end: number;
}

function collectCallableDeclarationDiagnostics(line: string, lineIndex: number): SyntaxDiagnostic[] {
  const code_end = getCodeEndCharacter(line);
  const modifier_diagnostic = getCallableModifierDiagnostic(line, lineIndex, code_end);
  if (modifier_diagnostic !== undefined) {
    return [modifier_diagnostic];
  }

  const head = getCallableDeclarationHead(line, code_end);
  if (head === undefined) {
    return [];
  }

  if (head.kind === 'declare') {
    return collectDeclareDeclarationDiagnostics(line, lineIndex, code_end);
  }

  const name_start = skipWhitespace(line, head.headEnd, code_end);
  if (name_start >= code_end || !isIdentifierStart(line[name_start])) {
    return [createMalformedCallableDiagnostic(
      'Callable declaration is missing a name.',
      lineIndex,
      name_start,
      name_start
    )];
  }

  const name_end = readIdentifierEnd(line, name_start, code_end);
  const opening_paren = line.indexOf('(', name_end);
  let signature_tail_start = name_end;
  if (opening_paren !== -1 && opening_paren < code_end) {
    const closing_paren = findClosingParenInCode(line, opening_paren, code_end);
    if (closing_paren === undefined) {
      return [createMalformedCallableDiagnostic(
        'Callable parameter list is missing a closing parenthesis.',
        lineIndex,
        opening_paren,
        code_end
      )];
    }

    const parameter_diagnostics = collectParameterListDiagnostics(
      line,
      lineIndex,
      opening_paren + 1,
      closing_paren
    );
    if (parameter_diagnostics.length > 0) {
      return parameter_diagnostics;
    }

    signature_tail_start = closing_paren + 1;
  }

  const return_type_diagnostic = getMalformedReturnTypeDiagnostic(
    line,
    lineIndex,
    signature_tail_start,
    code_end
  );
  return return_type_diagnostic === undefined ? [] : [return_type_diagnostic];
}

function getCallableModifierDiagnostic(
  line: string,
  lineIndex: number,
  codeEnd: number
): SyntaxDiagnostic | undefined {
  const tokens = readLeadingIdentifierTokens(line, codeEnd);
  const callable_index = tokens.findIndex((token) => isCallableDeclarationToken(token));
  if (callable_index === -1) {
    return undefined;
  }

  let visibility_token: CallableDeclarationToken | undefined;
  let static_token: CallableDeclarationToken | undefined;
  for (let token_index = 0; token_index < callable_index; token_index += 1) {
    const token = tokens[token_index];
    if (isVisibilityModifier(token)) {
      if (static_token !== undefined) {
        return createMalformedCallableDiagnostic(
          'Visibility modifier must precede Static in a callable declaration.',
          lineIndex,
          token.start,
          token.end
        );
      }
      if (visibility_token !== undefined) {
        return createMalformedCallableDiagnostic(
          'Callable declaration has incompatible visibility modifiers.',
          lineIndex,
          token.start,
          token.end
        );
      }
      visibility_token = token;
      continue;
    }

    if (token.lowerText === 'static') {
      if (static_token !== undefined) {
        return createMalformedCallableDiagnostic(
          'Static modifier cannot be repeated in a callable declaration.',
          lineIndex,
          token.start,
          token.end
        );
      }
      static_token = token;
      continue;
    }

    return undefined;
  }

  const callable_token = tokens[callable_index];
  if (callable_token.lowerText === 'declare') {
    if (static_token !== undefined) {
      return createMalformedCallableDiagnostic(
        'Static modifier is not valid for Declare statements.',
        lineIndex,
        static_token.start,
        static_token.end
      );
    }
    if (visibility_token?.lowerText === 'friend') {
      return createMalformedCallableDiagnostic(
        'Friend modifier is not valid for Declare statements.',
        lineIndex,
        visibility_token.start,
        visibility_token.end
      );
    }
  }

  return undefined;
}

function readLeadingIdentifierTokens(line: string, codeEnd: number): CallableDeclarationToken[] {
  const tokens: CallableDeclarationToken[] = [];
  let character_index = skipWhitespace(line, 0, codeEnd);
  while (character_index < codeEnd && isIdentifierStart(line[character_index])) {
    const token_start = character_index;
    const token_end = readIdentifierEnd(line, token_start, codeEnd);
    const text = line.slice(token_start, token_end);
    tokens.push({
      text,
      lowerText: text.toLowerCase(),
      start: token_start,
      end: token_end
    });
    character_index = skipWhitespace(line, token_end, codeEnd);
  }

  return tokens;
}

function isCallableDeclarationToken(token: CallableDeclarationToken): boolean {
  return token.lowerText === 'sub'
    || token.lowerText === 'function'
    || token.lowerText === 'property'
    || token.lowerText === 'event'
    || token.lowerText === 'declare';
}

function isVisibilityModifier(token: CallableDeclarationToken): boolean {
  return token.lowerText === 'public'
    || token.lowerText === 'private'
    || token.lowerText === 'friend';
}

function getCallableDeclarationHead(line: string, codeEnd: number): CallableDeclarationHead | undefined {
  const code_text = line.slice(0, codeEnd);
  const match =
    /^\s*(?:(?:Public|Private|Friend|Static)\s+)*(?:(Sub|Function|Event)\b|Property\s+(Get|Let|Set)\b|Declare\b)/i.exec(code_text);
  if (match === null) {
    return undefined;
  }

  if (/Declare\b/i.test(match[0])) {
    return {
      kind: 'declare',
      headEnd: match[0].length
    };
  }
  if (match[2] !== undefined) {
    return {
      kind: 'property',
      propertyKind: canonicalPropertyKind(match[2]),
      headEnd: match[0].length
    };
  }

  return {
    kind: match[1].toLowerCase() as 'sub' | 'function' | 'event',
    headEnd: match[0].length
  };
}

function collectDeclareDeclarationDiagnostics(
  line: string,
  lineIndex: number,
  codeEnd: number
): SyntaxDiagnostic[] {
  const declare_match =
    /^\s*(?:(?:Public|Private)\s+)?Declare\s+(?:PtrSafe\s+)?(?:Sub|Function)\s+([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(line.slice(0, codeEnd));
  if (declare_match === null) {
    const declare_start = line.search(/\bDeclare\b/i);
    return [createMalformedCallableDiagnostic(
      'Declare statement is missing a Sub or Function name.',
      lineIndex,
      declare_start === -1 ? 0 : declare_start,
      codeEnd
    )];
  }

  const name = declare_match[1];
  const name_start = line.indexOf(name, declare_match.index);
  if (!/\bLib\s+"(?:""|[^"])*"/i.test(line.slice(name_start, codeEnd))) {
    return [createMalformedCallableDiagnostic(
      'Declare statement must specify Lib "library".',
      lineIndex,
      name_start,
      codeEnd
    )];
  }

  const opening_paren = line.indexOf('(', name_start + name.length);
  if (opening_paren !== -1 && opening_paren < codeEnd) {
    const closing_paren = findClosingParenInCode(line, opening_paren, codeEnd);
    if (closing_paren === undefined) {
      return [createMalformedCallableDiagnostic(
        'Callable parameter list is missing a closing parenthesis.',
        lineIndex,
        opening_paren,
        codeEnd
      )];
    }

    const parameter_diagnostics = collectParameterListDiagnostics(
      line,
      lineIndex,
      opening_paren + 1,
      closing_paren
    );
    if (parameter_diagnostics.length > 0) {
      return parameter_diagnostics;
    }
  }

  return [];
}

function collectParameterListDiagnostics(
  line: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic[] {
  if (startCharacter === endCharacter) {
    return [];
  }

  const segments = splitParameterSegments(line, startCharacter, endCharacter);
  for (let segment_index = 0; segment_index < segments.length; segment_index += 1) {
    const segment = segments[segment_index];
    const trimmed_start = skipWhitespace(line, segment.start, segment.end);
    const trimmed_end = trimEndIndex(line, segment.end);
    if (trimmed_start >= trimmed_end) {
      return [createMalformedCallableDiagnostic(
        'Parameter declaration is missing.',
        lineIndex,
        segment.start,
        segment.end
      )];
    }

    const segment_text = line.slice(trimmed_start, trimmed_end);
    const parameter_match =
      /^(?:(?:Optional|ByVal|ByRef|ParamArray)\s+)*([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(segment_text);
    if (parameter_match === null) {
      return [createMalformedCallableDiagnostic(
        'Parameter declaration is missing a name.',
        lineIndex,
        trimmed_start,
        trimmed_end
      )];
    }

    const optional_match = /\bOptional\b/i.exec(segment_text);
    const param_array_match = /\bParamArray\b/i.exec(segment_text);
    if (optional_match !== null && param_array_match !== null) {
      return [createMalformedCallableDiagnostic(
        'ParamArray cannot be combined with Optional.',
        lineIndex,
        trimmed_start + param_array_match.index,
        trimmed_start + param_array_match.index + param_array_match[0].length
      )];
    }
    if (param_array_match !== null && segment_index < segments.length - 1) {
      return [createMalformedCallableDiagnostic(
        'ParamArray must be the final parameter.',
        lineIndex,
        trimmed_start + param_array_match.index,
        trimmed_start + param_array_match.index + param_array_match[0].length
      )];
    }

    const default_value_match = /=\s*$/i.exec(segment_text);
    if (default_value_match !== null) {
      const equals_index = line.indexOf('=', trimmed_start);
      return [createMalformedCallableDiagnostic(
        'Optional parameter default value is missing.',
        lineIndex,
        equals_index,
        equals_index + 1
      )];
    }
  }

  return [];
}

function getMalformedReturnTypeDiagnostic(
  line: string,
  lineIndex: number,
  startCharacter: number,
  codeEnd: number
): SyntaxDiagnostic | undefined {
  const return_text = line.slice(startCharacter, codeEnd);
  const as_match = /\bAs\b/i.exec(return_text);
  if (as_match === null) {
    return undefined;
  }

  const as_start = startCharacter + as_match.index;
  const type_start = skipWhitespace(line, as_start + as_match[0].length, codeEnd);
  if (type_start >= codeEnd || !isIdentifierStart(line[type_start])) {
    return createMalformedCallableDiagnostic(
      'Callable return type is missing after As.',
      lineIndex,
      as_start,
      codeEnd
    );
  }

  return undefined;
}

function splitParameterSegments(
  line: string,
  startCharacter: number,
  endCharacter: number
): Array<{ start: number; end: number }> {
  const segments: Array<{ start: number; end: number }> = [];
  let segment_start = startCharacter;
  let character_index = startCharacter;
  let is_in_string = false;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }

        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }
    if (character === ',') {
      segments.push({ start: segment_start, end: character_index });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({ start: segment_start, end: endCharacter });
  return segments;
}

function findClosingParenInCode(line: string, openParen: number, endCharacter: number): number | undefined {
  let character_index = openParen + 1;
  let is_in_string = false;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }

        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === ')') {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

function createMalformedCallableDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedCallableDeclaration',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

function readIdentifierEnd(line: string, startCharacter: number, endCharacter: number): number {
  let character_index = startCharacter + 1;
  while (character_index < endCharacter && isIdentifierPart(line[character_index])) {
    character_index += 1;
  }

  return character_index;
}

function trimEndIndex(line: string, endCharacter: number): number {
  let character_index = endCharacter;
  while (character_index > 0 && /\s/.test(line[character_index - 1])) {
    character_index -= 1;
  }

  return character_index;
}

function canonicalPropertyKind(value: string): 'Get' | 'Let' | 'Set' {
  const lower_value = value.toLowerCase();
  if (lower_value === 'let') {
    return 'Let';
  }
  return lower_value === 'set' ? 'Set' : 'Get';
}

interface DeclarationListPrefix {
  kind: 'variable' | 'constant' | 'redim';
  declaratorsStart: number;
}

function collectDeclarationDiagnostics(line: string, lineIndex: number): SyntaxDiagnostic[] {
  const code_end = getCodeEndCharacter(line);
  if (getCallableDeclarationHead(line, code_end) !== undefined) {
    return [];
  }

  const def_type_diagnostics = collectDefTypeDeclarationDiagnostics(line, lineIndex, code_end);
  if (def_type_diagnostics !== undefined) {
    return def_type_diagnostics;
  }

  const with_events_diagnostics = collectWithEventsDeclarationDiagnostics(line, lineIndex, code_end);
  if (with_events_diagnostics !== undefined) {
    return with_events_diagnostics;
  }

  const prefix = getDeclarationListPrefix(line, code_end);
  if (prefix === undefined) {
    return [];
  }

  return collectDeclarationListDiagnostics(
    line,
    lineIndex,
    prefix.declaratorsStart,
    code_end,
    prefix.kind
  );
}

function getDeclarationListPrefix(line: string, codeEnd: number): DeclarationListPrefix | undefined {
  const first_token = readIdentifierTokenAt(line, skipWhitespace(line, 0, codeEnd), codeEnd);
  if (first_token === undefined) {
    return undefined;
  }

  if (first_token.lowerText === 'dim' || first_token.lowerText === 'static') {
    return {
      kind: 'variable',
      declaratorsStart: skipWhitespace(line, first_token.end, codeEnd)
    };
  }

  if (first_token.lowerText === 'const') {
    return {
      kind: 'constant',
      declaratorsStart: skipWhitespace(line, first_token.end, codeEnd)
    };
  }

  if (first_token.lowerText === 'redim') {
    const after_redim = skipWhitespace(line, first_token.end, codeEnd);
    const preserve_token = readIdentifierTokenAt(line, after_redim, codeEnd);
    const declarators_start = preserve_token?.lowerText === 'preserve'
      ? skipWhitespace(line, preserve_token.end, codeEnd)
      : after_redim;
    return {
      kind: 'redim',
      declaratorsStart: declarators_start
    };
  }

  if (first_token.lowerText !== 'public' && first_token.lowerText !== 'private') {
    return undefined;
  }

  const after_visibility = skipWhitespace(line, first_token.end, codeEnd);
  const second_token = readIdentifierTokenAt(line, after_visibility, codeEnd);
  if (second_token === undefined) {
    return {
      kind: 'variable',
      declaratorsStart: after_visibility
    };
  }

  if (second_token.lowerText === 'const') {
    return {
      kind: 'constant',
      declaratorsStart: skipWhitespace(line, second_token.end, codeEnd)
    };
  }

  if (isNonDataDeclarationKeyword(second_token.lowerText)) {
    return undefined;
  }

  return {
    kind: 'variable',
    declaratorsStart: after_visibility
  };
}

function isNonDataDeclarationKeyword(value: string): boolean {
  return value === 'sub'
    || value === 'function'
    || value === 'property'
    || value === 'event'
    || value === 'declare'
    || value === 'enum'
    || value === 'type'
    || value === 'implements'
    || value === 'option'
    || value === 'attribute';
}

function collectWithEventsDeclarationDiagnostics(
  line: string,
  lineIndex: number,
  codeEnd: number
): SyntaxDiagnostic[] | undefined {
  const match = /^\s*(?:(?:Public|Private|Dim)\s+)?WithEvents\b/i.exec(line.slice(0, codeEnd));
  if (match === null) {
    return undefined;
  }

  const name_start = skipWhitespace(line, match[0].length, codeEnd);
  const name_token = readIdentifierTokenAt(line, name_start, codeEnd);
  if (name_token === undefined || name_token.lowerText === 'as') {
    return [createMalformedDeclarationDiagnostic(
      'WithEvents declaration is missing an identifier.',
      lineIndex,
      name_start,
      name_token?.end ?? name_start
    )];
  }

  const after_name = skipWhitespace(line, name_token.end, codeEnd);
  if (!startsWithKeywordAt(line, after_name, 'as', codeEnd)) {
    return [createMalformedDeclarationDiagnostic(
      'WithEvents declaration must include As type.',
      lineIndex,
      after_name,
      codeEnd
    )];
  }

  const type_diagnostic = getTypeAnnotationDiagnostic(line, lineIndex, after_name, codeEnd);
  return type_diagnostic === undefined ? [] : [type_diagnostic];
}

function collectDefTypeDeclarationDiagnostics(
  line: string,
  lineIndex: number,
  codeEnd: number
): SyntaxDiagnostic[] | undefined {
  const match =
    /^\s*(DefBool|DefByte|DefInt|DefLng|DefLngLng|DefLngPtr|DefCur|DefSng|DefDbl|DefDec|DefDate|DefStr|DefObj|DefVar)\b/i.exec(line.slice(0, codeEnd));
  if (match === null) {
    return undefined;
  }

  const ranges_start = skipWhitespace(line, match[0].length, codeEnd);
  if (ranges_start >= codeEnd) {
    return [createMalformedDeclarationDiagnostic(
      'DefType declaration is missing a range.',
      lineIndex,
      ranges_start,
      ranges_start
    )];
  }

  const diagnostics: SyntaxDiagnostic[] = [];
  for (const segment of splitTopLevelSegments(line, ranges_start, codeEnd)) {
    const trimmed_start = skipWhitespace(line, segment.start, segment.end);
    const trimmed_end = trimEndIndex(line, segment.end);
    const range_text = line.slice(trimmed_start, trimmed_end);
    if (!/^[A-Za-z](?:\s*-\s*[A-Za-z])?$/.test(range_text)) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'DefType declaration range is malformed.',
        lineIndex,
        trimmed_start,
        trimmed_end
      ));
    }
  }

  return diagnostics;
}

function collectDeclarationListDiagnostics(
  line: string,
  lineIndex: number,
  startCharacter: number,
  codeEnd: number,
  kind: 'variable' | 'constant' | 'redim'
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  for (const segment of splitTopLevelSegments(line, startCharacter, codeEnd)) {
    diagnostics.push(...collectDeclaratorDiagnostics(line, lineIndex, segment.start, segment.end, kind));
  }

  return diagnostics;
}

function collectDeclaratorDiagnostics(
  line: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number,
  kind: 'variable' | 'constant' | 'redim'
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const trimmed_start = skipWhitespace(line, startCharacter, endCharacter);
  const trimmed_end = trimEndIndex(line, endCharacter);
  if (trimmed_start >= trimmed_end) {
    return [createMalformedDeclarationDiagnostic(
      'Declaration declarator is missing.',
      lineIndex,
      startCharacter,
      endCharacter
    )];
  }

  const name_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (name_token === undefined || name_token.lowerText === 'as') {
    return [createMalformedDeclarationDiagnostic(
      'Declaration is missing an identifier.',
      lineIndex,
      trimmed_start,
      name_token?.end ?? trimmed_end
    )];
  }

  let character_index = skipWhitespace(line, name_token.end, trimmed_end);
  if (character_index < trimmed_end && line[character_index] === '(') {
    const closing_paren = findClosingParenInCode(line, character_index, trimmed_end);
    if (closing_paren === undefined) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'Array bounds are missing a closing parenthesis.',
        lineIndex,
        character_index,
        trimmed_end
      ));
      return diagnostics;
    }

    const bounds_start = character_index + 1;
    if (!isValidArrayBounds(line.slice(bounds_start, closing_paren))) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'Array bounds are malformed.',
        lineIndex,
        bounds_start,
        closing_paren
      ));
    }
    character_index = skipWhitespace(line, closing_paren + 1, trimmed_end);
  }

  const constant_equals_index = kind === 'constant'
    ? findTopLevelEquals(line, character_index, trimmed_end)
    : undefined;
  const type_annotation_end = constant_equals_index ?? trimmed_end;
  if (startsWithKeywordAt(line, character_index, 'as', type_annotation_end)) {
    const type_diagnostic = getTypeAnnotationDiagnostic(line, lineIndex, character_index, type_annotation_end);
    if (type_diagnostic !== undefined) {
      diagnostics.push(type_diagnostic);
      return diagnostics;
    }
    character_index = skipWhitespace(
      line,
      readTypeAnnotationEnd(line, character_index, type_annotation_end),
      trimmed_end
    );
  }

  if (kind === 'constant') {
    const equals_index = constant_equals_index ?? findTopLevelEquals(line, character_index, trimmed_end);
    if (equals_index === undefined) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'Constant initializer is missing.',
        lineIndex,
        trimmed_end,
        trimmed_end
      ));
      return diagnostics;
    }

    const initializer_start = skipWhitespace(line, equals_index + 1, trimmed_end);
    if (initializer_start >= trimmed_end) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'Constant initializer is missing.',
        lineIndex,
        equals_index,
        trimmed_end
      ));
    } else if (!isPlausibleConstantInitializer(line.slice(initializer_start, trimmed_end))) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'Constant initializer is malformed.',
        lineIndex,
        initializer_start,
        trimmed_end
      ));
    }
  }

  return diagnostics;
}

function getTypeAnnotationDiagnostic(
  line: string,
  lineIndex: number,
  asStart: number,
  endCharacter: number
): SyntaxDiagnostic | undefined {
  let type_start = skipWhitespace(line, asStart + 'As'.length, endCharacter);
  if (startsWithKeywordAt(line, type_start, 'new', endCharacter)) {
    type_start = skipWhitespace(line, type_start + 'New'.length, endCharacter);
  }

  const type_end = readTypeNameEnd(line, type_start, endCharacter);
  if (type_end === undefined) {
    return createMalformedDeclarationDiagnostic(
      'Declaration type annotation is missing a type.',
      lineIndex,
      asStart,
      endCharacter
    );
  }

  let after_type = skipWhitespace(line, type_end, endCharacter);
  const fixed_length_suffix_end = readFixedLengthStringSuffixEnd(line, type_start, type_end, after_type, endCharacter);
  if (fixed_length_suffix_end !== undefined) {
    after_type = skipWhitespace(line, fixed_length_suffix_end, endCharacter);
  }
  if (after_type < endCharacter) {
    return createMalformedDeclarationDiagnostic(
      'Declaration type annotation is malformed.',
      lineIndex,
      after_type,
      endCharacter
    );
  }

  return undefined;
}

function readTypeAnnotationEnd(line: string, asStart: number, endCharacter: number): number {
  let type_start = skipWhitespace(line, asStart + 'As'.length, endCharacter);
  if (startsWithKeywordAt(line, type_start, 'new', endCharacter)) {
    type_start = skipWhitespace(line, type_start + 'New'.length, endCharacter);
  }

  const type_end = readTypeNameEnd(line, type_start, endCharacter);
  if (type_end === undefined) {
    return type_start;
  }

  const after_type = skipWhitespace(line, type_end, endCharacter);
  return readFixedLengthStringSuffixEnd(line, type_start, type_end, after_type, endCharacter) ?? type_end;
}

function readTypeNameEnd(line: string, startCharacter: number, endCharacter: number): number | undefined {
  if (startCharacter >= endCharacter || !isIdentifierStart(line[startCharacter])) {
    return undefined;
  }

  let type_end = readIdentifierEnd(line, startCharacter, endCharacter);
  if (line[type_end] === '.') {
    const member_start = type_end + 1;
    if (member_start >= endCharacter || !isIdentifierStart(line[member_start])) {
      return undefined;
    }
    type_end = readIdentifierEnd(line, member_start, endCharacter);
  }

  return type_end;
}

function readFixedLengthStringSuffixEnd(
  line: string,
  typeStart: number,
  typeEnd: number,
  suffixStart: number,
  endCharacter: number
): number | undefined {
  if (line.slice(typeStart, typeEnd).toLowerCase() !== 'string' || line[suffixStart] !== '*') {
    return undefined;
  }

  const length_start = skipWhitespace(line, suffixStart + 1, endCharacter);
  const length_match = /^\d+/.exec(line.slice(length_start, endCharacter));
  return length_match === null ? undefined : length_start + length_match[0].length;
}

function isValidArrayBounds(text: string): boolean {
  const trimmed_text = text.trim();
  if (trimmed_text === '') {
    return true;
  }

  return trimmed_text.split(',').every((segment) => {
    const trimmed_segment = segment.trim();
    return trimmed_segment !== ''
      && !/^To\b/i.test(trimmed_segment)
      && !/\bTo\s*$/i.test(trimmed_segment)
      && !/[+\-*/\\^&=<>]\s*$/.test(trimmed_segment);
  });
}

function isPlausibleConstantInitializer(text: string): boolean {
  const trimmed_text = text.trim();
  return trimmed_text !== ''
    && !/[,+\-*/\\^&=<>]\s*$/.test(trimmed_text)
    && !/^(?:,|[*/\\^&=<>])/.test(trimmed_text);
}

function splitTopLevelSegments(
  line: string,
  startCharacter: number,
  endCharacter: number
): Array<{ start: number; end: number }> {
  const segments: Array<{ start: number; end: number }> = [];
  let segment_start = startCharacter;
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === ',' && paren_depth === 0) {
      segments.push({ start: segment_start, end: character_index });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({ start: segment_start, end: endCharacter });
  return segments;
}

function findTopLevelEquals(line: string, startCharacter: number, endCharacter: number): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === '=' && paren_depth === 0) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

function readIdentifierTokenAt(
  line: string,
  startCharacter: number,
  endCharacter: number
): CallableDeclarationToken | undefined {
  if (startCharacter >= endCharacter || !isIdentifierStart(line[startCharacter])) {
    return undefined;
  }

  const token_end = readIdentifierEnd(line, startCharacter, endCharacter);
  const text = line.slice(startCharacter, token_end);
  return {
    text,
    lowerText: text.toLowerCase(),
    start: startCharacter,
    end: token_end
  };
}

function startsWithKeywordAt(
  line: string,
  startCharacter: number,
  keyword: string,
  endCharacter: number
): boolean {
  const token = readIdentifierTokenAt(line, startCharacter, endCharacter);
  return token?.lowerText === keyword.toLowerCase();
}

function createMalformedDeclarationDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedDeclaration',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

type DeclarationBlockKind = 'enum' | 'type';

interface ActiveDeclarationBlock {
  kind: DeclarationBlockKind;
  openerLine: number;
  keywordStart: number;
  keywordEnd: number;
}

interface DeclarationBlockHeader {
  kind: DeclarationBlockKind;
  keywordStart: number;
  keywordEnd: number;
  diagnostics: SyntaxDiagnostic[];
}

function collectDeclarationBlockDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let active_block: ActiveDeclarationBlock | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const trimmed_start = skipWhitespace(line, 0, code_end);
    if (trimmed_start >= code_end || isCommentOnlyLine(line)) {
      continue;
    }

    const closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (active_block !== undefined) {
      if (closer !== undefined) {
        if (closer.kind !== active_block.kind) {
          diagnostics.push(createMalformedDeclarationBlockDiagnostic(
            `Mismatched declaration block closer; expected ${formatDeclarationBlockCloser(active_block.kind)}.`,
            line_index,
            closer.start,
            closer.end
          ));
        }
        active_block = undefined;
        continue;
      }

      diagnostics.push(...collectDeclarationBlockMemberDiagnostics(line, line_index, code_end, active_block.kind));
      continue;
    }

    if (closer !== undefined) {
      diagnostics.push(createMalformedDeclarationBlockDiagnostic(
        `Unexpected ${formatDeclarationBlockCloser(closer.kind)} without a matching ${formatDeclarationBlockName(closer.kind)} block.`,
        line_index,
        closer.start,
        closer.end
      ));
      continue;
    }

    const header = getDeclarationBlockHeader(line, line_index, code_end);
    if (header === undefined) {
      continue;
    }

    diagnostics.push(...header.diagnostics);
    active_block = {
      kind: header.kind,
      openerLine: line_index,
      keywordStart: header.keywordStart,
      keywordEnd: header.keywordEnd
    };
  }

  if (active_block !== undefined) {
    diagnostics.push(createMalformedDeclarationBlockDiagnostic(
      `${formatDeclarationBlockName(active_block.kind)} block is missing ${formatDeclarationBlockCloser(active_block.kind)}.`,
      active_block.openerLine,
      active_block.keywordStart,
      active_block.keywordEnd
    ));
  }

  return diagnostics;
}

function getDeclarationBlockHeader(
  line: string,
  lineIndex: number,
  codeEnd: number
): DeclarationBlockHeader | undefined {
  const first_token = readIdentifierTokenAt(line, skipWhitespace(line, 0, codeEnd), codeEnd);
  if (first_token === undefined) {
    return undefined;
  }

  let keyword_token = first_token;
  let invalid_visibility_token: CallableDeclarationToken | undefined;
  if (first_token.lowerText === 'public' || first_token.lowerText === 'private' || first_token.lowerText === 'friend') {
    const second_token = readIdentifierTokenAt(line, skipWhitespace(line, first_token.end, codeEnd), codeEnd);
    if (second_token === undefined || (second_token.lowerText !== 'enum' && second_token.lowerText !== 'type')) {
      return undefined;
    }
    keyword_token = second_token;
    if (first_token.lowerText === 'friend') {
      invalid_visibility_token = first_token;
    }
  }

  if (keyword_token.lowerText !== 'enum' && keyword_token.lowerText !== 'type') {
    return undefined;
  }

  const kind = keyword_token.lowerText as DeclarationBlockKind;
  const diagnostics: SyntaxDiagnostic[] = [];
  if (invalid_visibility_token !== undefined) {
    diagnostics.push(createMalformedDeclarationBlockDiagnostic(
      `${formatDeclarationBlockName(kind)} declaration has an invalid visibility modifier.`,
      lineIndex,
      invalid_visibility_token.start,
      invalid_visibility_token.end
    ));
  }

  const name_start = skipWhitespace(line, keyword_token.end, codeEnd);
  const name_token = readIdentifierTokenAt(line, name_start, codeEnd);
  if (name_token === undefined) {
    diagnostics.push(createMalformedDeclarationBlockDiagnostic(
      `${formatDeclarationBlockName(kind)} declaration is missing a name.`,
      lineIndex,
      name_start,
      name_start
    ));
  } else {
    const after_name = skipWhitespace(line, name_token.end, codeEnd);
    if (after_name < codeEnd) {
      diagnostics.push(createMalformedDeclarationBlockDiagnostic(
        `${formatDeclarationBlockName(kind)} declaration header is malformed.`,
        lineIndex,
        after_name,
        codeEnd
      ));
    }
  }

  return {
    kind,
    keywordStart: keyword_token.start,
    keywordEnd: keyword_token.end,
    diagnostics
  };
}

function getDeclarationBlockCloser(
  line: string,
  lineIndex: number,
  codeEnd: number
): { kind: DeclarationBlockKind; start: number; end: number } | undefined {
  const match = /^\s*End\s+(Enum|Type)\b/i.exec(line.slice(0, codeEnd));
  if (match === null) {
    return undefined;
  }

  return {
    kind: match[1].toLowerCase() as DeclarationBlockKind,
    start: line.search(/\S/),
    end: match[0].length
  };
}

function collectDeclarationBlockMemberDiagnostics(
  line: string,
  lineIndex: number,
  codeEnd: number,
  kind: DeclarationBlockKind
): SyntaxDiagnostic[] {
  return kind === 'enum'
    ? collectEnumMemberDiagnostics(line, lineIndex, codeEnd)
    : collectTypeFieldDiagnostics(line, lineIndex, codeEnd);
}

function collectEnumMemberDiagnostics(line: string, lineIndex: number, codeEnd: number): SyntaxDiagnostic[] {
  const trimmed_start = skipWhitespace(line, 0, codeEnd);
  const trimmed_end = trimEndIndex(line, codeEnd);
  const first_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (first_token === undefined) {
    return [createMalformedDeclarationBlockDiagnostic(
      'Enum member declaration is missing a name.',
      lineIndex,
      trimmed_start,
      trimmed_end
    )];
  }

  if (isInvalidEnumMemberStatementKeyword(first_token.lowerText)) {
    return [createMalformedDeclarationBlockDiagnostic(
      'Statement is not valid inside an Enum block.',
      lineIndex,
      trimmed_start,
      trimmed_end
    )];
  }

  const after_name = skipWhitespace(line, first_token.end, trimmed_end);
  if (after_name >= trimmed_end) {
    return [];
  }

  if (line[after_name] !== '=') {
    return [createMalformedDeclarationBlockDiagnostic(
      'Enum member declaration is malformed.',
      lineIndex,
      after_name,
      trimmed_end
    )];
  }

  const initializer_start = skipWhitespace(line, after_name + 1, trimmed_end);
  if (initializer_start >= trimmed_end || !isPlausibleConstantInitializer(line.slice(initializer_start, trimmed_end))) {
    return [createMalformedDeclarationBlockDiagnostic(
      'Enum member initializer is malformed.',
      lineIndex,
      initializer_start >= trimmed_end ? after_name : initializer_start,
      trimmed_end
    )];
  }

  return [];
}

function isInvalidEnumMemberStatementKeyword(value: string): boolean {
  return value === 'dim'
    || value === 'static'
    || value === 'public'
    || value === 'private'
    || value === 'const'
    || value === 'redim'
    || value === 'sub'
    || value === 'function'
    || value === 'property'
    || value === 'event'
    || value === 'declare'
    || value === 'enum'
    || value === 'type'
    || value === 'withevents'
    || value === 'implements';
}

function collectTypeFieldDiagnostics(line: string, lineIndex: number, codeEnd: number): SyntaxDiagnostic[] {
  const trimmed_start = skipWhitespace(line, 0, codeEnd);
  const trimmed_end = trimEndIndex(line, codeEnd);
  const first_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (first_token !== undefined && isInvalidTypeFieldStatementKeyword(first_token.lowerText)) {
    return [createMalformedDeclarationBlockDiagnostic(
      'Statement is not valid inside a Type block.',
      lineIndex,
      trimmed_start,
      trimmed_end
    )];
  }

  return collectDeclaratorDiagnostics(line, lineIndex, trimmed_start, trimmed_end, 'variable')
    .map((diagnostic) => ({
      ...diagnostic,
      code: 'syntax.malformedDeclarationBlock' as const
    }));
}

function isInvalidTypeFieldStatementKeyword(value: string): boolean {
  return value === 'dim'
    || value === 'static'
    || value === 'public'
    || value === 'private'
    || value === 'const'
    || value === 'redim'
    || value === 'sub'
    || value === 'function'
    || value === 'property'
    || value === 'event'
    || value === 'declare'
    || value === 'enum'
    || value === 'type'
    || value === 'withevents'
    || value === 'implements';
}

function formatDeclarationBlockName(kind: DeclarationBlockKind): 'Enum' | 'Type' {
  return kind === 'enum' ? 'Enum' : 'Type';
}

function formatDeclarationBlockCloser(kind: DeclarationBlockKind): 'End Enum' | 'End Type' {
  return kind === 'enum' ? 'End Enum' : 'End Type';
}

function createMalformedDeclarationBlockDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedDeclarationBlock',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

type ExecutableBlockKind =
  | 'sub'
  | 'function'
  | 'property'
  | 'if'
  | 'select'
  | 'with'
  | 'for'
  | 'do'
  | 'while';

interface ExecutableBlock {
  kind: ExecutableBlockKind;
  openerName: string;
  openerLine: number;
  openerStart: number;
  openerEnd: number;
  expectedCloser: string;
}

interface ExecutableBlockCloser {
  kind: ExecutableBlockKind;
  label: string;
  openerName: string;
  start: number;
  end: number;
}

function collectBlockStructureDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const stack: ExecutableBlock[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '' || isCommentOnlyLine(line) || isHeaderLine(structure_text)) {
      continue;
    }

    const declaration_closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (skipped_declaration_block !== undefined) {
      if (declaration_closer?.kind === skipped_declaration_block) {
        skipped_declaration_block = undefined;
      }
      continue;
    }

    const declaration_header = getDeclarationBlockHeader(line, line_index, code_end);
    if (declaration_header !== undefined) {
      skipped_declaration_block = declaration_header.kind;
      continue;
    }
    if (declaration_closer !== undefined) {
      continue;
    }

    const closer = getExecutableBlockCloser(line, code_end);
    if (closer !== undefined) {
      const open_block = stack[stack.length - 1];
      if (open_block === undefined) {
        if (shouldSuppressUnexpectedCallableCloser(lines, line_index, closer)) {
          continue;
        }
        diagnostics.push(createMalformedBlockStructureDiagnostic(
          `Unexpected ${closer.label} without a matching ${closer.openerName} block.`,
          line_index,
          closer.start,
          closer.end
        ));
        continue;
      }

      if (closer.kind !== open_block.kind) {
        const matching_index = findLastExecutableBlockIndex(stack, closer.kind);
        if (matching_index === -1) {
          diagnostics.push(createMalformedBlockStructureDiagnostic(
            `Unexpected ${closer.label} without a matching ${closer.openerName} block.`,
            line_index,
            closer.start,
            closer.end
          ));
          continue;
        }

        diagnostics.push(createMalformedBlockStructureDiagnostic(
          `Mismatched block closer; expected ${open_block.expectedCloser}.`,
          line_index,
          closer.start,
          closer.end
        ));

        stack.length = matching_index;
        continue;
      }

      stack.pop();
      continue;
    }

    const opener = getExecutableBlockOpener(line, code_end);
    if (opener !== undefined) {
      stack.push({
        ...opener,
        openerLine: line_index
      });
    }
  }

  for (let stack_index = stack.length - 1; stack_index >= 0; stack_index -= 1) {
    const open_block = stack[stack_index];
    diagnostics.push(createMalformedBlockStructureDiagnostic(
      `${open_block.openerName} block is missing ${open_block.expectedCloser}.`,
      open_block.openerLine,
      open_block.openerStart,
      open_block.openerEnd
    ));
  }

  return diagnostics;
}

function getExecutableBlockOpener(
  line: string,
  codeEnd: number
): Omit<ExecutableBlock, 'openerLine'> | undefined {
  const code_text = line.slice(0, codeEnd);
  const structure_text = getCodeTextForStructure(line).trim();
  const matchers: Array<{
    kind: ExecutableBlockKind;
    openerName: string;
    expectedCloser: string;
    pattern: RegExp;
    keyword: string;
  }> = [
    {
      kind: 'sub',
      openerName: 'Sub',
      expectedCloser: 'End Sub',
      pattern: new RegExp(`^\\s*(?:(?:Public|Private|Friend|Static)\\s+)*Sub\\s+${C_IDENTIFIER_PATTERN.source}\\b`, 'i'),
      keyword: 'Sub'
    },
    {
      kind: 'function',
      openerName: 'Function',
      expectedCloser: 'End Function',
      pattern: new RegExp(`^\\s*(?:(?:Public|Private|Friend|Static)\\s+)*Function\\s+${C_IDENTIFIER_PATTERN.source}\\b`, 'i'),
      keyword: 'Function'
    },
    {
      kind: 'property',
      openerName: 'Property',
      expectedCloser: 'End Property',
      pattern: new RegExp(`^\\s*(?:(?:Public|Private|Friend|Static)\\s+)*Property\\s+(?:Get|Let|Set)\\s+${C_IDENTIFIER_PATTERN.source}\\b`, 'i'),
      keyword: 'Property'
    },
    {
      kind: 'if',
      openerName: 'If',
      expectedCloser: 'End If',
      pattern: /^\s*If\b.*\bThen\s*$/i,
      keyword: 'If'
    },
    {
      kind: 'for',
      openerName: 'For',
      expectedCloser: 'Next',
      pattern: new RegExp(`^\\s*For\\s+(?:Each\\s+${C_IDENTIFIER_PATTERN.source}\\s+In\\s+\\S|${C_IDENTIFIER_PATTERN.source}\\s*=\\s*\\S.+\\bTo\\b\\s*\\S)`, 'i'),
      keyword: 'For'
    },
    {
      kind: 'do',
      openerName: 'Do',
      expectedCloser: 'Loop',
      pattern: /^\s*Do(?:\s+(?:While|Until)\s+\S.*)?\s*$/i,
      keyword: 'Do'
    },
    {
      kind: 'while',
      openerName: 'While',
      expectedCloser: 'Wend',
      pattern: /^\s*While\s+\S/i,
      keyword: 'While'
    },
    {
      kind: 'select',
      openerName: 'Select',
      expectedCloser: 'End Select',
      pattern: /^\s*Select\s+Case\s+\S/i,
      keyword: 'Select'
    },
    {
      kind: 'with',
      openerName: 'With',
      expectedCloser: 'End With',
      pattern: /^\s*With\s+\S/i,
      keyword: 'With'
    }
  ];

  for (const matcher of matchers) {
    if (matcher.kind === 'if' && /^ElseIf\b/i.test(structure_text)) {
      continue;
    }
    if (!matcher.pattern.test(code_text)) {
      continue;
    }

    const keyword_match = new RegExp(`\\b${matcher.keyword}\\b`, 'i').exec(code_text);
    const opener_start = keyword_match?.index ?? line.search(/\S/);
    return {
      kind: matcher.kind,
      openerName: matcher.openerName,
      openerStart: opener_start,
      openerEnd: opener_start + matcher.keyword.length,
      expectedCloser: matcher.expectedCloser
    };
  }

  return undefined;
}

function getExecutableBlockCloser(line: string, codeEnd: number): ExecutableBlockCloser | undefined {
  const code_text = line.slice(0, codeEnd);
  const end_match = /^\s*End\s+(Sub|Function|Property|If|Select|With)\s*$/i.exec(code_text);
  if (end_match !== null) {
    const closer_name = `End ${canonicalExecutableCloserName(end_match[1])}`;
    const kind = executableCloserKind(end_match[1]);
    return {
      kind,
      label: closer_name,
      openerName: executableOpenerName(kind),
      start: line.search(/\S/),
      end: end_match[0].length
    };
  }

  const next_match = new RegExp(`^\\s*Next(?:\\s+${C_IDENTIFIER_PATTERN.source}(?:\\s*,\\s*${C_IDENTIFIER_PATTERN.source})*)?\\s*$`, 'i').exec(code_text);
  if (next_match !== null) {
    return {
      kind: 'for',
      label: 'Next',
      openerName: 'For',
      start: line.search(/\S/),
      end: next_match[0].length
    };
  }

  const loop_match = /^\s*Loop(?:\s+(?:While|Until)\b.+)?\s*$/i.exec(code_text);
  if (loop_match !== null) {
    return {
      kind: 'do',
      label: 'Loop',
      openerName: 'Do',
      start: line.search(/\S/),
      end: loop_match[0].length
    };
  }

  const wend_match = /^\s*Wend\s*$/i.exec(code_text);
  if (wend_match === null) {
    return undefined;
  }

  return {
    kind: 'while',
    label: 'Wend',
    openerName: 'While',
    start: line.search(/\S/),
    end: wend_match[0].length
  };
}

function shouldSuppressUnexpectedCallableCloser(
  lines: string[],
  closerLine: number,
  closer: ExecutableBlockCloser
): boolean {
  if (closer.kind !== 'sub' && closer.kind !== 'function' && closer.kind !== 'property') {
    return false;
  }

  for (let line_index = closerLine - 1; line_index >= 0; line_index -= 1) {
    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    if (skipWhitespace(line, 0, code_end) >= code_end || isCommentOnlyLine(line)) {
      continue;
    }

    const head = getCallableDeclarationHead(line, code_end);
    if (head === undefined) {
      return false;
    }

    const head_kind = head.kind === 'sub' || head.kind === 'function' || head.kind === 'property'
      ? head.kind
      : undefined;
    return head_kind === closer.kind
      && collectCallableDeclarationDiagnostics(line, line_index).length > 0;
  }

  return false;
}

function executableCloserKind(value: string): ExecutableBlockKind {
  const lower_value = value.toLowerCase();
  if (lower_value === 'sub') {
    return 'sub';
  }
  if (lower_value === 'function') {
    return 'function';
  }
  if (lower_value === 'property') {
    return 'property';
  }
  if (lower_value === 'if') {
    return 'if';
  }
  if (lower_value === 'select') {
    return 'select';
  }
  if (lower_value === 'with') {
    return 'with';
  }
  if (lower_value === 'next') {
    return 'for';
  }
  if (lower_value === 'loop') {
    return 'do';
  }
  return 'while';
}

function canonicalExecutableCloserName(value: string): string {
  const lower_value = value.toLowerCase();
  if (lower_value === 'sub') {
    return 'Sub';
  }
  if (lower_value === 'function') {
    return 'Function';
  }
  if (lower_value === 'property') {
    return 'Property';
  }
  if (lower_value === 'if') {
    return 'If';
  }
  if (lower_value === 'select') {
    return 'Select';
  }
  if (lower_value === 'with') {
    return 'With';
  }
  if (lower_value === 'next') {
    return 'Next';
  }
  if (lower_value === 'loop') {
    return 'Loop';
  }
  return 'Wend';
}

function executableOpenerName(kind: ExecutableBlockKind): string {
  if (kind === 'sub') {
    return 'Sub';
  }
  if (kind === 'function') {
    return 'Function';
  }
  if (kind === 'property') {
    return 'Property';
  }
  if (kind === 'if') {
    return 'If';
  }
  if (kind === 'select') {
    return 'Select';
  }
  if (kind === 'with') {
    return 'With';
  }
  if (kind === 'for') {
    return 'For';
  }
  if (kind === 'do') {
    return 'Do';
  }
  return 'While';
}

function findLastExecutableBlockIndex(stack: ExecutableBlock[], kind: ExecutableBlockKind): number {
  for (let stack_index = stack.length - 1; stack_index >= 0; stack_index -= 1) {
    if (stack[stack_index].kind === kind) {
      return stack_index;
    }
  }

  return -1;
}

function createMalformedBlockStructureDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedBlockStructure',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

interface ControlFlowState {
  kind: 'if' | 'select';
  seenElse?: boolean;
  seenCaseElse?: boolean;
}

function collectControlFlowDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const stack: ControlFlowState[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '' || isCommentOnlyLine(line) || isHeaderLine(structure_text)) {
      continue;
    }

    const declaration_closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (skipped_declaration_block !== undefined) {
      if (declaration_closer?.kind === skipped_declaration_block) {
        skipped_declaration_block = undefined;
      }
      continue;
    }
    const declaration_header = getDeclarationBlockHeader(line, line_index, code_end);
    if (declaration_header !== undefined) {
      skipped_declaration_block = declaration_header.kind;
      continue;
    }
    if (declaration_closer !== undefined) {
      continue;
    }

    const trimmed_start = line.search(/\S/);
    const trimmed_end = getCodeEndCharacter(line);
    const trimmed_code = line.slice(trimmed_start === -1 ? 0 : trimmed_start, trimmed_end).trimEnd();

    if (/^If\b/i.test(trimmed_code)) {
      if (!/\bThen\b/i.test(trimmed_code)) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'If block opener must include Then.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      } else if (/\bThen\s*$/i.test(trimmed_code)) {
        stack.push({ kind: 'if', seenElse: false });
      }
      continue;
    }

    if (/^ElseIf\b/i.test(trimmed_code)) {
      const if_state = findLastControlFlowState(stack, 'if');
      if (!/\bThen\b/i.test(trimmed_code)) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'ElseIf clause must include Then.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      } else if (if_state?.seenElse === true) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'ElseIf cannot appear after Else in the same If block.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      }
      continue;
    }

    if (/^Else\b/i.test(trimmed_code)) {
      const if_state = findLastControlFlowState(stack, 'if');
      if (if_state !== undefined) {
        if (if_state.seenElse === true) {
          diagnostics.push(createMalformedControlFlowDiagnostic(
            'Else cannot appear more than once in the same If block.',
            line_index,
            trimmed_start,
            trimmed_end
          ));
        }
        if_state.seenElse = true;
      }
      continue;
    }

    if (/^End\s+If\s*$/i.test(trimmed_code)) {
      popLastControlFlowState(stack, 'if');
      continue;
    }

    if (/^Select\s+Case\b/i.test(trimmed_code)) {
      if (!/^Select\s+Case\s+\S/i.test(trimmed_code)) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'Select Case opener must include an expression.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      } else {
        stack.push({ kind: 'select', seenCaseElse: false });
      }
      continue;
    }

    if (/^Case\b/i.test(trimmed_code)) {
      const select_state = findLastControlFlowState(stack, 'select');
      const is_case_else = /^Case\s+Else\b/i.test(trimmed_code);
      if (!is_case_else && !/^Case\s+\S/i.test(trimmed_code)) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'Case clause must include an expression or Else.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      } else if (select_state?.seenCaseElse === true && !is_case_else) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'Case cannot appear after Case Else in the same Select block.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      }
      if (select_state !== undefined && is_case_else) {
        select_state.seenCaseElse = true;
      }
      continue;
    }

    if (/^End\s+Select\s*$/i.test(trimmed_code)) {
      popLastControlFlowState(stack, 'select');
      continue;
    }

    if (/^For\s+Each\b/i.test(trimmed_code)) {
      if (!new RegExp(`^For\\s+Each\\s+${C_IDENTIFIER_PATTERN.source}\\s+In\\s+\\S`, 'i').test(trimmed_code)) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'For Each opener must include an item and collection expression.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      }
      continue;
    }

    if (/^For\b/i.test(trimmed_code)) {
      if (!new RegExp(`^For\\s+${C_IDENTIFIER_PATTERN.source}\\s*=\\s*\\S.+\\bTo\\b\\s*\\S`, 'i').test(trimmed_code)) {
        diagnostics.push(createMalformedControlFlowDiagnostic(
          'For opener must include a start expression and To expression.',
          line_index,
          trimmed_start,
          trimmed_end
        ));
      }
      continue;
    }

    if (/^Loop\s+(?:While|Until)\b/i.test(trimmed_code) && !/^Loop\s+(?:While|Until)\s+\S/i.test(trimmed_code)) {
      const condition_kind = /^Loop\s+While\b/i.test(trimmed_code) ? 'While' : 'Until';
      diagnostics.push(createMalformedControlFlowDiagnostic(
        `Loop ${condition_kind} clause must include a condition.`,
        line_index,
        trimmed_start,
        trimmed_end
      ));
      continue;
    }

    if (/^Do\s+(?:While|Until)\b/i.test(trimmed_code) && !/^Do\s+(?:While|Until)\s+\S/i.test(trimmed_code)) {
      const condition_kind = /^Do\s+While\b/i.test(trimmed_code) ? 'While' : 'Until';
      diagnostics.push(createMalformedControlFlowDiagnostic(
        `Do ${condition_kind} opener must include a condition.`,
        line_index,
        trimmed_start,
        trimmed_end
      ));
      continue;
    }

    if (/^While\b/i.test(trimmed_code) && !/^While\s+\S/i.test(trimmed_code)) {
      diagnostics.push(createMalformedControlFlowDiagnostic(
        'While opener must include a condition.',
        line_index,
        trimmed_start,
        trimmed_end
      ));
      continue;
    }

    if (/^With\b/i.test(trimmed_code) && !/^With\s+\S/i.test(trimmed_code)) {
      diagnostics.push(createMalformedControlFlowDiagnostic(
        'With opener must include a receiver expression.',
        line_index,
        trimmed_start,
        trimmed_end
      ));
    }
  }

  return diagnostics;
}

function findLastControlFlowState(
  stack: ControlFlowState[],
  kind: ControlFlowState['kind']
): ControlFlowState | undefined {
  for (let stack_index = stack.length - 1; stack_index >= 0; stack_index -= 1) {
    if (stack[stack_index].kind === kind) {
      return stack[stack_index];
    }
  }

  return undefined;
}

function popLastControlFlowState(stack: ControlFlowState[], kind: ControlFlowState['kind']): void {
  for (let stack_index = stack.length - 1; stack_index >= 0; stack_index -= 1) {
    if (stack[stack_index].kind === kind) {
      stack.length = stack_index;
      return;
    }
  }
}

function createMalformedControlFlowDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedControlFlow',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

interface ExpressionSpan {
  start: number;
  end: number;
}

interface ExpressionOperatorRange {
  start: number;
  end: number;
}

function collectExpressionDiagnostics(
  lines: string[],
  codeStartLine: number,
  skipLines: Set<number>
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    if (skipLines.has(line_index)) {
      continue;
    }

    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '' || isCommentOnlyLine(line) || isHeaderLine(structure_text)) {
      continue;
    }
    if (collectLexicalSyntaxDiagnostics(line, line_index).length > 0) {
      continue;
    }
    if (getInvalidTrailingCommentContinuationRange(line, line_index) !== undefined) {
      continue;
    }

    const declaration_closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (skipped_declaration_block !== undefined) {
      if (declaration_closer?.kind === skipped_declaration_block) {
        skipped_declaration_block = undefined;
      }
      continue;
    }

    const declaration_header = getDeclarationBlockHeader(line, line_index, code_end);
    if (declaration_header !== undefined) {
      skipped_declaration_block = declaration_header.kind;
      continue;
    }
    if (declaration_closer !== undefined) {
      continue;
    }

    for (const segment of getStatementSegments(line)) {
      const segment_end = Math.min(segment.end, code_end);
      if (segment.start >= segment_end) {
        continue;
      }

      const spans = getExpressionSpansForDiagnostics(line, segment.start, segment_end);
      for (const span of spans) {
        diagnostics.push(...collectMalformedExpressionDiagnostics(line, line_index, span.start, span.end));
      }
    }
  }

  return diagnostics;
}

function getExpressionSpansForDiagnostics(
  line: string,
  segmentStart: number,
  segmentEnd: number
): ExpressionSpan[] {
  const trimmed_start = skipWhitespace(line, segmentStart, segmentEnd);
  const trimmed_end = trimEndIndex(line, segmentEnd);
  if (trimmed_start >= trimmed_end) {
    return [];
  }

  const first_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (first_token === undefined) {
    return [];
  }

  if (first_token.lowerText === 'if' || first_token.lowerText === 'elseif') {
    const then_start = findKeywordOutsideLiterals(line, 'then', first_token.end, trimmed_end);
    return then_start === undefined
      ? []
      : [{ start: first_token.end, end: then_start }];
  }

  if (first_token.lowerText === 'while' || first_token.lowerText === 'with') {
    return [{ start: first_token.end, end: trimmed_end }];
  }

  if (first_token.lowerText === 'do' || first_token.lowerText === 'loop') {
    const condition_keyword = readIdentifierTokenAt(line, skipWhitespace(line, first_token.end, trimmed_end), trimmed_end);
    return condition_keyword !== undefined
      && (condition_keyword.lowerText === 'while' || condition_keyword.lowerText === 'until')
      ? [{ start: condition_keyword.end, end: trimmed_end }]
      : [];
  }

  if (first_token.lowerText === 'select') {
    const case_keyword = readIdentifierTokenAt(line, skipWhitespace(line, first_token.end, trimmed_end), trimmed_end);
    return case_keyword?.lowerText === 'case'
      ? [{ start: case_keyword.end, end: trimmed_end }]
      : [];
  }

  if (first_token.lowerText === 'case') {
    const after_case = skipWhitespace(line, first_token.end, trimmed_end);
    if (startsWithKeywordAt(line, after_case, 'else', trimmed_end)) {
      return [];
    }

    return splitTopLevelSegments(line, after_case, trimmed_end);
  }

  if (shouldSkipExpressionDiagnosticsForStatement(first_token.lowerText)) {
    return [];
  }

  const equals_index = findTopLevelAssignmentEquals(line, first_token.end, trimmed_end);
  return equals_index === undefined
    ? []
    : [{ start: equals_index + 1, end: trimmed_end }];
}

function shouldSkipExpressionDiagnosticsForStatement(firstToken: string): boolean {
  return firstToken === 'attribute'
    || firstToken === 'option'
    || firstToken === 'const'
    || firstToken === 'dim'
    || firstToken === 'static'
    || firstToken === 'redim'
    || firstToken === 'public'
    || firstToken === 'private'
    || firstToken === 'friend'
    || firstToken === 'declare'
    || firstToken === 'sub'
    || firstToken === 'function'
    || firstToken === 'property'
    || firstToken === 'event'
    || firstToken === 'enum'
    || firstToken === 'type'
    || firstToken === 'implements'
    || firstToken === 'end';
}

function collectMalformedExpressionDiagnostics(
  line: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const trimmed_start = skipWhitespace(line, startCharacter, endCharacter);
  const trimmed_end = trimEndIndex(line, endCharacter);
  if (trimmed_start >= trimmed_end) {
    diagnostics.push(createMalformedExpressionDiagnostic(
      'Expression is missing an operand.',
      lineIndex,
      startCharacter,
      endCharacter
    ));
    return diagnostics;
  }

  const paren_stack: number[] = [];
  let character_index = trimmed_start;
  let expecting_operand = true;
  let last_operator: ExpressionOperatorRange | undefined;

  while (character_index < trimmed_end) {
    const character = line[character_index];
    if (/\s/.test(character)) {
      character_index += 1;
      continue;
    }

    if (character === '"') {
      const string_end = getStringLiteralEnd(line, character_index);
      if (string_end === undefined || string_end > trimmed_end) {
        break;
      }
      expecting_operand = false;
      last_operator = undefined;
      character_index = string_end;
      continue;
    }

    if (character === '#' && !shouldSkipHashCharacter(line, character_index)) {
      const closing_index = line.indexOf('#', character_index + 1);
      if (closing_index === -1 || closing_index >= trimmed_end) {
        break;
      }
      expecting_operand = false;
      last_operator = undefined;
      character_index = closing_index + 1;
      continue;
    }

    if (character === '(') {
      paren_stack.push(character_index);
      expecting_operand = true;
      last_operator = undefined;
      character_index += 1;
      continue;
    }

    if (character === ')') {
      if (paren_stack.length === 0) {
        diagnostics.push(createMalformedExpressionDiagnostic(
          'Unexpected closing parenthesis in expression.',
          lineIndex,
          character_index,
          character_index + 1
        ));
        return diagnostics;
      }

      paren_stack.pop();
      expecting_operand = false;
      last_operator = undefined;
      character_index += 1;
      continue;
    }

    if (character === ',') {
      if (expecting_operand) {
        diagnostics.push(createMalformedExpressionDiagnostic(
          'Expression is missing an operand before this separator.',
          lineIndex,
          character_index,
          character_index + 1
        ));
        return diagnostics;
      }

      expecting_operand = true;
      last_operator = undefined;
      character_index += 1;
      continue;
    }

    if (isExpressionSymbolicOperatorStart(character)) {
      const operator_end = readExpressionSymbolicOperatorEnd(line, character_index, trimmed_end);
      if (expecting_operand && !isUnaryExpressionOperator(line.slice(character_index, operator_end))) {
        diagnostics.push(createMalformedExpressionDiagnostic(
          'Expression is missing an operand before this operator.',
          lineIndex,
          character_index,
          operator_end
        ));
        return diagnostics;
      }

      expecting_operand = true;
      last_operator = { start: character_index, end: operator_end };
      character_index = operator_end;
      continue;
    }

    const token = readIdentifierTokenAt(line, character_index, trimmed_end);
    if (token !== undefined) {
      if (isExpressionWordOperator(token.lowerText)) {
        if (expecting_operand && token.lowerText !== 'not') {
          diagnostics.push(createMalformedExpressionDiagnostic(
            'Expression is missing an operand before this operator.',
            lineIndex,
            token.start,
            token.end
          ));
          return diagnostics;
        }

        expecting_operand = true;
        last_operator = { start: token.start, end: token.end };
        character_index = token.end;
        continue;
      }

      expecting_operand = false;
      last_operator = undefined;
      character_index = token.end;
      continue;
    }

    const number_end = readNumericLiteralEnd(line, character_index, trimmed_end);
    if (number_end !== undefined) {
      expecting_operand = false;
      last_operator = undefined;
      character_index = number_end;
      continue;
    }

    character_index += 1;
  }

  if (paren_stack.length > 0) {
    const open_paren = paren_stack[paren_stack.length - 1];
    diagnostics.push(createMalformedExpressionDiagnostic(
      'Parenthesized expression is missing a closing parenthesis.',
      lineIndex,
      open_paren,
      open_paren + 1
    ));
    return diagnostics;
  }

  if (expecting_operand && last_operator !== undefined) {
    diagnostics.push(createMalformedExpressionDiagnostic(
      'Expression is missing an operand after this operator.',
      lineIndex,
      last_operator.start,
      last_operator.end
    ));
  }

  return diagnostics;
}

function findTopLevelAssignmentEquals(
  line: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === '=' && paren_depth === 0 && isAssignmentEquals(line, character_index)) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

function isAssignmentEquals(line: string, equalsIndex: number): boolean {
  const previous_character = findPreviousNonWhitespace(line, equalsIndex - 1);
  if (
    previous_character !== undefined
    && (line[previous_character] === '<' || line[previous_character] === '>' || line[previous_character] === ':')
  ) {
    return false;
  }

  return true;
}

function findKeywordOutsideLiterals(
  line: string,
  keyword: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  const lower_keyword = keyword.toLowerCase();

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }

    if (isKeywordTokenAt(line, character_index, lower_keyword, endCharacter)) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

function isKeywordTokenAt(
  line: string,
  characterIndex: number,
  lowerKeyword: string,
  endCharacter: number
): boolean {
  if (line.slice(characterIndex, characterIndex + lowerKeyword.length).toLowerCase() !== lowerKeyword) {
    return false;
  }

  const before = characterIndex === 0 ? '' : line[characterIndex - 1];
  const after_index = characterIndex + lowerKeyword.length;
  const after = after_index >= endCharacter ? '' : line[after_index];
  return (before === '' || !isIdentifierPart(before))
    && (after === '' || !isIdentifierPart(after));
}

function isExpressionSymbolicOperatorStart(character: string): boolean {
  return '+-*/\\^&=<>'.includes(character);
}

function readExpressionSymbolicOperatorEnd(line: string, startCharacter: number, endCharacter: number): number {
  const character = line[startCharacter];
  const next_character = startCharacter + 1 < endCharacter ? line[startCharacter + 1] : '';
  if ((character === '<' || character === '>') && next_character === '=') {
    return startCharacter + 2;
  }
  if (character === '<' && next_character === '>') {
    return startCharacter + 2;
  }

  return startCharacter + 1;
}

function isUnaryExpressionOperator(operatorText: string): boolean {
  return operatorText === '+' || operatorText === '-';
}

function isExpressionWordOperator(value: string): boolean {
  return value === 'and'
    || value === 'or'
    || value === 'xor'
    || value === 'eqv'
    || value === 'imp'
    || value === 'mod'
    || value === 'like'
    || value === 'is'
    || value === 'not';
}

function readNumericLiteralEnd(
  line: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  if (!/[0-9]/.test(line[startCharacter] ?? '')) {
    return undefined;
  }

  let character_index = startCharacter + 1;
  while (character_index < endCharacter && /[0-9]/.test(line[character_index])) {
    character_index += 1;
  }

  if (line[character_index] === '.') {
    character_index += 1;
    while (character_index < endCharacter && /[0-9]/.test(line[character_index])) {
      character_index += 1;
    }
  }

  if (character_index < endCharacter && /[%&!#@$]/.test(line[character_index])) {
    character_index += 1;
  }

  return character_index;
}

function createMalformedExpressionDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedExpression',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

interface LogicalCodeSource extends LogicalSourceText {
  startLine: number;
  endLine: number;
}

function collectCallSyntaxDiagnostics(
  lines: string[],
  codeStartLine: number,
  skipLines: Set<number>
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    if (skipLines.has(line_index) || isContinuationTail(lines, line_index)) {
      continue;
    }

    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '' || isCommentOnlyLine(line) || isHeaderLine(structure_text)) {
      continue;
    }
    if (collectLexicalSyntaxDiagnostics(line, line_index).length > 0) {
      continue;
    }

    const declaration_closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (skipped_declaration_block !== undefined) {
      if (declaration_closer?.kind === skipped_declaration_block) {
        skipped_declaration_block = undefined;
      }
      continue;
    }

    const declaration_header = getDeclarationBlockHeader(line, line_index, code_end);
    if (declaration_header !== undefined) {
      skipped_declaration_block = declaration_header.kind;
      continue;
    }
    if (declaration_closer !== undefined) {
      continue;
    }

    const source = getLogicalCodeSourceFromLine(lines, line_index);
    if (source === undefined || source.positions.length === 0) {
      continue;
    }
    if (sourceLineRange(source).some((source_line) =>
      skipLines.has(source_line)
      || collectLexicalSyntaxDiagnostics(lines[source_line] ?? '', source_line).length > 0
      || getInvalidTrailingCommentContinuationRange(lines[source_line] ?? '', source_line) !== undefined
    )) {
      continue;
    }

    for (const segment of getCallStatementSegments(source.text)) {
      diagnostics.push(...collectMalformedCallDiagnosticsForSegment(source, segment.start, segment.end));
    }
  }

  return diagnostics;
}

function sourceLineRange(source: LogicalCodeSource): number[] {
  const lines: number[] = [];
  for (let line_index = source.startLine; line_index <= source.endLine; line_index += 1) {
    lines.push(line_index);
  }

  return lines;
}

function getCallStatementSegments(text: string): StatementSegment[] {
  const segments: StatementSegment[] = [];
  let segment_start = 0;
  let character_index = 0;
  let is_in_string = false;

  while (character_index < text.length) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === "'" || isRemCommentStart(text, character_index)) {
      break;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }
    if (character === ':' && text[character_index + 1] !== '=') {
      segments.push({
        start: segment_start,
        end: character_index,
        terminator: character_index,
        text: text.slice(segment_start, character_index)
      });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({
    start: segment_start,
    end: character_index,
    text: text.slice(segment_start, character_index)
  });
  return segments;
}

function isContinuationTail(lines: string[], lineIndex: number): boolean {
  return lineIndex > 0 && getCodeContinuationMarkerStart(lines[lineIndex - 1] ?? '') !== undefined;
}

function getLogicalCodeSourceFromLine(lines: string[], startLine: number): LogicalCodeSource | undefined {
  const text_parts: string[] = [];
  const positions: SourcePosition[] = [];

  for (let line_index = startLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index] ?? '';
    const continuation_marker = getCodeContinuationMarkerStart(line);
    const line_end = continuation_marker ?? getCodeEndCharacter(line);

    text_parts.push(line.slice(0, line_end));
    for (let character = 0; character < line_end; character += 1) {
      positions.push({ line: line_index, character });
    }

    if (continuation_marker === undefined) {
      return {
        text: text_parts.join(''),
        positions,
        startLine,
        endLine: line_index
      };
    }
  }

  return undefined;
}

function collectMalformedCallDiagnosticsForSegment(
  source: LogicalCodeSource,
  segmentStart: number,
  segmentEnd: number
): SyntaxDiagnostic[] {
  const trimmed_start = skipWhitespace(source.text, segmentStart, segmentEnd);
  const trimmed_end = trimEndIndex(source.text, segmentEnd);
  if (trimmed_start >= trimmed_end) {
    return [];
  }

  const first_token = readIdentifierTokenAt(source.text, trimmed_start, trimmed_end);
  if (first_token === undefined || shouldSkipCallDiagnosticsForStatement(first_token.lowerText)) {
    return [];
  }

  if (first_token.lowerText === 'call') {
    return collectCallKeywordDiagnostics(source, first_token.end, trimmed_end);
  }

  if (first_token.lowerText === 'raiseevent') {
    return collectRaiseEventCallDiagnostics(source, first_token.end, trimmed_end);
  }

  const parenthesized_diagnostics = collectParenthesizedCallDiagnostics(source, trimmed_start, trimmed_end, {
    disallowNamedArguments: false
  });
  if (parenthesized_diagnostics.length > 0) {
    return parenthesized_diagnostics;
  }

  const target = readCallableTargetAt(source.text, trimmed_start, trimmed_end);
  if (target === undefined) {
    return [];
  }

  const args_start = skipWhitespace(source.text, target.end, trimmed_end);
  if (args_start >= trimmed_end || source.text[args_start] === '=') {
    return [];
  }

  return collectCallArgumentListDiagnostics(source, args_start, trimmed_end, {
    disallowNamedArguments: false
  });
}

function shouldSkipCallDiagnosticsForStatement(firstToken: string): boolean {
  return firstToken === 'attribute'
    || firstToken === 'option'
    || firstToken === 'const'
    || firstToken === 'dim'
    || firstToken === 'static'
    || firstToken === 'redim'
    || firstToken === 'public'
    || firstToken === 'private'
    || firstToken === 'friend'
    || firstToken === 'declare'
    || firstToken === 'sub'
    || firstToken === 'function'
    || firstToken === 'property'
    || firstToken === 'event'
    || firstToken === 'enum'
    || firstToken === 'type'
    || firstToken === 'implements'
    || firstToken === 'end'
    || firstToken === 'if'
    || firstToken === 'elseif'
    || firstToken === 'else'
    || firstToken === 'for'
    || firstToken === 'do'
    || firstToken === 'loop'
    || firstToken === 'while'
    || firstToken === 'wend'
    || firstToken === 'with'
    || firstToken === 'select'
    || firstToken === 'case'
    || firstToken === 'next'
    || firstToken === 'exit';
}

function collectCallKeywordDiagnostics(
  source: LogicalCodeSource,
  callKeywordEnd: number,
  segmentEnd: number
): SyntaxDiagnostic[] {
  const target_start = skipWhitespace(source.text, callKeywordEnd, segmentEnd);
  const target = readCallableTargetAt(source.text, target_start, segmentEnd);
  if (target === undefined) {
    return [createMalformedCallDiagnostic(
      'Call statement is missing a procedure name.',
      source,
      target_start,
      Math.min(target_start + 1, segmentEnd)
    )];
  }

  const args_start = skipWhitespace(source.text, target.end, segmentEnd);
  if (args_start >= segmentEnd) {
    return [];
  }

  if (source.text[args_start] !== '(') {
    return [createMalformedCallDiagnostic(
      'Call statement arguments must be enclosed in parentheses.',
      source,
      args_start,
      segmentEnd
    )];
  }

  return collectSingleParenthesizedCallDiagnostics(source, args_start, segmentEnd, {
    disallowNamedArguments: false
  });
}

function collectRaiseEventCallDiagnostics(
  source: LogicalCodeSource,
  raiseEventEnd: number,
  segmentEnd: number
): SyntaxDiagnostic[] {
  const target_start = skipWhitespace(source.text, raiseEventEnd, segmentEnd);
  const target = readCallableTargetAt(source.text, target_start, segmentEnd);
  if (target === undefined) {
    return [createMalformedCallDiagnostic(
      'RaiseEvent statement is missing an event name.',
      source,
      target_start,
      Math.min(target_start + 1, segmentEnd)
    )];
  }

  const args_start = skipWhitespace(source.text, target.end, segmentEnd);
  if (args_start >= segmentEnd) {
    return [];
  }

  if (source.text[args_start] === '(') {
    return collectSingleParenthesizedCallDiagnostics(source, args_start, segmentEnd, {
      disallowNamedArguments: true
    });
  }

  return collectCallArgumentListDiagnostics(source, args_start, segmentEnd, {
    disallowNamedArguments: true
  });
}

function collectParenthesizedCallDiagnostics(
  source: LogicalCodeSource,
  startCharacter: number,
  endCharacter: number,
  options: { disallowNamedArguments: boolean }
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let character_index = startCharacter;

  while (character_index < endCharacter) {
    const character = source.text[character_index];
    if (character === '"') {
      const string_end = getStringLiteralEnd(source.text, character_index);
      if (string_end === undefined || string_end > endCharacter) {
        break;
      }
      character_index = string_end;
      continue;
    }

    if (character === '(' && isCallArgumentListOpenParen(source.text, character_index, startCharacter)) {
      diagnostics.push(...collectSingleParenthesizedCallDiagnostics(source, character_index, endCharacter, options));
      const close_paren = findMatchingParen(source.text, character_index, endCharacter);
      if (close_paren === undefined) {
        return diagnostics;
      }
      character_index = close_paren + 1;
      continue;
    }

    character_index += 1;
  }

  return diagnostics;
}

function collectSingleParenthesizedCallDiagnostics(
  source: LogicalCodeSource,
  openParen: number,
  endCharacter: number,
  options: { disallowNamedArguments: boolean }
): SyntaxDiagnostic[] {
  const close_paren = findMatchingParen(source.text, openParen, endCharacter);
  if (close_paren === undefined) {
    if (isInProgressCallArgumentList(source.text, openParen + 1, endCharacter)) {
      return [];
    }

    return [createMalformedCallDiagnostic(
      'Call argument list is missing a closing parenthesis.',
      source,
      openParen,
      openParen + 1
    )];
  }

  return collectCallArgumentListDiagnostics(source, openParen + 1, close_paren, options);
}

function isInProgressCallArgumentList(text: string, startCharacter: number, endCharacter: number): boolean {
  const trimmed_start = skipWhitespace(text, startCharacter, endCharacter);
  if (trimmed_start >= endCharacter) {
    return false;
  }

  return findPreviousTopLevelComma(text, startCharacter, endCharacter) !== undefined;
}

function findPreviousTopLevelComma(
  text: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;
  let comma_index: number | undefined;

  while (character_index < endCharacter) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === ',' && paren_depth === 0) {
      comma_index = character_index;
    }

    character_index += 1;
  }

  return comma_index;
}

function collectCallArgumentListDiagnostics(
  source: LogicalCodeSource,
  startCharacter: number,
  endCharacter: number,
  options: { disallowNamedArguments: boolean }
): SyntaxDiagnostic[] {
  if (startCharacter >= endCharacter) {
    return [];
  }

  const segments = splitTopLevelSegments(source.text, startCharacter, endCharacter);
  for (let segment_index = 0; segment_index < segments.length; segment_index += 1) {
    const segment = segments[segment_index];
    const trimmed_start = skipWhitespace(source.text, segment.start, segment.end);
    const trimmed_end = trimEndIndex(source.text, segment.end);
    if (trimmed_start >= trimmed_end) {
      if (segment_index === segments.length - 1 && segment_index > 0) {
        const comma_index = findPreviousNonWhitespace(source.text, segment.start - 1);
        return [createMalformedCallDiagnostic(
          'Call argument list has a missing argument after this comma.',
          source,
          comma_index ?? segment.start,
          (comma_index ?? segment.start) + 1
        )];
      }
      continue;
    }

    const named_separator = findTopLevelNamedArgumentSeparator(source.text, trimmed_start, trimmed_end);
    if (named_separator === undefined) {
      continue;
    }

    if (options.disallowNamedArguments) {
      return [createMalformedCallDiagnostic(
        'RaiseEvent arguments cannot use named-argument syntax.',
        source,
        named_separator,
        named_separator + 2
      )];
    }

    const name_end = trimEndIndex(source.text, named_separator);
    const name_token = readIdentifierTokenAt(source.text, trimmed_start, name_end);
    if (name_token === undefined || skipWhitespace(source.text, name_token.end, name_end) < name_end) {
      return [createMalformedCallDiagnostic(
        'Named argument is missing a valid name.',
        source,
        trimmed_start,
        named_separator
      )];
    }

    const value_start = skipWhitespace(source.text, named_separator + 2, trimmed_end);
    if (value_start >= trimmed_end) {
      return [createMalformedCallDiagnostic(
        'Named argument is missing a value.',
        source,
        named_separator,
        named_separator + 2
      )];
    }
  }

  return [];
}

function readCallableTargetAt(
  text: string,
  startCharacter: number,
  endCharacter: number
): { start: number; end: number } | undefined {
  let character_index = skipWhitespace(text, startCharacter, endCharacter);
  const target_start = character_index;
  if (text[character_index] === '.') {
    character_index = skipWhitespace(text, character_index + 1, endCharacter);
  }

  let token = readIdentifierTokenAt(text, character_index, endCharacter);
  if (token === undefined) {
    return undefined;
  }
  character_index = token.end;

  while (true) {
    const dot_index = skipWhitespace(text, character_index, endCharacter);
    if (text[dot_index] !== '.') {
      break;
    }

    const member_start = skipWhitespace(text, dot_index + 1, endCharacter);
    token = readIdentifierTokenAt(text, member_start, endCharacter);
    if (token === undefined) {
      break;
    }
    character_index = token.end;
  }

  return {
    start: target_start,
    end: character_index
  };
}

function isCallArgumentListOpenParen(text: string, openParen: number, startCharacter: number): boolean {
  const previous_character = findPreviousNonWhitespace(text, openParen - 1);
  return previous_character !== undefined
    && previous_character >= startCharacter
    && (isIdentifierPart(text[previous_character]) || text[previous_character] === ')');
}

function findTopLevelNamedArgumentSeparator(
  text: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter - 1) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === ':' && text[character_index + 1] === '=' && paren_depth === 0) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

function createMalformedCallDiagnostic(
  message: string,
  source: LogicalCodeSource,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedCall',
    message,
    range: getLogicalSourceRange(source, startCharacter, endCharacter),
    severity: 'error',
    source: 'vba-language-server'
  };
}

type LeadingDotContext = 'none' | 'with' | 'continued';

function collectMemberAccessDiagnostics(
  lines: string[],
  codeStartLine: number,
  skipLines: Set<number>
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;
  let with_depth = 0;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '' || isCommentOnlyLine(line) || isHeaderLine(structure_text)) {
      continue;
    }

    const declaration_closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (skipped_declaration_block !== undefined) {
      if (declaration_closer?.kind === skipped_declaration_block) {
        skipped_declaration_block = undefined;
      }
      continue;
    }

    const declaration_header = getDeclarationBlockHeader(line, line_index, code_end);
    if (declaration_header !== undefined) {
      skipped_declaration_block = declaration_header.kind;
      continue;
    }
    if (declaration_closer !== undefined) {
      continue;
    }

    if (/^End\s+With\b/i.test(structure_text)) {
      with_depth = Math.max(0, with_depth - 1);
    }

    if (
      !skipLines.has(line_index)
      && collectLexicalSyntaxDiagnostics(line, line_index).length === 0
      && getInvalidTrailingCommentContinuationRange(line, line_index) === undefined
    ) {
      const leading_dot_context = getLeadingDotContext(lines, line_index, with_depth);
      diagnostics.push(...collectMalformedMemberAccessDiagnosticsForLine(
        line,
        line_index,
        code_end,
        leading_dot_context
      ));
    }

    if (/^With\b/i.test(structure_text) && !/^With\b.*\bThen\b/i.test(structure_text)) {
      with_depth += 1;
    }
  }

  return diagnostics;
}

function getLeadingDotContext(
  lines: string[],
  lineIndex: number,
  withDepth: number
): LeadingDotContext {
  if (withDepth > 0) {
    return 'with';
  }
  return isContinuationTail(lines, lineIndex) ? 'continued' : 'none';
}

function collectMalformedMemberAccessDiagnosticsForLine(
  line: string,
  lineIndex: number,
  codeEnd: number,
  leadingDotContext: LeadingDotContext
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let character_index = 0;
  let is_in_string = false;

  while (character_index < codeEnd) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }

    if (character !== '.' && character !== '!') {
      character_index += 1;
      continue;
    }

    if (character === '.' && isDecimalPoint(line, character_index, codeEnd)) {
      character_index += 1;
      continue;
    }

    const previous_character = findPreviousNonWhitespace(line, character_index - 1);
    const is_leading_dot = character === '.'
      && (previous_character === undefined || line[previous_character] === ':');
    const member_start = skipWhitespace(line, character_index + 1, codeEnd);
    if (
      character === '.'
      && member_start >= codeEnd
      && isSingleIdentifierQualifierDot(line, character_index)
    ) {
      character_index += 1;
      continue;
    }

    if (is_leading_dot) {
      if (leadingDotContext === 'none') {
        diagnostics.push(createMalformedMemberAccessDiagnostic(
          'Leading-dot member access is only valid inside a With block or continued member chain.',
          lineIndex,
          character_index,
          character_index + 1
        ));
        character_index += 1;
        continue;
      }

      if (
        leadingDotContext === 'continued'
        && (member_start >= codeEnd || !isIdentifierStart(line[member_start]))
      ) {
        diagnostics.push(createMalformedMemberAccessDiagnostic(
          'Member access is missing a member name.',
          lineIndex,
          character_index,
          character_index + 1
        ));
        character_index += 1;
        continue;
      }

      character_index += 1;
      continue;
    }

    if (member_start >= codeEnd || !isIdentifierStart(line[member_start])) {
      diagnostics.push(createMalformedMemberAccessDiagnostic(
        'Member access is missing a member name.',
        lineIndex,
        character_index,
        character_index + 1
      ));
      character_index += 1;
      continue;
    }

    character_index += 1;
  }

  return diagnostics;
}

function isSingleIdentifierQualifierDot(line: string, dotIndex: number): boolean {
  const qualifier_text = line.slice(0, dotIndex).trim();
  return new RegExp(`^${C_IDENTIFIER_PATTERN.source}$`).test(qualifier_text);
}

function isDecimalPoint(line: string, dotIndex: number, codeEnd: number): boolean {
  return dotIndex > 0
    && dotIndex + 1 < codeEnd
    && /[0-9]/.test(line[dotIndex - 1])
    && /[0-9]/.test(line[dotIndex + 1]);
}

function createMalformedMemberAccessDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedMemberAccess',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

function collectStatementSpecificDiagnostics(
  lines: string[],
  codeStartLine: number,
  skipLines: Set<number>
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    if (skipLines.has(line_index)) {
      continue;
    }

    const line = lines[line_index];
    const code_end = getCodeEndCharacter(line);
    const structure_text = getCodeTextForStructure(line).trim();
    if (structure_text === '' || isCommentOnlyLine(line) || isHeaderLine(structure_text)) {
      continue;
    }
    if (
      collectLexicalSyntaxDiagnostics(line, line_index).length > 0
      || getInvalidTrailingCommentContinuationRange(line, line_index) !== undefined
    ) {
      continue;
    }

    const declaration_closer = getDeclarationBlockCloser(line, line_index, code_end);
    if (skipped_declaration_block !== undefined) {
      if (declaration_closer?.kind === skipped_declaration_block) {
        skipped_declaration_block = undefined;
      }
      continue;
    }

    const declaration_header = getDeclarationBlockHeader(line, line_index, code_end);
    if (declaration_header !== undefined) {
      skipped_declaration_block = declaration_header.kind;
      continue;
    }
    if (declaration_closer !== undefined) {
      continue;
    }

    for (const segment of getStatementSegments(line)) {
      const diagnostic = getStatementSpecificDiagnostic(line, line_index, segment);
      if (diagnostic !== undefined) {
        diagnostics.push(diagnostic);
      }
    }
  }

  return diagnostics;
}

function getStatementSpecificDiagnostic(
  line: string,
  lineIndex: number,
  segment: StatementSegment
): SyntaxDiagnostic | undefined {
  const statement = getStatementBoundaryText(segment);
  const text = statement.text;
  if (text === '' || isLineNumberStatement(text) || isValidLabelSegment(segment, text)) {
    return undefined;
  }

  const first_token = readIdentifierTokenAt(text, 0, text.length);
  if (first_token === undefined) {
    return undefined;
  }

  const start = statement.start;
  switch (first_token.lowerText) {
    case 'let':
    case 'set':
    case 'lset':
    case 'rset':
      return getAssignmentKeywordStatementDiagnostic(line, lineIndex, text, start, first_token.text);
    case 'mid':
      return getMidStatementDiagnostic(line, lineIndex, text, start);
    case 'goto':
    case 'gosub':
      return hasBranchTarget(text.slice(first_token.end))
        ? undefined
        : createMalformedStatementDiagnostic(
          `${first_token.text} statement is missing a branch target.`,
          lineIndex,
          start,
          start + text.length
        );
    case 'on':
      return getOnStatementDiagnostic(lineIndex, text, start);
    case 'resume':
      return getResumeStatementDiagnostic(lineIndex, text, start);
    case 'exit':
      if (/^Exit\s+(?:Sub|Function|Property|For|Do)\b/i.test(text)) {
        return undefined;
      }
      return /^Exit\s+(?:Sub|Function|Property|For|Do)\s*$/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          'Exit statement must specify Sub, Function, Property, For, or Do.',
          lineIndex,
          start,
          start + text.length
        );
    case 'error':
      return /\S/.test(text.slice(first_token.end))
        ? undefined
        : createMalformedStatementDiagnostic('Error statement is missing an error number.', lineIndex, start, start + text.length);
    case 'load':
    case 'unload':
    case 'erase':
    case 'kill':
    case 'mkdir':
    case 'rmdir':
    case 'chdir':
    case 'chdrive':
    case 'appactivate':
    case 'sendkeys':
      return /\S/.test(text.slice(first_token.end))
        ? undefined
        : createMalformedStatementDiagnostic(
          `${formatStatementKeyword(first_token.text)} statement is missing an argument.`,
          lineIndex,
          start,
          start + text.length
        );
    case 'randomize':
      return undefined;
    case 'reset':
    case 'beep':
      return text.slice(first_token.end).trim() === ''
        ? undefined
        : createMalformedStatementDiagnostic(
          `${formatStatementKeyword(first_token.text)} statement does not take arguments.`,
          lineIndex,
          start + first_token.end + /^\s*/.exec(text.slice(first_token.end))![0].length,
          start + text.length
        );
    case 'seek':
    case 'lock':
    case 'unlock':
      return /^Seek\s+#\s*\S+(?:\s*,\s*\S.*)?$/i.test(text)
        || /^(?:Lock|Unlock)\s+#\s*\S+(?:\s*,\s*\S.*)?$/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          `${formatStatementKeyword(first_token.text)} statement must include a file number.`,
          lineIndex,
          start,
          start + text.length
        );
    case 'open':
      return /^Open\s+\S.+\bFor\s+(?:Input|Output|Append|Binary|Random)\b.*\bAs\s+#\s*\S+/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          'Open statement must include For mode and As # file number.',
          lineIndex,
          start,
          start + text.length
        );
    case 'close':
      return getCloseStatementDiagnostic(lineIndex, text, start);
    case 'get':
    case 'put':
      return /^(?:Get|Put)\s+#\s*\S+/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          `${formatStatementKeyword(first_token.text)} statement must include a file number.`,
          lineIndex,
          start,
          start + text.length
        );
    case 'input':
      return /^Input\s+#\s*\S+\s*,\s*\S/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          'Input # statement must include a file number and target list.',
          lineIndex,
          start,
          start + text.length
        );
    case 'line':
      return /^Line\s+Input\s+#\s*\S+\s*,\s*\S/i.test(text)
        ? undefined
        : /^Line\s+Input\b/i.test(text)
          ? createMalformedStatementDiagnostic(
            'Line Input # statement must include a file number and target.',
            lineIndex,
            start,
            start + text.length
          )
          : undefined;
    case 'print':
    case 'write':
      return /^(?:Print|Write)\s+#\s*\S+/i.test(text) || !/^(?:Print|Write)\s+#/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          `${formatStatementKeyword(first_token.text)} # statement has a malformed file number.`,
          lineIndex,
          start,
          start + text.length
        );
    case 'name':
      return /\bAs\s+\S/i.test(text)
        ? undefined
        : createMalformedStatementDiagnostic('Name statement must include As newPath.', lineIndex, start, start + text.length);
    case 'filecopy':
      return hasTopLevelComma(text, first_token.end, text.length)
        ? undefined
        : createMalformedStatementDiagnostic(
          'FileCopy statement must include source and destination expressions.',
          lineIndex,
          start,
          start + text.length
        );
    case 'setattr':
      return hasTopLevelComma(text, first_token.end, text.length)
        ? undefined
        : createMalformedStatementDiagnostic(
          'SetAttr statement must include path and attributes expressions.',
          lineIndex,
          start,
          start + text.length
        );
    case 'date':
    case 'time':
      return new RegExp(`^${first_token.text}\\s*=\\s*\\S`, 'i').test(text)
        ? undefined
        : createMalformedStatementDiagnostic(
          `${formatStatementKeyword(first_token.text)} statement must assign with =.`,
          lineIndex,
          start,
          start + text.length
        );
    default:
      return undefined;
  }
}

function getAssignmentKeywordStatementDiagnostic(
  line: string,
  lineIndex: number,
  text: string,
  startCharacter: number,
  keyword: string
): SyntaxDiagnostic | undefined {
  const equals_index = findTopLevelEquals(line, startCharacter + keyword.length, startCharacter + text.length);
  return equals_index === undefined
    ? createMalformedStatementDiagnostic(
      `${formatStatementKeyword(keyword)} statement must assign with =.`,
      lineIndex,
      startCharacter + text.length,
      startCharacter + text.length
    )
    : undefined;
}

function getMidStatementDiagnostic(
  line: string,
  lineIndex: number,
  text: string,
  startCharacter: number
): SyntaxDiagnostic | undefined {
  if (/^Mid\$?\s*\(.+\)\s*=\s*\S/i.test(text)) {
    return undefined;
  }

  return createMalformedStatementDiagnostic(
    'Mid statement must include a parenthesized target and assignment.',
    lineIndex,
    startCharacter,
    startCharacter + text.length
  );
}

function getOnStatementDiagnostic(lineIndex: number, text: string, startCharacter: number): SyntaxDiagnostic | undefined {
  if (/^On\s+Error\s+Resume\s+Next\s*$/i.test(text)) {
    return undefined;
  }
  if (/^On\s+Error\s+GoTo\s+\S/i.test(text)) {
    return undefined;
  }
  if (/^On\s+\S.+\s+Go(?:To|Sub)\s+\S/i.test(text)) {
    return undefined;
  }
  if (/^On\s+Error\s+GoTo\s*$/i.test(text)) {
    const goto_index = text.search(/\bGoTo\b/i);
    return createMalformedStatementDiagnostic(
      'On Error GoTo statement is missing a branch target.',
      lineIndex,
      startCharacter + goto_index,
      startCharacter + text.length
    );
  }

  return createMalformedStatementDiagnostic(
    'On statement is malformed.',
    lineIndex,
    startCharacter,
    startCharacter + text.length
  );
}

function getResumeStatementDiagnostic(lineIndex: number, text: string, startCharacter: number): SyntaxDiagnostic | undefined {
  if (/^Resume\s*$/i.test(text) || /^Resume\s+(?!Next\b)\S+\s*$/i.test(text)) {
    return undefined;
  }
  if (/^Resume\s+Next\s*$/i.test(text)) {
    return undefined;
  }

  const extra_match = /^Resume\s+Next\s+(\S.*)$/i.exec(text);
  if (extra_match !== null) {
    const extra_start = text.indexOf(extra_match[1]);
    return createMalformedStatementDiagnostic(
      'Resume Next cannot include an additional target.',
      lineIndex,
      startCharacter + extra_start,
      startCharacter + text.length
    );
  }

  return createMalformedStatementDiagnostic('Resume statement is malformed.', lineIndex, startCharacter, startCharacter + text.length);
}

function getCloseStatementDiagnostic(lineIndex: number, text: string, startCharacter: number): SyntaxDiagnostic | undefined {
  const rest = text.replace(/^Close\b/i, '').trim();
  if (rest === '' || /^#\s*\S+(?:\s*,\s*#\s*\S+)*$/i.test(rest)) {
    return undefined;
  }

  const hash_index = text.indexOf('#');
  return createMalformedStatementDiagnostic(
    'Close statement has a malformed file number list.',
    lineIndex,
    hash_index === -1 ? startCharacter : startCharacter + hash_index,
    startCharacter + text.length
  );
}

function isLineNumberStatement(text: string): boolean {
  return /^\d+\s*$/.test(text);
}

function isValidLabelSegment(segment: StatementSegment, text: string): boolean {
  return segment.terminator !== undefined && /^[A-Za-z_][A-Za-z0-9_]*$/.test(text);
}

function hasBranchTarget(text: string): boolean {
  return /^\s*(?:\d+|[A-Za-z_][A-Za-z0-9_]*)\s*$/i.test(text);
}

function hasTopLevelComma(text: string, startCharacter: number, endCharacter: number): boolean {
  return splitTopLevelSegments(text, startCharacter, endCharacter).length > 1;
}

function formatStatementKeyword(keyword: string): string {
  return keyword[0].toUpperCase() + keyword.slice(1);
}

function createMalformedStatementDiagnostic(
  message: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedStatement',
    message,
    range: {
      start: { line: lineIndex, character: startCharacter },
      end: { line: lineIndex, character: endCharacter }
    },
    severity: 'error',
    source: 'vba-language-server'
  };
}

interface StatementSegment {
  start: number;
  end: number;
  terminator?: number;
  text: string;
}

function collectStatementBoundaryDiagnostics(line: string, lineIndex: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  const segments = getStatementSegments(line);

  for (let segment_index = 0; segment_index < segments.length; segment_index += 1) {
    const segment = segments[segment_index];
    if (segment.terminator !== undefined && segment.text.trim() === '') {
      diagnostics.push({
        code: 'syntax.invalidStatementSeparator',
        message: 'Statement separator cannot create an empty statement.',
        range: {
          start: { line: lineIndex, character: segment.terminator },
          end: { line: lineIndex, character: segment.terminator + 1 }
        },
        severity: 'error',
        source: 'vba-language-server'
      });
      continue;
    }

    if (isLabelOnlySegment(segment, segment_index)) {
      continue;
    }

    const statement = getStatementBoundaryText(segment);
    if (statement.text === '') {
      continue;
    }

    if (/[,;)\]]/.test(statement.text[0])) {
      diagnostics.push({
        code: 'syntax.unexpectedToken',
        message: 'Unexpected token at statement start.',
        range: {
          start: { line: lineIndex, character: statement.start },
          end: { line: lineIndex, character: statement.start + 1 }
        },
        severity: 'error',
        source: 'vba-language-server'
      });
      continue;
    }

    const unexpected_range = getUnexpectedTokenAfterCompleteStatementRange(statement.text, statement.start, lineIndex);
    if (unexpected_range !== undefined) {
      diagnostics.push({
        code: 'syntax.unexpectedToken',
        message: 'Unexpected token after a complete statement.',
        range: unexpected_range,
        severity: 'error',
        source: 'vba-language-server'
      });
      continue;
    }

    const next_unexpected_range = getUnexpectedTokenAfterNextStatementRange(statement.text, statement.start, lineIndex);
    if (next_unexpected_range !== undefined) {
      diagnostics.push({
        code: 'syntax.unexpectedToken',
        message: 'Unexpected token after a complete statement.',
        range: next_unexpected_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }
  }

  return diagnostics;
}

function getStatementSegments(line: string): StatementSegment[] {
  const segments: StatementSegment[] = [];
  let segment_start = 0;
  let character_index = 0;

  while (character_index < line.length) {
    const character = line[character_index];
    if (character === "'" || isRemCommentStart(line, character_index)) {
      break;
    }

    if (character === '"') {
      const string_end = getStringLiteralEnd(line, character_index);
      if (string_end === undefined) {
        break;
      }

      character_index = string_end;
      continue;
    }

    if (character === '#' && !shouldSkipHashCharacter(line, character_index)) {
      const closing_index = line.indexOf('#', character_index + 1);
      if (closing_index === -1) {
        break;
      }

      character_index = closing_index + 1;
      continue;
    }

    if (character === ':') {
      segments.push({
        start: segment_start,
        end: character_index,
        terminator: character_index,
        text: line.slice(segment_start, character_index)
      });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({
    start: segment_start,
    end: character_index,
    text: line.slice(segment_start, character_index)
  });
  return segments;
}

function isLabelOnlySegment(segment: StatementSegment, segmentIndex: number): boolean {
  return segmentIndex === 0
    && segment.terminator !== undefined
    && /^(?:\d+|[A-Za-z_][A-Za-z0-9_]*)$/.test(segment.text.trim());
}

function getStatementBoundaryText(segment: StatementSegment): { text: string; start: number } {
  const leading_whitespace = /^\s*/.exec(segment.text)?.[0].length ?? 0;
  let text = segment.text.slice(leading_whitespace);
  let start = segment.start + leading_whitespace;
  const line_number = /^\d+\s+/.exec(text);
  if (line_number !== null) {
    text = text.slice(line_number[0].length);
    start += line_number[0].length;
  }

  return {
    text: text.trimEnd(),
    start
  };
}

function getUnexpectedTokenAfterCompleteStatementRange(
  text: string,
  startCharacter: number,
  lineIndex: number
): SourceRange | undefined {
  const complete_statement_patterns = [
    /^Option\s+Explicit\b/i,
    /^Option\s+Base\s+[01]\b/i,
    /^Option\s+Compare\s+(?:Binary|Text|Database)\b/i,
    /^End\s+(?:Sub|Function|Property|If|Select|With|Enum|Type)\b/i,
    /^Exit\s+(?:Sub|Function|Property|For|Do)\b/i,
    /^Wend\b/i,
    /^Else\b/i,
    /^Case\s+Else\b/i,
    /^Loop\b(?!\s+(?:While|Until)\b)/i
  ];

  for (const pattern of complete_statement_patterns) {
    const match = pattern.exec(text);
    if (match === null) {
      continue;
    }

    const rest = text.slice(match[0].length);
    const unexpected_match = /\S/.exec(rest);
    if (unexpected_match === null) {
      return undefined;
    }

    const unexpected_start = startCharacter + match[0].length + unexpected_match.index;
    return {
      start: { line: lineIndex, character: unexpected_start },
      end: { line: lineIndex, character: startCharacter + text.length }
    };
  }

  return undefined;
}

function getUnexpectedTokenAfterNextStatementRange(
  text: string,
  startCharacter: number,
  lineIndex: number
): SourceRange | undefined {
  const next_match = /^Next\b/i.exec(text);
  if (next_match === null) {
    return undefined;
  }

  let character_index = skipWhitespace(text, next_match[0].length, text.length);
  if (character_index >= text.length) {
    return undefined;
  }

  while (character_index < text.length) {
    if (!isIdentifierStart(text[character_index])) {
      return {
        start: { line: lineIndex, character: startCharacter + character_index },
        end: { line: lineIndex, character: startCharacter + text.length }
      };
    }

    character_index += 1;
    while (character_index < text.length && isIdentifierPart(text[character_index])) {
      character_index += 1;
    }

    character_index = skipWhitespace(text, character_index, text.length);
    if (character_index >= text.length) {
      return undefined;
    }

    if (text[character_index] !== ',') {
      return {
        start: { line: lineIndex, character: startCharacter + character_index },
        end: { line: lineIndex, character: startCharacter + text.length }
      };
    }

    character_index += 1;
    character_index = skipWhitespace(text, character_index, text.length);
    if (character_index >= text.length) {
      return {
        start: { line: lineIndex, character: startCharacter + text.lastIndexOf(',') },
        end: { line: lineIndex, character: startCharacter + text.length }
      };
    }
  }

  return undefined;
}

function collectPhysicalLineContinuationDiagnostics(lines: string[], codeStartLine: number): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    const invalid_spacing_range = getInvalidContinuationMarkerSpacingRange(lines[line_index], line_index);
    if (invalid_spacing_range !== undefined) {
      diagnostics.push({
        code: 'syntax.invalidContinuationMarkerSpacing',
        message: 'Code line-continuation marker must be preceded by whitespace.',
        range: invalid_spacing_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }

    const invalid_text_range = getInvalidContinuationMarkerTextRange(lines[line_index], line_index);
    if (invalid_text_range !== undefined) {
      diagnostics.push({
        code: 'syntax.invalidContinuationMarkerText',
        message: 'Code line-continuation marker cannot be followed by source text.',
        range: invalid_text_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }

    const incomplete_range = getIncompleteContinuationRange(lines, line_index);
    if (incomplete_range !== undefined) {
      diagnostics.push({
        code: 'syntax.incompleteContinuation',
        message: 'Code line-continuation marker must be followed by continued source text.',
        range: incomplete_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }

    const missing_marker_range = getMissingContinuationMarkerRange(lines, line_index);
    if (missing_marker_range !== undefined) {
      diagnostics.push({
        code: 'syntax.missingContinuationMarker',
        message: 'Continued source text requires a code line-continuation marker.',
        range: missing_marker_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }

    const trailing_comment_range = getInvalidTrailingCommentContinuationRange(lines[line_index], line_index);
    if (trailing_comment_range !== undefined) {
      diagnostics.push({
        code: 'syntax.invalidTrailingCommentContinuation',
        message: 'Code line-continuation marker cannot be followed by a comment.',
        range: trailing_comment_range,
        severity: 'error',
        source: 'vba-language-server'
      });
    }
  }

  return diagnostics;
}

function getInvalidContinuationMarkerSpacingRange(line: string, lineIndex: number): SourceRange | undefined {
  const code_end = getCodeEndCharacter(line);
  if (code_end < line.length) {
    return undefined;
  }

  const marker_index = findPreviousNonWhitespace(line, code_end - 1);
  if (
    marker_index === undefined
    || line[marker_index] !== '_'
    || marker_index === 0
    || /\s/.test(line[marker_index - 1])
    || isIdentifierPart(line[marker_index - 1])
  ) {
    return undefined;
  }

  return {
    start: { line: lineIndex, character: marker_index },
    end: { line: lineIndex, character: marker_index + 1 }
  };
}

function getMissingContinuationMarkerRange(lines: string[], lineIndex: number): SourceRange | undefined {
  const line = lines[lineIndex] ?? '';
  if (getCodeContinuationMarkerStart(line) !== undefined || !hasSourceText(lines[lineIndex + 1] ?? '')) {
    return undefined;
  }

  const code_end = getCodeEndCharacter(line);
  const code_text = line.slice(0, code_end).trimEnd();
  if (!/[,(]$/.test(code_text)) {
    return undefined;
  }

  const current_indent = skipWhitespace(line, 0, line.length);
  const next_line = lines[lineIndex + 1] ?? '';
  const next_code_start = skipWhitespace(next_line, 0, getCodeEndCharacter(next_line));
  if (next_code_start <= current_indent) {
    return undefined;
  }

  return {
    start: { line: lineIndex, character: code_end },
    end: { line: lineIndex, character: code_end }
  };
}

function getIncompleteContinuationRange(lines: string[], lineIndex: number): SourceRange | undefined {
  const line = lines[lineIndex] ?? '';
  const marker_index = getCodeContinuationMarkerStart(line);
  if (marker_index === undefined || hasSourceText(lines[lineIndex + 1] ?? '')) {
    return undefined;
  }

  return {
    start: { line: lineIndex, character: marker_index },
    end: { line: lineIndex, character: marker_index + 1 }
  };
}

function hasSourceText(line: string): boolean {
  return line.slice(0, getCodeEndCharacter(line)).trim().length > 0;
}

function getInvalidContinuationMarkerTextRange(line: string, lineIndex: number): SourceRange | undefined {
  const code_end = getCodeEndCharacter(line);
  for (let character_index = 1; character_index < code_end - 1; character_index += 1) {
    if (
      line[character_index] !== '_'
      || !isCodePosition(line, character_index)
      || !/\s/.test(line[character_index - 1])
      || !/\s/.test(line[character_index + 1])
    ) {
      continue;
    }

    const following_code_start = skipWhitespace(line, character_index + 1, code_end);
    if (following_code_start < code_end) {
      return {
        start: { line: lineIndex, character: character_index },
        end: { line: lineIndex, character: line.length }
      };
    }
  }

  return undefined;
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

  const attribute_line = lines.findIndex((line) => /^\s*Attribute\s+VB_Name\b/i.test(line));
  return attribute_line === -1 ? lines.length : attribute_line;
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
    if (isRemCommentStart(line, character_index)) {
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
        : selectHostApplicationQualifiedDefinition(
          getUnqualifiedHostDefinitions(project),
          host_application,
          segments[1].name
        );
      if (host_root !== undefined && (segments.length === 2 || host_root.kind !== 'enum')) {
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

function isInMalformedExpressionRegion(module: VbaModule, position: SourcePosition): boolean {
  return module.syntaxDiagnostics.some((diagnostic) =>
    diagnostic.code === 'syntax.malformedExpression'
    && diagnostic.range.start.line === position.line
    && position.character >= diagnostic.range.start.character
  );
}

function isInMalformedMemberAccessRegion(module: VbaModule, position: SourcePosition): boolean {
  return module.syntaxDiagnostics.some((diagnostic) =>
    diagnostic.code === 'syntax.malformedMemberAccess'
    && diagnostic.range.start.line === position.line
    && position.character >= diagnostic.range.start.character
  );
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
  const value = definition.value === undefined ? undefined : `Value: ${definition.value}`;
  return detail === undefined
    ? [value, definition.documentation].filter((section) => section !== undefined && section !== '').join('\n\n')
    : [detail, value, definition.documentation].filter((section) => section !== undefined && section !== '').join('\n\n');
}

function getHostDefinitionDetail(definition: HostDefinition): string | undefined {
  if (definition.hostApplication === undefined) {
    return undefined;
  }

  if (definition.parentName !== undefined) {
    return `${formatHostApplicationName(definition.hostApplication)}.${definition.parentName}.${definition.name}`;
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
  if (definition.kind === 'constant') {
    return 'constant';
  }

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
    case 'constant':
      return 'variable';
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
