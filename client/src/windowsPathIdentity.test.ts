import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { relativeWindowsDescendantPath, windowsPathKey } from './windowsPathIdentity';

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
