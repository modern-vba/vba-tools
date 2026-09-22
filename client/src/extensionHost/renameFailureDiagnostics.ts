import { useVbaRenameFailureObserverForTest } from '../rename';

const observationLimit = 4;
const fieldLimit = 512;

export async function withRenameFailureDiagnostics(
  scenario: string,
  action: () => Promise<void>,
  report: (text: string) => void = console.error
): Promise<void> {
  const failures: string[] = [];
  const observer = useVbaRenameFailureObserverForTest(({ phase, error }) => {
    const fields = [`phase=${quoteField(phase)}`];
    if (isRecord(error)) {
      if (typeof error.code === 'number' && Number.isFinite(error.code)) {
        fields.push(`code=${error.code}`);
      }
      if (isRecord(error.data)) {
        for (const field of ['reason', 'condition', 'path'] as const) {
          const value = error.data[field];
          if (typeof value === 'string') fields.push(`${field}=${quoteField(value)}`);
        }
      }
    }
    failures.push(fields.join(' '));
    if (failures.length > observationLimit) failures.shift();
  });
  try {
    await action();
  } catch (error) {
    try {
      report([
        '[Rename failure diagnostics]',
        `Scenario: ${quoteField(scenario)}`,
        ...failures,
        '[/Rename failure diagnostics]'
      ].join('\n'));
    } catch {
      // Reporting must not replace the original integration-test failure.
    }
    throw error;
  } finally {
    observer.dispose();
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function quoteField(value: string): string {
  let bounded = value.slice(0, fieldLimit);
  for (;;) {
    const quoted = JSON.stringify(bounded)
      .replace(/\u2028/g, '\\u2028')
      .replace(/\u2029/g, '\\u2029');
    if (quoted.length <= fieldLimit) return quoted;
    bounded = bounded.slice(0, bounded.length - Math.ceil((quoted.length - fieldLimit) / 6));
  }
}
