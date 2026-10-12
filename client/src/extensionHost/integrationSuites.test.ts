import test from 'node:test';
import assert from 'node:assert/strict';
import { parseExtensionHostTestProfile, runExtensionHostIntegrationSuites } from './integrationSuites';

test('ordinary Extension Host verification runs editor checks without touching Excel prerequisites', async () => {
  const completed = await runExtensionHostIntegrationSuites('ordinary', [
    { name: 'editor diagnostics', requiresExcel: false, run: async () => {} },
    { name: 'native workbook diagnostics', requiresExcel: true, run: async () => {
      throw new Error('Microsoft Excel executable was not found in the registered App Paths entries.');
    } }
  ]);
  assert.deepEqual(completed, ['editor diagnostics']);
});

test('the explicit Windows Excel profile runs native checks independently from ordinary editor checks', async () => {
  const completed = await runExtensionHostIntegrationSuites('windows-excel', [
    { name: 'editor diagnostics', requiresExcel: false, run: async () => {
      throw new Error('Ordinary profile must not be repeated by the native profile.');
    } },
    { name: 'native workbook diagnostics', requiresExcel: true, run: async () => {} }
  ]);
  assert.deepEqual(completed, ['native workbook diagnostics']);
});

test('the native profile requires the explicit --windows-excel runner argument', () => {
  assert.equal(parseExtensionHostTestProfile([]), 'ordinary');
  assert.equal(parseExtensionHostTestProfile(['--windows-excel']), 'windows-excel');
});

test('invalid or duplicated profile arguments fail instead of silently omitting native coverage', () => {
  for (const args of [['--windows-excel=false'], ['--windows-excel', '--windows-excel'], ['--unknown']]) {
    assert.throws(() => parseExtensionHostTestProfile(args), /accepts only --windows-excel/);
  }
});

test('native prerequisite failures propagate as failures, never as successful skipped coverage', async () => {
  await assert.rejects(runExtensionHostIntegrationSuites('windows-excel', [
    { name: 'native workbook diagnostics', requiresExcel: true, run: async () => {
      throw new Error('Microsoft Excel executable was not found.');
    } }
  ]), /Microsoft Excel executable was not found/);
});
