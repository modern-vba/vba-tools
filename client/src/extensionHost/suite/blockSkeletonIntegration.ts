import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  Position,
  Selection,
  TextDocument,
  Uri,
  commands,
  window,
  workspace
} from 'vscode';
import {
  BlockSkeletonInsertionPlan,
  BlockSkeletonInsertionRequest,
  getBlockSkeletonInsertionPlanProvider,
  useBlockSkeletonInsertionPlanProviderForTest
} from '../../blockSkeletonInsertion';

const afterNativeEnterCommand =
  'vbaTools.blockSkeletonInsertion.afterNativeEnter';

interface BlockSkeletonCase {
  readonly name: string;
  readonly originalLines: readonly string[];
  readonly headerLine: number;
  readonly expectedLines: readonly string[];
  readonly expectedCursor: Position;
  readonly fileExtension?: 'bas' | 'cls' | 'frm';
  readonly lineEnding?: '\n' | '\r\n';
  readonly editorOptions?: {
    readonly insertSpaces: boolean;
    readonly tabSize: number;
    readonly indentSize: number;
  };
  readonly expectedPlan: Pick<
    BlockSkeletonInsertionPlan,
    'textBeforeCursor' | 'textAfterCursor'
  > | null;
}

export async function runBlockSkeletonIntegrationTests(): Promise<void> {
  await runProductionCase({
    name: 'the production language server inserts a continued tab-indented Function skeleton',
    originalLines: [
      '\tPrivate Function Build( _',
      '        ByVal value As Long) As String'
    ],
    headerLine: 1,
    expectedLines: [
      '\tPrivate Function Build( _',
      '        ByVal value As Long) As String',
      '\t\t',
      '\tEnd Function'
    ],
    expectedCursor: new Position(2, 2),
    lineEnding: '\r\n',
    editorOptions: {
      insertSpaces: false,
      tabSize: 4,
      indentSize: 4
    },
    expectedPlan: {
      textBeforeCursor: '\r\n\t\t',
      textAfterCursor: '\r\n\tEnd Function'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a Property Get skeleton',
    originalLines: [
      'Public Property Get Value() As Long'
    ],
    headerLine: 0,
    expectedLines: [
      'Public Property Get Value() As Long',
      '  ',
      'End Property'
    ],
    expectedCursor: new Position(1, 2),
    lineEnding: '\r\n',
    expectedPlan: {
      textBeforeCursor: '\r\n  ',
      textAfterCursor: '\r\nEnd Property'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a Property Let skeleton',
    originalLines: [
      'Public Property Let Value(ByVal assignedValue As Long)'
    ],
    headerLine: 0,
    expectedLines: [
      'Public Property Let Value(ByVal assignedValue As Long)',
      '  ',
      'End Property'
    ],
    expectedCursor: new Position(1, 2),
    fileExtension: 'cls',
    lineEnding: '\r\n',
    expectedPlan: {
      textBeforeCursor: '\r\n  ',
      textAfterCursor: '\r\nEnd Property'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a Property Set skeleton in a form module',
    originalLines: [
      'VERSION 5.00',
      'Begin VB.Form Dialog',
      'End',
      'Attribute VB_Name = "Dialog"',
      'Public Property Set Value(ByVal assignedValue As Object)'
    ],
    headerLine: 4,
    expectedLines: [
      'VERSION 5.00',
      'Begin VB.Form Dialog',
      'End',
      'Attribute VB_Name = "Dialog"',
      'Public Property Set Value(ByVal assignedValue As Object)',
      '  ',
      'End Property'
    ],
    expectedCursor: new Position(5, 2),
    fileExtension: 'frm',
    expectedPlan: {
      textBeforeCursor: '\n  ',
      textAfterCursor: '\nEnd Property'
    }
  });

  await runProductionCase({
    name: 'the production language server leaves Event declarations to native Enter',
    originalLines: [
      'Public Event Changed(ByVal value As Long)'
    ],
    headerLine: 0,
    expectedLines: [
      'Public Event Changed(ByVal value As Long)',
      ''
    ],
    expectedCursor: new Position(1, 0),
    fileExtension: 'cls',
    lineEnding: '\r\n',
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves Declare Sub declarations to native Enter',
    originalLines: [
      'Public Declare Sub Run Lib "library" ()'
    ],
    headerLine: 0,
    expectedLines: [
      'Public Declare Sub Run Lib "library" ()',
      ''
    ],
    expectedCursor: new Position(1, 0),
    lineEnding: '\r\n',
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves Declare Function declarations to native Enter',
    originalLines: [
      'Private Declare PtrSafe Function Read Lib "library" () As Long'
    ],
    headerLine: 0,
    expectedLines: [
      'Private Declare PtrSafe Function Read Lib "library" () As Long',
      ''
    ],
    expectedCursor: new Position(1, 0),
    lineEnding: '\r\n',
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves an illegal Global Function in a class module to native Enter',
    originalLines: [
      'Global Function Build() As String'
    ],
    headerLine: 0,
    expectedLines: [
      'Global Function Build() As String',
      ''
    ],
    expectedCursor: new Position(1, 0),
    fileExtension: 'cls',
    lineEnding: '\r\n',
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server inserts a continued Enum skeleton before a same-level declaration',
    originalLines: [
      '  pUbLiC _',
      "      eNuM State   ' keep",
      '',
      '  Public Sub Run()',
      '  End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      '  pUbLiC _',
      "      eNuM State   ' keep",
      '    ',
      '  End Enum',
      '',
      '  Public Sub Run()',
      '  End Sub'
    ],
    expectedCursor: new Position(2, 4),
    lineEnding: '\r\n',
    expectedPlan: {
      textBeforeCursor: '\r\n    ',
      textAfterCursor: '\r\n  End Enum'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a Public Enum skeleton in a class module',
    originalLines: [
      'Public Enum State'
    ],
    headerLine: 0,
    expectedLines: [
      'Public Enum State',
      '  ',
      'End Enum'
    ],
    expectedCursor: new Position(1, 2),
    fileExtension: 'cls',
    lineEnding: '\r\n',
    expectedPlan: {
      textBeforeCursor: '\r\n  ',
      textAfterCursor: '\r\nEnd Enum'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a Public Enum skeleton in a form module',
    originalLines: [
      'VERSION 5.00',
      'Begin VB.Form Dialog',
      'End',
      'Attribute VB_Name = "Dialog"',
      'Public Enum State'
    ],
    headerLine: 4,
    expectedLines: [
      'VERSION 5.00',
      'Begin VB.Form Dialog',
      'End',
      'Attribute VB_Name = "Dialog"',
      'Public Enum State',
      '  ',
      'End Enum'
    ],
    expectedCursor: new Position(5, 2),
    fileExtension: 'frm',
    expectedPlan: {
      textBeforeCursor: '\n  ',
      textAfterCursor: '\nEnd Enum'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a tab-indented Private Type skeleton in a class module',
    originalLines: [
      '\tPrivate Type Record'
    ],
    headerLine: 0,
    expectedLines: [
      '\tPrivate Type Record',
      '\t\t',
      '\tEnd Type'
    ],
    expectedCursor: new Position(1, 2),
    fileExtension: 'cls',
    lineEnding: '\r\n',
    editorOptions: {
      insertSpaces: false,
      tabSize: 4,
      indentSize: 4
    },
    expectedPlan: {
      textBeforeCursor: '\r\n\t\t',
      textAfterCursor: '\r\n\tEnd Type'
    }
  });

  await runProductionCase({
    name: 'the production language server leaves a non-private Type in a form module to native Enter',
    originalLines: [
      'VERSION 5.00',
      'Begin VB.Form Dialog',
      'End',
      'Attribute VB_Name = "Dialog"',
      'Type Record'
    ],
    headerLine: 4,
    expectedLines: [
      'VERSION 5.00',
      'Begin VB.Form Dialog',
      'End',
      'Attribute VB_Name = "Dialog"',
      'Type Record',
      ''
    ],
    expectedCursor: new Position(5, 0),
    fileExtension: 'frm',
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves an invalid Friend Enum to native Enter',
    originalLines: [
      'Friend Enum State'
    ],
    headerLine: 0,
    expectedLines: [
      'Friend Enum State',
      ''
    ],
    expectedCursor: new Position(1, 0),
    fileExtension: 'cls',
    lineEnding: '\r\n',
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves an existing Enum member to native Enter',
    originalLines: [
      'Public Enum State',
      '    Ready'
    ],
    headerLine: 0,
    expectedLines: [
      'Public Enum State',
      '',
      '    Ready'
    ],
    expectedCursor: new Position(1, 0),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves a candidate-owned End Type to native Enter',
    originalLines: [
      'Private Type Record',
      'End Type'
    ],
    headerLine: 0,
    expectedLines: [
      'Private Type Record',
      '',
      'End Type'
    ],
    expectedCursor: new Position(1, 0),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server inserts a block If skeleton',
    originalLines: [
      'Public Sub Main()',
      '    If True Then',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    If True Then',
      '      ',
      '    End If',
      'End Sub'
    ],
    expectedCursor: new Position(2, 6),
    expectedPlan: {
      textBeforeCursor: '\n      ',
      textAfterCursor: '\n    End If'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a nested block If skeleton',
    originalLines: [
      'Public Sub Example()',
      '    If True Then',
      '        If True Then',
      '    End If',
      'End Sub'
    ],
    headerLine: 2,
    expectedLines: [
      'Public Sub Example()',
      '    If True Then',
      '        If True Then',
      '          ',
      '        End If',
      '    End If',
      'End Sub'
    ],
    expectedCursor: new Position(3, 10),
    expectedPlan: {
      textBeforeCursor: '\n          ',
      textAfterCursor: '\n        End If'
    }
  });

  await runProductionCase({
    name: 'the production language server leaves single-line If to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    If True Then Debug.Print "value"',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    If True Then Debug.Print "value"',
      '    ',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves an owned Else branch to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    If True Then',
      '    Else',
      '    End If',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    If True Then',
      '    ',
      '    Else',
      '    End If',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server inserts a With skeleton',
    originalLines: [
      'Public Sub Main()',
      '    With target.Parent',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    With target.Parent',
      '      ',
      '    End With',
      'End Sub'
    ],
    expectedCursor: new Position(2, 6),
    expectedPlan: {
      textBeforeCursor: '\n      ',
      textAfterCursor: '\n    End With'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a continued With skeleton at the final physical line',
    originalLines: [
      'Public Sub Main()',
      '  With Worksheets( _',
      '        "Sheet1")',
      'End Sub'
    ],
    headerLine: 2,
    expectedLines: [
      'Public Sub Main()',
      '  With Worksheets( _',
      '        "Sheet1")',
      '    ',
      '  End With',
      'End Sub'
    ],
    expectedCursor: new Position(3, 4),
    expectedPlan: {
      textBeforeCursor: '\n    ',
      textAfterCursor: '\n  End With'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a nested With skeleton without stealing the ancestor closer',
    originalLines: [
      'Public Sub Main()',
      '    With target',
      '        With .Font',
      '    End With',
      'End Sub'
    ],
    headerLine: 2,
    expectedLines: [
      'Public Sub Main()',
      '    With target',
      '        With .Font',
      '          ',
      '        End With',
      '    End With',
      'End Sub'
    ],
    expectedCursor: new Position(3, 10),
    expectedPlan: {
      textBeforeCursor: '\n          ',
      textAfterCursor: '\n        End With'
    }
  });

  await runProductionCase({
    name: 'the production language server preserves an If ancestor branch after a With skeleton',
    originalLines: [
      'Public Sub Main()',
      '    If Ready() Then',
      '        With target',
      '    Else',
      '    End If',
      'End Sub'
    ],
    headerLine: 2,
    expectedLines: [
      'Public Sub Main()',
      '    If Ready() Then',
      '        With target',
      '          ',
      '        End With',
      '    Else',
      '    End If',
      'End Sub'
    ],
    expectedCursor: new Position(3, 10),
    expectedPlan: {
      textBeforeCursor: '\n          ',
      textAfterCursor: '\n        End With'
    }
  });

  await runProductionCase({
    name: 'the production language server leaves an existing With body to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    With target',
      '        .Value = 1',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    With target',
      '    ',
      '        .Value = 1',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves a candidate-owned End With to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    With target',
      '    End With',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    With target',
      '    ',
      '    End With',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves an invalid With header to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    With target +',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    With target +',
      '    ',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server inserts a continued tab-indented For skeleton',
    originalLines: [
      'Public Sub Main()',
      '\tfOr index = source.To _',
      '        tO maximum.Step',
      'End Sub'
    ],
    headerLine: 2,
    expectedLines: [
      'Public Sub Main()',
      '\tfOr index = source.To _',
      '        tO maximum.Step',
      '\t\t',
      '\tNext',
      'End Sub'
    ],
    expectedCursor: new Position(3, 2),
    lineEnding: '\r\n',
    editorOptions: {
      insertSpaces: false,
      tabSize: 4,
      indentSize: 4
    },
    expectedPlan: {
      textBeforeCursor: '\r\n\t\t',
      textAfterCursor: '\r\n\tNext'
    }
  });

  await runProductionCase({
    name: 'the production language server inserts a nested For Each skeleton without stealing the ancestor Next',
    originalLines: [
      'Public Sub Main()',
      '    For outerIndex = 1 To 3',
      '        For Each item In items',
      '    Next',
      'End Sub'
    ],
    headerLine: 2,
    expectedLines: [
      'Public Sub Main()',
      '    For outerIndex = 1 To 3',
      '        For Each item In items',
      '          ',
      '        Next',
      '    Next',
      'End Sub'
    ],
    expectedCursor: new Position(3, 10),
    expectedPlan: {
      textBeforeCursor: '\n          ',
      textAfterCursor: '\n        Next'
    }
  });

  await runProductionCase({
    name: 'the production language server leaves a candidate-owned Next to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    For index = 1 To 3',
      '    Next index',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    For index = 1 To 3',
      '    ',
      '    Next index',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves Do While to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    Do While Ready()',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    Do While Ready()',
      '    ',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });

  await runProductionCase({
    name: 'the production language server leaves While to native Enter',
    originalLines: [
      'Public Sub Main()',
      '    While Ready()',
      'End Sub'
    ],
    headerLine: 1,
    expectedLines: [
      'Public Sub Main()',
      '    While Ready()',
      '    ',
      'End Sub'
    ],
    expectedCursor: new Position(2, 4),
    expectedPlan: null
  });
}

async function runProductionCase(testCase: BlockSkeletonCase): Promise<void> {
  await runTest(testCase.name, async () => {
    const lineEnding = testCase.lineEnding ?? '\n';
    const editorOptions = testCase.editorOptions ?? {
      insertSpaces: true,
      tabSize: 4,
      indentSize: 2
    };
    const originalText = testCase.originalLines.join(lineEnding);
    const expectedText = testCase.expectedLines.join(lineEnding);
    const documentUri = Uri.file(join(
      tmpdir(),
      `vba-tools-block-skeleton-${randomUUID()}.${testCase.fileExtension ?? 'bas'}`
    ));
    let fileCreated = false;
    let openedDocument: TextDocument | undefined;
    let observer: { dispose(): void } | undefined;

    try {
      await workspace.fs.writeFile(documentUri, Buffer.from(originalText, 'utf8'));
      fileCreated = true;
      const document = await workspace.openTextDocument(documentUri);
      openedDocument = document;
      assert.equal(document.languageId, 'vba');
      const editor = await window.showTextDocument(document);
      editor.options = editorOptions;
      const initialDocumentVersion = document.version;
      const end = document.lineAt(testCase.headerLine).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');

      const productionProvider = getBlockSkeletonInsertionPlanProvider();
      const warmup = productionProvider({
        documentUri: document.uri.toString(),
        documentVersion: document.version,
        position: {
          line: end.line,
          character: end.character
        },
        options: {
          insertSpaces: editorOptions.insertSpaces,
          indentSize: editorOptions.indentSize,
          tabSize: editorOptions.tabSize
        }
      });
      await warmup.response;

      let capturedRequest: BlockSkeletonInsertionRequest | undefined;
      let capturedResponse: BlockSkeletonInsertionPlan | null | undefined;
      let capturedError: unknown;
      let responseSettled = false;
      let cancellationCount = 0;
      observer = useBlockSkeletonInsertionPlanProviderForTest((request) => {
        capturedRequest = request;
        const pending = productionProvider(request);
        return {
          response: pending.response.then(
            (response) => {
              capturedResponse = response;
              responseSettled = true;
              return response;
            },
            (error) => {
              capturedError = error;
              responseSettled = true;
              throw error;
            }
          ),
          cancel: () => {
            cancellationCount++;
            pending.cancel();
          }
        };
      });

      await executeGuardedEnter();

      await waitFor(
        () => responseSettled
          && document.getText() === expectedText
          && editor.selection.active.isEqual(testCase.expectedCursor),
        2_000,
        () => [
          `text=${JSON.stringify(document.getText())}`,
          `cursor=${formatPosition(editor.selection.active)}`,
          `request=${JSON.stringify(capturedRequest)}`,
          `response=${JSON.stringify(capturedResponse)}`,
          `error=${String(capturedError)}`,
          `settled=${responseSettled}`,
          `cancellations=${cancellationCount}`
        ].join('; ')
      );
      assert.equal(capturedError, undefined);
      assert.equal(cancellationCount, 0);
      assert.deepEqual(capturedRequest, {
        documentUri: document.uri.toString(),
        documentVersion: initialDocumentVersion + 1,
        position: {
          line: testCase.headerLine,
          character: end.character
        },
        options: {
          insertSpaces: editorOptions.insertSpaces,
          tabSize: editorOptions.tabSize,
          indentSize: editorOptions.indentSize
        }
      });
      assert.deepEqual(
        capturedResponse,
        testCase.expectedPlan === null
          ? null
          : {
              documentVersion: initialDocumentVersion + 1,
              position: {
                line: testCase.headerLine,
                character: end.character
              },
              ...testCase.expectedPlan
            }
      );
      assert.equal(document.getText(), expectedText);
      assert.deepEqual(editor.selection.active, testCase.expectedCursor);

      await commands.executeCommand('undo');
      await waitFor(
        () => document.getText() === originalText
          && editor.selection.active.isEqual(end)
      );
      assert.equal(document.getText(), originalText);
      assert.deepEqual(editor.selection.active, end);
    } finally {
      observer?.dispose();
      try {
        if (
          openedDocument !== undefined
          && window.activeTextEditor?.document !== openedDocument
        ) {
          await window.showTextDocument(openedDocument);
        }
        if (openedDocument?.isDirty) {
          await commands.executeCommand('workbench.action.files.revert');
        }
        if (window.activeTextEditor?.document === openedDocument) {
          await commands.executeCommand('workbench.action.closeActiveEditor');
        }
      } finally {
        if (fileCreated) {
          await workspace.fs.delete(documentUri, { useTrash: false });
        }
      }
    }
  });
}

function executeGuardedEnter(): Thenable<unknown> {
  return commands.executeCommand('runCommands', {
    commands: [
      'lineBreakInsert',
      afterNativeEnterCommand
    ]
  });
}

function formatPosition(position: Position | undefined): string {
  return position === undefined ? '(none)' : `(${position.line}, ${position.character})`;
}

async function waitFor(
  condition: () => boolean,
  timeoutMilliseconds = 1_000,
  describeState: () => string = () => ''
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (!condition()) {
    if (Date.now() >= deadline) {
      throw new Error(
        `Condition was not met within ${timeoutMilliseconds} ms. ${describeState()}`.trim()
      );
    }

    await delay(10);
  }
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}

async function runTest(name: string, body: () => Promise<void>): Promise<void> {
  const startedAt = Date.now();
  try {
    await body();
    console.log(`PASS ${name} (${Date.now() - startedAt} ms)`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    throw error;
  }
}
