import {
  createHostApplicationSelection,
  getBundledHostDefinitions
} from './officeHostCatalog';
import {
  getUnqualifiedHostDefinitions,
  selectHostApplicationQualifiedDefinition,
  selectUnqualifiedHostDefinition,
  withHostMemberContext
} from './hostDefinitionCatalog';
import {
  resolveHostApplicationQualifier,
  resolveHostQualifiedPath,
  resolveUnqualifiedHostEnumQualifier
} from './hostDefinitionLookup';
import {
  collectDeclarationListDiagnostics,
  collectDeclaratorDiagnostics,
  collectDefTypeDeclarationDiagnostics,
  collectWithEventsDeclarationDiagnostics,
  getDeclarationListPrefix,
  parseModuleDataDeclarationLists,
  parseProcedureDataDeclarationLists
} from './declarationList';
import {
  collectMalformedCallDiagnosticsForSegment,
  getCallExpressionAt,
  getCallStatementSegments,
  shouldSkipCallDiagnosticsForStatement
} from './callSyntax';
import {
  parseContinuedMemberChainEndingBefore,
  parseMemberChainEndingAt
} from './memberChainSyntax';
import {
  resolveActiveWithReceiverType,
  resolveMemberChainReceiverType,
  resolveMemberChainTarget
} from './memberChainResolution';
import {
  findTypeNameForExpression,
  resolveName
} from './nameResolution';
import {
  buildSourceCallableSignature,
  parseSourceCallableParameterDefinitions,
  parseSourceCallableReturnTypeName
} from './sourceCallableSignature';
import type {
  CallableParameter,
  CallableSignature,
  HostApplication,
  HostDefinition
} from './hostDefinition';
import {
  getCodeContinuationMarkerStart,
  getCodeEndCharacter,
  getLogicalCodeSourceFromLine,
  getTopLevelStatementSegments,
  hasCommentContinuationMarker,
  hasSourceText,
  isContinuationTail,
  sourceLineRange,
  type LogicalCodeSource,
  type StatementSegment
} from './logicalSource';
import {
  C_IDENTIFIER_PATTERN,
  findClosingParenInCode,
  findKeywordOutsideLiterals,
  findPreviousNonWhitespace,
  findTopLevelAssignmentEquals,
  findTopLevelEquals,
  getCodeTextForStructure,
  getStringLiteralEnd,
  isIdentifierName,
  isIdentifierPart,
  isIdentifierStart,
  isPlausibleConstantInitializer,
  isRemCommentStart,
  readIdentifierEnd,
  readIdentifierTokenAt,
  skipWhitespace,
  splitTopLevelSegments,
  startsWithKeywordAt,
  trimEndIndex,
  type VbaIdentifierToken
} from './vbaText';
import {
  comparePosition,
  containsPosition,
  containsRange,
  sameRange,
  type SourcePosition,
  type SourceRange
} from './sourceRange';
import { sameName } from './vbaNames';
import {
  fallbackModuleIdentity,
  getFolderUri,
  isVbaSourceUri,
  sameUri,
  uriPathname
} from './vbaUris';
import {
  getIdentifierPrefix,
  getIdentifierRangesInCode,
  isCodePosition
} from './vbaIdentifierSource';
import {
  findSourceTypeModule,
  resolveCurrentModuleEventDefinition
} from './sourceDefinitionLookup';
import type { VbaProject } from './vbaProjectModel';
import {
  findHostTypeDefinition,
  resolveTypeNameRef
} from './typeResolution';
import {
  completionKindForHostDefinition,
  completionKindForVbaDefinition,
  getHostDefinitionDetail,
  getParameterDocumentation,
  renderCallableParameterMetadata,
  renderDocumentationComment,
  renderHostDefinitionHover,
  renderSignatureDocumentation,
  renderSourceCallableParameterMetadata,
  semanticTokenModifiersForHostDefinition,
  semanticTokenModifiersForVbaDefinition,
  semanticTokenTypeForHostDefinition,
  semanticTokenTypeForVbaDefinition,
  type CompletionEntryKind,
  type SemanticTokenModifier,
  type SemanticTokenType,
  type VbaSemanticToken
} from './vbaPresentation';
import type {
  DefinitionLocation,
  DocumentationComment,
  MemberCompletionRequest,
  ModuleMember,
  ProcedureScope,
  SyntaxDiagnostic,
  SyntaxDiagnosticCode,
  SyntaxDiagnosticSeverity,
  TypeResolutionRef,
  VbaDefinition,
  VbaModule,
  VbaModuleKind,
  WithEventsDeclaration
} from './vbaSourceModel';

export type { VbaProject } from './vbaProjectModel';
export {
  VBA_SEMANTIC_TOKEN_MODIFIERS,
  VBA_SEMANTIC_TOKEN_TYPES
} from './vbaPresentation';
export { resolveName } from './nameResolution';
export type {
  CompletionEntryKind,
  SemanticTokenModifier,
  SemanticTokenType,
  VbaSemanticToken
} from './vbaPresentation';
export type {
  CallableParameter,
  CallableSignature,
  HostApplication,
  HostDefinition,
  HostDefinitionKind
} from './hostDefinition';
export type { SourcePosition, SourceRange } from './sourceRange';
export type {
  DefinitionLocation,
  NameResolutionResult,
  SyntaxDiagnostic,
  SyntaxDiagnosticCode,
  SyntaxDiagnosticSeverity
} from './vbaSourceModel';

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

export interface BuildVbaProjectOptions {
  hostDefinitions?: HostDefinition[];
  mainHostApplication?: HostApplication;
  additionalHostApplications?: HostApplication[];
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
    || (
      isInMalformedMemberAccessRegion(current_module, request.position)
      && !isHostEnumQualifierCompletion(project, current_module, request.position)
    )
  ) {
    return [];
  }

  const member_completion = getMemberCompletionAt(current_module.lines, request.position);
  if (member_completion !== undefined) {
    return getTypedMemberCompletions(project, current_module, request.position, member_completion);
  }

  const end_statement_completion = getEndStatementCompletionAt(current_module, request.position);
  const prefix = getIdentifierPrefix(current_module.lines, request.position).toLowerCase();
  const source_candidates = getSourceCompletionDefinitions(project, current_module, request.position)
    .filter((definition) => prefix === '' || definition.name.toLowerCase().startsWith(prefix))
    .map((definition) => ({
      label: definition.name,
      kind: completionKindForVbaDefinition(definition)
    }));
  const host_definitions = getUnqualifiedHostDefinitions(project.hostDefinitions);
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
    ...source_candidates,
    ...host_candidates
  ]);
}

function getSourceCompletionDefinitions(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition
): VbaDefinition[] {
  const procedure_scope = currentModule.procedureScopes.find((scope) => containsPosition(scope.range, position));
  const local_definitions = procedure_scope?.definitions ?? [];
  const current_module_definitions = currentModule.definitions;
  const project_definitions = project.modules
    .filter((module) =>
      sameUri(module.folderUri, currentModule.folderUri)
        && !sameUri(module.uri, currentModule.uri)
    )
    .flatMap((module) => module.definitions)
    .filter((definition) => definition.visibility === 'public');

  return [
    ...local_definitions,
    ...current_module_definitions,
    ...project_definitions
  ];
}

function isUnqualifiedHostCompletionDefinition(
  project: VbaProject,
  definition: HostDefinition,
  hostDefinitions: HostDefinition[] = getUnqualifiedHostDefinitions(project.hostDefinitions)
): boolean {
  const matches = hostDefinitions.filter((candidate) => sameName(candidate.name, definition.name));
  return selectUnqualifiedHostDefinition(
    matches,
    project.hostApplicationSelection.mainHostApplication
  ) === definition;
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

  return getUnqualifiedHostDefinitions(project.hostDefinitions)
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
    return getHostMemberCompletionEntries(host_definition, request.prefix);
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

  const host_enum = resolveHostEnumCompletionQualifier(project, currentModule, position, request.qualifier);
  if (host_enum !== undefined) {
    return getHostMemberCompletionEntries(host_enum, request.prefix);
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

function getHostMemberCompletionEntries(
  definition: HostDefinition,
  prefix: string
): CompletionEntry[] {
  const normalized_prefix = prefix.toLowerCase();
  return (definition.members ?? [])
    .filter((member) => normalized_prefix === '' || member.name.toLowerCase().startsWith(normalized_prefix))
    .map((member) => withHostMemberContext(definition, member))
    .map((member) => ({
      label: member.name,
      kind: completionKindForHostDefinition(member),
      detail: getHostDefinitionDetail(member)
    }));
}

function isHostEnumQualifierCompletion(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition
): boolean {
  const request = getMemberCompletionAt(currentModule.lines, position);
  return request === undefined
    ? false
    : resolveHostEnumCompletionQualifier(project, currentModule, position, request.qualifier) !== undefined;
}

function resolveHostEnumCompletionQualifier(
  project: VbaProject,
  currentModule: VbaModule,
  position: SourcePosition,
  qualifier: string
): HostDefinition | undefined {
  if (qualifier.includes('.')) {
    const host_definition = resolveHostQualifiedPath(project, currentModule, qualifier);
    return host_definition?.kind === 'enum' ? host_definition : undefined;
  }

  return resolveUnqualifiedHostEnumQualifier(project, currentModule, position, qualifier);
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
    tokens.push(...getConditionalCompilationSemanticTokens(current_module.lines[line_index], line_index));

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
      const token_modifiers = resolution.source === 'host'
        ? semanticTokenModifiersForHostDefinition(resolution.definition)
        : semanticTokenModifiersForVbaLocation(project, resolution.definition);

      const token: VbaSemanticToken = {
        range,
        tokenType: token_type
      };
      if (token_modifiers !== undefined && token_modifiers.length > 0) {
        token.tokenModifiers = token_modifiers;
      }
      tokens.push(token);
    }
  }

  return uniqueSemanticTokens(tokens).sort(compareSemanticTokens);
}

function getConditionalCompilationSemanticTokens(line: string, lineIndex: number): VbaSemanticToken[] {
  const code_end = getCodeEndCharacter(line);
  const directive_start = skipWhitespace(line, 0, code_end);
  if (directive_start >= code_end || line[directive_start] !== '#') {
    return [];
  }

  const keyword_start = skipWhitespace(line, directive_start + 1, code_end);
  const keyword = readIdentifierTokenAt(line, keyword_start, code_end);
  if (keyword === undefined) {
    return [];
  }

  if (keyword.lowerText === 'const') {
    const name_start = skipWhitespace(line, keyword.end, code_end);
    const name = readIdentifierTokenAt(line, name_start, code_end);
    return name === undefined
      ? []
      : [toMacroSemanticToken(lineIndex, name.start, name.end)];
  }

  if (keyword.lowerText !== 'if' && keyword.lowerText !== 'elseif') {
    return [];
  }

  const tokens: VbaSemanticToken[] = [];
  let character_index = skipWhitespace(line, keyword.end, code_end);
  while (character_index < code_end) {
    const token = readIdentifierTokenAt(line, character_index, code_end);
    if (token === undefined) {
      character_index += 1;
      continue;
    }

    if (token.lowerText === 'then') {
      break;
    }

    if (!isConditionalCompilationExpressionKeyword(token.lowerText)) {
      tokens.push(toMacroSemanticToken(lineIndex, token.start, token.end));
    }
    character_index = token.end;
  }

  return tokens;
}

function toMacroSemanticToken(line: number, startCharacter: number, endCharacter: number): VbaSemanticToken {
  return {
    range: {
      start: { line, character: startCharacter },
      end: { line, character: endCharacter }
    },
    tokenType: 'macro'
  };
}

function isConditionalCompilationExpressionKeyword(value: string): boolean {
  return value === 'and'
    || value === 'or'
    || value === 'not'
    || value === 'xor'
    || value === 'eqv'
    || value === 'imp'
    || value === 'mod'
    || value === 'true'
    || value === 'false';
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

  if (call_expression.eventReference === true) {
    const definition = resolveCurrentModuleEventDefinition(current_module, call_expression.name);
    return definition?.signature === undefined
      ? undefined
      : getSourceSignatureHelp(project, definition, call_expression.activeParameter);
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
    return getHostSignatureHelp(
      resolution.definition,
      call_expression.activeParameter,
      call_expression.namedArgumentName
    );
  }

  const definition = findDefinitionByLocation(project, resolution.definition);
  return definition?.signature === undefined
    ? undefined
    : getSourceSignatureHelp(
        project,
        definition,
        call_expression.activeParameter,
        call_expression.namedArgumentName
      );
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
    sameUri(candidate.folderUri, target_module.folderUri)
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

    const declaration_diagnostics = lexical_diagnostics.length === 0
        && callable_diagnostics.length === 0
        && getCodeContinuationMarkerStart(line) === undefined
        && !isContinuationTail(lines, line_index)
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

interface CallableDeclarationHead {
  kind: 'sub' | 'function' | 'property' | 'event' | 'declare';
  propertyKind?: 'Get' | 'Let' | 'Set';
  headEnd: number;
}

type CallableDeclarationToken = VbaIdentifierToken;

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

function canonicalPropertyKind(value: string): 'Get' | 'Let' | 'Set' {
  const lower_value = value.toLowerCase();
  if (lower_value === 'let') {
    return 'Let';
  }
  return lower_value === 'set' ? 'Set' : 'Get';
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

interface DiagnosticSourceLine {
  lineIndex: number;
  line: string;
  codeEnd: number;
  structureText: string;
  skipped: boolean;
}

interface DiagnosticSourceLineOptions {
  skipLines?: Set<number>;
  includeSkippedLines?: boolean;
  skipContinuationTails?: boolean;
  skipLexicalDiagnostics?: boolean;
  skipInvalidTrailingCommentContinuation?: boolean;
}

function getDiagnosticSourceLines(
  lines: string[],
  codeStartLine: number,
  options: DiagnosticSourceLineOptions = {}
): DiagnosticSourceLine[] {
  const source_lines: DiagnosticSourceLine[] = [];
  let skipped_declaration_block: DeclarationBlockKind | undefined;

  for (let line_index = codeStartLine; line_index < lines.length; line_index += 1) {
    if (options.skipContinuationTails === true && isContinuationTail(lines, line_index)) {
      continue;
    }

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

    const skipped = options.skipLines?.has(line_index) ?? false;
    if (skipped && options.includeSkippedLines !== true) {
      continue;
    }
    if (options.skipLexicalDiagnostics === true && collectLexicalSyntaxDiagnostics(line, line_index).length > 0) {
      continue;
    }
    if (
      options.skipInvalidTrailingCommentContinuation === true
      && getInvalidTrailingCommentContinuationRange(line, line_index) !== undefined
    ) {
      continue;
    }

    source_lines.push({
      lineIndex: line_index,
      line,
      codeEnd: code_end,
      structureText: structure_text,
      skipped
    });
  }

  return source_lines;
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

  for (const source_line of getDiagnosticSourceLines(lines, codeStartLine)) {
    const closer = getExecutableBlockCloser(source_line.line, source_line.codeEnd);
    if (closer !== undefined) {
      const open_block = stack[stack.length - 1];
      if (open_block === undefined) {
        if (shouldSuppressUnexpectedCallableCloser(lines, source_line.lineIndex, closer)) {
          continue;
        }
        diagnostics.push(createMalformedBlockStructureDiagnostic(
          `Unexpected ${closer.label} without a matching ${closer.openerName} block.`,
          source_line.lineIndex,
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
            source_line.lineIndex,
            closer.start,
            closer.end
          ));
          continue;
        }

        diagnostics.push(createMalformedBlockStructureDiagnostic(
          `Mismatched block closer; expected ${open_block.expectedCloser}.`,
          source_line.lineIndex,
          closer.start,
          closer.end
        ));

        stack.length = matching_index;
        continue;
      }

      stack.pop();
      continue;
    }

    const opener = getExecutableBlockOpener(source_line.line, source_line.codeEnd);
    if (opener !== undefined) {
      stack.push({
        ...opener,
        openerLine: source_line.lineIndex
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

  for (const source_line of getDiagnosticSourceLines(lines, codeStartLine)) {
    const line = source_line.line;
    const line_index = source_line.lineIndex;
    const trimmed_start = line.search(/\S/);
    const trimmed_end = source_line.codeEnd;
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

  for (const source_line of getDiagnosticSourceLines(lines, codeStartLine, {
    skipLines,
    skipLexicalDiagnostics: true,
    skipInvalidTrailingCommentContinuation: true
  })) {
    for (const segment of getStatementSegments(source_line.line)) {
      const segment_end = Math.min(segment.end, source_line.codeEnd);
      if (segment.start >= segment_end) {
        continue;
      }

      const spans = getExpressionSpansForDiagnostics(source_line.line, segment.start, segment_end);
      for (const span of spans) {
        diagnostics.push(...collectMalformedExpressionDiagnostics(
          source_line.line,
          source_line.lineIndex,
          span.start,
          span.end
        ));
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

function collectCallSyntaxDiagnostics(
  lines: string[],
  codeStartLine: number,
  skipLines: Set<number>
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];

  for (const source_line of getDiagnosticSourceLines(lines, codeStartLine, {
    skipLines,
    skipContinuationTails: true,
    skipLexicalDiagnostics: true
  })) {
    const source = getLogicalCodeSourceFromLine(lines, source_line.lineIndex);
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

type LeadingDotContext = 'none' | 'with' | 'continued';

function collectMemberAccessDiagnostics(
  lines: string[],
  codeStartLine: number,
  skipLines: Set<number>
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let with_depth = 0;

  for (const source_line of getDiagnosticSourceLines(lines, codeStartLine, {
    skipLines,
    includeSkippedLines: true
  })) {
    if (/^End\s+With\b/i.test(source_line.structureText)) {
      with_depth = Math.max(0, with_depth - 1);
    }

    if (
      !source_line.skipped
      && collectLexicalSyntaxDiagnostics(source_line.line, source_line.lineIndex).length === 0
      && getInvalidTrailingCommentContinuationRange(source_line.line, source_line.lineIndex) === undefined
    ) {
      const leading_dot_context = getLeadingDotContext(lines, source_line.lineIndex, with_depth);
      diagnostics.push(...collectMalformedMemberAccessDiagnosticsForLine(
        source_line.line,
        source_line.lineIndex,
        source_line.codeEnd,
        leading_dot_context
      ));
    }

    if (/^With\b/i.test(source_line.structureText) && !/^With\b.*\bThen\b/i.test(source_line.structureText)) {
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

  for (const source_line of getDiagnosticSourceLines(lines, codeStartLine, {
    skipLines,
    skipLexicalDiagnostics: true,
    skipInvalidTrailingCommentContinuation: true
  })) {
    for (const segment of getStatementSegments(source_line.line)) {
      const diagnostic = getStatementSpecificDiagnostic(source_line.line, source_line.lineIndex, segment);
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

    const data_declaration_result = parseModuleDataDeclarationLists(uri, lines, line_index);
    if (data_declaration_result !== undefined) {
      definitions.push(...data_declaration_result.definitions);
      withEventsDeclarations.push(...data_declaration_result.declarations);
      moduleMembers.push({
        range: createModuleMemberRange(lines, line_index, data_declaration_result.endLine),
        definitions: data_declaration_result.definitions,
        procedureScopes: [],
        withEventsDeclarations: data_declaration_result.declarations,
        implements: []
      });
      line_index = data_declaration_result.endLine;
      continue;
    }

    const event_match =
      /^\s*(?:(Public|Private)\s+)?Event\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(([^)]*)\))?/i.exec(line);
    if (event_match !== null) {
      const visibility = (event_match[1]?.toLowerCase() ?? 'public') as 'public' | 'private';
      const name = event_match[2];
      const name_start = line.indexOf(name);
      const parameter_start = line.indexOf('(', name_start + name.length) + 1;
      const parameter_definitions = event_match[3] === undefined
        ? []
        : parseSourceCallableParameterDefinitions(uri, line, line_index, event_match[3], parameter_start);
      const definition: VbaDefinition = {
        name,
        kind: 'event',
        visibility,
        uri,
        range: {
          start: { line: line_index, character: name_start },
          end: { line: line_index, character: name_start + name.length }
        },
        documentation: parseDocumentationComment(lines, line_index),
        signature: buildSourceCallableSignature(line, name, parameter_definitions)
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
      : parseSourceCallableParameterDefinitions(uri, line, line_index, procedure_match[5], parameter_start);
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
      signature: buildSourceCallableSignature(line, name, parameter_definitions),
      typeName: parseSourceCallableReturnTypeName(line)
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
    const data_declaration_result = parseProcedureDataDeclarationLists(uri, lines, line_index);
    if (data_declaration_result !== undefined) {
      definitions.push(...data_declaration_result.definitions);
      line_index = Math.min(data_declaration_result.endLine, end_line - 1);
    }
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

    return host_type.members
      .map((member) => withHostMemberContext(host_type, member))
      .map((member) => ({
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

function sameDefinitionLocation(left: DefinitionLocation, right: DefinitionLocation): boolean {
  return sameUri(left.uri, right.uri)
    && sameRange(left.range, right.range);
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
  activeParameter: number,
  namedArgumentName?: string
): SignatureHelpResult | undefined {
  if (definition.signature === undefined) {
    return undefined;
  }

  const selected_parameter = selectActiveSignatureParameter(
    definition.signature,
    activeParameter,
    namedArgumentName
  );
  const documentation = findDocumentationForDefinition(project, definition);
  const parameter_docs = getParameterDocumentation(documentation);
  return toSignatureHelpResult(
    definition.signature,
    selected_parameter,
    renderSignatureDocumentation(documentation),
    (parameter) => parameter_docs.get(parameter.name.toLowerCase()) ?? renderSourceCallableParameterMetadata(parameter)
  );
}

function getHostSignatureHelp(
  definition: HostDefinition,
  activeParameter: number,
  namedArgumentName?: string
): SignatureHelpResult | undefined {
  if (definition.signature === undefined) {
    return undefined;
  }

  const selected_parameter = selectActiveSignatureParameter(
    definition.signature,
    activeParameter,
    namedArgumentName
  );
  return toSignatureHelpResult(
    definition.signature,
    selected_parameter,
    definition.signature.documentation ?? definition.documentation,
    (parameter) => parameter.documentation ?? renderCallableParameterMetadata(parameter)
  );
}

function selectActiveSignatureParameter(
  signature: CallableSignature,
  fallbackParameter: number,
  namedArgumentName?: string
): number {
  if (namedArgumentName === undefined) {
    return fallbackParameter;
  }

  const matches = signature.parameters
    .map((parameter, index) => ({ parameter, index }))
    .filter(({ parameter }) => parameter.name !== '' && sameName(parameter.name, namedArgumentName));
  return matches.length === 1
    ? matches[0].index
    : fallbackParameter;
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
      sameUri(module.folderUri, owner_module.folderUri)
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

function getModuleKind(uri: string): VbaModuleKind {
  if (/\.bas$/i.test(uriPathname(uri))) {
    return 'standard';
  }
  if (/\.frm$/i.test(uriPathname(uri))) {
    return 'form';
  }

  return 'class';
}

function semanticTokenTypeForVbaLocation(
  project: VbaProject,
  location: DefinitionLocation
): SemanticTokenType | undefined {
  const definition = findDefinitionByLocation(project, location);
  return definition === undefined ? undefined : semanticTokenTypeForVbaDefinition(definition);
}

function semanticTokenModifiersForVbaLocation(
  project: VbaProject,
  location: DefinitionLocation
): SemanticTokenModifier[] | undefined {
  const definition = findDefinitionByLocation(project, location);
  return definition === undefined ? undefined : semanticTokenModifiersForVbaDefinition(definition);
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
      token.tokenType,
      ...(token.tokenModifiers ?? [])
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
    || left.tokenType.localeCompare(right.tokenType)
    || (left.tokenModifiers ?? []).join(':').localeCompare((right.tokenModifiers ?? []).join(':'));
}
