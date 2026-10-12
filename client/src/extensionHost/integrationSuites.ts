export type ExtensionHostTestProfile = 'ordinary' | 'windows-excel';

export interface ExtensionHostIntegrationSuite {
  name: string;
  requiresExcel: boolean;
  run: () => Promise<void>;
}

export async function runExtensionHostIntegrationSuites(
  profile: ExtensionHostTestProfile,
  suites: readonly ExtensionHostIntegrationSuite[]
): Promise<string[]> {
  const completed: string[] = [];
  for (const suite of suites) {
    if (suite.requiresExcel !== (profile === 'windows-excel')) continue;
    await suite.run();
    completed.push(suite.name);
  }
  return completed;
}

export function parseExtensionHostTestProfile(args: readonly string[]): ExtensionHostTestProfile {
  if (args.length === 0) return 'ordinary';
  if (args.length === 1 && args[0] === '--windows-excel') return 'windows-excel';
  throw new Error('Extension Host test runner accepts only --windows-excel or no arguments.');
}
