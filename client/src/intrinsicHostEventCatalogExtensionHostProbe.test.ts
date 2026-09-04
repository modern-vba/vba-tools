import test from 'node:test';
import assert from 'node:assert/strict';

import {
  IntrinsicHostEventCatalogExtensionHostProbe
} from './intrinsicHostEventCatalogExtensionHostProbe';

test('Extension Host catalog probe is inert without Test mode and both environment gates', () => {
  assert.equal(
    IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({}, true, true),
    undefined
  );
  assert.equal(
    IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({
      VBA_TOOLS_EXTENSION_HOST_TEST: '1'
    }, true, true),
    undefined
  );
  assert.equal(
    IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({
      VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted'
    }, true, true),
    undefined
  );
  assert.equal(
    IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({
      VBA_TOOLS_EXTENSION_HOST_TEST: '1',
      VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted'
    }, true, false),
    undefined
  );
});

test('Extension Host catalog probe exposes controlled trusted and actual untrusted test modes', () => {
  const trusted = IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({
    VBA_TOOLS_EXTENSION_HOST_TEST: '1',
    VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted'
  }, false, true);
  const untrusted = IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({
    VBA_TOOLS_EXTENSION_HOST_TEST: '1',
    VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'actual-untrusted'
  }, false, true);

  assert.ok(trusted);
  assert.equal(trusted.actualWorkspaceTrusted, false);
  assert.equal(trusted.effectiveWorkspaceTrusted, true);
  assert.ok(untrusted);
  assert.equal(untrusted.actualWorkspaceTrusted, false);
  assert.equal(untrusted.effectiveWorkspaceTrusted, false);
});

test('Extension Host companion probe releases a pending invocation when its process signal aborts', async () => {
  const probe = IntrinsicHostEventCatalogExtensionHostProbe.fromEnvironment({
    VBA_TOOLS_EXTENSION_HOST_TEST: '1',
    VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted',
    VBA_TOOLS_COMPANION_RESOLUTION_TEST: '1'
  }, true, true);
  assert.ok(probe);
  const controller = new AbortController();

  const invocation = probe.runCompanionProcess(
    String.raw`C:\vba-tools\vba-dev.exe`,
    ['capabilities', '--format', 'json'],
    controller.signal
  );
  assert.equal(
    probe.createCompanionApi().snapshot().pendingInvocationCount,
    1
  );

  controller.abort();

  await assert.rejects(invocation, { name: 'AbortError' });
  assert.equal(
    probe.createCompanionApi().snapshot().pendingInvocationCount,
    0
  );
});
