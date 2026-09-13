export interface CodeIndentationOptions {
  readonly tabSize: number;
  readonly indentSize: number;
  readonly insertSpaces: boolean;
}

export interface CodeIndentationConfiguration {
  readonly detectIndentation: boolean;
  readonly tabSize: number;
  readonly indentSize?: number;
  readonly insertSpaces: boolean;
}

export interface CodeIndentationDocument {
  readonly uri: { toString(): string };
  readonly version: number;
  readonly languageId: string;
  readonly isClosed: boolean;
}

export interface CodeIndentationEditor {
  readonly document: CodeIndentationDocument;
  options: {
    tabSize?: number | string;
    indentSize?: number | string;
    insertSpaces?: boolean | string;
  };
}

export interface CodeIndentationRequest {
  readonly textDocument: { readonly uri: string; readonly version: number };
  readonly options: {
    readonly tabSize: number;
    readonly indentSize?: number;
    readonly insertSpaces: boolean;
  };
}

export interface CodeIndentationResult extends CodeIndentationOptions {
  readonly uri: string;
  readonly version: number;
}

export interface CodeIndentationHost {
  getConfiguration(document: CodeIndentationDocument): CodeIndentationConfiguration;
  detect(request: CodeIndentationRequest): Promise<CodeIndentationResult | null>;
}

interface EditorIndentationState {
  readonly configuration: CodeIndentationConfiguration;
  readonly version: number;
  options?: CodeIndentationOptions;
  optionsKey: string;
  resolved: boolean;
  pending?: Promise<boolean>;
}

function optionsKey(options: CodeIndentationEditor['options']): string {
  return JSON.stringify([options.tabSize, options.indentSize, options.insertSpaces]);
}

function resolvedOptions(options: CodeIndentationEditor['options']): CodeIndentationOptions | undefined {
  if (typeof options.tabSize !== 'number' || !Number.isInteger(options.tabSize) || options.tabSize <= 0
      || typeof options.indentSize !== 'number' || !Number.isInteger(options.indentSize) || options.indentSize <= 0
      || typeof options.insertSpaces !== 'boolean') {
    return undefined;
  }
  return { tabSize: options.tabSize, indentSize: options.indentSize, insertSpaces: options.insertSpaces };
}

function configurationKey(configuration: CodeIndentationConfiguration): string {
  return JSON.stringify([configuration.detectIndentation, configuration.tabSize,
    configuration.indentSize, configuration.insertSpaces]);
}

/** Resolves one editor's indentation without editing its source text. */
export class CodeIndentation {
  private readonly states = new Map<CodeIndentationDocument, EditorIndentationState>();
  private disposed = false;

  constructor(private readonly host: CodeIndentationHost) {}

  async resolve(
    document: CodeIndentationDocument,
    editor?: CodeIndentationEditor
  ): Promise<CodeIndentationOptions | undefined> {
    if (this.disposed || document.isClosed) {
      return undefined;
    }
    if (editor !== undefined) {
      if (!await this.ensure(editor) || this.disposed || document.isClosed) {
        return undefined;
      }
      const current = this.states.get(document);
      return current?.resolved
        && configurationKey(current.configuration) === configurationKey(this.host.getConfiguration(document))
        ? current.options : undefined;
    }
    const configured = this.host.getConfiguration(document);
    const state = this.states.get(document);
    if (state?.resolved && configurationKey(state.configuration) === configurationKey(configured)) {
      return state.options;
    }
    const hiddenEditor: CodeIndentationEditor = {
      document,
      options: {
        tabSize: configured.tabSize,
        indentSize: configured.indentSize ?? configured.tabSize,
        insertSpaces: configured.insertSpaces
      }
    };
    // A hidden formatting request has no editor to update. Its transient
    // resolution must not suppress applying a style when an editor opens later.
    const hiddenIndentation = new CodeIndentation(this.host);
    try {
      const result = await hiddenIndentation.ensure(hiddenEditor) ? resolvedOptions(hiddenEditor.options) : undefined;
      if (this.disposed || document.isClosed
          || configurationKey(this.host.getConfiguration(document)) !== configurationKey(configured)) {
        return undefined;
      }
      const current = this.states.get(document);
      if (current?.resolved && configurationKey(current.configuration) === configurationKey(configured)) {
        return current.options;
      }
      return current === state ? result : undefined;
    } finally {
      hiddenIndentation.dispose();
    }
  }

  dispose(): void {
    this.disposed = true;
    this.states.clear();
  }

  close(document: CodeIndentationDocument): void {
    this.states.delete(document);
  }

  observeEditor(editor: CodeIndentationEditor): void {
    if (!this.disposed && !editor.document.isClosed && !this.states.has(editor.document)) {
      this.states.set(editor.document, {
        configuration: this.host.getConfiguration(editor.document),
        version: editor.document.version,
        options: resolvedOptions(editor.options),
        optionsKey: optionsKey(editor.options),
        resolved: false
      });
    }
  }

  observeOptions(editor: CodeIndentationEditor): void {
    const state = this.states.get(editor.document);
    if (state === undefined) {
      return;
    }
    if (configurationKey(state.configuration)
        !== configurationKey(this.host.getConfiguration(editor.document))) {
      this.states.delete(editor.document);
      return;
    }
    if (state.optionsKey !== optionsKey(editor.options)) {
      state.optionsKey = optionsKey(editor.options);
      state.options = resolvedOptions(editor.options);
      state.resolved = true;
    }
  }

  async ensure(editor: CodeIndentationEditor): Promise<boolean> {
    while (!this.disposed && !editor.document.isClosed) {
      const state = this.states.get(editor.document);
      const requestedVersion = state?.pending ? state.version : editor.document.version;
      const resolved = await this.ensureCurrent(editor);
      if (resolved || editor.document.version === requestedVersion) {
        return resolved;
      }
      // Only initial unresolved work is retried. A resolved/manual style never
      // re-enters detection merely because ordinary typing changes the source.
    }
    return false;
  }

  private async ensureCurrent(editor: CodeIndentationEditor): Promise<boolean> {
    if (this.disposed || editor.document.isClosed) {
      return false;
    }
    const configured = this.host.getConfiguration(editor.document);
    let existing = this.states.get(editor.document);
    if (existing && configurationKey(existing.configuration) !== configurationKey(configured)) {
      this.states.delete(editor.document);
      existing = undefined;
    }
    if (existing?.resolved) {
      return true;
    }
    if (existing?.pending) {
      return existing.pending;
    }
    const state: EditorIndentationState = {
      configuration: configured,
      version: editor.document.version,
      options: resolvedOptions(editor.options),
      optionsKey: optionsKey(editor.options),
      resolved: false
    };
    this.states.set(editor.document, state);
    if (!configured.detectIndentation) {
      const options = {
        tabSize: configured.tabSize,
        indentSize: configured.indentSize ?? configured.tabSize,
        insertSpaces: configured.insertSpaces
      };
      state.optionsKey = optionsKey(options);
      state.options = options;
      editor.options = options;
      state.resolved = true;
      return true;
    }
    state.pending = this.detect(editor, state)
      .catch(() => false)
      .finally(() => { state.pending = undefined; });
    return state.pending;
  }

  private async detect(editor: CodeIndentationEditor, state: EditorIndentationState): Promise<boolean> {
    const configured = state.configuration;
    const version = editor.document.version;
    const uri = editor.document.uri.toString();
    const result = await this.host.detect({
      textDocument: {
        uri,
        version
      },
      options: {
        tabSize: configured.tabSize,
        insertSpaces: configured.insertSpaces,
        ...(configured.indentSize === undefined ? {} : { indentSize: configured.indentSize })
      }
    });
    if (result === null || result.uri !== uri || result.version !== version
        || editor.document.version !== version || editor.document.isClosed
        || this.states.get(editor.document) !== state
        || configurationKey(this.host.getConfiguration(editor.document)) !== configurationKey(configured)) {
      return false;
    }
    if (state.resolved) {
      return true;
    }
    if (optionsKey(editor.options) !== state.optionsKey) {
      state.options = resolvedOptions(editor.options);
      state.resolved = true;
      return true;
    }
    const options = {
      tabSize: result.tabSize,
      indentSize: result.indentSize,
      insertSpaces: result.insertSpaces
    };
    state.optionsKey = optionsKey(options);
    state.options = options;
    editor.options = options;
    state.resolved = true;
    return true;
  }
}
