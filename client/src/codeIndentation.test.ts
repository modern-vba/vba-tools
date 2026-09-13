import test from 'node:test';
import assert from 'node:assert/strict';
import {
  CodeIndentation,
  CodeIndentationConfiguration,
  CodeIndentationEditor,
  CodeIndentationResult
} from './codeIndentation';

function createEditor(): CodeIndentationEditor {
  return {
    document: {
      uri: { toString: () => 'file:///work/Settings.cls' },
      version: 1,
      languageId: 'vba',
      isClosed: false
    },
    options: { tabSize: 2, indentSize: 2, insertSpaces: true }
  };
}

const configuration: CodeIndentationConfiguration = {
  detectIndentation: true,
  tabSize: 4,
  insertSpaces: true
};

test('code-only indentation is applied before the editor can format', async () => {
  const editor = createEditor();
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: async (request) => {
      assert.deepEqual(request.options, { tabSize: 4, insertSpaces: true });
      return {
        ...request.textDocument,
        tabSize: 4,
        indentSize: 4,
        insertSpaces: true
      };
    }
  });

  assert.equal(await indentation.ensure(editor), true);
  assert.deepEqual(editor.options, { tabSize: 4, indentSize: 4, insertSpaces: true });
  assert.equal(editor.document.version, 1);
});

test('a manual indentation choice made during detection wins and survives later formatting', async () => {
  const editor = createEditor();
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: () => new Promise(resolve => { complete = resolve; })
  });
  const pending = indentation.ensure(editor);
  editor.options = { tabSize: 8, indentSize: 3, insertSpaces: true };
  complete({ uri: editor.document.uri.toString(), version: 1,
    tabSize: 4, indentSize: 4, insertSpaces: true });

  assert.equal(await pending, true);
  assert.deepEqual(editor.options, { tabSize: 8, indentSize: 3, insertSpaces: true });
  assert.equal(await indentation.ensure(editor), true);
  assert.deepEqual(editor.options, { tabSize: 8, indentSize: 3, insertSpaces: true });
});

test('detection discards an obsolete result and resolves the current version before formatting', async () => {
  const editor = createEditor();
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: request => request.textDocument.version === 1
      ? new Promise(resolve => { complete = resolve; })
      : Promise.resolve({ ...request.textDocument, tabSize: 6, indentSize: 6, insertSpaces: true })
  });
  const pending = indentation.ensure(editor);
  Object.assign(editor.document, { version: 2 });
  complete({ uri: editor.document.uri.toString(), version: 1,
    tabSize: 4, indentSize: 4, insertSpaces: true });

  assert.equal(await pending, true);
  assert.deepEqual(editor.options, { tabSize: 6, indentSize: 6, insertSpaces: true });
});

test('disabled detection applies the configured independent widths without querying the server', async () => {
  const editor = createEditor();
  const indentation = new CodeIndentation({
    getConfiguration: () => ({ ...configuration, detectIndentation: false, tabSize: 8, indentSize: 3 }),
    detect: async () => { throw new Error('Disabled detection must not contact the server.'); }
  });

  assert.equal(await indentation.ensure(editor), true);
  assert.deepEqual(editor.options, { tabSize: 8, indentSize: 3, insertSpaces: true });
});

test('a changed configuration supersedes an in-flight detection and applies on the next format', async () => {
  const editor = createEditor();
  let configured: CodeIndentationConfiguration = configuration;
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => configured,
    detect: () => new Promise(resolve => { complete = resolve; })
  });
  const pending = indentation.ensure(editor);
  configured = { detectIndentation: false, tabSize: 8, indentSize: 3, insertSpaces: false };
  complete({ uri: editor.document.uri.toString(), version: 1,
    tabSize: 4, indentSize: 4, insertSpaces: true });

  assert.equal(await pending, false);
  assert.equal(await indentation.ensure(editor), true);
  assert.deepEqual(editor.options, { tabSize: 8, indentSize: 3, insertSpaces: false });
});

test('manual choices are remembered even when the user returns to the original width before detection completes', async () => {
  const editor = createEditor();
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: () => new Promise(resolve => { complete = resolve; })
  });
  const pending = indentation.ensure(editor);
  editor.options = { tabSize: 8, indentSize: 8, insertSpaces: true };
  indentation.observeOptions(editor);
  editor.options = { tabSize: 2, indentSize: 2, insertSpaces: true };
  indentation.observeOptions(editor);
  complete({ uri: editor.document.uri.toString(), version: 1,
    tabSize: 4, indentSize: 4, insertSpaces: true });

  assert.equal(await pending, true);
  assert.deepEqual(editor.options, { tabSize: 2, indentSize: 2, insertSpaces: true });
});

test('an unavailable server does not poison later indentation resolution', async () => {
  const editor = createEditor();
  let available = false;
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: async request => {
      if (!available) { throw new Error('Server restarting'); }
      return { ...request.textDocument, tabSize: 4, indentSize: 4, insertSpaces: true };
    }
  });
  assert.equal(await indentation.ensure(editor), false);
  available = true;
  assert.equal(await indentation.ensure(editor), true);
  assert.equal(editor.options.indentSize, 4);
});

test('disposing the editor lifecycle discards pending results', async () => {
  const editor = createEditor();
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: () => new Promise(resolve => { complete = resolve; })
  });
  const pending = indentation.ensure(editor);
  indentation.dispose();
  complete({ uri: editor.document.uri.toString(), version: 1,
    tabSize: 4, indentSize: 4, insertSpaces: true });
  assert.equal(await pending, false);
  assert.equal(editor.options.indentSize, 2);
});

test('manual indentation selected while the server is starting is preserved', async () => {
  const editor = createEditor();
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: async request => ({ ...request.textDocument,
      tabSize: 4, indentSize: 4, insertSpaces: true })
  });
  indentation.observeEditor(editor);
  editor.options = { tabSize: 8, indentSize: 3, insertSpaces: true };
  indentation.observeOptions(editor);
  assert.equal(await indentation.ensure(editor), true);
  assert.deepEqual(editor.options, { tabSize: 8, indentSize: 3, insertSpaces: true });
});

test('hidden formatting resolves code-only defaults without preventing later visible editor resolution', async () => {
  const editor = createEditor();
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: async request => ({ ...request.textDocument,
      tabSize: 4, indentSize: 4, insertSpaces: true })
  });
  assert.deepEqual(await indentation.resolve(editor.document), {
    tabSize: 4, indentSize: 4, insertSpaces: true
  });
  assert.equal(editor.options.indentSize, 2);
  assert.deepEqual(await indentation.resolve(editor.document, editor), {
    tabSize: 4, indentSize: 4, insertSpaces: true
  });
  assert.equal(editor.options.indentSize, 4);
});

test('hidden formatting retains the manual style selected while its editor was visible', async () => {
  const editor = createEditor();
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: async request => ({ ...request.textDocument,
      tabSize: 4, indentSize: 4, insertSpaces: true })
  });
  await indentation.ensure(editor);
  editor.options = { tabSize: 8, indentSize: 3, insertSpaces: false };
  indentation.observeOptions(editor);
  assert.deepEqual(await indentation.resolve(editor.document), {
    tabSize: 8, indentSize: 3, insertSpaces: false
  });
});

test('hidden resolution respects subsequent manual choices and parent disposal', async () => {
  for (const action of ['manual', 'dispose', 'new-editor'] as const) {
    const editor = createEditor();
    let complete!: (result: CodeIndentationResult) => void;
    let delayed = action !== 'new-editor';
    const indentation = new CodeIndentation({
      getConfiguration: () => configuration,
      detect: request => delayed
        ? new Promise(resolve => { complete = resolve; })
        : Promise.resolve({ ...request.textDocument, tabSize: 4, indentSize: 4, insertSpaces: true })
    });
    if (action === 'new-editor') {
      await indentation.ensure(editor);
      const reopened = { document: editor.document, options: { ...editor.options } };
      indentation.observeEditor(reopened);
      reopened.options = { tabSize: 8, indentSize: 3, insertSpaces: true };
      indentation.observeOptions(reopened);
      assert.equal((await indentation.resolve(editor.document))?.indentSize, 3);
      continue;
    }
    const pending = indentation.resolve(editor.document);
    if (action === 'manual') {
      indentation.observeEditor(editor);
      editor.options = { tabSize: 8, indentSize: 3, insertSpaces: true };
      indentation.observeOptions(editor);
    } else {
      indentation.dispose();
    }
    complete({ uri: editor.document.uri.toString(), version: 1,
      tabSize: 4, indentSize: 4, insertSpaces: true });
    assert.equal((await pending)?.indentSize, action === 'manual' ? 3 : undefined);
  }
});

test('pending visible resolution preserves a replacement editor manual choice', async () => {
  const editor = createEditor();
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => configuration,
    detect: () => new Promise(resolve => { complete = resolve; })
  });
  const pending = indentation.resolve(editor.document, editor);
  const replacement = { document: editor.document,
    options: { tabSize: 8, indentSize: 8, insertSpaces: true } };
  indentation.observeOptions(replacement);
  complete({ uri: editor.document.uri.toString(), version: 1,
    tabSize: 4, indentSize: 4, insertSpaces: true });
  assert.equal((await pending)?.indentSize, 8);
  assert.equal((await indentation.resolve(editor.document))?.indentSize, 8);
  assert.equal((await indentation.resolve(editor.document, editor))?.indentSize, 8);
});
