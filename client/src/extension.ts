import * as path from 'node:path';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import { Buffer } from 'node:buffer';

import {
  CancellationTokenSource,
  DebugAdapterExecutable,
  DebugConfiguration,
  DebugConfigurationProviderTriggerKind,
  ExtensionContext,
  ExtensionMode,
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
  RenameRequest,
  State
} from 'vscode-languageclient/node';
import {
  promptForFirstRunDoctor,
  runDoctorCommand
} from './doctorCommand';
import {
  CommonModulesToolCommand,
  parseCommonModuleNamesInput,
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
  runReferenceListCommand,
  runReferenceQuickPickWorkflow
} from './referenceCommand';
import {
  ReferenceQuickPickItem,
  showReferenceQuickPick
} from './referenceQuickPick';
import {
  createWorkbookBackedTestExplorer
} from './testExplorer';
import {
  createCallerOwnedSourceSnapshotCapture
} from './snapshotSourceInventory';
import {
  createSnapshotSourceInventoryVscodeAdapter
} from './snapshotSourceInventoryVscodeAdapter';
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
  createVbaLanguageServerReferenceCatalogCacheRoot,
  createVbaSignatureHelpClientCapabilitiesFeature
} from './languageServer';
import { CaseOnlyVbaFileRenameAdapter } from './caseOnlyVbaFileRename';
import { createVbaRenameMiddleware } from './rename';
import {
  ProjectManifestLanguageServerSync,
  registerProjectManifestLanguageServerSync
} from './projectManifestLanguageServerSync';
import {
  IntrinsicHostEventCatalogLifecycle
} from './intrinsicHostEventCatalogLifecycle';
import {
  IntrinsicHostEventCatalogExtensionHostProbe,
  VbaToolsExtensionHostTestApi
} from './intrinsicHostEventCatalogExtensionHostProbe';
import {
  IntrinsicHostEventCatalogStatusObserver
} from './intrinsicHostEventCatalogStatus';
import {
  runIntrinsicHostEventCatalogRefreshCommand
} from './intrinsicHostEventCatalogRefreshCommand';
import {
  createVscodeDiagnosticCollectionAdapter,
  createVscodeTestControllerAdapter
} from './vscodeAdapters';
import {
  openVbaDevTerminal
} from './vbaDevTerminalCommand';
import {
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
import { createLazyOutputChannel } from './lazyOutputChannel';
import {
  runResolvedVbaDevCommandInvocation,
  runVbaDevCommandInvocation,
  VbaDevCommandRuntimeOptions
} from './devtoolRuntime';
import { NewExcelProjectCommand } from './newExcelProjectCommand';
import {
  CommandPaletteInvocationSnapshot,
  CommandPaletteProjectTarget,
  CommandPaletteTargetScope,
  resolveCommandPaletteProjectTargetFromManifestText,
  resolveCommandPaletteTarget
} from './commandPaletteTarget';
import {
  captureCommandPaletteInvocationSnapshot
} from './commandPaletteInvocationSnapshot';
import {
  CommandPaletteDocumentQuickPickItem,
  chooseCommandPaletteDocumentWithQuickPick,
  resolveCommandPalettePathIdentity
} from './commandPaletteTargetAdapter';
import {
  ManagedToolingCommandOperations,
  ManagedToolingWorkspaceTrustGate,
  createManagedToolingCommandHandlers
} from './workspaceTrust';
import {
  CompanionExecutableLanguageServerLifecycle
} from './companionLanguageServerLifecycle';
import { ProjectManifestMutationCoordinator } from './projectManifestMutation';
import {
  createProjectManifestMutationVscodeAdapter
} from './projectManifestMutationVscodeAdapter';

let client: LanguageClient | undefined;
let outputChannel: OutputChannel | undefined;
let toolDiagnosticReporter: VbaDevDiagnosticReporter | undefined;
let activeVscodeDebugIntegration: VscodeDebugIntegration | undefined;
let activeVbaDevResolver: VbaDevSessionResolver | undefined;
let intrinsicHostEventCatalogLifecycle: IntrinsicHostEventCatalogLifecycle | undefined;
let companionExecutableLanguageServerLifecycle:
  CompanionExecutableLanguageServerLifecycle | undefined;

type CommandPaletteTargetResolver = NonNullable<
  VbaDevCommandRuntimeOptions['resolveCommandPaletteTarget']
>;

export async function activate(
  context: ExtensionContext
): Promise<VbaToolsExtensionHostTestApi | undefined> {
  const hostEventCatalogTestProbe =
    IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment(
      process.env,
      workspace.isTrusted,
      context.extensionMode === ExtensionMode.Test
    );
  const isWorkspaceTrusted = (): boolean => (
    hostEventCatalogTestProbe?.effectiveWorkspaceTrusted ?? workspace.isTrusted
  );
  const extensionOutputChannel = createLazyOutputChannel(
    'VBA Tools',
    () => window.createOutputChannel('VBA Tools')
  );
  outputChannel = extensionOutputChannel;
  context.subscriptions.push(extensionOutputChannel);
  const projectManifestMutationAdapter = createProjectManifestMutationVscodeAdapter({
    resolvePathIdentity: resolveCommandPalettePathIdentity,
    readFileBytes: (filePath) => fs.readFile(filePath),
    decodeManifestBytes: decodeProjectManifestBytes,
    loadProjectTarget: async (manifestPath, bytes) =>
      resolveCommandPaletteProjectTargetFromManifestText(
        manifestPath,
        decodeProjectManifestBytes(bytes),
        resolveCommandPalettePathIdentity
      ),
    getOpenTextDocuments: () => workspace.textDocuments,
    onDidOpenTextDocument: (listener) => workspace.onDidOpenTextDocument(listener),
    onDidChangeTextDocument: (listener) => workspace.onDidChangeTextDocument(listener),
    onDidSaveTextDocument: (listener) => workspace.onDidSaveTextDocument(listener),
    onDidCloseTextDocument: (listener) => workspace.onDidCloseTextDocument(listener),
    getActiveTextEditor: () => window.activeTextEditor,
    showTextDocument: async (document) => {
      const openDocument = workspace.textDocuments.find((candidate) =>
        candidate.uri.toString() === document.uri.toString());
      if (openDocument === undefined) {
        throw new Error('The selected project manifest buffer is no longer open.');
      }
      return window.showTextDocument(openDocument, {
        preview: false,
        preserveFocus: false
      });
    },
    executeRevertCommand: () => commands.executeCommand('workbench.action.files.revert'),
    showWarningMessage: (message, options, ...items) =>
      window.showWarningMessage(message, options, ...items),
    createSnapshotUri: (scheme, snapshotId, role) => Uri.from({
      scheme,
      path: `/${snapshotId}/${role}/vba-project.json`
    }),
    registerSnapshotContentProvider: (scheme, provider) =>
      workspace.registerTextDocumentContentProvider(scheme, provider),
    showDiff: (editorSnapshot, diskSnapshot, title) => commands.executeCommand(
      'vscode.diff',
      editorSnapshot,
      diskSnapshot,
      title
    ),
    outputChannel: extensionOutputChannel
  });
  context.subscriptions.push(projectManifestMutationAdapter);
  const projectManifestMutationCoordinator = new ProjectManifestMutationCoordinator(
    projectManifestMutationAdapter
  );
  const caseOnlyVbaFileRenameAdapter = new CaseOnlyVbaFileRenameAdapter(
    message => extensionOutputChannel.appendLine(message)
  );
  context.subscriptions.push(caseOnlyVbaFileRenameAdapter);
  const vbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: context.extensionPath,
    configuredPathProvider: getConfiguredDevToolPath,
    reportLog: (log) => appendVbaDevResolutionLog(outputChannel, log),
    reportNotice: (notice) => reportVbaDevResolutionNotice(outputChannel, notice),
    runProcess: hostEventCatalogTestProbe?.controlsCompanionResolution !== true
      ? undefined
      : (file, args, signal) => hostEventCatalogTestProbe.runCompanionProcess(
          file,
          args,
          signal
        )
  });
  activeVbaDevResolver = vbaDevResolver;
  const newExcelProjectCommand = new NewExcelProjectCommand({
    resolveCompanionExecutable: () => vbaDevResolver.resolve(),
    runCommand: async (resolution, args) => {
      const nameOptionIndex = args.indexOf('--name');
      const title = args[0] === 'doctor'
        ? 'VBA Tools: Checking Excel VBA project prerequisites'
        : `VBA Tools: Creating Excel VBA project "${args[nameOptionIndex + 1]}"`;
      return window.withProgress(
        {
          location: ProgressLocation.Notification,
          title,
          cancellable: true
        },
        (progress, token) => runResolvedVbaDevCommandInvocation({
          extensionRoot: context.extensionPath,
          outputChannel: extensionOutputChannel,
          revealOutput: false,
          cancellationToken: token,
          reportCancellationProgress: (message) => progress.report({ message })
        }, resolution, args)
      );
    },
    showProjectNameInput: async (options) => window.showInputBox({
      ...options,
      valueSelection: options.valueSelection === undefined
        ? undefined
        : [...options.valueSelection]
    }),
    showParentFolder: async (options) => (await window.showOpenDialog({
      title: options.title,
      openLabel: options.openLabel,
      defaultUri: options.defaultUri === undefined
        ? undefined
        : Uri.file(options.defaultUri.fsPath),
      canSelectFiles: false,
      canSelectFolders: true,
      canSelectMany: false
    }))?.[0],
    getWorkspaceFolders: () => workspace.workspaceFolders?.map(
      (folder) => folder.uri
    ) ?? [],
    getActiveResource: () => window.activeTextEditor?.document.uri,
    showInformationMessage: async (message, ...actions) => (
      window.showInformationMessage(message, ...actions)
    ),
    showWarningMessage: async (message, ...actions) => (
      window.showWarningMessage(message, ...actions)
    ),
    showErrorMessage: async (message, options, ...actions) => (
      options === undefined
        ? window.showErrorMessage(message, ...actions)
        : window.showErrorMessage(message, options, ...actions)
    ),
    showOutput: () => extensionOutputChannel.show(true),
    appendOutput: (text) => extensionOutputChannel.appendLine(text),
    openSetupInstructions: async () => {
      const setupInstructions = Uri.file(
        path.join(context.extensionPath, 'README.md')
      ).with({ fragment: '2---prepare-excel' });
      await commands.executeCommand('markdown.showPreview', setupInstructions);
    },
    openSettings: async () => {
      await commands.executeCommand(
        'workbench.action.openSettings',
        'vbaTools.devtool.path'
      );
    },
    openManifest: async (manifestPath) => {
      const document = await workspace.openTextDocument(Uri.file(manifestPath));
      await window.showTextDocument(document);
    },
    openFolderInNewWindow: async (projectRoot) => {
      await commands.executeCommand(
        'vscode.openFolder',
        Uri.file(projectRoot),
        true
      );
    }
  });
  const workspaceTrustGate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: isWorkspaceTrusted,
    invalidateManagedToolingState: () => {
      vbaDevResolver.invalidate();
      newExcelProjectCommand.invalidatePreflight();
    },
    showWarningMessage: (message, ...actions) => (
      window.showWarningMessage(message, ...actions)
    ),
    executeCommand: (command) => commands.executeCommand(command)
  });
  const captureSnapshotSourceInventoryFromVscode = createSnapshotSourceInventoryVscodeAdapter({
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
  });
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
      captureSourceInventory: captureSnapshotSourceInventoryFromVscode
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
  const userFormEventsStatusItem = window.createStatusBarItem(
    'vbaTools.userFormEvents.status',
    StatusBarAlignment.Left,
    100
  );
  userFormEventsStatusItem.name = 'VBA UserForm Events';
  context.subscriptions.push(userFormEventsStatusItem);
  const userFormEventsStatus = new IntrinsicHostEventCatalogStatusObserver({
    updateStatus: (view) => {
      userFormEventsStatusItem.text = view.text;
      userFormEventsStatusItem.tooltip = view.tooltip;
      userFormEventsStatusItem.command = view.command;
      if (view.visible) {
        userFormEventsStatusItem.show();
      } else {
        userFormEventsStatusItem.hide();
      }
    },
    appendOutput: (line) => outputChannel?.appendLine(line)
  });
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async (invocation) => {
      if (!isWorkspaceTrusted()) {
        throw new Error(
          'UserForm Event catalog acquisition is unavailable in Restricted Mode.'
        );
      }
      if (hostEventCatalogTestProbe !== undefined) {
        await vbaDevResolver.resolve();
        return hostEventCatalogTestProbe.runHostEventList(invocation);
      }
      const result = await runVbaDevCommandInvocation({
        extensionRoot: context.extensionPath,
        vbaDevResolver,
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
      hostEventCatalogTestProbe?.observeNotification(method, parameters);
    },
    isNotificationTargetAvailable: () => client?.state === State.Running,
    onTransition: (transition) => {
      userFormEventsStatus.observe(transition);
      hostEventCatalogTestProbe?.observeTransition(transition);
    }
  });
  intrinsicHostEventCatalogLifecycle = lifecycle;
  let projectManifestLanguageServerSync: ProjectManifestLanguageServerSync | undefined;
  try {
    const serverOptions = createVbaLanguageServerOptions({
      extensionRoot: context.extensionPath,
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
      }),
      createVbaRenameMiddleware({
        getLanguageClient: () => {
          const languageClient = client;
          return languageClient === undefined
            ? undefined
            : {
                asTextDocumentIdentifier: document => (
                  languageClient.code2ProtocolConverter
                    .asTextDocumentIdentifier(document)
                ),
                asPosition: position => (
                  languageClient.code2ProtocolConverter.asPosition(position)
                ),
                sendRenameRequest: (parameters, token) => (
                  languageClient.sendRequest(
                    RenameRequest.type,
                    parameters,
                    token
                  )
                ),
                asWorkspaceEdit: (edit, token) => (
                  languageClient.protocol2CodeConverter.asWorkspaceEdit(edit, token)
                ),
                handleFailedRenameRequest: (error, token) => (
                  languageClient.handleFailedRequest(
                    RenameRequest.type,
                    token,
                    error,
                    null
                  )
                )
              };
        },
        captureCaseOnlyFileRenames: renames => (
          caseOnlyVbaFileRenameAdapter.capture(renames)
        )
      })
    );

    client = new LanguageClient(
      'vbaLanguageServer',
      'VBA Language Server',
      serverOptions,
      clientOptions
    );
    client.registerFeature(createVbaSignatureHelpClientCapabilitiesFeature());

    context.subscriptions.push(client);
    const languageClient = client;
    const companionLifecycle = new CompanionExecutableLanguageServerLifecycle({
      isTrusted: isWorkspaceTrusted,
      resolveCompanion: () => vbaDevResolver.resolve(),
      observeCompanionResolution: (listener) => vbaDevResolver.onDidResolve(listener),
      sendNotification: (method, parameters) => (
        languageClient.sendNotification(method, parameters)
      ),
      startUserFormEventCatalog: () => {
        void lifecycle.activate();
      },
      reportResolutionError: (error) => {
        if (!isReportedVbaDevResolutionFailure(error)) {
          reportUnreportedVbaDevResolutionFailure(outputChannel, error);
        }
      },
      reportPublicationError: (error) => outputChannel?.appendLine(
        'VBA Tools could not publish the validated vba-dev companion to '
        + 'the current language-server connection; language assistance '
        + `continues with the registry-only catalog: ${error instanceof Error ? error.message : String(error)}`
      )
    });
    companionExecutableLanguageServerLifecycle = companionLifecycle;
    const observeCompanionReadiness = (isRunning: boolean): void => {
      companionLifecycle.observeLanguageClientRunning(isRunning);
      if (isRunning) {
        companionLifecycle.activateTrustedServices();
      }
    };
    context.subscriptions.push(
      languageClient.onDidChangeState((event) => {
        observeCompanionReadiness(event.newState === State.Running);
      }),
      workspace.onDidGrantWorkspaceTrust(() => {
        observeCompanionReadiness(languageClient.state === State.Running);
      })
    );
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
      onDidSynchronizeLanguageClient: async () => {
        await lifecycle.replayCurrentSnapshot();
      }
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
  const captureTestSourceSnapshot = createCallerOwnedSourceSnapshotCapture(
    captureSnapshotSourceInventoryFromVscode,
    {
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
    get configuredDebugAdapterPath() { return getConfiguredDebugAdapterPath(); },
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
      await runDoctorWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator
      );
    },
    'vbaTools.openVbaDevTerminal': async () => {
      await openVbaDevTerminalCommand(context, vbaDevResolver);
    },
    'vbaTools.newExcel': async () => {
      await newExcelProjectCommand.run();
    },
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
    'vbaTools.userFormEvents.refresh': async () => {
      await runIntrinsicHostEventCatalogRefreshCommand({
        refreshCatalog: () => lifecycle.refresh(),
        runWithCancellableProgress: async (title, task) => {
          await window.withProgress({
            location: ProgressLocation.Notification,
            title,
            cancellable: true
          }, async (_progress, token) => task(token));
        },
        showErrorMessage: async (message, action) =>
          window.showErrorMessage(message, action),
        showOutput: () => outputChannel?.show()
      });
    },
    'vbaTools.commonModules.add': async () => {
      await runCommonModulesCommandWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator,
        'add',
        'VBA Tools: Add Common Module'
      );
    },
    'vbaTools.commonModules.list': async () => {
      await runCommonModulesCommandWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator,
        'list',
        'VBA Tools: List Common Modules'
      );
    },
    'vbaTools.commonModules.update': async () => {
      await runCommonModulesCommandWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator,
        'update',
        'VBA Tools: Update Common Modules'
      );
    },
    'vbaTools.references.list': async () => {
      await runReferenceCommandWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator,
        'list',
        'VBA Tools: List References'
      );
    },
    'vbaTools.references.add': async () => {
      await runReferenceCommandWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator,
        'add',
        'VBA Tools: Add Reference'
      );
    },
    'vbaTools.references.remove': async () => {
      await runReferenceCommandWithProgress(
        context,
        vbaDevResolver,
        projectManifestMutationCoordinator,
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
    'vbaTools.userFormEvents.showOutput',
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
  if (client?.state === State.Running) {
    companionExecutableLanguageServerLifecycle?.observeLanguageClientRunning(true);
    companionExecutableLanguageServerLifecycle?.activateTrustedServices();
  }
  await projectManifestLanguageServerSync?.flush();
  await workbookBackedTestExplorer.refresh();
  await promptForActiveWorkbookBackedProject(
    context,
    isWorkspaceTrusted(),
    managedToolingCommands.find(
      (command) => command.commandId === 'vbaTools.doctor'
      )?.handler
  );
  if (hostEventCatalogTestProbe === undefined) {
    return undefined;
  }
  return {
    companionExecutable: hostEventCatalogTestProbe.createCompanionApi(),
    intrinsicHostEventCatalog: hostEventCatalogTestProbe.createApi(async () => {
      const languageClient = client;
      if (languageClient === undefined) {
        throw new Error('The VBA language client is unavailable.');
      }
      await languageClient.restart();
      await projectManifestLanguageServerSync?.flush();
    })
  };
}

export async function deactivate(): Promise<void> {
  const companionLifecycle = companionExecutableLanguageServerLifecycle;
  companionLifecycle?.dispose();
  companionExecutableLanguageServerLifecycle = undefined;
  activeVbaDevResolver?.invalidate();
  activeVbaDevResolver = undefined;
  intrinsicHostEventCatalogLifecycle?.shutdown();
  await Promise.all([
    companionLifecycle?.flush(),
    intrinsicHostEventCatalogLifecycle?.flush()
  ]);
  intrinsicHostEventCatalogLifecycle = undefined;
  await activeVscodeDebugIntegration?.shutdown();
  activeVscodeDebugIntegration = undefined;
  await client?.stop();
  client = undefined;
  outputChannel = undefined;
  toolDiagnosticReporter = undefined;
}

async function promptForActiveWorkbookBackedProject(
  context: ExtensionContext,
  workspaceTrusted: boolean,
  runDoctor: ((request?: unknown) => PromiseLike<unknown> | unknown) | undefined
): Promise<void> {
  if (!workspaceTrusted || runDoctor === undefined) {
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
  vbaDevResolver: CompanionExecutableResolver,
  projectManifestMutationCoordinator: ProjectManifestMutationCoordinator
): Promise<void> {
  const targetSnapshot = captureVscodeCommandPaletteInvocationSnapshot();
  const resolveTarget = createCommandPaletteTargetResolver(targetSnapshot);
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
        activeFilePath: targetSnapshot.activeFilePath,
        workspaceRoots: targetSnapshot.workspaceRoots ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        resolveCommandPaletteTarget: resolveTarget,
        projectManifestMutationCoordinator,
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
  const targetSnapshot = captureVscodeCommandPaletteInvocationSnapshot();
  const resolveTarget = createCommandPaletteTargetResolver(targetSnapshot);
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
        activeFilePath: targetSnapshot.activeFilePath,
        workspaceRoots: targetSnapshot.workspaceRoots ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        resolveCommandPaletteTarget: resolveTarget,
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
  const targetSnapshot = captureVscodeCommandPaletteInvocationSnapshot();
  const resolveTarget = createCommandPaletteTargetResolver(targetSnapshot);
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;

  await runExportCommand({
    extensionRoot: context.extensionPath,
    vbaDevResolver,
    activeFilePath: targetSnapshot.activeFilePath,
    workspaceRoots: targetSnapshot.workspaceRoots ?? [],
    fileExists,
    findProjectManifests,
    chooseProject,
    resolveCommandPaletteTarget: resolveTarget,
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
  projectManifestMutationCoordinator: ProjectManifestMutationCoordinator,
  toolCommandName: CommonModulesToolCommand,
  title: string
): Promise<void> {
  const targetSnapshot = captureVscodeCommandPaletteInvocationSnapshot();
  const resolveTarget = createCommandPaletteTargetResolver(targetSnapshot);
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
        activeFilePath: targetSnapshot.activeFilePath,
        workspaceRoots: targetSnapshot.workspaceRoots ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        resolveCommandPaletteTarget: resolveTarget,
        projectManifestMutationCoordinator,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message: string) => window.showErrorMessage(message),
        showInformationMessage: (message: string) => window.showInformationMessage(message),
        showWarningMessage: (message: string, action: string) =>
          window.showWarningMessage(message, action),
        showOutput: () => channel.show(),
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

  const moduleNames = parseCommonModuleNamesInput(value);

  return moduleNames.length > 0 ? moduleNames : undefined;
}

async function runReferenceCommandWithProgress(
  context: ExtensionContext,
  vbaDevResolver: CompanionExecutableResolver,
  projectManifestMutationCoordinator: ProjectManifestMutationCoordinator,
  toolCommandName: 'add' | 'list' | 'remove',
  title: string
): Promise<void> {
  const targetSnapshot = captureVscodeCommandPaletteInvocationSnapshot();
  const resolveTarget = createCommandPaletteTargetResolver(targetSnapshot);
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;
  const options = {
    extensionRoot: context.extensionPath,
    vbaDevResolver,
    activeFilePath: targetSnapshot.activeFilePath,
    workspaceRoots: targetSnapshot.workspaceRoots ?? [],
    fileExists,
    findProjectManifests,
    chooseProject,
    resolveCommandPaletteTarget: resolveTarget,
    projectManifestMutationCoordinator,
    outputChannel: channel,
    diagnosticReporter: toolDiagnosticReporter,
    showErrorMessage: (message: string) => window.showErrorMessage(message)
  };

  if (toolCommandName === 'list') {
    await window.withProgress(
      {
        location: ProgressLocation.Notification,
        title,
        cancellable: true
      },
      async (progress, token) => runReferenceListCommand({
        ...options,
        reportCancellationProgress: (message: string) => progress.report({ message }),
        cancellationToken: token
      })
    );
    return;
  }

  await runReferenceQuickPickWorkflow({
    ...options,
    selectReferences: (request) => showReferenceQuickPick({
      title: request.title,
      createQuickPick: () => window.createQuickPick<ReferenceQuickPickItem>(),
      createCancellationSource: () => new CancellationTokenSource(),
      discover: request.discover
    }),
    runMutationWithProgress: async (progressTitle, task) => {
      await window.withProgress(
        {
          location: ProgressLocation.Notification,
          title: progressTitle,
          cancellable: true
        },
        async (_progress, token) => task(token)
      );
    },
    showInformationMessage: (message) => window.showInformationMessage(message),
    showWarningMessage: (message, action) => window.showWarningMessage(message, action),
    showReferenceErrorMessage: (message, action) => window.showErrorMessage(message, action),
    showOutput: () => channel.show()
  }, toolCommandName);
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

function captureVscodeCommandPaletteInvocationSnapshot(): CommandPaletteInvocationSnapshot {
  return captureCommandPaletteInvocationSnapshot({
    activeTextEditor: window.activeTextEditor,
    visibleTextEditors: window.visibleTextEditors,
    textDocuments: workspace.textDocuments,
    workspaceFolders: workspace.workspaceFolders
  });
}

function createCommandPaletteTargetResolver(
  snapshot: CommandPaletteInvocationSnapshot
): CommandPaletteTargetResolver {
  return (scope: CommandPaletteTargetScope) => resolveCommandPaletteTarget({
    scope,
    snapshot,
    workspaceRoots: snapshot.workspaceRoots ?? [],
    fileExists,
    findProjectManifests,
    readTextFile,
    resolvePathIdentity: resolveCommandPalettePathIdentity,
    chooseProject: chooseCommandPaletteProject,
    chooseDocument: (documents, initiallyFocused) =>
      chooseCommandPaletteDocumentWithQuickPick(
        () => window.createQuickPick<CommandPaletteDocumentQuickPickItem>(),
        documents,
        initiallyFocused,
        'Select VBA document'
      ),
    showErrorMessage: (message) => window.showErrorMessage(message)
  });
}

async function chooseCommandPaletteProject(
  candidates: readonly CommandPaletteProjectTarget[]
): Promise<CommandPaletteProjectTarget | undefined> {
  const selected = await window.showQuickPick(
    candidates.map((candidate) => ({
      label: candidate.projectName,
      description: candidate.projectRoot,
      detail: candidate.manifestPath,
      candidate
    })),
    { title: 'Select WorkbookBackedProject' }
  );
  return selected?.candidate;
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

function decodeProjectManifestBytes(bytes: Uint8Array): string {
  const buffer = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
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
