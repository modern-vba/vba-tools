import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { resolveSnapshotProviders } from './snapshotProviders';

test('Debug accepts source-workbook preparation without an independent snapshot Build-analysis feature', async () => {
  const cliCapabilities = {
    toolVersion: '0.1.1', contractVersion: '1.0', activeWindowsCodePage: 1252,
    featureVersions: {
      'build.sourceSnapshot': '2.0',
      'debug.sourceWorkbookPreparation': '1.0',
      'invocation.stdinCancellation': '1.0',
      'invocation.stdinWorkbookConfirmation': '1.0',
      'sourceSnapshot.activeWindowsCodePage': '1.0'
    },
    commands: { 'prepare-debug': { outputSchemaVersion: '1.0' } }
  };
  const adapterCapabilities = {
    toolVersion: '0.1.1', contractVersion: '1.0', protocolVersion: '2.0',
    transports: ['stdio'], sessionIdFormat: 'lowercase-hex-32',
    commands: ['cleanup', 'doctor'], commandSchemaVersions: { doctor: '1.0' },
    featureVersions: { 'doctor.stdinCancellation': '1.0', 'debug.sourceWorkbook': '1.0' },
    requiredVbaDevFeatureVersions: {
      'build.sourceSnapshot': '2.0',
      'debug.sourceWorkbookPreparation': '1.0',
      'invocation.stdinCancellation': '1.0',
      'invocation.stdinWorkbookConfirmation': '1.0',
      'sourceSnapshot.activeWindowsCodePage': '1.0'
    }
  };

  const providers = await resolveSnapshotProviders({
    extensionRoot: path.resolve('extension-root'),
    requiredContract: {
      contractVersion: '1.0', featureVersions: {
        ...cliCapabilities.featureVersions,
        'build.sourceSnapshotAnalysis': '1.0', 'test.sourceSnapshot': '2.0'
      },
      commandSchemaVersions: { 'prepare-debug': '1.0', build: '3.0', test: '1.2' }
    },
    requiredDebugAdapterContract: adapterCapabilities,
    vbaDevResolver: { resolve: async () => ({
      executablePath: path.resolve('vba-dev.exe'), bundledPath: path.resolve('vba-dev.exe'),
      source: 'bundled', capabilities: cliCapabilities
    }) },
    vbaDebugAdapterResolver: { resolve: async () => ({
      executablePath: path.resolve('vba-debug-adapter.exe'), capabilities: adapterCapabilities
    }) },
    capabilitiesProcess: async () => ({ stdout: JSON.stringify(cliCapabilities), stderr: '' })
  });

  assert.equal(providers.vbaDev.capabilities.featureVersions?.['debug.sourceWorkbookPreparation'], '1.0');
  assert.equal(providers.adapter.capabilities.featureVersions['debug.sourceWorkbook'], '1.0');
});

test('Test snapshot admission still requires the CLI Test snapshot feature', async () => {
  const cliCapabilities = {
    toolVersion: '0.1.1', contractVersion: '1.0', activeWindowsCodePage: 1252,
    featureVersions: {
      'build.sourceSnapshot': '2.0',
      'build.sourceSnapshotAnalysis': '1.0',
      'sourceSnapshot.activeWindowsCodePage': '1.0'
    },
    commands: {}
  };
  const adapterCapabilities = {
    toolVersion: '0.1.1', contractVersion: '1.0', protocolVersion: '2.0',
    transports: ['stdio'], sessionIdFormat: 'lowercase-hex-32',
    commands: ['cleanup', 'doctor'], commandSchemaVersions: { doctor: '1.0' },
    featureVersions: { 'doctor.stdinCancellation': '1.0', 'debug.sourceWorkbook': '1.0' },
    requiredVbaDevFeatureVersions: {
      'build.sourceSnapshot': '2.0',
      'debug.sourceWorkbookPreparation': '1.0',
      'invocation.stdinCancellation': '1.0',
      'invocation.stdinWorkbookConfirmation': '1.0',
      'sourceSnapshot.activeWindowsCodePage': '1.0'
    }
  };

  await assert.rejects(resolveSnapshotProviders({
    purpose: 'test',
    extensionRoot: path.resolve('extension-root'),
    requiredContract: {
      contractVersion: '1.0', featureVersions: cliCapabilities.featureVersions,
      commandSchemaVersions: {}
    },
    requiredDebugAdapterContract: adapterCapabilities,
    vbaDevResolver: { resolve: async () => ({
      executablePath: path.resolve('vba-dev.exe'), bundledPath: path.resolve('vba-dev.exe'),
      source: 'bundled', capabilities: cliCapabilities
    }) },
    vbaDebugAdapterResolver: { resolve: async () => ({
      executablePath: path.resolve('vba-debug-adapter.exe'), capabilities: adapterCapabilities
    }) },
    capabilitiesProcess: async () => ({ stdout: JSON.stringify(cliCapabilities), stderr: '' })
  }), /Snapshot schema 2/);
});

test('Test rejects an injected adapter that lacks the declared source-workbook dependency map', async () => {
  const cliCapabilities = {
    toolVersion: '0.1.1', contractVersion: '1.0', activeWindowsCodePage: 1252,
    featureVersions: {
      'build.sourceSnapshot': '2.0', 'build.sourceSnapshotAnalysis': '1.0',
      'test.sourceSnapshot': '2.0', 'sourceSnapshot.activeWindowsCodePage': '1.0'
    },
    commands: { test: { outputSchemaVersion: '1.2' } }
  };
  const adapterContract = {
    contractVersion: '1.0', protocolVersion: '2.0', transports: ['stdio'],
    sessionIdFormat: 'lowercase-hex-32', commands: ['cleanup', 'doctor'],
    commandSchemaVersions: { doctor: '1.0' },
    featureVersions: { 'doctor.stdinCancellation': '1.0', 'debug.sourceWorkbook': '1.0' },
    requiredVbaDevFeatureVersions: {
      'build.sourceSnapshot': '2.0', 'debug.sourceWorkbookPreparation': '1.0',
      'invocation.stdinCancellation': '1.0', 'invocation.stdinWorkbookConfirmation': '1.0',
      'sourceSnapshot.activeWindowsCodePage': '1.0'
    }
  };
  const staleAdapter = {
    toolVersion: '0.1.1', ...adapterContract,
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '2.0' }
  };

  await assert.rejects(resolveSnapshotProviders({
    purpose: 'test', extensionRoot: path.resolve('extension-root'),
    requiredContract: {
      contractVersion: '1.0', featureVersions: cliCapabilities.featureVersions,
      commandSchemaVersions: { test: '1.2' }
    },
    requiredDebugAdapterContract: adapterContract,
    vbaDevResolver: { resolve: async () => ({
      executablePath: path.resolve('vba-dev.exe'), bundledPath: path.resolve('vba-dev.exe'),
      source: 'bundled', capabilities: cliCapabilities
    }) },
    vbaDebugAdapterResolver: { resolve: async () => ({
      executablePath: path.resolve('vba-debug-adapter.exe'), capabilities: staleAdapter
    }) },
    capabilitiesProcess: async () => ({ stdout: JSON.stringify(cliCapabilities), stderr: '' })
  }), /Snapshot schema 2/);
});
