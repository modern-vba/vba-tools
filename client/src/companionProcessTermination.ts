export type AbnormalProcessTermination =
  | { readonly kind: 'exit'; readonly signedExitCode: number; readonly hexExitCode: string }
  | { readonly kind: 'signal'; readonly signal: string };

export function classifyAbnormalProcessTermination(
  exitCode: number | null | undefined,
  signal: string | null | undefined
): AbnormalProcessTermination | undefined {
  if (signal !== null && signal !== undefined && signal.length > 0) {
    return { kind: 'signal', signal };
  }
  if (exitCode === null || exitCode === undefined || !Number.isInteger(exitCode)) {
    return undefined;
  }
  const unsignedExitCode = exitCode >>> 0;
  if (unsignedExitCode < 0x80000000) {
    return undefined;
  }
  return {
    kind: 'exit',
    signedExitCode: unsignedExitCode | 0,
    hexExitCode: `0x${unsignedExitCode.toString(16).toUpperCase().padStart(8, '0')}`
  };
}

export function classifyAbnormalProcessError(error: unknown): AbnormalProcessTermination | undefined {
  if (!(error instanceof Error)) {
    return undefined;
  }
  const processError = error as Error & { code?: unknown; signal?: unknown };
  return classifyAbnormalProcessTermination(
    typeof processError.code === 'number' ? processError.code : undefined,
    typeof processError.signal === 'string' ? processError.signal : undefined
  );
}

export function formatAbnormalProcessTermination(
  role: string,
  stage: string,
  executablePath: string,
  attempt: number,
  maximumAttempts: number,
  termination: AbnormalProcessTermination,
  outcome: 'retry-pending' | 'recovered' | 'failed' | 'not-retried'
): string {
  const status = termination.kind === 'exit'
    ? `exitCodeSigned=${termination.signedExitCode} exitCodeHex=${termination.hexExitCode}`
    : `signal=${termination.signal}`;
  return `Companion process terminated abnormally: role=${role} stage=${stage} ` +
    `executable=${JSON.stringify(executablePath)} attempt=${attempt}/${maximumAttempts} ` +
    `${status} outcome=${outcome}`;
}
