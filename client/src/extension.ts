import * as path from 'node:path';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';

import {
  CancellationTokenSource,
  DebugAdapterExecutable,
  DebugConfiguration,
  DebugConfigurationProviderTriggerKind,
  ExtensionContext,
  OutputChannel,
  ProgressLocation,
  RelativePattern,
  SourceBreakpoint,
  StatusBarAlignment,
  Uri,
  commands,
  debug,
  languages,
  tests,
  window,
  workspace
} from 'vscode';
import {
  DocumentFormattingRequest,
  LanguageClient,
  State
} from 'vscode-languageclient/node';
import {
  promptForFirstRunDoctor,
  runDoctorCommand
} from './doctorCommand';
import {
  CommonModulesToolCommand,
  runCommonModulesAddCommand,
  runCommonModulesListCommand,
  runCommonModulesUpdateCommand
} from './commonModulesCommand';
import {
  WorkbookBackedProjectCandidate,
  findNearestProjectManifest
} from './projectDiscovery';
import {
  WorkbookBackedProjectToolCommand,
  runWorkbookBackedProjectCommand
} from './projectCommand';
import { ExportCommandRequest, runExportCommand } from './exportCommand';
import {
  ReferenceToolCommand,
  runReferenceAddCommand,
  runReferenceListCommand,
  runReferenceRemoveCommand
} from './referenceCommand';
import {
  createWorkbookBackedTestExplorer
} from './testExplorer';
import {
  captureSnapshotSourceInventory,
  createCallerOwnedSourceSnapshotCapture
} from './snapshotSourceInventory';
import {
  registerWorkbookBackedTestExplorerRefresh
} from './testExplorerRefresh';
import {
  registerWorkbookBackedTestExplorerSourceInvalidation
} from './testExplorerInvalidation';
import {
  VbaDevDiagnosticReporter
} from './toolDiagnostics';
import {
  createVbaDocumentFormattingMiddleware
} from './documentFormatting';
import {
  createVbaLanguageClientOptions,
  createVbaLanguageServerOptions,
  createVbaLanguageServerReferenceCatalogCacheRoot
} from './languageServer';
import {
  ProjectManifestLanguageServerSync,
  registerProjectManifestLanguageServerSync
} from './projectManifestLanguageServerSync';
import {
  HostClassProjectionLifecycle
} from './hostClassProjectionLifecycle';
import {
  classifyHostClassTextDocumentChange,
  HostClassProjectionWorkspace,
  HostClassProjectionWorkspaceDocument
} from './hostClassProjectionWorkspace';
import {
  HostClassProjectionStatusObserver
} from './hostClassProjectionStatus';
import {
  HostClassProjectionWatcherRegistry
} from './hostClassProjectionWatcherRegistry';
import {
  collectHostClassProjectionFormSources
} from './hostClassProjectionSourceCollection';
import {
  runHostClassProjectionRefreshCommand
} from './hostClassProjectionRefreshCommand';
import {
  createVscodeDiagnosticCollectionAdapter,
  createVscodeTestControllerAdapter
} from './vscodeAdapters';
import {
  openVbaDevTerminal
} from './vbaDevTerminalCommand';
import {
  CompanionExecutableResolution,
  CompanionExecutableResolver,
  VbaDevResolutionLog,
  VbaDevResolutionNotice,
  VbaDevResolutionNoticeAction,
  VbaDevSessionResolver,
  formatVbaDevResolutionLog,
  isReportedVbaDevResolutionFailure,
  noCompatibleVbaDevMessage
} from './devtool';
import {
  runBlockSkeletonInsertionAfterNativeEnter
} from './blockSkeletonInsertionAdapter';
import {
  BlockSkeletonInsertionPlan,
  createLanguageClientBlockSkeletonInsertionPlanProvider,
  formatBlockSkeletonInsertionFallbackTrace,
  useBlockSkeletonInsertionPlanProvider
} from './blockSkeletonInsertion';
import { NativeLineBreakRecorder } from './nativeLineBreak';
import {
  VscodeDebugIntegration,
  createVbaDebugConfigurationProvider,
  handleVbaDebugLifecycleRequest,
  stopVbaDebugSessionAfterLifecycleFailure
} from './vscodeDebugIntegration';
import type { VbaDebugConfiguration } from './vscodeDebugConfiguration';
import { decodeVbaSourceFileText } from './vbaSourceFileText';
import { createLazyOutputChannel } from './lazyOutputChannel';
import { runVbaDevCommandInvocation } from './devtoolRuntime';
import {
  ManagedToolingCommandOperations,
  ManagedToolingWorkspaceTrustGate,
  createManagedToolingCommandHandlers,
  resolveCompanionExecutableForLanguageActivation
} from './workspaceTrust';

let client: LanguageClient | undefined;
let outputChannel: OutputChannel | undefined;
let toolDiagnosticReporter: VbaDevDiagnosticReporter | undefined;
let activeVscodeDebugIntegration: VscodeDebugIntegration | undefined;
let hostClassProjectionLifecycle: HostClassProjectionLifecycle | undefined;
let hostClassProjectionWorkspace: HostClassProjectionWorkspace | undefined;

export async function activate(context: ExtensionContext): Promise<void> {
  const extensionOutputChannel = createLazyOutputChannel(
    'VBA Tools',
    () => window.createOutputChannel('VBA Tools')
  );
  outputChannel = extensionOutputChannel;
  context.subscriptions.push(extensionOutputChannel);
  const vbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: context.extensionPath,
    configuredPathProvider: getConfiguredDevToolPath,
    reportLog: (log) => appendVbaDevResolutionLog(outputChannel, log),
    reportNotice: (notice) => reportVbaDevResolutionNotice(outputChannel, notice)
  });
  const backgroundVbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: context.extensionPath,
    configuredPathProvider: getConfiguredDevToolPath,
    reportLog: (log) => appendVbaDevResolutionLog(outputChannel, log)
  });
  const workspaceTrustGate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => workspace.isTrusted,
    invalidateManagedToolingState: () => {
      vbaDevResolver.invalidate();
      backgroundVbaDevResolver.invalidate();
    },
    showWarningMessage: (message, ...actions) => (
      window.showWarningMessage(message, ...actions)
    ),
    executeCommand: (command) => commands.executeCommand(command)
  });
  let initialVbaDevResolution: CompanionExecutableResolution | undefined;
  try {
    initialVbaDevResolution = await resolveCompanionExecutableForLanguageActivation(
      workspace.isTrusted,
      () => vbaDevResolver.resolve()
    );
  } catch (error) {
    if (!isReportedVbaDevResolutionFailure(error)) {
      reportUnreportedVbaDevResolutionFailure(outputChannel, error);
    }
  }
  const vscodeDebugIntegration = new VscodeDebugIntegration({
    extensionRoot: context.extensionPath,
    getConfiguredDevToolPath,
    getConfiguredDebugAdapterPath,
    vbaDevResolver,
    requireTrustedWorkspace: () => (
      workspaceTrustGate.requireTrusted('managed-tooling')
    ),
    reportDebugAdapterCleanupWarning: (message) => {
      outputChannel?.appendLine(`[vba-debug-adapter] ${message}`);
    },
    debugConfigurationHost: {
      get workspaceRoots() {
        return workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [];
      },
      getActiveEditor: () => {
        const editor = window.activeTextEditor;
        if (editor?.document.uri.scheme !== 'file') {
          return undefined;
        }

        return {
          uriPath: editor.document.uri.fsPath,
          line: editor.selection.active.line,
          character: editor.selection.active.character
        };
      },
      getOpenTextDocuments: () => workspace.textDocuments
        .filter((document) => document.uri.scheme === 'file')
        .map((document) => ({
          uriPath: document.uri.fsPath,
          isDirty: document.isDirty,
          save: () => document.save()
        })),
      getSourceBreakpoints: () => debug.breakpoints
        .filter((breakpoint): breakpoint is SourceBreakpoint => (
          breakpoint instanceof SourceBreakpoint
          && breakpoint.location.uri.scheme === 'file'
        ))
        .map((breakpoint) => ({
          uriPath: breakpoint.location.uri.fsPath,
          line: breakpoint.location.range.start.line,
          enabled: breakpoint.enabled,
          condition: breakpoint.condition,
          hitCondition: breakpoint.hitCondition,
          logMessage: breakpoint.logMessage
        })),
      findProjectManifests: async () => findProjectManifests(),
      readTextFile,
      readSourceText: async (filePath) => decodeVbaSourceFileText(
        await workspace.fs.readFile(Uri.file(filePath))
      ),
      findExportedSourceFiles: async (sourceSetPath) => (
        await workspace.findFiles(
          new RelativePattern(sourceSetPath, '**/*.{bas,cls,frm}'),
          null
        )
      ).map((uri) => uri.fsPath),
      captureSourceInventory: (sourceSetPath, cancellationToken) => captureSnapshotSourceInventory(
        sourceSetPath,
        {
          getActiveWindowsCodePage: () => vbaDevResolver.readActiveWindowsCodePage(),
          getOpenTextDocuments: () => workspace.textDocuments.map((document) => ({
            uriScheme: document.uri.scheme,
            uriPath: document.uri.scheme === 'file' ? document.uri.fsPath : undefined,
            fileName: document.fileName,
            isDirty: document.isDirty,
            encoding: document.encoding,
            getText: () => document.getText()
          })),
          findSourceFiles: async (sourceSetPath) => (
            await workspace.findFiles(
              new RelativePattern(sourceSetPath, '**/*.{bas,cls,frm,frx}'),
              null
            )
          ).map((uri) => uri.fsPath),
          readFile: async (filePath) => workspace.fs.readFile(Uri.file(filePath)),
          encodeText: async (text, encoding) => workspace.encode(text, { encoding }),
          decodeText: async (bytes, encoding) => workspace.decode(bytes, { encoding })
        },
        cancellationToken
      )
    }
  });
  activeVscodeDebugIntegration = vscodeDebugIntegration;
  const debugConfigurationProvider = createVbaDebugConfigurationProvider(
    vscodeDebugIntegration,
    (message) => window.showErrorMessage(message),
    () => workspaceTrustGate.requireTrusted('managed-tooling')
  );
  context.subscriptions.push(
    debug.registerDebugConfigurationProvider('vba', {
      provideDebugConfigurations: () => (
        [...debugConfigurationProvider.provideDebugConfigurations()] as DebugConfiguration[]
      ),
      resolveDebugConfiguration: (_folder, configuration) => (
        debugConfigurationProvider.resolveDebugConfiguration(
          configuration as VbaDebugConfiguration
        ) as DebugConfiguration | undefined
      ),
      resolveDebugConfigurationWithSubstitutedVariables: (folder, configuration, token) => (
        debugConfigurationProvider.resolveDebugConfigurationWithSubstitutedVariables(
          configuration as VbaDebugConfiguration,
          folder?.uri.fsPath,
          token
        ) as Promise<DebugConfiguration | undefined>
      )
    }, DebugConfigurationProviderTriggerKind.Dynamic),
    debug.registerDebugAdapterDescriptorFactory('vba', {
      createDebugAdapterDescriptor: async (session) => {
        try {
          const executable = await vscodeDebugIntegration.createDebugAdapterExecutable({
            id: session.id,
            workspaceRoot: session.workspaceFolder?.uri.fsPath,
            configuration: session.configuration as VbaDebugConfiguration,
            stop: () => debug.stopDebugging(session)
          });
          if (executable === undefined) {
            return undefined;
          }
          return new DebugAdapterExecutable(
            executable.command,
            [...executable.args],
            executable.options
          );
        } catch (error) {
          vscodeDebugIntegration.releaseSession(session.id);
          throw error;
        }
      }
    }),
    debug.registerDebugAdapterTrackerFactory('vba', {
      createDebugAdapterTracker: (session) => ({
        onWillReceiveMessage: (message) => {
          void handleVbaDebugLifecycleRequest(
            vscodeDebugIntegration,
            session.configuration as VbaDebugConfiguration,
            message,
            (command, argumentsValue) => session.customRequest(command, argumentsValue)
          )?.catch((error: unknown) => stopVbaDebugSessionAfterLifecycleFailure(
            error,
            (message) => { void window.showErrorMessage(message); },
            () => debug.stopDebugging(session),
            () => session.customRequest('disconnect', { terminateDebuggee: true })
          ));
        },
        onDidSendMessage: (message) => {
          vscodeDebugIntegration.observeDebugAdapterMessage(
            session.configuration as VbaDebugConfiguration,
            message
          );
        },
        onExit: () => {
          void vscodeDebugIntegration.handleAdapterExit(session.id);
        }
      })
    }),
    debug.onDidTerminateDebugSession((session) => {
      if (session.type === 'vba') {
        vscodeDebugIntegration.releaseSession(session.id);
      }
    })
  );
  const nativeLineBreakRecorder = new NativeLineBreakRecorder();
  context.subscriptions.push(nativeLineBreakRecorder);
  const sourceFileWatcher = workspace.createFileSystemWatcher('**/*.{bas,cls,frm}');
  const projectManifestWatcher = workspace.createFileSystemWatcher('**/vba-project.json');
  context.subscriptions.push(
    sourceFileWatcher,
    projectManifestWatcher
  );
  const hostClassStatusItem = window.createStatusBarItem(
    'vbaTools.hostClasses.status',
    StatusBarAlignment.Left,
    100
  );
  hostClassStatusItem.name = 'VBA Host Events';
  context.subscriptions.push(hostClassStatusItem);
  const hostClassStatus = new HostClassProjectionStatusObserver({
    updateStatus: (view) => {
      hostClassStatusItem.text = view.text;
      hostClassStatusItem.tooltip = view.tooltip;
      hostClassStatusItem.command = view.command;
      if (view.visible) {
        hostClassStatusItem.show();
      } else {
        hostClassStatusItem.hide();
      }
    },
    appendOutput: (line) => outputChannel?.appendLine(line)
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      if (!workspace.isTrusted) {
        throw new Error(
          'Host-class inspection is unavailable in Restricted Mode.'
        );
      }
      const result = await runVbaDevCommandInvocation({
        extensionRoot: context.extensionPath,
        vbaDevResolver: backgroundVbaDevResolver,
        outputChannel: extensionOutputChannel,
        revealOutput: false,
        cancellationToken: invocation.cancellationToken
      }, invocation.args);
      if (result === undefined) {
        throw new Error('A compatible vba-dev executable was not available.');
      }
      return {
        exitCode: result.exitCode,
        stdout: result.stdout,
        stderr: result.stderr,
        cancelled: result.cancelled
      };
    },
    sendNotification: async (method, parameters) => {
      const languageClient = client;
      if (languageClient === undefined || languageClient.state !== State.Running) {
        throw new Error('The VBA language client is not running.');
      }
      await languageClient.sendNotification(method, parameters);
    },
    onTransition: (transition) => hostClassStatus.observe(transition)
  });
  hostClassProjectionLifecycle = lifecycle;
  const routeTrustedHostClassEvent = (
    operation: () => Promise<void>
  ): void => {
    if (workspace.isTrusted) {
      void operation();
    }
  };
  let hostWorkspace: HostClassProjectionWorkspace;
  const hostClassWatchers = new HostClassProjectionWatcherRegistry({
    createWatcher: (basePath, pattern) => {
      const watcher = workspace.createFileSystemWatcher(
        new RelativePattern(Uri.file(basePath), pattern)
      );
      return {
        onDidCreate: (listener) => watcher.onDidCreate(
          (uri) => listener(uri.fsPath)),
        onDidChange: (listener) => watcher.onDidChange(
          (uri) => listener(uri.fsPath)),
        onDidDelete: (listener) => watcher.onDidDelete(
          (uri) => listener(uri.fsPath)),
        dispose: () => watcher.dispose()
      };
    },
    sourceFileChanged: (filePath) => routeTrustedHostClassEvent(
      () => hostWorkspace.sourceFileChanged(filePath)),
    templateFileChanged: (filePath) => routeTrustedHostClassEvent(
      () => hostWorkspace.templateFileChanged(filePath))
  });
  context.subscriptions.push(hostClassWatchers);
  hostWorkspace = new HostClassProjectionWorkspace({
    lifecycle,
    findProjectManifests,
    readManifestText: readHostClassManifestText,
    collectHostClassSources: collectHostClassProjectionSources,
    onActiveDocumentsChanged: (documents) => hostClassWatchers.synchronize(documents),
    reportError: (error) => outputChannel?.appendLine(
      `VBA Tools host-class workspace update failed: ${
        error instanceof Error ? error.message : String(error)
      }`
    )
  });
  hostClassProjectionWorkspace = hostWorkspace;
  context.subscriptions.push(
    projectManifestWatcher.onDidCreate((uri) =>
      routeTrustedHostClassEvent(() => hostWorkspace.manifestChanged(uri.fsPath))),
    projectManifestWatcher.onDidChange((uri) =>
      routeTrustedHostClassEvent(() => hostWorkspace.manifestChanged(uri.fsPath))),
    projectManifestWatcher.onDidDelete((uri) =>
      routeTrustedHostClassEvent(() => hostWorkspace.manifestRemoved(uri.fsPath))),
    workspace.onDidOpenTextDocument((document) => {
      routeHostClassTextDocumentChange(document.uri.scheme, document.uri.fsPath, hostWorkspace);
    }),
    workspace.onDidChangeTextDocument((event) => {
      routeHostClassTextDocumentChange(
        event.document.uri.scheme,
        event.document.uri.fsPath,
        hostWorkspace
      );
    }),
    workspace.onDidCloseTextDocument((document) => {
      routeHostClassTextDocumentChange(document.uri.scheme, document.uri.fsPath, hostWorkspace);
    }),
    workspace.onDidChangeWorkspaceFolders(() => {
      routeTrustedHostClassEvent(() => hostWorkspace.reconcileWorkspaceFolders(
        workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? []
      ));
    }),
    workspace.onDidGrantWorkspaceTrust(() => {
      if (client?.state === State.Running) {
        void hostWorkspace.activate();
      }
    })
  );
  let projectManifestLanguageServerSync: ProjectManifestLanguageServerSync | undefined;
  try {
    const serverOptions = createVbaLanguageServerOptions({
      extensionRoot: context.extensionPath,
      vbaDevExecutablePath: initialVbaDevResolution?.executablePath,
      referenceCatalogCacheRoot: createVbaLanguageServerReferenceCatalogCacheRoot(
        context.globalStorageUri.fsPath
      )
    });

    const clientOptions = createVbaLanguageClientOptions(
      sourceFileWatcher,
      projectManifestWatcher,
      createVbaDocumentFormattingMiddleware({
        getLanguageClient: () => {
          const languageClient = client;
          return languageClient === undefined
            ? undefined
            : {
                asTextDocumentIdentifier: (document) => (
                  languageClient.code2ProtocolConverter.asTextDocumentIdentifier(document)
                ),
                asFormattingOptions: (options, fileOptions) => (
                  languageClient.code2ProtocolConverter.asFormattingOptions(options, fileOptions)
                ),
                sendDocumentFormattingRequest: (parameters, token) => (
                  languageClient.sendRequest(DocumentFormattingRequest.type, parameters, token)
                ),
                asTextEdits: (edits, token) => (
                  languageClient.protocol2CodeConverter.asTextEdits(edits, token)
                ),
                handleFailedDocumentFormattingRequest: (error, token) => (
                  languageClient.handleFailedRequest(
                    DocumentFormattingRequest.type,
                    token,
                    error,
                    null
                  )
                )
              };
        },
        getTextEditors: () => window.visibleTextEditors,
        getFileFormattingOptions: (document) => {
          const filesConfiguration = workspace.getConfiguration('files', document.uri);
          return {
            trimTrailingWhitespace: filesConfiguration.get<boolean>('trimTrailingWhitespace'),
            trimFinalNewlines: filesConfiguration.get<boolean>('trimFinalNewlines'),
            insertFinalNewline: filesConfiguration.get<boolean>('insertFinalNewline')
          };
        }
      })
    );

    client = new LanguageClient(
      'vbaLanguageServer',
      'VBA Language Server',
      serverOptions,
      clientOptions
    );

    context.subscriptions.push(client);
    const languageClient = client;
    context.subscriptions.push(useBlockSkeletonInsertionPlanProvider(
      createLanguageClientBlockSkeletonInsertionPlanProvider(
        {
          sendRequest: (method, parameters, token) => languageClient.sendRequest<
            BlockSkeletonInsertionPlan | null
          >(method, parameters, token)
        },
        () => new CancellationTokenSource()
      )
    ));
    projectManifestLanguageServerSync = registerProjectManifestLanguageServerSync({
      getOpenDocuments: () => workspace.textDocuments,
      onDidOpenTextDocument: (listener) => workspace.onDidOpenTextDocument(listener),
      onDidChangeTextDocument: (listener) => workspace.onDidChangeTextDocument(listener),
      onDidCloseTextDocument: (listener) => workspace.onDidCloseTextDocument(listener),
      isLanguageClientRunning: () => languageClient.state === State.Running,
      onDidChangeLanguageClientRunning: (listener) => languageClient.onDidChangeState(
        (event) => listener(event.newState === State.Running)
      ),
      sendNotification: (method, parameters) => languageClient.sendNotification(method, parameters),
      subscriptions: context.subscriptions,
      reportError: (error) => outputChannel?.appendLine(
        `VBA Tools could not synchronize vba-project.json: ${error instanceof Error ? error.message : String(error)}`
      ),
      onDidSynchronizeLanguageClient: () => lifecycle.replayDesiredSnapshots()
    });
  } catch (error) {
    void window.showWarningMessage(error instanceof Error ? error.message : String(error));
  }

  const toolDiagnosticCollection = languages.createDiagnosticCollection('vba-dev');
  context.subscriptions.push(toolDiagnosticCollection);
  toolDiagnosticReporter = new VbaDevDiagnosticReporter(
    createVscodeDiagnosticCollectionAdapter(toolDiagnosticCollection)
  );
  const testController = tests.createTestController(
    'vbaTools.workbookBackedProjects',
    'VBA Workbook Tests'
  );
  context.subscriptions.push(testController);
  const captureTestSourceSnapshot = createCallerOwnedSourceSnapshotCapture({
    getActiveWindowsCodePage: () => vbaDevResolver.readActiveWindowsCodePage(),
    getOpenTextDocuments: () => workspace.textDocuments.map((document) => ({
      uriScheme: document.uri.scheme,
      uriPath: document.uri.scheme === 'file' ? document.uri.fsPath : undefined,
      fileName: document.fileName,
      isDirty: document.isDirty,
      encoding: document.encoding,
      getText: () => document.getText()
    })),
    findSourceFiles: async (sourceSetPath) => (
      await workspace.findFiles(
        new RelativePattern(sourceSetPath, '**/*.{bas,cls,frm,frx}'),
        null
      )
    ).map((uri) => uri.fsPath),
    readFile: async (filePath) => workspace.fs.readFile(Uri.file(filePath)),
    encodeText: async (text, encoding) => workspace.encode(text, { encoding }),
    decodeText: async (bytes, encoding) => workspace.decode(bytes, { encoding }),
    createTemporaryDirectory: async () => fs.mkdtemp(
      path.join(tmpdir(), 'vba-tools-test-source-')),
    createDirectory: async (directoryPath) => {
      await fs.mkdir(directoryPath, { recursive: true });
    },
    writeFile: async (filePath, bytes) => {
      await fs.writeFile(filePath, bytes);
    },
    removeDirectory: async (directoryPath) => {
      await fs.rm(directoryPath, { recursive: true, force: true });
    },
    wait: async (milliseconds) => new Promise((resolve) => {
      setTimeout(resolve, milliseconds);
    })
  });
  const workbookBackedTestExplorer = createWorkbookBackedTestExplorer({
    controller: createVscodeTestControllerAdapter(testController),
    extensionRoot: context.extensionPath,
    vbaDevResolver,
    workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
    findProjectManifests,
    readTextFile,
    openTextDocuments: () => workspace.textDocuments
      .filter((document) => document.uri.scheme === 'file')
      .map((document) => ({
        uriPath: document.uri.fsPath,
        isDirty: document.isDirty
      })),
    captureSourceSnapshot: captureTestSourceSnapshot,
    requireTrustedWorkspace: () => (
      workspaceTrustGate.requireTrusted('managed-tooling')
    ),
    outputChannel,
    showErrorMessage: (message: string) => window.showErrorMessage(message)
  });
  registerWorkbookBackedTestExplorerSourceInvalidation({
    sourceWatcher: {
      onDidCreate: (listener) => sourceFileWatcher.onDidCreate(
        (uri) => listener(uri.fsPath)),
      onDidChange: (listener) => sourceFileWatcher.onDidChange(
        (uri) => listener(uri.fsPath)),
      onDidDelete: (listener) => sourceFileWatcher.onDidDelete(
        (uri) => listener(uri.fsPath))
    },
    onDidChangeTextDocument: (listener) => workspace.onDidChangeTextDocument((event) => {
      if (event.document.uri.scheme === 'file') {
        listener({
          uriPath: event.document.uri.fsPath
        });
      }
    }),
    subscriptions: context.subscriptions,
    explorer: workbookBackedTestExplorer
  });
  registerWorkbookBackedTestExplorerRefresh({
    watcher: {
      onDidCreate: (listener) => projectManifestWatcher.onDidCreate(
        (uri) => listener(uri.fsPath)),
      onDidChange: (listener) => projectManifestWatcher.onDidChange(
        (uri) => listener(uri.fsPath)),
      onDidDelete: (listener) => projectManifestWatcher.onDidDelete(
        (uri) => listener(uri.fsPath))
    },
    subscriptions: context.subscriptions,
    explorer: workbookBackedTestExplorer,
    showErrorMessage: (message) => window.showErrorMessage(message)
  });
  const managedToolingOperations = {
    'vbaTools.doctor': async () => {
      await runDoctorWithProgress(context, vbaDevResolver);
    },
    'vbaTools.openVbaDevTerminal': async () => {
      await openVbaDevTerminalCommand(context, vbaDevResolver);
    },
    'vbaTools.newExcel': () => undefined,
    'vbaTools.export': async (request?: unknown) => {
      await runExportCommandWithConsent(
        context,
        vbaDevResolver,
        request as ExportCommandRequest | undefined
      );
    },
    'vbaTools.build': async () => {
      await runWorkbookBackedProjectCommandWithProgress(
        context,
        vbaDevResolver,
        'build',
        'VBA Tools: Build'
      );
    },
    'vbaTools.test': async () => {
      await runWorkbookBackedProjectCommandWithProgress(
        context,
        vbaDevResolver,
        'test',
        'VBA Tools: Test'
      );
    },
    'vbaTools.publish': async () => {
      await runWorkbookBackedProjectCommandWithProgress(
        context,
        vbaDevResolver,
        'publish',
        'VBA Tools: Publish'
      );
    },
    'vbaTools.hostClasses.refresh': async () => {
      await runHostClassProjectionRefreshCommand({
        getActiveDocuments: () => hostWorkspace.getActiveDocuments(),
        chooseDocument: async (documents) => {
          const selected = await window.showQuickPick(
            documents.map((document) => ({
              label: document.context.document,
              description: document.context.project,
              detail: document.context.sourceTemplate,
              document
            })),
            { title: 'Select a VBA document to refresh Host Events' }
          );
          return selected?.document;
        },
        refreshDocument: (selectedContext) =>
          lifecycle.refreshDocument(selectedContext),
        runWithCancellableProgress: async (title, task) => {
          await window.withProgress({
            location: ProgressLocation.Notification,
            title,
            cancellable: true
          }, async (_progress, token) => task(token));
        },
        showWarningMessage: async (message, action) =>
          window.showWarningMessage(message, action),
        showErrorMessage: async (message, action) =>
          window.showErrorMessage(message, action),
        showOutput: () => outputChannel?.show()
      });
    },
    'vbaTools.commonModules.add': async () => {
      await runCommonModulesCommandWithProgress(
        context,
        vbaDevResolver,
        'add',
        'VBA Tools: Add Common Module'
      );
    },
    'vbaTools.commonModules.list': async () => {
      await runCommonModulesCommandWithProgress(
        context,
        vbaDevResolver,
        'list',
        'VBA Tools: List Common Modules'
      );
    },
    'vbaTools.commonModules.update': async () => {
      await runCommonModulesCommandWithProgress(
        context,
        vbaDevResolver,
        'update',
        'VBA Tools: Update Common Modules'
      );
    },
    'vbaTools.references.list': async () => {
      await runReferenceCommandWithProgress(
        context,
        vbaDevResolver,
        'list',
        'VBA Tools: List References'
      );
    },
    'vbaTools.references.add': async () => {
      await runReferenceCommandWithProgress(
        context,
        vbaDevResolver,
        'add',
        'VBA Tools: Add Reference'
      );
    },
    'vbaTools.references.remove': async () => {
      await runReferenceCommandWithProgress(
        context,
        vbaDevResolver,
        'remove',
        'VBA Tools: Remove Reference'
      );
    }
  } satisfies ManagedToolingCommandOperations;
  const managedToolingCommands = createManagedToolingCommandHandlers(
    workspaceTrustGate,
    managedToolingOperations
  );
  for (const command of managedToolingCommands) {
    context.subscriptions.push(commands.registerCommand(
      command.commandId,
      command.handler
    ));
  }
  context.subscriptions.push(commands.registerCommand(
    'vbaTools.hostClasses.showOutput',
    () => outputChannel?.show()
  ));
  context.subscriptions.push(commands.registerCommand(
    'vbaTools.blockSkeletonInsertion.afterNativeEnter',
    () => {
      void runBlockSkeletonInsertionAfterNativeEnter(
        nativeLineBreakRecorder
      ).then((result) => {
        if (result === undefined) {
          return;
        }

        const trace = formatBlockSkeletonInsertionFallbackTrace(
          result,
          workspace.getConfiguration('vbaLanguageServer').get<string>(
            'trace.server',
            'off'
          )
        );
        if (trace !== undefined) {
          client?.traceOutputChannel.appendLine(trace);
        }
      }).catch(() => undefined);
    }
  ));
  await client?.start();
  await projectManifestLanguageServerSync?.flush();
  if (workspace.isTrusted && client?.state === State.Running) {
    await hostWorkspace.activate();
  }
  await workbookBackedTestExplorer.refresh();
  await promptForActiveWorkbookBackedProject(
    context,
    managedToolingCommands.find(
      (command) => command.commandId === 'vbaTools.doctor'
    )?.handler
  );
}

export async function deactivate(): Promise<void> {
  hostClassProjectionWorkspace?.shutdown();
  hostClassProjectionLifecycle?.shutdown();
  await hostClassProjectionWorkspace?.flush();
  await hostClassProjectionLifecycle?.flush();
  hostClassProjectionWorkspace = undefined;
  hostClassProjectionLifecycle = undefined;
  await activeVscodeDebugIntegration?.shutdown();
  activeVscodeDebugIntegration = undefined;
  await client?.stop();
  client = undefined;
  outputChannel = undefined;
  toolDiagnosticReporter = undefined;
}

async function promptForActiveWorkbookBackedProject(
  context: ExtensionContext,
  runDoctor: ((request?: unknown) => PromiseLike<unknown> | unknown) | undefined
): Promise<void> {
  if (!workspace.isTrusted || runDoctor === undefined) {
    return;
  }

  const activeFilePath = getActiveFilePath();
  if (!activeFilePath) {
    return;
  }

  const manifestPath = await findNearestProjectManifest(activeFilePath, fileExists);
  if (!manifestPath) {
    return;
  }

  await promptForFirstRunDoctor({
    workspaceState: context.workspaceState,
    showInformationMessage: (message, ...items) => window.showInformationMessage(message, ...items),
    runDoctor: async () => {
      await runDoctor();
    }
  });
}

async function runDoctorWithProgress(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title: 'VBA Tools: Doctor',
      cancellable: true
    },
    async (progress, token) => {
      await runDoctorCommand({
        extensionRoot: context.extensionPath,
        vbaDevResolver,
        configuredDebugAdapterPath: getConfiguredDebugAdapterPath(),
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message) => window.showErrorMessage(message),
        reportCancellationProgress: (message) => progress.report({ message }),
        cancellationToken: token
      });
    }
  );
}

async function openVbaDevTerminalCommand(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver
): Promise<void> {
  await openVbaDevTerminal({
    extensionRoot: context.extensionPath,
    vbaDevResolver,
    activeFilePath: getActiveFilePath(),
    workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
    chooseWorkspaceRoot,
    createTerminal: (options) => window.createTerminal(options),
    showErrorMessage: (message) => window.showErrorMessage(message)
  });
}

async function runWorkbookBackedProjectCommandWithProgress(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver,
  toolCommandName: WorkbookBackedProjectToolCommand,
  title: string
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title,
      cancellable: true
    },
    async (progress, token) => {
      await runWorkbookBackedProjectCommand({
        toolCommandName,
        title,
        extensionRoot: context.extensionPath,
        vbaDevResolver,
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showWarningMessage: (message, ...items) =>
          window.showWarningMessage(message, ...items),
        showErrorMessage: (message) => window.showErrorMessage(message),
        reportCancellationProgress: (message) => progress.report({ message }),
        cancellationToken: token
      });
    }
  );
}

async function runExportCommandWithConsent(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver,
  request?: ExportCommandRequest
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;

  await runExportCommand({
    extensionRoot: context.extensionPath,
    vbaDevResolver,
    activeFilePath: getActiveFilePath(),
    workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
    fileExists,
    findProjectManifests,
    chooseProject,
    readTextFile,
    showWarningMessage: (message, options, ...items) =>
      window.showWarningMessage(message, options, ...items),
    runWithProgress: (task) => window.withProgress(
      {
        location: ProgressLocation.Notification,
        title: 'VBA Tools: Export',
        cancellable: true
      },
      async (progress, token) => task(
        token,
        (message) => progress.report({ message })
      )
    ),
    outputChannel: channel,
    diagnosticReporter: toolDiagnosticReporter,
    showErrorMessage: (message) => window.showErrorMessage(message)
  }, request);
}

async function runCommonModulesCommandWithProgress(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver,
  toolCommandName: CommonModulesToolCommand,
  title: string
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;
  const moduleNames = toolCommandName === 'add'
    ? await promptForCommonModuleNames()
    : undefined;
  if (toolCommandName === 'add' && moduleNames === undefined) {
    return;
  }

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title,
      cancellable: true
    },
    async (progress, token) => {
      const options = {
        extensionRoot: context.extensionPath,
        vbaDevResolver,
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message: string) => window.showErrorMessage(message),
        reportCancellationProgress: (message: string) => progress.report({ message }),
        cancellationToken: token
      };

      if (toolCommandName === 'add') {
        await runCommonModulesAddCommand(options, moduleNames ?? []);
      } else if (toolCommandName === 'update') {
        await runCommonModulesUpdateCommand(options);
      } else {
        await runCommonModulesListCommand(options);
      }
    }
  );
}

async function promptForCommonModuleNames(): Promise<readonly string[] | undefined> {
  const value = await window.showInputBox({
    title: 'Add Common Module',
    prompt: 'Enter one or more CommonModuleName values separated by spaces.'
  });
  if (value === undefined) {
    return undefined;
  }

  const moduleNames = value
    .split(/\s+/)
    .map((moduleName) => moduleName.trim())
    .filter((moduleName) => moduleName.length > 0);

  return moduleNames.length > 0 ? moduleNames : undefined;
}

async function runReferenceCommandWithProgress(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver,
  toolCommandName: ReferenceToolCommand,
  title: string
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;
  const referenceName = toolCommandName === 'list'
    ? undefined
    : await promptForReferenceName(title);
  if (toolCommandName !== 'list' && referenceName === undefined) {
    return;
  }

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title,
      cancellable: true
    },
    async (progress, token) => {
      const options = {
        extensionRoot: context.extensionPath,
        vbaDevResolver,
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message: string) => window.showErrorMessage(message),
        reportCancellationProgress: (message: string) => progress.report({ message }),
        cancellationToken: token
      };

      if (toolCommandName === 'add') {
        await runReferenceAddCommand(options, referenceName ?? '');
      } else if (toolCommandName === 'remove') {
        await runReferenceRemoveCommand(options, referenceName ?? '');
      } else {
        await runReferenceListCommand(options);
      }
    }
  );
}

async function promptForReferenceName(title: string): Promise<string | undefined> {
  const value = await window.showInputBox({
    title,
    prompt: 'Enter the exact Reference.Description name.'
  });
  if (value === undefined) {
    return undefined;
  }

  return value.trim();
}

function appendVbaDevResolutionLog(
  channel: OutputChannel | undefined,
  log: VbaDevResolutionLog
): void {
  for (const line of formatVbaDevResolutionLog(log)) {
    channel?.appendLine(line);
  }
}

function reportVbaDevResolutionNotice(
  channel: OutputChannel | undefined,
  notice: VbaDevResolutionNotice
): void {
  const response = notice.severity === 'warning'
    ? window.showWarningMessage(notice.message, ...notice.actions)
    : window.showErrorMessage(notice.message, ...notice.actions);
  void response.then((action) => {
    if (action === VbaDevResolutionNoticeAction.OpenSettings) {
      void commands.executeCommand('workbench.action.openSettings', 'vbaTools.devtool.path');
    } else if (action === VbaDevResolutionNoticeAction.ShowOutput) {
      channel?.show();
    }
  });
}

function reportUnreportedVbaDevResolutionFailure(
  channel: OutputChannel | undefined,
  error: unknown
): void {
  channel?.appendLine(
    `vba-dev companion resolution failed before candidate validation: ${error instanceof Error ? error.message : String(error)}`
  );
  reportVbaDevResolutionNotice(channel, {
    severity: 'error',
    message: noCompatibleVbaDevMessage,
    actions: [
      VbaDevResolutionNoticeAction.OpenSettings,
      VbaDevResolutionNoticeAction.ShowOutput
    ]
  });
}

function getConfiguredDevToolPath(): string | undefined {
  const configured = workspace.getConfiguration('vbaTools').get<string>('devtool.path');
  return configured && configured.trim().length > 0 ? configured : undefined;
}

function getConfiguredDebugAdapterPath(): string | undefined {
  const configured = workspace.getConfiguration('vbaTools').get<string>('debugAdapter.path');
  return configured && configured.trim().length > 0 ? configured : undefined;
}

function getActiveFilePath(): string | undefined {
  const editor = window.activeTextEditor;
  return editor?.document.uri.scheme === 'file' ? editor.document.uri.fsPath : undefined;
}

function routeHostClassTextDocumentChange(
  uriScheme: string,
  filePath: string,
  hostWorkspace: HostClassProjectionWorkspace
): void {
  if (!workspace.isTrusted || uriScheme !== 'file') {
    return;
  }

  const route = classifyHostClassTextDocumentChange(
    uriScheme,
    filePath,
    workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
    hostWorkspace.getActiveDocuments()
  );
  if (route === 'manifest') {
    void hostWorkspace.manifestChanged(filePath);
    return;
  }

  if (route === 'source') {
    void hostWorkspace.sourceFileChanged(filePath);
  }
}

async function readHostClassManifestText(
  manifestPath: string
): Promise<string | undefined> {
  const openDocument = workspace.textDocuments.find((document) =>
    document.uri.scheme === 'file' &&
    sameCanonicalFilePath(document.uri.fsPath, manifestPath)
  );
  if (openDocument !== undefined) {
    return openDocument.getText();
  }

  try {
    return await readTextFile(manifestPath);
  } catch {
    return undefined;
  }
}

async function collectHostClassProjectionSources(
  document: HostClassProjectionWorkspaceDocument
): Promise<readonly {
  readonly sourceUri: string;
  readonly kind: 'form';
  readonly text: string;
}[]> {
  const uris = await workspace.findFiles(
    new RelativePattern(document.sourceSetPath, '**/*.frm'),
    null
  );
  return collectHostClassProjectionFormSources(
    document.sourceSetPath,
    uris.map((uri) => ({
      filePath: uri.fsPath,
      sourceUri: uri.toString()
    })),
    workspace.textDocuments.map((openDocument) => ({
      scheme: openDocument.uri.scheme,
      filePath: openDocument.uri.fsPath,
      sourceUri: openDocument.uri.toString(),
      text: openDocument.getText()
    })),
    async (source) => decodeVbaSourceFileText(
      await workspace.fs.readFile(Uri.file(source.filePath))
    )
  );
}

function sameCanonicalFilePath(left: string, right: string): boolean {
  return path.normalize(path.resolve(left)).toLowerCase() ===
    path.normalize(path.resolve(right)).toLowerCase();
}

async function fileExists(filePath: string): Promise<boolean> {
  try {
    const stat = await fs.stat(filePath);
    return stat.isFile();
  } catch {
    return false;
  }
}

async function readTextFile(filePath: string): Promise<string> {
  const buffer = await fs.readFile(filePath);
  if (buffer.length >= 2 && buffer[0] === 0xff && buffer[1] === 0xfe) {
    return buffer.subarray(2).toString('utf16le');
  }

  if (buffer.length >= 3 && buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) {
    return buffer.subarray(3).toString('utf8');
  }

  return buffer.toString('utf8');
}

async function findProjectManifests(): Promise<readonly string[]> {
  const uris = await workspace.findFiles('**/vba-project.json', '**/{node_modules,.git}/**');
  return uris.map((uri) => uri.fsPath);
}

async function chooseProject(
  candidates: readonly WorkbookBackedProjectCandidate[]
): Promise<WorkbookBackedProjectCandidate | undefined> {
  const selected = await window.showQuickPick(
    candidates.map((candidate) => ({
      label: path.basename(candidate.projectRoot),
      description: candidate.projectRoot,
      candidate
    })),
    {
      title: 'Select WorkbookBackedProject'
    }
  );

  return selected?.candidate;
}

async function chooseWorkspaceRoot(workspaceRoots: readonly string[]): Promise<string | undefined> {
  const selected = await window.showQuickPick(
    workspaceRoots.map((workspaceRoot) => ({
      label: path.basename(workspaceRoot) || workspaceRoot,
      description: workspaceRoot,
      workspaceRoot
    })),
    {
      title: 'Select vba-dev Terminal Folder'
    }
  );

  return selected?.workspaceRoot;
}
