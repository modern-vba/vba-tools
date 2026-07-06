import { formatHostApplicationName } from './officeHostCatalog';
import type {
  CallableParameter,
  HostDefinition
} from './hostDefinition';
import type { SourceRange } from './sourceRange';
import type {
  DocumentationComment,
  VbaDefinition
} from './vbaSourceModel';

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
  | 'event'
  | 'macro';

export type SemanticTokenModifier = 'readonly';

export interface VbaSemanticToken {
  range: SourceRange;
  tokenType: SemanticTokenType;
  tokenModifiers?: SemanticTokenModifier[];
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
  'event',
  'macro'
];

export const VBA_SEMANTIC_TOKEN_MODIFIERS: SemanticTokenModifier[] = [
  'readonly'
];

export function renderDocumentationComment(documentation: DocumentationComment): string {
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

export function renderHostDefinitionHover(definition: HostDefinition): string {
  const detail = getHostDefinitionDetail(definition);
  const value = definition.value === undefined ? undefined : `Value: ${definition.value}`;
  return detail === undefined
    ? [value, definition.documentation].filter((section) => section !== undefined && section !== '').join('\n\n')
    : [detail, value, definition.documentation].filter((section) => section !== undefined && section !== '').join('\n\n');
}

export function getHostDefinitionDetail(definition: HostDefinition): string | undefined {
  if (definition.hostApplication === undefined) {
    return undefined;
  }

  if (definition.parentName !== undefined) {
    return `${formatHostApplicationName(definition.hostApplication)}.${definition.parentName}.${definition.name}`;
  }

  return `${formatHostApplicationName(definition.hostApplication)}.${definition.name}`;
}

export function renderSignatureDocumentation(documentation: DocumentationComment | undefined): string | undefined {
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

export function renderCallableParameterMetadata(parameter: CallableParameter): string | undefined {
  const sections = [
    parameter.typeName,
    parameter.optional === true ? 'Optional.' : undefined,
    parameter.defaultValue === undefined ? undefined : `Default: ${parameter.defaultValue}.`
  ].filter((section) => section !== undefined && section !== '');

  return sections.length === 0 ? undefined : sections.join(' ');
}

export function renderSourceCallableParameterMetadata(parameter: CallableParameter): string | undefined {
  if (parameter.optional !== true
    && parameter.isParamArray !== true
    && parameter.defaultValue === undefined) {
    return undefined;
  }

  return renderCallableParameterMetadata(parameter);
}

export function getParameterDocumentation(documentation: DocumentationComment | undefined): Map<string, string> {
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

export function completionKindForVbaDefinition(definition: VbaDefinition): CompletionEntryKind {
  if (definition.kind === 'constant') {
    return 'constant';
  }

  const token_type = semanticTokenTypeForVbaDefinition(definition);
  return token_type === undefined ? 'variable' : completionKindForSemanticTokenType(token_type);
}

export function completionKindForHostDefinition(definition: HostDefinition): CompletionEntryKind {
  if (definition.kind === 'constant') {
    return 'constant';
  }

  return completionKindForSemanticTokenType(semanticTokenTypeForHostDefinition(definition));
}

export function semanticTokenTypeForVbaDefinition(definition: VbaDefinition): SemanticTokenType | undefined {
  switch (definition.kind) {
    case 'sub':
    case 'function':
      return 'function';
    case 'property':
    case 'typeField':
      return 'property';
    case 'variable':
    case 'local':
      return 'variable';
    case 'constant':
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

export function semanticTokenModifiersForVbaDefinition(
  definition: VbaDefinition
): SemanticTokenModifier[] | undefined {
  return definition.kind === 'constant' ? ['readonly'] : undefined;
}

export function semanticTokenTypeForHostDefinition(definition: HostDefinition): SemanticTokenType {
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

export function semanticTokenModifiersForHostDefinition(
  definition: HostDefinition
): SemanticTokenModifier[] | undefined {
  return definition.kind === 'constant' ? ['readonly'] : undefined;
}

function completionKindForSemanticTokenType(tokenType: SemanticTokenType): CompletionEntryKind {
  return tokenType === 'macro' ? 'variable' : tokenType;
}
