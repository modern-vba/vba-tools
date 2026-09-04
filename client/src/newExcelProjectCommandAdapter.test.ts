import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';

const extensionSource = readFileSync(
  path.join(process.cwd(), 'client', 'src', 'extension.ts'),
  'utf8'
);
const readme = readFileSync(path.join(process.cwd(), 'README.md'), 'utf8');

test('extension activation owns one guided Excel project coordinator and invalidates its preflight with trust state', () => {
  assert.match(
    extensionSource,
    /const newExcelProjectCommand = new NewExcelProjectCommand\(\{[\s\S]*?\}\);/
  );
  assert.match(
    extensionSource,
    /invalidateManagedToolingState: \(\) => \{\s*vbaDevResolver\.invalidate\(\);\s*newExcelProjectCommand\.invalidatePreflight\(\);\s*\}/
  );
  assert.match(
    extensionSource,
    /'vbaTools\.newExcel': async \(\) => \{\s*await newExcelProjectCommand\.run\(\);\s*\}/
  );
  assert.doesNotMatch(
    extensionSource,
    /'vbaTools\.newExcel': \(\) => undefined/
  );
});

test('guided Excel project phases use the same resolution with distinct cancellable progress and closed output capture', () => {
  assert.match(
    extensionSource,
    /runCommand: async \(resolution, args\) => \{\s*const nameOptionIndex = args\.indexOf\('--name'\);\s*const title = args\[0\] === 'doctor'\s*\? 'VBA Tools: Checking Excel VBA project prerequisites'\s*: `VBA Tools: Creating Excel VBA project "\$\{args\[nameOptionIndex \+ 1\]\}"`;\s*return window\.withProgress\(\s*\{\s*location: ProgressLocation\.Notification,\s*title,\s*cancellable: true\s*\},[\s\S]*?runResolvedVbaDevCommandInvocation\(\{[\s\S]*?outputChannel: extensionOutputChannel,[\s\S]*?revealOutput: false,[\s\S]*?cancellationToken: token,[\s\S]*?reportCancellationProgress: \(message\) => progress\.report\(\{ message \}\)[\s\S]*?\}, resolution, args\)/
  );
  assert.match(
    extensionSource,
    /appendOutput: \(text\) => extensionOutputChannel\.appendLine\(text\)/
  );
});

test('guided Excel project adapter owns fixed input UI, setup preview, and explicit navigation', () => {
  assert.match(
    extensionSource,
    /showProjectNameInput: async \(options\) => window\.showInputBox\(\{[\s\S]*?\.\.\.options,[\s\S]*?valueSelection:[\s\S]*?\}\)/
  );
  assert.match(
    extensionSource,
    /showParentFolder: async \(options\) => \(await window\.showOpenDialog\(\{[\s\S]*?canSelectFiles: false,[\s\S]*?canSelectFolders: true,[\s\S]*?canSelectMany: false[\s\S]*?\}\)\)\?\.\[0\]/
  );
  assert.match(
    extensionSource,
    /showErrorMessage: async \(message, options, \.\.\.actions\) => \(\s*options === undefined\s*\? window\.showErrorMessage\(message, \.\.\.actions\)\s*: window\.showErrorMessage\(message, options, \.\.\.actions\)\s*\)/
  );
  assert.match(
    extensionSource,
    /path\.join\(context\.extensionPath, 'README\.md'\)[\s\S]*?\.with\(\{ fragment: '2---prepare-excel' \}\)[\s\S]*?commands\.executeCommand\('markdown\.showPreview', setupInstructions\)/
  );
  assert.match(
    extensionSource,
    /openManifest: async \(manifestPath\) => \{\s*const document = await workspace\.openTextDocument\(Uri\.file\(manifestPath\)\);\s*await window\.showTextDocument\(document\);\s*\}/
  );
  assert.match(
    extensionSource,
    /openFolderInNewWindow: async \(projectRoot\) => \{[\s\S]*?commands\.executeCommand\(\s*'vscode\.openFolder',\s*Uri\.file\(projectRoot\),\s*true\s*\)/
  );
});

test('Getting Started directs project creation through the guided command', () => {
  const step = readme.match(
    /### 3 - Create a workbook-backed project([\s\S]*?)### 4 - Migrate an existing workbook/
  )?.[1] ?? '';

  assert.match(step, /VBA Tools: Create Excel VBA Project/);
  assert.match(step, /project name[\s\S]*parent folder/i);
  assert.doesNotMatch(step, /VBA Tools: Open vba-dev Terminal/);
  assert.doesNotMatch(step, /vba-dev new excel -n example_book/);
});
