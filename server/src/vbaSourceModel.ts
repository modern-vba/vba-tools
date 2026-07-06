import type { CallableSignature, HostApplication, HostDefinition } from './hostDefinition';
import type { LogicalSourceText } from './logicalSource';
import type { SourcePosition, SourceRange } from './sourceRange';

export interface DefinitionLocation {
  uri: string;
  range: SourceRange;
}

export interface DocumentationComment {
  brief: string[];
  details: string[];
  params: string[];
  returns?: string;
}

export interface VbaDefinition {
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
    | 'constant'
    | 'variable'
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

export interface MemberChainSegment {
  name: string;
  range: SourceRange;
  hasCall: boolean;
}

export interface MemberChainExpression {
  segments: MemberChainSegment[];
  targetSegmentIndex: number;
  usesWithReceiver?: boolean;
}

export interface MemberCompletionRequest {
  qualifier: string;
  prefix: string;
  receiverChain?: MemberChainExpression;
  usesWithReceiver?: boolean;
}

export interface CallExpression {
  name: string;
  nameStart: number;
  activeParameter: number;
  namedArgumentName?: string;
  chain?: MemberChainExpression;
  eventReference?: boolean;
}

export interface WithReceiverDeclaration {
  chain?: MemberChainExpression;
  end: SourcePosition;
}

export interface WithReceiverSourceText extends LogicalSourceText {
  endLine: number;
  endCharacter: number;
  hasCommentContinuation: boolean;
}

export type TypeResolutionRef =
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

export interface ResolvedChainSegment {
  resolution?: NameResolutionResult;
  typeRef?: TypeResolutionRef;
}

export interface ProcedureScope {
  range: SourceRange;
  definitions: VbaDefinition[];
}

export interface ModuleMember {
  range: SourceRange;
  definitions: VbaDefinition[];
  procedureScopes: ProcedureScope[];
  withEventsDeclarations: WithEventsDeclaration[];
  implements: string[];
}

export interface WithEventsDeclaration {
  name: string;
  typeName: string;
}

export type VbaModuleKind = 'standard' | 'class' | 'form';

export interface VbaModule {
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

export type NameResolutionResult =
  | {
      source: 'vba';
      definition: DefinitionLocation;
    }
  | {
      source: 'host';
      definition: HostDefinition;
    };

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
