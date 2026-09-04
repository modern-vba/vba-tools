import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  CompanionExecutableLanguageServerLifecycle
} from './companionLanguageServerLifecycle';
import { VbaDevSessionResolver } from './devtool';

test('companion resolution starts only after trusted language assistance is operational', async () => {
  let resolveCompanion!: (value: {
    readonly executablePath: string;
    readonly capabilities: {
      readonly commands: Record<string, { readonly outputSchemaVersion: string }>;
    };
  }) => void;
  let resolutionCalls = 0;
  let userFormActivationCalls = 0;
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: () => {
      resolutionCalls += 1;
      return new Promise((resolve) => {
        resolveCompanion = resolve;
      });
    },
    sendNotification: async () => undefined,
    startUserFormEventCatalog: () => {
      userFormActivationCalls += 1;
    }
  });

  lifecycle.activateTrustedServices();
  assert.equal(resolutionCalls, 0);
  assert.equal(userFormActivationCalls, 0);

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  assert.equal(resolutionCalls, 1);
  assert.equal(userFormActivationCalls, 1);

  lifecycle.activateTrustedServices();
  assert.equal(resolutionCalls, 1);
  assert.equal(userFormActivationCalls, 1);

  resolveCompanion({
    executablePath: String.raw`C:\tools\vba-dev.exe`,
    capabilities: {
      commands: {
        'reference list': { outputSchemaVersion: '1.0' }
      }
    }
  });
  await lifecycle.flush();
});

test('restricted language assistance never starts managed companion work', async () => {
  let resolutionCalls = 0;
  let userFormActivationCalls = 0;
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => false,
    resolveCompanion: async () => {
      resolutionCalls += 1;
      throw new Error('Restricted Mode must not resolve a companion.');
    },
    sendNotification: async () => {
      throw new Error('Restricted Mode must not publish a companion.');
    },
    startUserFormEventCatalog: () => {
      userFormActivationCalls += 1;
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await lifecycle.flush();

  assert.equal(resolutionCalls, 0);
  assert.equal(userFormActivationCalls, 0);
});

test('validated companion resolution is published with the strict reference-list contract', async () => {
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => ({
      executablePath: String.raw`C:\tools\vba-dev.exe`,
      capabilities: {
        commands: {
          'reference list': { outputSchemaVersion: '1.0' }
        }
      }
    }),
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    },
    startUserFormEventCatalog: () => undefined
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await lifecycle.flush();

  assert.deepEqual(notifications, [{
    method: 'vba/companionExecutable',
    parameters: {
      schemaVersion: '1.0',
      executablePath: String.raw`C:\tools\vba-dev.exe`,
      referenceListOutputSchemaVersion: '1.0'
    }
  }]);
});

test('a language-client restart replays the pinned companion without another resolution', async () => {
  let resolutionCalls = 0;
  const notifications: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => {
      resolutionCalls += 1;
      return {
        executablePath: String.raw`C:\tools\vba-dev.exe`,
        capabilities: {
          commands: {
            'reference list': { outputSchemaVersion: '1.0' }
          }
        }
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    startUserFormEventCatalog: () => undefined
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await lifecycle.flush();
  lifecycle.observeLanguageClientRunning(false);
  lifecycle.observeLanguageClientRunning(true);
  await lifecycle.flush();

  assert.equal(resolutionCalls, 1);
  assert.equal(notifications.length, 2);
  assert.deepEqual(notifications[1], notifications[0]);
});

test('deactivation fences a late companion resolution from the language server', async () => {
  let completeResolution!: () => void;
  const notifications: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: () => new Promise((resolve) => {
      completeResolution = () => resolve({
        executablePath: String.raw`C:\tools\vba-dev.exe`,
        capabilities: {
          commands: {
            'reference list': { outputSchemaVersion: '1.0' }
          }
        }
      });
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    startUserFormEventCatalog: () => undefined
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  lifecycle.dispose();
  completeResolution();
  await lifecycle.flush();

  assert.deepEqual(notifications, []);
});

test('deactivation observes a cancelled resolution without reporting a stale failure', async () => {
  let rejectResolution!: (error: Error) => void;
  const errors: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: () => new Promise((_resolve, reject) => {
      rejectResolution = reject;
    }),
    sendNotification: async () => undefined,
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      errors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  lifecycle.dispose();
  rejectResolution(new Error('The capability process was cancelled.'));
  await lifecycle.flush();

  assert.deepEqual(errors, []);
});

test('a trust change fences a late companion resolution from the language server', async () => {
  let trusted = true;
  let completeResolution!: () => void;
  const notifications: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => trusted,
    resolveCompanion: () => new Promise((resolve) => {
      completeResolution = () => resolve({
        executablePath: String.raw`C:\tools\vba-dev.exe`,
        capabilities: {
          commands: {
            'reference list': { outputSchemaVersion: '1.0' }
          }
        }
      });
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    startUserFormEventCatalog: () => undefined
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  trusted = false;
  completeResolution();
  await lifecycle.flush();

  assert.deepEqual(notifications, []);
});

test('a trust change suppresses a stale companion resolution failure', async () => {
  let trusted = true;
  let rejectResolution!: (error: Error) => void;
  const errors: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => trusted,
    resolveCompanion: () => new Promise((_resolve, reject) => {
      rejectResolution = reject;
    }),
    sendNotification: async () => undefined,
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      errors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  trusted = false;
  rejectResolution(new Error('The stale capability process failed.'));
  await lifecycle.flush();

  assert.deepEqual(errors, []);
});

test('a companion resolution failure is observed without rejecting lifecycle cleanup', async () => {
  const resolutionErrors: unknown[] = [];
  const publicationErrors: unknown[] = [];
  const failure = new Error('capability probe failed');
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => {
      throw failure;
    },
    sendNotification: async () => {
      throw new Error('No notification is expected.');
    },
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      resolutionErrors.push(error);
    },
    reportPublicationError: (error) => {
      publicationErrors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await assert.doesNotReject(lifecycle.flush());

  assert.deepEqual(resolutionErrors, [failure]);
  assert.deepEqual(publicationErrors, []);
});

test('a companion recovered by another session consumer is published without a lifecycle retry', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  let companionAvailable = false;
  let resolutionAttempts = 0;
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    requiredContract: {
      contractVersion: '1.0',
      commandSchemaVersions: {
        'reference list': '1.0'
      }
    },
    runProcess: async () => {
      resolutionAttempts += 1;
      if (!companionAvailable) {
        throw new Error('The companion is temporarily unavailable.');
      }

      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            'reference list': { outputSchemaVersion: '1.0' }
          }
        }),
        stderr: ''
      };
    }
  });
  const notifications: unknown[] = [];
  const resolutionErrors: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: () => resolver.resolve(),
    observeCompanionResolution: (listener) => resolver.onDidResolve(listener),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      resolutionErrors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await lifecycle.flush();

  assert.equal(resolutionAttempts, 1);
  assert.equal(resolutionErrors.length, 1);
  assert.deepEqual(notifications, []);

  companionAvailable = true;
  const recoveredForCommand = await resolver.resolve();
  await lifecycle.flush();

  assert.equal(resolutionAttempts, 2);
  assert.equal(await resolver.resolve(), recoveredForCommand);
  assert.equal(resolutionAttempts, 2);
  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    executablePath: recoveredForCommand.executablePath,
    referenceListOutputSchemaVersion: '1.0'
  }]);
});

test('a current-generation publication failure is reported separately and retried only for the next running server generation', async () => {
  let resolutionCalls = 0;
  let publicationCalls = 0;
  const resolutionErrors: unknown[] = [];
  const publicationErrors: unknown[] = [];
  const failure = new Error('current connection rejected notification');
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => {
      resolutionCalls += 1;
      return {
        executablePath: String.raw`C:\tools\vba-dev.exe`,
        capabilities: {
          commands: {
            'reference list': { outputSchemaVersion: '1.0' }
          }
        }
      };
    },
    sendNotification: async () => {
      publicationCalls += 1;
      if (publicationCalls === 1) {
        throw failure;
      }
    },
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      resolutionErrors.push(error);
    },
    reportPublicationError: (error) => {
      publicationErrors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await lifecycle.flush();
  lifecycle.observeLanguageClientRunning(false);
  lifecycle.observeLanguageClientRunning(true);
  await lifecycle.flush();

  assert.equal(resolutionCalls, 1);
  assert.equal(publicationCalls, 2);
  assert.deepEqual(resolutionErrors, []);
  assert.deepEqual(publicationErrors, [failure]);
});

test('a publication rejection from a stopped connection is silently discarded', async () => {
  let rejectPublication!: (error: Error) => void;
  let signalPublicationStarted!: () => void;
  const publicationStarted = new Promise<void>((resolve) => {
    signalPublicationStarted = resolve;
  });
  const resolutionErrors: unknown[] = [];
  const publicationErrors: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => ({
      executablePath: String.raw`C:\tools\vba-dev.exe`,
      capabilities: {
        commands: {
          'reference list': { outputSchemaVersion: '1.0' }
        }
      }
    }),
    sendNotification: () => new Promise((_resolve, reject) => {
      rejectPublication = reject;
      signalPublicationStarted();
    }),
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      resolutionErrors.push(error);
    },
    reportPublicationError: (error) => {
      publicationErrors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await publicationStarted;
  lifecycle.observeLanguageClientRunning(false);
  rejectPublication(new Error('stopped connection rejected notification'));
  await lifecycle.flush();

  assert.deepEqual(resolutionErrors, []);
  assert.deepEqual(publicationErrors, []);
});

test('a publication rejection from a stale generation is silently discarded before replay', async () => {
  let rejectFirstPublication!: (error: Error) => void;
  let signalFirstPublicationStarted!: () => void;
  const firstPublicationStarted = new Promise<void>((resolve) => {
    signalFirstPublicationStarted = resolve;
  });
  let publicationCalls = 0;
  const publicationErrors: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => ({
      executablePath: String.raw`C:\tools\vba-dev.exe`,
      capabilities: {
        commands: {
          'reference list': { outputSchemaVersion: '1.0' }
        }
      }
    }),
    sendNotification: () => {
      publicationCalls += 1;
      if (publicationCalls !== 1) {
        return Promise.resolve();
      }
      return new Promise((_resolve, reject) => {
        rejectFirstPublication = reject;
        signalFirstPublicationStarted();
      });
    },
    startUserFormEventCatalog: () => undefined,
    reportPublicationError: (error) => {
      publicationErrors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await firstPublicationStarted;
  lifecycle.observeLanguageClientRunning(false);
  lifecycle.observeLanguageClientRunning(true);
  rejectFirstPublication(new Error('stale connection rejected notification'));
  await lifecycle.flush();

  assert.equal(publicationCalls, 2);
  assert.deepEqual(publicationErrors, []);
});

test('an invalid reference-list capability fails closed before publication', async () => {
  const notifications: unknown[] = [];
  const errors: unknown[] = [];
  const lifecycle = new CompanionExecutableLanguageServerLifecycle({
    isTrusted: () => true,
    resolveCompanion: async () => ({
      executablePath: String.raw`C:\tools\vba-dev.exe`,
      capabilities: {
        commands: {
          'reference list': { outputSchemaVersion: '0.9' }
        }
      }
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    startUserFormEventCatalog: () => undefined,
    reportResolutionError: (error) => {
      errors.push(error);
    }
  });

  lifecycle.observeLanguageClientRunning(true);
  lifecycle.activateTrustedServices();
  await lifecycle.flush();

  assert.deepEqual(notifications, []);
  assert.equal(errors.length, 1);
  assert.match(String(errors[0]), /reference list.*1\.0/u);
});
