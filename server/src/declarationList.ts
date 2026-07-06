import {
  getLogicalCodeSourceFromLine,
  getLogicalSourceRange,
  getTopLevelStatementSegments,
  isContinuationTail,
  type LogicalCodeSource,
  type LogicalSourceText
} from './logicalSource';
import type {
  SyntaxDiagnostic,
  VbaDefinition,
  WithEventsDeclaration
} from './vbaSourceModel';
import {
  findClosingParenInCode,
  findTopLevelEquals,
  isIdentifierStart,
  isPlausibleConstantInitializer,
  readIdentifierEnd,
  readIdentifierTokenAt,
  skipWhitespace,
  splitTopLevelSegments,
  startsWithKeywordAt,
  trimEndIndex
} from './vbaText';

export interface DeclarationListPrefix {
  kind: 'variable' | 'constant' | 'redim';
  declaratorsStart: number;
}

interface DeclarationStatementSource {
  source: LogicalCodeSource;
  start: number;
  end: number;
}

export interface ParsedDefinitionList {
  definitions: VbaDefinition[];
  endLine: number;
}

export interface ParsedWithEventsDeclarationList extends ParsedDefinitionList {
  declarations: WithEventsDeclaration[];
}

export function getDeclarationListPrefix(
  line: string,
  codeEnd: number,
  startCharacter = 0
): DeclarationListPrefix | undefined {
  const first_token = readIdentifierTokenAt(line, skipWhitespace(line, startCharacter, codeEnd), codeEnd);
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

export function collectWithEventsDeclarationDiagnostics(
  line: string,
  lineIndex: number,
  codeEnd: number
): SyntaxDiagnostic[] | undefined {
  const match = /^\s*(?:(?:Public|Private|Dim)\s+)?WithEvents\b/i.exec(line.slice(0, codeEnd));
  if (match === null) {
    return undefined;
  }

  const diagnostics: SyntaxDiagnostic[] = [];
  const declarators_start = skipWhitespace(line, match[0].length, codeEnd);
  for (const segment of splitTopLevelSegments(line, declarators_start, codeEnd)) {
    const trimmed_start = skipWhitespace(line, segment.start, segment.end);
    const trimmed_end = trimEndIndex(line, segment.end);
    const name_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
    if (name_token === undefined || name_token.lowerText === 'as') {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'WithEvents declaration is missing an identifier.',
        lineIndex,
        trimmed_start,
        name_token?.end ?? trimmed_start
      ));
      continue;
    }

    const after_name = skipWhitespace(line, name_token.end, trimmed_end);
    if (!startsWithKeywordAt(line, after_name, 'as', trimmed_end)) {
      diagnostics.push(createMalformedDeclarationDiagnostic(
        'WithEvents declaration must include As type.',
        lineIndex,
        after_name,
        trimmed_end
      ));
      continue;
    }

    const type_diagnostic = getTypeAnnotationDiagnostic(line, lineIndex, after_name, trimmed_end);
    if (type_diagnostic !== undefined) {
      diagnostics.push(type_diagnostic);
    }
  }

  return diagnostics;
}

export function collectDefTypeDeclarationDiagnostics(
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

export function collectDeclarationListDiagnostics(
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

export function collectDeclaratorDiagnostics(
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

export function parseProcedureDataDeclarationLists(
  uri: string,
  lines: string[],
  lineIndex: number
): ParsedDefinitionList | undefined {
  const statements = getDeclarationStatementSources(lines, lineIndex);
  const definitions: VbaDefinition[] = [];
  let matched = false;

  for (const statement of statements) {
    const constant_result = parseSourceConstantDefinitionsFromStatement(
      uri,
      statement.source,
      statement.start,
      statement.end,
      'procedure'
    );
    if (constant_result.prefixMatched) {
      matched = true;
      definitions.push(...constant_result.definitions);
      continue;
    }

    const variable_result = parseVariableDefinitionsFromStatement(
      uri,
      statement.source,
      statement.start,
      statement.end,
      'local',
      'local'
    );
    if (variable_result.prefixMatched) {
      matched = true;
      definitions.push(...variable_result.definitions);
    }
  }

  return matched && statements[0] !== undefined
    ? {
        definitions,
        endLine: statements[0].source.endLine
      }
    : undefined;
}

export function parseModuleDataDeclarationLists(
  uri: string,
  lines: string[],
  lineIndex: number
): ParsedWithEventsDeclarationList | undefined {
  const statements = getDeclarationStatementSources(lines, lineIndex);
  const definitions: VbaDefinition[] = [];
  const declarations: WithEventsDeclaration[] = [];
  let matched = false;

  for (const statement of statements) {
    const with_events_result = parseWithEventsDeclarationListFromStatement(uri, statement);
    if (with_events_result !== undefined) {
      matched = true;
      definitions.push(...with_events_result.definitions);
      declarations.push(...with_events_result.declarations);
      continue;
    }

    const visibility = getSourceDeclarationVisibility(statement.source.text, statement.end, statement.start);
    const variable_result = parseVariableDefinitionsFromStatement(
      uri,
      statement.source,
      statement.start,
      statement.end,
      visibility,
      'variable'
    );
    if (variable_result.prefixMatched) {
      matched = true;
      definitions.push(...variable_result.definitions);
      continue;
    }

    const constant_result = parseSourceConstantDefinitionsFromStatement(
      uri,
      statement.source,
      statement.start,
      statement.end,
      'module'
    );
    if (constant_result.prefixMatched) {
      matched = true;
      definitions.push(...constant_result.definitions);
    }
  }

  return matched && statements[0] !== undefined
    ? {
        definitions,
        declarations,
        endLine: statements[0].source.endLine
      }
    : undefined;
}

function getDeclarationStatementSources(lines: string[], lineIndex: number): DeclarationStatementSource[] {
  if (isContinuationTail(lines, lineIndex)) {
    return [];
  }

  const source = getLogicalCodeSourceFromLine(lines, lineIndex);
  if (source === undefined) {
    return [];
  }

  return getTopLevelStatementSegments(source.text).map((segment) => ({
    source,
    start: segment.start,
    end: segment.end
  }));
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

function parseSourceConstantDefinitionsFromStatement(
  uri: string,
  source: LogicalSourceText,
  startCharacter: number,
  endCharacter: number,
  scope: 'module' | 'procedure'
): { prefixMatched: boolean; definitions: VbaDefinition[] } {
  const line = source.text;
  const prefix = getDeclarationListPrefix(line, endCharacter, startCharacter);
  if (prefix?.kind !== 'constant') {
    return { prefixMatched: false, definitions: [] };
  }

  const visibility = scope === 'procedure'
    ? 'local'
    : getSourceDeclarationVisibility(line, endCharacter, startCharacter);
  const definitions: VbaDefinition[] = [];
  const segments = splitTopLevelSegments(line, Math.max(prefix.declaratorsStart, startCharacter), endCharacter);
  for (const segment of segments) {
    if (hasMalformedDeclaratorArrayBounds(line, segment.start, segment.end)) {
      return { prefixMatched: true, definitions: [] };
    }
    if (hasMalformedDeclaratorTypeAnnotation(line, segment.start, segment.end)) {
      return { prefixMatched: true, definitions: [] };
    }

    const definition = parseSourceConstantDeclaratorDefinitionFromSource(
      uri,
      source,
      segment.start,
      segment.end,
      visibility
    );
    if (definition !== undefined) {
      definitions.push(definition);
    }
  }

  return { prefixMatched: true, definitions };
}

function parseSourceConstantDeclaratorDefinitionFromSource(
  uri: string,
  source: LogicalSourceText,
  startCharacter: number,
  endCharacter: number,
  visibility: 'public' | 'private' | 'local'
): VbaDefinition | undefined {
  const line = source.text;
  const trimmed_start = skipWhitespace(line, startCharacter, endCharacter);
  const trimmed_end = trimEndIndex(line, endCharacter);
  const name_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (name_token === undefined || name_token.lowerText === 'as') {
    return undefined;
  }

  const equals_index = findTopLevelEquals(line, name_token.end, trimmed_end);
  const type_annotation_end = equals_index ?? trimmed_end;

  return {
    name: name_token.text,
    kind: 'constant',
    visibility,
    uri,
    range: getLogicalSourceRange(source, name_token.start, name_token.end),
    typeName: parseDeclaratorTypeName(line, name_token.end, type_annotation_end)
  };
}

function parseVariableDefinitionsFromStatement(
  uri: string,
  source: LogicalSourceText,
  startCharacter: number,
  endCharacter: number,
  visibility: 'public' | 'private' | 'local',
  kind: 'local' | 'variable'
): { prefixMatched: boolean; definitions: VbaDefinition[] } {
  const line = source.text;
  const prefix = getDeclarationListPrefix(line, endCharacter, startCharacter);
  if (prefix?.kind !== 'variable') {
    return { prefixMatched: false, definitions: [] };
  }

  const definitions: VbaDefinition[] = [];
  for (const segment of splitTopLevelSegments(line, Math.max(prefix.declaratorsStart, startCharacter), endCharacter)) {
    if (hasMalformedDeclaratorArrayBounds(line, segment.start, segment.end)) {
      return { prefixMatched: true, definitions: [] };
    }
    if (hasMalformedDeclaratorTypeAnnotation(line, segment.start, segment.end)) {
      return { prefixMatched: true, definitions: [] };
    }

    const definition = parseVariableDeclaratorDefinitionFromSource(
      uri,
      source,
      segment.start,
      segment.end,
      visibility,
      kind
    );
    if (definition !== undefined) {
      definitions.push(definition);
    }
  }

  return { prefixMatched: true, definitions };
}

function parseWithEventsDeclarationListFromStatement(
  uri: string,
  statement: DeclarationStatementSource
): ParsedWithEventsDeclarationList | undefined {
  const line = statement.source.text;
  const statement_start = skipWhitespace(line, statement.start, statement.end);
  const match = /^(?:(?:Public|Private|Dim)\s+)?WithEvents\b/i.exec(line.slice(statement_start, statement.end));
  if (match === null) {
    return undefined;
  }

  const visibility = getSourceDeclarationVisibility(line, statement.end, statement.start);
  const declarators_start = skipWhitespace(line, statement_start + match[0].length, statement.end);
  const definitions: VbaDefinition[] = [];
  const declarations: WithEventsDeclaration[] = [];

  for (const segment of splitTopLevelSegments(line, declarators_start, statement.end)) {
    if (hasMalformedDeclaratorArrayBounds(line, segment.start, segment.end)) {
      return { definitions: [], declarations: [], endLine: statement.source.endLine };
    }
    if (hasMalformedDeclaratorTypeAnnotation(line, segment.start, segment.end)) {
      return { definitions: [], declarations: [], endLine: statement.source.endLine };
    }

    const definition = parseVariableDeclaratorDefinitionFromSource(
      uri,
      statement.source,
      segment.start,
      segment.end,
      visibility,
      'variable'
    );
    if (definition?.typeName === undefined) {
      continue;
    }

    definitions.push(definition);
    declarations.push({
      name: definition.name,
      typeName: definition.typeName
    });
  }

  return { definitions, declarations, endLine: statement.source.endLine };
}

function parseVariableDeclaratorDefinitionFromSource(
  uri: string,
  source: LogicalSourceText,
  startCharacter: number,
  endCharacter: number,
  visibility: 'public' | 'private' | 'local',
  kind: 'local' | 'variable'
): VbaDefinition | undefined {
  const line = source.text;
  const trimmed_start = skipWhitespace(line, startCharacter, endCharacter);
  const trimmed_end = trimEndIndex(line, endCharacter);
  const name_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (name_token === undefined || name_token.lowerText === 'as') {
    return undefined;
  }

  return {
    name: name_token.text,
    kind,
    visibility,
    uri,
    range: getLogicalSourceRange(source, name_token.start, name_token.end),
    typeName: parseDeclaratorTypeName(line, name_token.end, trimmed_end)
  };
}

function getSourceDeclarationVisibility(
  line: string,
  codeEnd: number,
  startCharacter = 0
): 'public' | 'private' {
  const first_token = readIdentifierTokenAt(line, skipWhitespace(line, startCharacter, codeEnd), codeEnd);
  return first_token?.lowerText === 'public' ? 'public' : 'private';
}

function hasMalformedDeclaratorTypeAnnotation(
  line: string,
  startCharacter: number,
  endCharacter: number
): boolean {
  const trimmed_start = skipWhitespace(line, startCharacter, endCharacter);
  const trimmed_end = trimEndIndex(line, endCharacter);
  const name_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (name_token === undefined || name_token.lowerText === 'as') {
    return false;
  }

  const equals_index = findTopLevelEquals(line, name_token.end, trimmed_end);
  const type_annotation_end = equals_index ?? trimmed_end;
  let character_index = skipWhitespace(line, name_token.end, type_annotation_end);
  if (character_index < type_annotation_end && line[character_index] === '(') {
    const closing_paren = findClosingParenInCode(line, character_index, type_annotation_end);
    if (closing_paren === undefined) {
      return false;
    }
    character_index = skipWhitespace(line, closing_paren + 1, type_annotation_end);
  }

  if (!startsWithKeywordAt(line, character_index, 'as', type_annotation_end)) {
    return false;
  }

  const type_name = parseDeclaratorTypeName(line, name_token.end, type_annotation_end);
  return type_name === undefined || type_name === '_';
}

function hasMalformedDeclaratorArrayBounds(
  line: string,
  startCharacter: number,
  endCharacter: number
): boolean {
  const trimmed_start = skipWhitespace(line, startCharacter, endCharacter);
  const trimmed_end = trimEndIndex(line, endCharacter);
  const name_token = readIdentifierTokenAt(line, trimmed_start, trimmed_end);
  if (name_token === undefined || name_token.lowerText === 'as') {
    return false;
  }

  const bounds_start = skipWhitespace(line, name_token.end, trimmed_end);
  return bounds_start < trimmed_end
    && line[bounds_start] === '('
    && findClosingParenInCode(line, bounds_start, trimmed_end) === undefined;
}

function parseDeclaratorTypeName(
  line: string,
  afterNameCharacter: number,
  endCharacter: number
): string | undefined {
  let character_index = skipWhitespace(line, afterNameCharacter, endCharacter);
  if (character_index < endCharacter && line[character_index] === '(') {
    const closing_paren = findClosingParenInCode(line, character_index, endCharacter);
    if (closing_paren === undefined) {
      return undefined;
    }
    character_index = skipWhitespace(line, closing_paren + 1, endCharacter);
  }

  if (!startsWithKeywordAt(line, character_index, 'as', endCharacter)) {
    return undefined;
  }

  let type_start = skipWhitespace(line, character_index + 'As'.length, endCharacter);
  if (startsWithKeywordAt(line, type_start, 'new', endCharacter)) {
    type_start = skipWhitespace(line, type_start + 'New'.length, endCharacter);
  }

  return readDeclarationTypeName(line, type_start, endCharacter);
}

function readDeclarationTypeName(
  line: string,
  startCharacter: number,
  endCharacter: number
): string | undefined {
  const qualifier = readIdentifierTokenAt(line, startCharacter, endCharacter);
  if (qualifier === undefined) {
    return undefined;
  }

  const dot_index = skipWhitespace(line, qualifier.end, endCharacter);
  if (dot_index >= endCharacter || line[dot_index] !== '.') {
    return qualifier.text;
  }

  const member_start = skipWhitespace(line, dot_index + 1, endCharacter);
  const member = readIdentifierTokenAt(line, member_start, endCharacter);
  return member === undefined
    ? undefined
    : `${qualifier.text}.${member.text}`;
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
