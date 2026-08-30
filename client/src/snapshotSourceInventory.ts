import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

export interface SnapshotSourceTextDocument {
  readonly uriScheme: string;
  readonly uriPath?: string | undefined;
  readonly fileName: string;
  readonly isDirty: boolean;
  readonly encoding: string;
  getText(): string;
}

export interface SnapshotSourceInventoryHost {
  getActiveWindowsCodePage(): number | Promise<number>;
  getOpenTextDocuments(): readonly SnapshotSourceTextDocument[];
  findSourceFiles(sourceSetPath: string): Promise<readonly string[]>;
  readFile(filePath: string): Promise<Uint8Array>;
  encodeText(text: string, encoding: string): Promise<Uint8Array>;
  decodeText(bytes: Uint8Array, encoding: string): Promise<string>;
}

export interface SnapshotSourceInventoryEntry {
  readonly relativePath: string;
  readonly sourceUri?: string | undefined;
  readonly encoding?: string | undefined;
  readonly bytes: Uint8Array;
}

export interface SnapshotSourceInventory {
  readonly sourceSetPath: string;
  readonly activeWindowsCodePage: number;
  readonly entries: readonly SnapshotSourceInventoryEntry[];
}

export interface SnapshotCaptureCancellationToken {
  readonly isCancellationRequested: boolean;
}

export interface CallerOwnedSourceSnapshotHost {
  createTemporaryDirectory(): Promise<string>;
  createDirectory(directoryPath: string): Promise<void>;
  writeFile(filePath: string, bytes: Uint8Array): Promise<void>;
  removeDirectory(directoryPath: string): Promise<void>;
  wait(milliseconds: number): Promise<void>;
}

export interface CallerOwnedSourceSnapshotCleanupResult {
  readonly retainedPath?: string | undefined;
}

export interface MaterializedCallerOwnedSourceSnapshot {
  readonly directoryPath: string;
  cleanup(): Promise<CallerOwnedSourceSnapshotCleanupResult>;
}

export type SnapshotSourceInventoryCapture = (
  sourceSetPath: string,
  cancellationToken?: SnapshotCaptureCancellationToken | undefined
) => Promise<SnapshotSourceInventory>;

export type CallerOwnedSourceSnapshotCapture = (
  sourceSetPath: string,
  cancellationToken?: SnapshotCaptureCancellationToken | undefined
) => Promise<MaterializedCallerOwnedSourceSnapshot>;

interface CapturedDirtySource {
  readonly filePath: string;
  readonly text: string;
  readonly encoding: string;
}

interface CapturedCleanSource {
  readonly bytes: Uint8Array;
  readonly encoding?: string | undefined;
}

export async function captureSnapshotSourceInventory(
  sourceSetPath: string,
  host: SnapshotSourceInventoryHost,
  cancellationToken: SnapshotCaptureCancellationToken = uncancelledSnapshotCaptureToken
): Promise<SnapshotSourceInventory> {
  throwIfSnapshotCaptureCancelled(cancellationToken);
  const resolvedSourceSetPath = path.resolve(sourceSetPath);
  const openTextDocuments = [...host.getOpenTextDocuments()];
  const dirtySources = captureDirtySources(
    resolvedSourceSetPath,
    openTextDocuments);
  throwIfSnapshotCaptureCancelled(cancellationToken);
  const [activeWindowsCodePage, capturedInventoriedPaths] = await Promise.all([
    Promise.resolve().then(() => host.getActiveWindowsCodePage()),
    Promise.resolve().then(() => host.findSourceFiles(resolvedSourceSetPath))
  ]);
  throwIfSnapshotCaptureCancelled(cancellationToken);
  const inventoriedPaths = [...capturedInventoriedPaths];
  const entriesByPath = new Map<string, SnapshotSourceInventoryEntry>();
  const dirtySourcesByPath = new Map(
    dirtySources.map((source) => [canonicalPath(source.filePath), source]));

  for (const filePath of inventoriedPaths) {
    throwIfSnapshotCaptureCancelled(cancellationToken);
    const relativePath = sourceRelativePath(resolvedSourceSetPath, filePath);
    const key = canonicalPath(filePath);
    if (entriesByPath.has(key)) {
      throw new Error(`Snapshot source inventory contains a duplicate path: ${filePath}`);
    }

    const dirtySource = dirtySourcesByPath.get(key);
    const cleanSource = dirtySource === undefined
      ? await readCleanSource(host, filePath, activeWindowsCodePage)
      : undefined;
    const bytes = dirtySource === undefined
      ? cleanSource!.bytes
      : await encodeDirtySource(host, dirtySource, activeWindowsCodePage);
    throwIfSnapshotCaptureCancelled(cancellationToken);
    entriesByPath.set(
      key,
      dirtySource === undefined
        ? freezeEntry(
            relativePath,
            bytes,
            cleanSource!.encoding === undefined ? undefined : pathToFileURL(filePath).href,
            cleanSource!.encoding
          )
        : freezeEntry(
            relativePath,
            bytes,
            pathToFileURL(dirtySource.filePath).href,
            canonicalTransportEncoding(dirtySource.encoding, activeWindowsCodePage)
          )
    );
    dirtySourcesByPath.delete(key);
  }

  for (const dirtySource of dirtySourcesByPath.values()) {
    throwIfSnapshotCaptureCancelled(cancellationToken);
    const relativePath = sourceRelativePath(resolvedSourceSetPath, dirtySource.filePath);
    const bytes = await encodeDirtySource(host, dirtySource, activeWindowsCodePage);
    throwIfSnapshotCaptureCancelled(cancellationToken);
    entriesByPath.set(
      canonicalPath(dirtySource.filePath),
      freezeEntry(
        relativePath,
        bytes,
        pathToFileURL(dirtySource.filePath).href,
        canonicalTransportEncoding(dirtySource.encoding, activeWindowsCodePage)
      ));
  }

  const entries = [...entriesByPath.values()]
    .sort((left, right) => compareOrdinal(
      canonicalRelativePath(left.relativePath),
      canonicalRelativePath(right.relativePath)));
  throwIfSnapshotCaptureCancelled(cancellationToken);
  return Object.freeze({
    sourceSetPath: resolvedSourceSetPath,
    activeWindowsCodePage,
    entries: Object.freeze(entries)
  });
}

export function createCallerOwnedSourceSnapshotCapture(
  captureSourceInventory: SnapshotSourceInventoryCapture,
  host: CallerOwnedSourceSnapshotHost
): CallerOwnedSourceSnapshotCapture {
  return async (
    sourceSetPath,
    cancellationToken = uncancelledSnapshotCaptureToken
  ) => materializeSnapshotSourceInventory(
    await captureSourceInventory(sourceSetPath, cancellationToken),
    host,
    cancellationToken);
}

export async function materializeSnapshotSourceInventory(
  inventory: SnapshotSourceInventory,
  host: CallerOwnedSourceSnapshotHost,
  cancellationToken: SnapshotCaptureCancellationToken = uncancelledSnapshotCaptureToken
): Promise<MaterializedCallerOwnedSourceSnapshot> {
  throwIfSnapshotCaptureCancelled(cancellationToken);
  const directoryPath = path.resolve(await host.createTemporaryDirectory());
  let cleanupResult: CallerOwnedSourceSnapshotCleanupResult | undefined;
  const cleanup = async (): Promise<CallerOwnedSourceSnapshotCleanupResult> => {
    cleanupResult ??= await removeSnapshotDirectoryWithRetries(directoryPath, host);
    return cleanupResult;
  };

  try {
    throwIfSnapshotCaptureCancelled(cancellationToken);
    const createdDirectories = new Set<string>();
    await createDirectoryOnce(directoryPath, host, createdDirectories);
    throwIfSnapshotCaptureCancelled(cancellationToken);
    for (const entry of inventory.entries) {
      throwIfSnapshotCaptureCancelled(cancellationToken);
      const filePath = resolveSnapshotEntryPath(directoryPath, entry.relativePath);
      await createDirectoryOnce(path.dirname(filePath), host, createdDirectories);
      throwIfSnapshotCaptureCancelled(cancellationToken);
      await host.writeFile(filePath, Uint8Array.from(entry.bytes));
      throwIfSnapshotCaptureCancelled(cancellationToken);
    }
  } catch (error) {
    const retained = await cleanup();
    const retainedMessage = retained.retainedPath === undefined
      ? ''
      : ` Retained snapshot directory: ${retained.retainedPath}`;
    throw new Error(
      `Could not materialize the caller-owned VBA source snapshot: ${errorMessage(error)}.${retainedMessage}`);
  }

  return Object.freeze({
    directoryPath,
    cleanup
  });
}

const uncancelledSnapshotCaptureToken: SnapshotCaptureCancellationToken = Object.freeze({
  isCancellationRequested: false
});

function throwIfSnapshotCaptureCancelled(
  cancellationToken: SnapshotCaptureCancellationToken
): void {
  if (cancellationToken.isCancellationRequested) {
    throw new Error('VBA source snapshot capture was cancelled.');
  }
}

async function createDirectoryOnce(
  directoryPath: string,
  host: CallerOwnedSourceSnapshotHost,
  createdDirectories: Set<string>
): Promise<void> {
  const key = canonicalPath(directoryPath);
  if (createdDirectories.has(key)) {
    return;
  }
  await host.createDirectory(directoryPath);
  createdDirectories.add(key);
}

function resolveSnapshotEntryPath(directoryPath: string, relativePath: string): string {
  if (relativePath.length === 0 || path.isAbsolute(relativePath)) {
    throw new Error(`Snapshot entry path must be relative: ${relativePath}`);
  }
  const filePath = path.resolve(directoryPath, relativePath);
  if (!isPathWithin(filePath, directoryPath)) {
    throw new Error(`Snapshot entry path escapes its caller-owned directory: ${relativePath}`);
  }
  return filePath;
}

async function removeSnapshotDirectoryWithRetries(
  directoryPath: string,
  host: CallerOwnedSourceSnapshotHost
): Promise<CallerOwnedSourceSnapshotCleanupResult> {
  const retryDelays = [25, 100] as const;
  for (let attempt = 0; attempt <= retryDelays.length; attempt += 1) {
    try {
      await host.removeDirectory(directoryPath);
      return Object.freeze({});
    } catch {
      const delay = retryDelays[attempt];
      if (delay !== undefined) {
        await host.wait(delay);
      }
    }
  }
  return Object.freeze({ retainedPath: directoryPath });
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function readCleanSource(
  host: SnapshotSourceInventoryHost,
  filePath: string,
  activeWindowsCodePage: number
): Promise<CapturedCleanSource> {
  const bytes = Uint8Array.from(await host.readFile(filePath));
  if (path.extname(filePath).toLowerCase() === '.frx') {
    return { bytes };
  }

  const bom = detectBom(bytes);
  if (bom === 'utf32le' || bom === 'utf32be') {
    throw new Error(`Clean VBA source uses unsupported UTF-32 encoding: ${filePath}`);
  }
  if (bom !== undefined) {
    const encoding = bom === 'utf8' ? 'utf8bom' : bom;
    if (await hasExactRoundTrip(host, bytes, encoding)) {
      return { bytes, encoding };
    }
    throw new Error(`Clean VBA source could not round-trip its recognized ${encoding} bytes: ${filePath}`);
  }

  if (await hasExactRoundTrip(host, bytes, 'utf8')) {
    return { bytes, encoding: 'utf8' };
  }

  const activeEncoding = activeCodePageEditorEncoding(activeWindowsCodePage);
  if (
    activeEncoding !== undefined
    && activeEncoding !== 'utf8'
    && await hasExactRoundTrip(host, bytes, activeEncoding)
  ) {
    return { bytes, encoding: `windows-${activeWindowsCodePage}` };
  }

  throw new Error(`Clean VBA source could not round-trip its original bytes: ${filePath}`);
}

async function hasExactRoundTrip(
  host: SnapshotSourceInventoryHost,
  bytes: Uint8Array,
  encoding: string
): Promise<boolean> {
  try {
    const text = await host.decodeText(bytes, encoding);
    const reencoded = await host.encodeText(text, encoding);
    return sameBytes(bytes, reencoded);
  } catch {
    return false;
  }
}

function activeCodePageEditorEncoding(activeWindowsCodePage: number): string | undefined {
  if (activeWindowsCodePage === 65001) {
    return 'utf8';
  }
  for (const [encoding, codePage] of legacyEditorCodePages) {
    if (codePage === activeWindowsCodePage) {
      return encoding;
    }
  }
  return undefined;
}

function captureDirtySources(
  sourceSetPath: string,
  documents: readonly SnapshotSourceTextDocument[]
): CapturedDirtySource[] {
  const sources: CapturedDirtySource[] = [];
  for (const document of documents) {
    const uriScheme = document.uriScheme;
    const uriPath = document.uriPath;
    const fileName = document.fileName;
    const isDirty = document.isDirty;
    const encoding = document.encoding;
    if (!isDirty || !isExportedVbaSource(fileName)) {
      continue;
    }
    if (uriScheme !== 'file' || uriPath === undefined) {
      if (isPathWithin(fileName, sourceSetPath)) {
        throw new Error(
          `A dirty VBA source must be saved under the selected source set before it can participate: ${fileName}`);
      }
      continue;
    }
    if (!isPathWithin(uriPath, sourceSetPath)) {
      continue;
    }

    sources.push(Object.freeze({
      filePath: path.resolve(uriPath),
      text: document.getText(),
      encoding
    }));
  }
  return sources;
}

async function encodeDirtySource(
  host: SnapshotSourceInventoryHost,
  source: CapturedDirtySource,
  activeWindowsCodePage: number
): Promise<Uint8Array> {
  validateDirtyEditorEncoding(source, activeWindowsCodePage);
  const bytes = Uint8Array.from(await host.encodeText(source.text, source.encoding));
  validateDirtyEncodingBytes(source, bytes);
  const decoded = await host.decodeText(bytes, source.encoding);
  const reencoded = Uint8Array.from(await host.encodeText(decoded, source.encoding));
  if (decoded !== source.text || !sameBytes(bytes, reencoded)) {
    throw new Error(
      `Dirty VBA source cannot round-trip exactly through editor encoding '${source.encoding}': ${source.filePath}`);
  }
  return bytes;
}

function validateDirtyEncodingBytes(
  source: CapturedDirtySource,
  bytes: Uint8Array
): void {
  const encoding = source.encoding.toLowerCase();
  const bom = detectBom(bytes);
  if (encoding === 'utf8' && bom !== undefined) {
    throw new Error(
      `Dirty VBA source editor encoding 'utf8' must not include a BOM: ${source.filePath}`);
  }
  if (encoding === 'utf8bom' && bom !== 'utf8') {
    throw new Error(
      `Dirty VBA source editor encoding 'utf8bom' requires a UTF-8 BOM: ${source.filePath}`);
  }
  if (encoding === 'utf16le' && bom !== 'utf16le') {
    throw new Error(
      `Dirty VBA source editor encoding 'utf16le' requires a UTF-16 LE BOM: ${source.filePath}`);
  }
  if (encoding === 'utf16be' && bom !== 'utf16be') {
    throw new Error(
      `Dirty VBA source editor encoding 'utf16be' requires a UTF-16 BE BOM: ${source.filePath}`);
  }
  if (legacyEditorCodePages.has(encoding) && bom !== undefined) {
    throw new Error(
      `Dirty VBA source legacy editor encoding '${source.encoding}' must not include a BOM: ${source.filePath}`);
  }
}

function detectBom(
  bytes: Uint8Array
): 'utf8' | 'utf16le' | 'utf16be' | 'utf32le' | 'utf32be' | undefined {
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    return 'utf8';
  }
  if (bytes[0] === 0xff && bytes[1] === 0xfe && bytes[2] === 0x00 && bytes[3] === 0x00) {
    return 'utf32le';
  }
  if (bytes[0] === 0x00 && bytes[1] === 0x00 && bytes[2] === 0xfe && bytes[3] === 0xff) {
    return 'utf32be';
  }
  if (bytes[0] === 0xff && bytes[1] === 0xfe) {
    return 'utf16le';
  }
  if (bytes[0] === 0xfe && bytes[1] === 0xff) {
    return 'utf16be';
  }
  return undefined;
}

function validateDirtyEditorEncoding(
  source: CapturedDirtySource,
  activeWindowsCodePage: number
): void {
  const encoding = source.encoding.toLowerCase();
  if (
    encoding === 'utf8'
    || encoding === 'utf8bom'
    || encoding === 'utf16le'
    || encoding === 'utf16be'
  ) {
    return;
  }

  const editorCodePage = legacyEditorCodePages.get(encoding);
  if (editorCodePage === undefined) {
    throw new Error(
      `Dirty VBA source uses unsupported editor encoding '${source.encoding}': ${source.filePath}`);
  }
  if (editorCodePage !== activeWindowsCodePage) {
    throw new Error(
      `Dirty VBA source editor encoding '${source.encoding}' does not match active Windows code page ${activeWindowsCodePage}: ${source.filePath}`);
  }
}

const legacyEditorCodePages = new Map<string, number>([
  ['windows874', 874],
  ['shiftjis', 932],
  ['gbk', 936],
  ['euckr', 949],
  ['cp950', 950],
  ['windows1250', 1250],
  ['windows1251', 1251],
  ['windows1252', 1252],
  ['windows1253', 1253],
  ['windows1254', 1254],
  ['windows1255', 1255],
  ['windows1256', 1256],
  ['windows1257', 1257],
  ['windows1258', 1258]
]);

function freezeEntry(
  relativePath: string,
  bytes: Uint8Array,
  sourceUri?: string,
  encoding?: string
): SnapshotSourceInventoryEntry {
  return Object.freeze({
    relativePath,
    ...(sourceUri === undefined ? {} : { sourceUri }),
    ...(encoding === undefined ? {} : { encoding }),
    bytes: Uint8Array.from(bytes)
  });
}

function canonicalTransportEncoding(
  editorEncoding: string,
  activeWindowsCodePage: number
): string {
  const encoding = editorEncoding.toLowerCase();
  if (
    encoding === 'utf8' ||
    encoding === 'utf8bom' ||
    encoding === 'utf16le' ||
    encoding === 'utf16be'
  ) {
    return encoding;
  }

  const codePage = legacyEditorCodePages.get(encoding);
  if (codePage === undefined || codePage !== activeWindowsCodePage) {
    throw new Error(
      `Dirty VBA source editor encoding '${editorEncoding}' cannot be transported for active Windows code page ${activeWindowsCodePage}.`
    );
  }
  return `windows-${codePage}`;
}

function sourceRelativePath(sourceSetPath: string, filePath: string): string {
  const resolvedFilePath = path.resolve(filePath);
  if (!isPathWithin(resolvedFilePath, sourceSetPath)) {
    throw new Error(`Snapshot source path is outside the selected source set: ${filePath}`);
  }
  return path.relative(sourceSetPath, resolvedFilePath);
}

function isPathWithin(filePath: string, directoryPath: string): boolean {
  const relativePath = path.relative(path.resolve(directoryPath), path.resolve(filePath));
  return relativePath.length > 0
    && !relativePath.startsWith(`..${path.sep}`)
    && relativePath !== '..'
    && !path.isAbsolute(relativePath);
}

function isExportedVbaSource(filePath: string): boolean {
  const extension = path.extname(filePath).toLowerCase();
  return extension === '.bas' || extension === '.cls' || extension === '.frm';
}

function canonicalPath(filePath: string): string {
  return path.normalize(path.resolve(filePath)).toLowerCase();
}

function canonicalRelativePath(relativePath: string): string {
  return path.normalize(relativePath).toLowerCase();
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
  return left.length === right.length
    && left.every((value, index) => value === right[index]);
}
