import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, stat, writeFile } from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  commands, extensions, TabInputText, Uri,
  version as vscodeVersion, window, workspace
} from 'vscode';
import {
  previewSemanticTokensProbe, PreviewTokenResponse
} from '../../previewSemanticTokensProbe';
import {
  assertVisibleSemanticName, validatePreviewTokenResponse
} from '../../previewPerformanceEvidence';
import { collectFreshPreviewOracle } from './previewPerformanceOracle';
import { capturePreviewRenderer } from './previewRendererObservation';
import {
  readPreviewSchedulerEvidence, waitForReferenceCatalogSettlement
} from './previewSchedulerEvidence';

interface LifecycleEvent {
  readonly kind: 'open' | 'close';
  readonly uri: string;
  readonly version: number;
  readonly at: number;
  readonly openVbaDocuments: readonly string[];
}

interface PreviewSample {
  readonly index: number;
  readonly file: string;
  readonly beforeOpenCount: number;
  readonly actionStartedAt: number;
  readonly milliseconds: number;
  readonly isPreview: boolean;
  readonly response: PreviewTokenResponse;
  readonly lifecycle: readonly LifecycleEvent[];
  readonly renderer?: unknown;
}

export async function runPreviewPerformanceMeasurement(): Promise<void> {
  const projectRoot = requiredEnvironment('VBA_TOOLS_PREVIEW_PROJECT');
  const resultDirectory = requiredEnvironment('VBA_TOOLS_PREVIEW_RESULT_DIRECTORY');
  const sourceRoot = path.join(projectRoot, 'src', 'CommonModules');
  const probe = previewSemanticTokensProbe;
  assert.ok(probe, 'Natural semantic-token provider observation must be enabled.');
  const events: LifecycleEvent[] = [];
  const observe = (kind: LifecycleEvent['kind'], document: {
    readonly languageId: string; readonly uri: Uri; readonly version: number;
  }) => {
    if (document.languageId === 'vba') {
      events.push({ kind, uri: document.uri.toString(), version: document.version,
        at: Date.now(), openVbaDocuments: openVbaDocuments() });
    }
  };
  const subscriptions = [
    workspace.onDidOpenTextDocument(document => observe('open', document)),
    workspace.onDidCloseTextDocument(document => observe('close', document))
  ];
  const samples: PreviewSample[] = [];
  const sourceTexts = new Map<string, string>();
  await mkdir(resultDirectory, { recursive: true });
  try {
    const extension = extensions.getExtension('modern-vba.vba-tools');
    assert.ok(extension);
    await extension.activate();
    await commands.executeCommand('notifications.clearAll');
    await commands.executeCommand('workbench.action.closeAllEditors');
    assert.equal(openVbaDocuments().length, 0);

    const openPreview = async (file: string, index: number): Promise<PreviewSample> => {
      const uri = Uri.file(path.join(sourceRoot, file));
      // Reveal only selects the real Explorer item; its native list preview action
      // starts the timed interval and causes all subsequent editor work.
      await commands.executeCommand('revealInExplorer', uri);
      const checkpoint = probe.responses.length;
      const eventCheckpoint = events.length;
      const beforeOpenCount = openVbaDocuments().length;
      const actionStartedAt = Date.now();
      await commands.executeCommand('list.selectAndPreserveFocus');
      const response = await untilValue(() => probe.responses.slice(checkpoint)
        .find(candidate => candidate.uri === uri.toString()), 90_000,
      `natural provider response for ${file}`);
      const tab = window.tabGroups.activeTabGroup.tabs.find(candidate =>
        candidate.input instanceof TabInputText
          && candidate.input.uri.toString() === uri.toString());
      assert.ok(tab, 'The Explorer action must create a real editor tab.');
      assert.equal(tab.isPreview, true, 'The measured editor must remain a preview.');
      const document = workspace.textDocuments.find(candidate =>
        candidate.uri.toString() === uri.toString());
      assert.ok(document);
      sourceTexts.set(uri.toString(), document.getText());
      const sample = {
        index, file, beforeOpenCount, actionStartedAt,
        milliseconds: response.completedAt - actionStartedAt,
        isPreview: tab.isPreview, response,
        lifecycle: events.slice(eventCheckpoint),
        ...(process.env.VBA_TOOLS_PREVIEW_RENDERER_PORT === undefined ? {} : {
          renderer: await capturePreviewRenderer(
            Number(process.env.VBA_TOOLS_PREVIEW_RENDERER_PORT),
            path.join(resultDirectory, index === -3 ? 'setup-large-class.png'
              : index === -2 ? 'setup-initial.png'
              : index === -1 ? 'setup-reference-ready.png'
                : `preview-${String(index + 1).padStart(2, '0')}.png`)
          )
        })
      };
      if (sample.renderer !== undefined) {
        assert.ok(sample.renderer.dom.activeTab.includes(file));
        const proof = assertVisibleSemanticName(document.getText(), response.data,
          sample.renderer.dom.lines.flatMap(line => line.spans));
        Object.assign(sample.renderer, { visibleSemanticName: proof });
      }
      console.log(`${index < 0 ? 'SETUP' : `PREVIEW ${index + 1}`} ${file}: `
        + `${sample.milliseconds} ms; beforeOpen=${beforeOpenCount}; `
        + `lifecycle=${sample.lifecycle.map(event => event.kind).join(',')}`);
      return sample;
    };

    // Only the two representative largest sources are individually tokenized
    // during preparation. Eight measured peers remain unvisited.
    const initialPreparation = await openPreview('Lib_Common.bas', -2);
    await waitForReferenceCatalogSettlement(path.join(resultDirectory, 'scheduler'));
    const referencePreparationSettledAt = Date.now();
    // Reference publication may replace the initial inventory after the first
    // provider result. Revisit this same setup source through normal Explorer
    // navigation to prepare its tokens against the final reference inputs.
    await commands.executeCommand('workbench.action.closeAllEditors');
    await untilValue(() => openVbaDocuments().length === 0 ? true : undefined,
      15_000, 'preparation source close after reference settlement');
    const setup = await openPreview('Lib_Common.bas', -1);
    const largeSourcePreparation = await openPreview('WorksheetService.cls', -3);
    const selectedFiles = [
      'WorksheetService.cls', 'Lib_Common.bas', 'WorkbookService.cls',
      'Lib_FileSystem.bas', 'ObjectList.cls', 'WorksheetRangeBounds.cls',
      'Lib_UnitTest.bas', 'FileSystemService.cls', 'Fx_Common.bas', 'ObjectSet.cls'
    ];
    for (let index = 0; index < 20; index++) {
      if (index % 2 === 0) {
        await commands.executeCommand('workbench.action.closeAllEditors');
        await untilValue(() => openVbaDocuments().length === 0 ? true : undefined,
          15_000, 'actual last VBA document close');
      }
      samples.push(await openPreview(selectedFiles[index % selectedFiles.length], index));
      await writeFile(path.join(resultDirectory, 'samples.partial.json'),
        JSON.stringify({ initialPreparation, setup, largeSourcePreparation,
          samples, lifecycle: events }, undefined, 2) + '\n');
    }

    // Fresh-process oracle starts after every measured sample. It cannot warm
    // the provider/server used by the Explorer measurement.
    const oracle = await collectFreshPreviewOracle(
      path.join(extension.extensionPath, 'bin', 'vba-language-server', 'win-x64',
        'vba-language-server.exe'),
      sourceTexts,
      path.join(resultDirectory, 'oracle')
    );
    for (const sample of samples) {
      const expected = oracle.get(sample.response.uri);
      assert.ok(expected);
      validatePreviewTokenResponse(sample.response, expected);
    }
    const preparedOracle = oracle.get(setup.response.uri);
    assert.ok(preparedOracle);
    validatePreviewTokenResponse(setup.response, preparedOracle);
    const preparedClassOracle = oracle.get(largeSourcePreparation.response.uri);
    assert.ok(preparedClassOracle);
    validatePreviewTokenResponse(largeSourcePreparation.response, preparedClassOracle);
    const corpusFiles = await enumerateSources(sourceRoot);
    const corpus = await Promise.all(corpusFiles.map(async file => {
      const bytes = await readFile(file);
      return { file, bytes: bytes.length, sha256: hash(bytes),
        lines: bytes.reduce((count, byte) => count + (byte === 10 ? 1 : 0),
          bytes.length > 0 && bytes.at(-1) !== 10 ? 1 : 0) };
    }));
    const report = {
      scenario: 'Actual VS Code Explorer tree selection and native preview action',
      commands: ['revealInExplorer (untimed selection)', 'list.selectAndPreserveFocus (timed)'],
      thresholdMilliseconds: 1000,
      allSamplesWithinBudget: samples.every(sample => sample.milliseconds <= 1000),
      sampleCount: samples.length,
      initialPreparation,
      finalReferencePreparation: setup,
      largeSourcePreparation,
      referencePreparationSettledAt,
      referencePreparationPolicy: 'Before measurement only: every admitted catalog job completes and no reference-catalog event occurs for 2,000 ms; same Lib_Common source revisited for final-reference tokens; no validation wait',
      measuredTargetPrewarming: ['Lib_Common.bas', 'WorksheetService.cls'],
      oracle: 'Separate fresh server after all samples; exact full-token arrays and UTF-8 open-text SHA-256 compared',
      correctness: 'all exact full-token arrays and accepted source revisions match',
      samples, lifecycle: events,
      schedulerEvidence: await readPreviewSchedulerEvidence(path.join(resultDirectory, 'scheduler')),
      runtime: { operatingSystem: `${os.type()} ${os.release()}`, architecture: os.arch(),
        cpu: os.cpus()[0]?.model, logicalProcessors: os.cpus().length,
        totalMemoryBytes: os.totalmem(), freeMemoryBytes: os.freemem(),
        vscodeVersion, node: process.versions.node, electron: process.versions.electron },
      corpus: { projectRoot, sourceDocuments: corpus.length,
        physicalLines: corpus.reduce((total, file) => total + file.lines, 0),
        sourceBytes: corpus.reduce((total, file) => total + file.bytes, 0), files: corpus },
      renderer: {
        providerBoundary: 'Natural full semantic-token result returned to VS Code; no explicit provider command',
        paintInterval: 'Exact paint completion unmeasured; read-only CDP DOM and screenshot captured after two animation frames without further input',
        semanticThemeRule: '*:vba foreground #01ff87 in isolated test profile only',
        visibleHighlighting: 'Every sample asserts a provider-classified visible identifier has rgb(1,255,135), with matching active tab, screenshot and DOM; no further input'
      },
      validation: 'Normal diagnostics remain enabled; measurement does not wait for project validation',
      cacheState: 'One initial project preparation; repeated actual zero-document retirement; no anchor or pinned tabs',
      aggregation: '20 individual samples; no excluded outliers; no timed warmups'
    };
    await writeFile(path.join(resultDirectory, 'report.json'), JSON.stringify(report, undefined, 2) + '\n');
    assert.equal(samples.length, 20);
    assert.ok(samples.some(sample => sample.beforeOpenCount === 0));
    assert.ok(samples.some(sample => sample.beforeOpenCount === 1));
    assert.ok(report.allSamplesWithinBudget,
      'Every resident-cache preview operation must finish within 1,000 ms.');
  } catch (error) {
    await writeFile(path.join(resultDirectory, 'failure.json'), JSON.stringify({
      error: error instanceof Error ? error.stack : String(error), samples, lifecycle: events
    }, undefined, 2) + '\n');
    throw error;
  } finally {
    for (const subscription of subscriptions) { subscription.dispose(); }
  }
}

function openVbaDocuments(): string[] {
  return workspace.textDocuments.filter(document =>
    document.languageId === 'vba' && !document.isClosed).map(document => document.uri.toString());
}

async function untilValue<T>(read: () => T | undefined, timeout: number, phase: string): Promise<T> {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const value = read();
    if (value !== undefined) { return value; }
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  throw new Error(`Timed out waiting for ${phase}.`);
}

async function enumerateSources(directory: string): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) { result.push(...await enumerateSources(file)); }
    else if (/\.(?:bas|cls|frm)$/iu.test(file)) { result.push(file); }
  }
  return result.sort();
}

function requiredEnvironment(name: string): string {
  const value = process.env[name];
  assert.ok(value, `${name} must be configured.`);
  return value;
}

function hash(value: string | Buffer): string {
  return createHash('sha256').update(value).digest('hex');
}
