import { runWithExtensionHostCleanup } from './testRunCleanup';

export async function runWithExtensionHostFixtureCleanup(
  directory: string,
  run: () => Promise<void>,
  release: () => Promise<void>
): Promise<void> {
  let runFailure: { error: unknown } | undefined;
  try {
    await run();
  } catch (error) {
    runFailure = { error };
  }
  try {
    // Keep the existing barrier: unproved surface release must not start removal.
    await release();
  } catch (error) {
    if (!runFailure) { throw error; }
    throw new AggregateError([runFailure.error, error],
      'Extension Host fixture and surface release failed.', { cause: runFailure.error });
  }
  await runWithExtensionHostCleanup([directory], async () => {
    if (runFailure) { throw runFailure.error; }
  });
}
