import type { SourcePosition } from './sourceRange';
import type { VbaModule } from './vbaSourceModel';

export interface CompletionSuppressionOptions {
  allowMalformedMemberAccess?: boolean;
}

export function shouldSuppressNameResolutionAt(
  module: VbaModule,
  position: SourcePosition
): boolean {
  return isInMalformedExpressionRegion(module, position)
    || isInMalformedMemberAccessRegion(module, position);
}

export function shouldSuppressCompletionAt(
  module: VbaModule,
  position: SourcePosition,
  options: CompletionSuppressionOptions = {}
): boolean {
  return isInMalformedExpressionRegion(module, position)
    || (
      isInMalformedMemberAccessRegion(module, position)
      && options.allowMalformedMemberAccess !== true
    );
}

export function shouldSuppressSignatureHelpAt(
  module: VbaModule,
  position: SourcePosition
): boolean {
  return isInMalformedMemberAccessRegion(module, position);
}

export function isInMalformedExpressionRegion(
  module: VbaModule,
  position: SourcePosition
): boolean {
  return module.syntaxDiagnostics.some((diagnostic) =>
    diagnostic.code === 'syntax.malformedExpression'
    && diagnostic.range.start.line === position.line
    && position.character >= diagnostic.range.start.character
  );
}

export function isInMalformedMemberAccessRegion(
  module: VbaModule,
  position: SourcePosition
): boolean {
  return module.syntaxDiagnostics.some((diagnostic) =>
    diagnostic.code === 'syntax.malformedMemberAccess'
    && diagnostic.range.start.line === position.line
    && position.character >= diagnostic.range.start.character
  );
}
