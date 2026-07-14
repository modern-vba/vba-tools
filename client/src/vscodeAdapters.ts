import {
  CancellationToken,
  Diagnostic,
  DiagnosticCollection,
  DiagnosticSeverity,
  Location,
  Position,
  Range,
  TestController,
  TestItem,
  TestMessage,
  TestRun,
  TestRunProfileKind,
  TestRunRequest,
  Uri
} from 'vscode';
import {
  TestControllerAdapter,
  TestExplorerItem,
  TestMessageLocation,
  TestRunLike,
  TestRunRequestLike
} from './testExplorer';
import {
  VbaDevDiagnostic,
  VbaDevDiagnosticCollection
} from './toolDiagnostics';

export function createVscodeTestControllerAdapter(controller: TestController): TestControllerAdapter {
  return {
    createTestItem: (id, label, uriPath) => controller.createTestItem(
      id,
      label,
      uriPath ? Uri.file(uriPath) : undefined
    ),
    replaceItems: (items) => controller.items.replace(items as TestItem[]),
    createRunProfile: (label, runHandler, isDefault) => {
      controller.createRunProfile(
        label,
        TestRunProfileKind.Run,
        async (request: TestRunRequest, token: CancellationToken) => {
          await runHandler(request as TestRunRequestLike, token);
        },
        isDefault
      );
    },
    createTestRun: (request) => toTestRunLike(controller.createTestRun(request as TestRunRequest))
  };
}

export function createVscodeDiagnosticCollectionAdapter(collection: DiagnosticCollection): VbaDevDiagnosticCollection {
  return {
    set: (uriPath, diagnostics) => {
      collection.set(
        Uri.file(uriPath),
        diagnostics.map((diagnostic) => toVscodeDiagnostic(diagnostic))
      );
    },
    delete: (uriPath) => {
      collection.delete(Uri.file(uriPath));
    }
  };
}

function toVscodeDiagnostic(diagnostic: VbaDevDiagnostic): Diagnostic {
  const vscodeDiagnostic = new Diagnostic(
    new Range(
      diagnostic.range.start.line,
      diagnostic.range.start.character,
      diagnostic.range.end.line,
      diagnostic.range.end.character
    ),
    diagnostic.message,
    toVscodeDiagnosticSeverity(diagnostic.severity)
  );
  vscodeDiagnostic.source = diagnostic.owner;
  vscodeDiagnostic.code = diagnostic.code;
  return vscodeDiagnostic;
}

function toVscodeDiagnosticSeverity(severity: VbaDevDiagnostic['severity']): DiagnosticSeverity {
  if (severity === 'error') {
    return DiagnosticSeverity.Error;
  }

  if (severity === 'warning') {
    return DiagnosticSeverity.Warning;
  }

  if (severity === 'information') {
    return DiagnosticSeverity.Information;
  }

  return DiagnosticSeverity.Hint;
}

function toTestRunLike(run: TestRun): TestRunLike {
  return {
    started: (item: TestExplorerItem) => run.started(item as TestItem),
    passed: (item: TestExplorerItem) => run.passed(item as TestItem),
    failed: (item: TestExplorerItem, message: string, location?: TestMessageLocation | undefined) => {
      run.failed(item as TestItem, toTestMessage(message, location));
    },
    errored: (item: TestExplorerItem, message: string, location?: TestMessageLocation | undefined) => {
      run.errored(item as TestItem, toTestMessage(message, location));
    },
    cancelled: (_item: TestExplorerItem) => {
      run.appendOutput('Test run cancelled.\r\n');
    },
    appendOutput: (output: string) => {
      if (output.length > 0) {
        run.appendOutput(output.replace(/\n/g, '\r\n'));
      }
    },
    end: () => run.end()
  };
}

function toTestMessage(message: string, location?: TestMessageLocation | undefined): TestMessage {
  const testMessage = new TestMessage(message);
  if (location) {
    testMessage.location = new Location(
      Uri.file(location.uriPath),
      new Position(location.line, location.character)
    );
  }

  return testMessage;
}
