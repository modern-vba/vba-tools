import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { readFileSync } from 'node:fs';
import { relativeSourceDescendantPath, relativeWindowsDescendantPath, sourcePathIdentity,
  tryParseSourceUri, tryParseWindowsSourceUri, windowsPathKey } from './windowsPathIdentity';

const corpus = JSON.parse(readFileSync(path.resolve(__dirname, '..', '..', 'fixtures', 'source-identity', 'cases.json'), 'utf8')) as {
  schemaVersion: number;
  cases: Array<{ id: string; uri: string; windows: { accepted: boolean; path?: string; group?: string };
    posix?: { accepted: boolean; path?: string; group?: string } }>;
};
assert.equal(corpus.schemaVersion, 1);
for (const fixture of corpus.cases) {
  test('Windows source URI conformance: ' + fixture.id, () => {
    const source = tryParseWindowsSourceUri(fixture.uri);
    assert.equal(source !== undefined, fixture.windows.accepted);
    if (source !== undefined) {
      assert.equal(source.originalUri, fixture.uri);
      assert.equal(source.filePath, fixture.windows.path);
      assert.equal(source.isWindowsPath, true);
      assert.equal(Object.isFrozen(source), true);
    }
  });
}

for (const fixture of corpus.cases) {
  if (fixture.posix === undefined) continue;
  test('native POSIX source URI conformance: ' + fixture.id, () => {
    const source = tryParseSourceUri(fixture.uri, 'posix');
    assert.equal(source !== undefined, fixture.posix!.accepted);
    if (source !== undefined) {
      assert.equal(source.originalUri, fixture.uri);
      assert.equal(source.filePath, fixture.posix!.path);
      assert.equal(source.identity, sourcePathIdentity(fixture.posix!.path!));
    }
  });
}

test('native POSIX source URI admission requires an original absolute slash path', () => {
  assert.equal(tryParseSourceUri(String.raw`file:\\\tmp\Source\A.bas`, 'posix'), undefined);
});

test('source URI authorities use the shared explicit character rules without Unicode whitespace folding', () => {
  assert.ok(tryParseWindowsSourceUri('file://server\u00a0name/share/A.bas'));
  assert.equal(tryParseWindowsSourceUri('file://server\u007fname/share/A.bas'), undefined);
});

test('Windows source URI equivalence follows corpus groups without folding distinct names', () => {
  const admitted = corpus.cases.filter(fixture => fixture.windows.accepted).map(fixture => ({
    fixture, source: tryParseWindowsSourceUri(fixture.uri)!
  }));
  for (const left of admitted) {
    for (const right of admitted) {
      assert.equal(left.source.identity === right.source.identity,
        left.fixture.windows.group === right.fixture.windows.group,
        left.fixture.id + ' compared with ' + right.fixture.id);
    }
  }
});

test('native POSIX source paths retain ordinal casing and literal backslashes', () => {
  assert.notEqual(sourcePathIdentity('/tmp/Σ.bas'), sourcePathIdentity('/tmp/ς.bas'));
  assert.notEqual(sourcePathIdentity('/tmp/Source/Nested\\A.bas'), sourcePathIdentity('/tmp/Source/Nested/A.bas'));
  assert.equal(relativeSourceDescendantPath('/tmp/Source', '/tmp/Source/Nested\\A.bas'), 'Nested\\A.bas');
  assert.equal(relativeSourceDescendantPath('/tmp/Source/Nested', '/tmp/Source/Nested\\A.bas'), undefined);
  assert.equal(relativeSourceDescendantPath('/tmp/Source', '/tmp/source/A.bas'), undefined);
  assert.equal(relativeSourceDescendantPath('/tmp/Source', '/tmp/Source/../A.bas'), undefined);
});

test('Windows lexical descendants compare whole ordinal segments and preserve the candidate suffix', () => {
  for (const [root, candidate, suffix] of [
    ['C:\\Σ\\Book', 'c:/ς/book/MiXeD/Module.bas', ['MiXeD', 'Module.bas']],
    ['C:\\Before\\..\\µ', 'C:\\Μ\\Nested\\.\\Module.bas', ['Nested', 'Module.bas']],
    ['C:\\', 'c:\\Folder\\Module.bas', ['Folder', 'Module.bas']],
    ['\\\\Σ\\Share\\Source', '\\\\ς\\share\\source\\Folder\\Module.bas', ['Folder', 'Module.bas']],
    ['\\\\Server\\Share\\', '\\\\server\\share\\Module.bas', ['Module.bas']],
    ['C:\\𐐀', 'C:\\𐐨\\Module.bas', ['Module.bas']]
  ] as const) {
    assert.equal(relativeWindowsDescendantPath(root, candidate), suffix.join(path.sep));
  }
});

test('Windows lexical descendants reject equality, siblings, other roots, escapes, and ordinal-distinct names', () => {
  for (const [root, candidate] of [
    ['C:\\Source', 'c:\\source\\.'],
    ['C:\\Source', 'C:\\Sources\\Module.bas'],
    ['C:\\Source', 'D:\\Source\\Module.bas'],
    ['C:\\Source', 'C:\\Source\\..\\Outside\\Module.bas'],
    ['\\\\Server\\Share\\Source', '\\\\Server\\Other\\Source\\Module.bas'],
    ['\\\\Server\\Share', '\\\\Elsewhere\\Share\\Module.bas'],
    ['C:\\K', 'C:\\K\\Module.bas'],
    ['C:\\é', 'C:\\e\u0301\\Module.bas']
  ]) {
    assert.equal(relativeWindowsDescendantPath(root, candidate), undefined);
  }
});

test('Windows lexical identity normalizes separators and dot segments without Unicode normalization', () => {
  assert.equal(windowsPathKey('C:\\Σ\\.\\Folder\\..\\µ.bas'), windowsPathKey('c:/ς/Μ.BAS'));
  assert.equal(windowsPathKey('\\\\Σ\\Share\\Folder\\'), windowsPathKey('\\\\ς\\share\\folder'));
  assert.notEqual(windowsPathKey('C:\\K.bas'), windowsPathKey('C:\\K.bas'));
  assert.notEqual(windowsPathKey('C:\\é.bas'), windowsPathKey('C:\\e\u0301.bas'));
});
