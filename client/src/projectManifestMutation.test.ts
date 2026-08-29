import assert from 'node:assert/strict';
import test from 'node:test';
import * as path from 'node:path';

import {
  ProjectManifestDirtyPreflightChoice,
  ProjectManifestEditorSnapshot,
  ProjectManifestPreflightMismatchChoice,
  ProjectManifestRecoveryChoice,
  ProjectManifestMutationCoordinator,
  ProjectManifestMutationPorts
} from './projectManifestMutation';
import {
  CommandPaletteProjectTarget,
  CommandPaletteTarget
} from './commandPaletteTarget';

test('byte-identical manifest state requires no editor reconciliation', async () => {
  const bytes = Uint8Array.from([1, 2, 3]);
  const target = createTarget('C:\\work\\one\\vba-project.json');
  const reports: string[] = [];
  const ports = createPorts(target.project, bytes, reports);
  const coordinator = new ProjectManifestMutationCoordinator(ports);

  const result = await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => ({ exitCode: 0, cancelled: false, marker: 'operation-owned' })
  });

  assert.equal(result.status, 'completed');
  assert.equal(result.manifestOutcome, 'unchanged');
  assert.equal(result.coherence, 'notRequired');
  assert.equal(result.processResult?.marker, 'operation-owned');
  assert.deepEqual(reports, ['manifestUnchanged']);
});

test('canonical manifest aliases reject a same-window mutation as busy without queuing it', async () => {
  const harness = new MutationHarness();
  const firstTarget = createTarget('C:\\work\\one\\vba-project.json');
  const aliasTarget = createTarget('C:\\alias\\one\\vba-project.json');
  harness.identities.set(firstTarget.project.manifestPath, {
    canonicalPath: firstTarget.project.manifestPath,
    objectIdentity: 'volume-1:file-42'
  });
  harness.identities.set(aliasTarget.project.manifestPath, {
    canonicalPath: aliasTarget.project.manifestPath,
    objectIdentity: 'volume-1:file-42'
  });
  harness.setDisk(firstTarget, 'unchanged');
  harness.setDisk(aliasTarget, 'unchanged');
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  const release = deferred<void>();
  const started = deferred<void>();

  const first = coordinator.run({
    command: 'Reference Add',
    target: firstTarget,
    run: async () => {
      started.resolve();
      await release.promise;
      return { exitCode: 0, cancelled: false };
    }
  });
  await started.promise;
  const second = await coordinator.run({
    command: 'Reference Remove',
    target: aliasTarget,
    run: async () => {
      assert.fail('a busy same-manifest mutation must not be queued or launched');
    }
  });

  assert.deepEqual(second, { status: 'rejected', reason: 'busy' });
  assert.equal(harness.reports.at(-1)?.kind, 'busy');
  assert.equal(harness.reports.at(-1)?.runningCommand, 'Reference Add');
  release.resolve();
  await first;
});

test('different canonical manifests can mutate independently', async () => {
  const harness = new MutationHarness();
  const firstTarget = createTarget('C:\\work\\one\\vba-project.json');
  const secondTarget = createTarget('C:\\work\\two\\vba-project.json');
  harness.setDisk(firstTarget, 'one');
  harness.setDisk(secondTarget, 'two');
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  const release = deferred<void>();
  const started = deferred<void>();

  const first = coordinator.run({
    command: 'Reference Add',
    target: firstTarget,
    run: async () => {
      started.resolve();
      await release.promise;
      return { exitCode: 0, cancelled: false };
    }
  });
  await started.promise;
  const second = await coordinator.run({
    command: 'Common Module Update',
    target: secondTarget,
    run: async () => ({ exitCode: 0, cancelled: false })
  });

  assert.equal(second.status, 'completed');
  release.resolve();
  await first;
});

test('a dirty manifest saves only the selected buffer and revalidates its exact target', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\one\\vba-project.json');
  harness.setDisk(target, 'disk-before-save');
  harness.setBuffers(createBuffer(target, 'editor-change', 1, true));
  harness.dirtyChoices.push('saveAndContinue');
  harness.saveBehavior = async (selected) => {
    assert.equal(selected.filePath, target.project.manifestPath);
    harness.setDisk(target, selected.text);
    harness.setBuffers({ ...selected, revision: 2, isDirty: false });
    return true;
  };
  let launches = 0;

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.status, 'completed');
  assert.equal(launches, 1);
  assert.deepEqual(harness.savedBuffers.map((buffer) => buffer.bufferId), ['manifest-buffer']);
});

test('dirty Cancel and save failure paths launch no process', async () => {
  for (const scenario of ['cancel', 'false', 'throw'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\${scenario}\\vba-project.json`);
    harness.setDisk(target, 'disk');
    harness.setBuffers(createBuffer(target, 'dirty editor', 1, true));
    harness.dirtyChoices.push(scenario === 'cancel' ? 'cancel' : 'saveAndContinue');
    harness.saveBehavior = async () => {
      if (scenario === 'throw') {
        throw new Error('save failed');
      }
      return false;
    };
    let launches = 0;

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Common Module Add',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.deepEqual(result, { status: 'rejected', reason: 'preflight' });
    assert.equal(launches, 0, scenario);
  }
});

test('renewed dirty state or a content revision after save launches no process', async () => {
  for (const scenario of ['dirty', 'revision'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\${scenario}\\vba-project.json`);
    harness.setDisk(target, 'saved');
    harness.setBuffers(createBuffer(target, 'saved', 1, true));
    harness.dirtyChoices.push('saveAndContinue');
    harness.saveBehavior = async (selected) => {
      harness.setBuffers({ ...selected, revision: 2, isDirty: scenario === 'dirty' });
      return true;
    };
    if (scenario === 'revision') {
      harness.beforeGetBuffers = (call) => {
        if (call === 3) {
          harness.setBuffers(createBuffer(target, 'changed again', 3, false));
        }
      };
    }
    let launches = 0;

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Reference Remove',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.equal(result.status, 'rejected');
    assert.equal(launches, 0, scenario);
  }
});

test('a closed or replacement buffer instance after save launches no process', async () => {
  for (const scenario of ['closed', 'replacement'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\save-${scenario}\\vba-project.json`);
    harness.setDisk(target, 'before');
    harness.setBuffers(createBuffer(target, 'saved', 1, true, 'selected-buffer'));
    harness.dirtyChoices.push('saveAndContinue');
    harness.saveBehavior = async (selected) => {
      harness.setDisk(target, selected.text);
      harness.setBuffers(...(scenario === 'closed' ? [] : [{
        ...selected,
        bufferId: 'replacement-buffer',
        revision: 1,
        isDirty: false
      }]));
      return true;
    };
    let launches = 0;

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Reference Add',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.deepEqual(result, { status: 'rejected', reason: 'preflight' }, scenario);
    assert.equal(launches, 0, scenario);
  }
});

test('ambiguous, newly opened, or revised clean preflight buffers launch no process', async () => {
  for (const scenario of ['ambiguous', 'opened', 'revised'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\preflight-${scenario}\\vba-project.json`);
    harness.setDisk(target, 'disk');
    if (scenario === 'ambiguous') {
      harness.setBuffers(
        createBuffer(target, 'disk', 1, false, 'first-buffer'),
        createBuffer(target, 'disk', 1, false, 'second-buffer')
      );
    } else if (scenario === 'revised') {
      harness.setBuffers(createBuffer(target, 'disk', 1, false));
    }
    harness.beforeGetBuffers = (call) => {
      if (call === 2 && scenario === 'opened') {
        harness.setBuffers(createBuffer(target, 'disk', 1, false));
      }
      if (call === 2 && scenario === 'revised') {
        harness.setBuffers(createBuffer(target, 'disk', 2, false));
      }
    };
    let launches = 0;

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Common Module Update',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.deepEqual(result, { status: 'rejected', reason: 'preflight' }, scenario);
    assert.equal(launches, 0, scenario);
  }
});

test('initial and post-save untrusted disk projections launch no process', async () => {
  for (const phase of ['initial', 'postSave'] as const) {
    for (const scenario of ['missing', 'unreadable', 'unusable'] as const) {
      const harness = new MutationHarness();
      const target = createTarget(
        `C:\\work\\preflight-${phase}-${scenario}\\vba-project.json`
      );
      harness.setDisk(target, phase === 'initial' ? 'disk' : 'before');
      if (phase === 'postSave') {
        harness.setBuffers(createBuffer(target, 'saved', 1, true));
        harness.dirtyChoices.push('saveAndContinue');
        harness.saveBehavior = async (selected) => {
          harness.setDisk(target, selected.text);
          harness.setBuffers({ ...selected, revision: 2, isDirty: false });
          makeDiskUntrusted(harness, target, scenario);
          return true;
        };
      } else {
        makeDiskUntrusted(harness, target, scenario);
      }
      let launches = 0;

      const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
        command: 'Reference Remove',
        target,
        run: async () => {
          launches += 1;
          return { exitCode: 0, cancelled: false };
        }
      });

      assert.deepEqual(
        result,
        { status: 'rejected', reason: 'preflight' },
        `${phase}:${scenario}`
      );
      assert.equal(launches, 0, `${phase}:${scenario}`);
    }
  }
});

test('final pre-launch disk, target, and buffer races launch no process', async () => {
  for (const scenario of [
    'diskBytes',
    'missingDisk',
    'target',
    'bufferBeforeDisk',
    'bufferAfterDisk'
  ] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\launch-${scenario}\\vba-project.json`);
    harness.setDisk(target, 'disk');
    harness.beforeReadManifest = (call) => {
      if (call !== 2) {
        return;
      }
      if (scenario === 'diskBytes') {
        harness.setDisk(target, 'changed-before-launch');
      } else if (scenario === 'missingDisk') {
        harness.removeDisk(target.project.manifestPath);
      } else if (scenario === 'target') {
        harness.setProject(target.project.manifestPath, {
          ...target.project,
          projectName: 'Retargeted'
        });
      }
    };
    harness.beforeGetBuffers = (call) => {
      if (scenario === 'bufferBeforeDisk' && call === 3 ||
          scenario === 'bufferAfterDisk' && call === 4) {
        harness.setBuffers(createBuffer(target, 'disk', 1, false));
      }
    };
    let launches = 0;

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Common Module Update',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.deepEqual(result, { status: 'rejected', reason: 'preflight' }, scenario);
    assert.equal(launches, 0, scenario);
  }
});

test('a post-save target disappearance fails closed without silent retargeting', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\one\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'after', 1, true));
  harness.dirtyChoices.push('saveAndContinue');
  harness.saveBehavior = async (selected) => {
    harness.setDisk(target, selected.text);
    harness.setBuffers({ ...selected, revision: 2, isDirty: false });
    const changed = createTarget(target.project.manifestPath).project;
    harness.setProject(target.project.manifestPath, {
      ...changed,
      projectName: 'Retargeted'
    });
    return true;
  };
  let launches = 0;

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.status, 'rejected');
  assert.equal(launches, 0);
  assert.ok(harness.reports.some((report) => report.kind === 'preflightTargetChanged'));
});

test('a clean preflight mismatch compares immutable snapshots then cancels without post-mutation choices', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\one\\vba-project.json');
  harness.setDisk(target, 'disk snapshot');
  harness.setBuffers(createBuffer(target, 'clean editor snapshot', 1, false));
  harness.mismatchChoices.push('compare', 'cancel');
  harness.recoveryChoices.push('keepEditing');
  let launches = 0;

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.deepEqual(result, { status: 'rejected', reason: 'preflight' });
  assert.equal(launches, 0);
  assert.deepEqual(harness.comparisons, [{
    phase: 'preflight',
    bufferText: 'clean editor snapshot',
    diskText: 'disk snapshot'
  }]);
  assert.deepEqual(harness.recoveryChoices, ['keepEditing']);
});

test('a cancelled preflight mismatch clears after fresh clean editor and disk equality', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\preflight-equality\\vba-project.json');
  harness.setDisk(target, 'disk snapshot');
  harness.setBuffers(createBuffer(target, 'stale clean editor', 1, false));
  harness.mismatchChoices.push('cancel');
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);

  const cancelled = await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => assert.fail('the mismatched preflight must not launch')
  });
  assert.deepEqual(cancelled, { status: 'rejected', reason: 'preflight' });

  harness.setBuffers(createBuffer(target, 'disk snapshot', 2, false));
  let launches = 0;
  const retried = await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(retried.status, 'completed');
  assert.equal(launches, 1);
});

test('verified preflight focus and revert restarts preflight from matching disk', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\one\\vba-project.json');
  harness.setDisk(target, 'disk snapshot');
  harness.setBuffers(createBuffer(target, 'stale clean editor', 1, false));
  harness.mismatchChoices.push('reload');
  harness.confirmReload = true;
  harness.revertBehavior = async (selected) => {
    harness.setBuffers({
      ...selected,
      revision: 2,
      text: 'disk snapshot',
      isDirty: false
    });
  };
  let launches = 0;

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.status, 'completed');
  assert.equal(launches, 1);
  assert.ok(harness.reports.some((report) => report.kind === 'reloadCompleted'));
});

test('preflight reload focus failure warns and retains the no-launch divergence block', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\preflight-focus-failure\\vba-project.json');
  harness.setDisk(target, 'disk snapshot');
  harness.setBuffers(createBuffer(target, 'stale clean editor', 1, false));
  harness.mismatchChoices.push('reload');
  harness.confirmReload = true;
  harness.revealBehavior = async () => {
    throw new Error('the manifest closed before focus');
  };
  harness.revertBehavior = async () => {
    assert.fail('revert must not run after focus failure');
  };
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  let launches = 0;
  const request = () => coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  const first = await request();
  const second = await request();

  assert.deepEqual(first, { status: 'rejected', reason: 'preflight' });
  assert.deepEqual(second, { status: 'rejected', reason: 'divergence' });
  assert.equal(launches, 0);
  assert.ok(harness.reports.some((report) => report.kind === 'reloadRefused'));
});

test('raw-byte changed and unchanged outcomes remain independent from every process status', async () => {
  for (const changed of [false, true]) {
    for (const process of [
      { exitCode: 0, cancelled: false },
      { exitCode: 7, cancelled: false },
      { exitCode: 130, cancelled: true }
    ]) {
      const harness = new MutationHarness();
      const target = createTarget(
        `C:\\work\\raw-${changed ? 'changed' : 'unchanged'}-${process.exitCode}` +
        '\\vba-project.json'
      );
      harness.setRawDisk(target, [0x41]);

      const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
        command: 'Common Module Update',
        target,
        run: async () => {
          if (changed) {
            harness.setRawDisk(target, [0x41, 0x00]);
          }
          return {
            ...process,
            operationNoOp: 'owned by command schema',
            sourceEffects: 'owned by Common Modules'
          };
        }
      });

      assert.equal(result.manifestOutcome, changed ? 'changed' : 'unchanged');
      assert.equal(result.coherence, changed ? 'coherent' : 'notRequired');
      assert.equal(result.processResult?.operationNoOp, 'owned by command schema');
      assert.equal(result.processResult?.sourceEffects, 'owned by Common Modules');
      assert.equal(
        harness.reports.some((report) => report.kind === 'abnormalManifestChange'),
        changed && (process.exitCode !== 0 || process.cancelled)
      );
    }
  }
});

test('missing, unreadable, and structurally unusable post-exit manifests become persistent untrusted blocks', async () => {
  for (const scenario of ['missing', 'unreadable', 'unusable'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\${scenario}\\vba-project.json`);
    harness.setDisk(target, 'before');
    const coordinator = new ProjectManifestMutationCoordinator(harness.ports);

    const result = await coordinator.run({
      command: 'Reference Add',
      target,
      run: async () => {
        if (scenario === 'missing') {
          harness.removeDisk(target.project.manifestPath);
        } else if (scenario === 'unreadable') {
          harness.beforeReadManifest = (call) => {
            if (call >= 3) {
              const error = new Error('access denied') as NodeJS.ErrnoException;
              error.code = 'EACCES';
              throw error;
            }
          };
        } else {
          harness.setDisk(target, 'unusable');
          harness.setProject(target.project.manifestPath, undefined);
        }
        return { exitCode: 1, cancelled: false };
      }
    });
    let launches = 0;
    const blocked = await coordinator.run({
      command: 'Reference Remove',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.equal(result.manifestOutcome, 'untrusted');
    assert.equal(result.coherence, 'untrusted');
    assert.deepEqual(blocked, { status: 'rejected', reason: 'divergence' });
    assert.equal(launches, 0);
    assert.ok(harness.reports.some((report) => report.kind === 'manualRepairRequired'));
  }
});

test('manual repair clears an untrusted block only after fresh usable disk and editor equality', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\repair\\vba-project.json');
  harness.setDisk(target, 'before');
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.removeDisk(target.project.manifestPath);
      return { exitCode: 1, cancelled: false };
    }
  });
  harness.setDisk(target, 'manually repaired');
  harness.setBuffers(createBuffer(target, 'still different', 1, false));
  let launches = 0;

  const stillBlocked = await coordinator.run({
    command: 'Reference Remove',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });
  assert.equal(stillBlocked.status, 'rejected');
  harness.setBuffers(createBuffer(target, 'manually repaired', 2, false));
  const repaired = await coordinator.run({
    command: 'Reference Remove',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(repaired.status, 'completed');
  assert.equal(launches, 1);
});

test('one clean baseline-to-final transition is passive-safe', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\native\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'after');
      harness.setBuffers(createBuffer(target, 'after', 2, false));
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.coherence, 'coherent');
  assert.ok(!harness.reports.some((report) => report.kind === 'editorDivergence'));
});

test('intermediate content, dirty evidence, and Auto Save remain competing evidence', async () => {
  for (const scenario of ['cleanIntermediate', 'dirtyAutoSave'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\${scenario}\\vba-project.json`);
    harness.setDisk(target, 'before');
    harness.setBuffers(createBuffer(target, 'before', 1, false));

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Reference Add',
      target,
      run: async () => {
        harness.setDisk(target, 'after');
        harness.setBuffers(createBuffer(
          target,
          'user intermediate',
          2,
          scenario === 'dirtyAutoSave'
        ));
        harness.setBuffers(createBuffer(target, 'after', 3, false));
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.equal(result.coherence, 'diverged', scenario);
    assert.ok(harness.reports.some((report) => report.kind === 'editorDivergence'));
  }
});

test('Compare preserves the intermediate competing revision after a later clean final revision', async () => {
  for (const intermediateDirty of [false, true]) {
    const harness = new MutationHarness();
    const target = createTarget(
      `C:\\work\\preserved-${intermediateDirty}\\vba-project.json`
    );
    harness.setDisk(target, 'baseline');
    harness.setBuffers(createBuffer(target, 'baseline', 1, false));
    harness.recoveryChoices.push('compare');

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Reference Add',
      target,
      run: async () => {
        harness.setDisk(target, 'CLI snapshot');
        harness.setBuffers(createBuffer(
          target,
          'competing intermediate',
          2,
          intermediateDirty
        ));
        harness.setBuffers(createBuffer(target, 'CLI snapshot', 3, false));
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.equal(result.coherence, 'coherent', `${intermediateDirty}`);
    assert.deepEqual(harness.comparisons, [{
      phase: 'postMutation',
      bufferText: 'competing intermediate',
      diskText: 'CLI snapshot'
    }]);
  }
});

test('closing a buffer removes editor risk, while close and reopen is competing evidence', async () => {
  for (const reopen of [false, true]) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\close-${reopen}\\vba-project.json`);
    harness.setDisk(target, 'before');
    harness.setBuffers(createBuffer(target, 'before', 1, false));

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Common Module Update',
      target,
      run: async () => {
        harness.setDisk(target, 'after');
        harness.setBuffers();
        if (reopen) {
          harness.setBuffers(createBuffer(target, 'after', 1, false, 'reopened-buffer'));
        }
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.equal(result.coherence, reopen ? 'diverged' : 'coherent');
  }
});

test('native synchronization can converge before and exactly at the two-second boundary', async () => {
  for (const elapsed of [1_999, 2_000]) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\clock-${elapsed}\\vba-project.json`);
    harness.setDisk(target, 'before');
    harness.setBuffers(createBuffer(target, 'before', 1, false));
    const pending = new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Reference Add',
      target,
      run: async () => {
        harness.setDisk(target, 'after');
        return { exitCode: 0, cancelled: false };
      }
    });
    await waitFor(() => harness.clock.pendingTimers > 0);

    harness.clock.advance(elapsed);
    harness.setBuffers(createBuffer(target, 'after', 2, false));
    const result = await pending;

    assert.equal(result.coherence, 'coherent', `${elapsed}ms`);
  }
});

test('native synchronization observed after two seconds is not accepted as timely convergence', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\clock-late\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  const pending = new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'after');
      return { exitCode: 0, cancelled: false };
    }
  });
  await waitFor(() => harness.clock.pendingTimers > 0);

  harness.clock.advance(2_001);
  harness.setBuffers(createBuffer(target, 'after', 2, false));
  const result = await pending;

  assert.equal(result.coherence, 'diverged');
  assert.ok(harness.reports.some((report) => report.kind === 'coherenceTimeout'));
});

test('passive synchronization times out at two seconds without focus, save, or reload', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\timeout\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  const pending = new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'after');
      return { exitCode: 0, cancelled: false };
    }
  });
  await waitFor(() => harness.clock.pendingTimers > 0);

  harness.clock.advance(2_000);
  const result = await pending;

  assert.equal(result.coherence, 'diverged');
  assert.equal(harness.activeIdentity, undefined);
  assert.deepEqual(harness.savedBuffers, []);
  assert.ok(harness.reports.some((report) => report.kind === 'coherenceTimeout'));
});

test('a disk change after the immutable post-exit snapshot enters stale recovery', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\concurrent\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  harness.beforeReadManifest = (call) => {
    if (call === 4) {
      harness.setDisk(target, 'concurrent writer');
    }
  };

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Remove',
    target,
    run: async () => {
      harness.setDisk(target, 'cli snapshot');
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.coherence, 'diverged');
  assert.ok(harness.reports.some((report) => report.kind === 'concurrentDiskChange'));
});

test('Compare Changes receives immutable snapshots and does not mutate editor or disk', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\compare\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  harness.recoveryChoices.push('compare');

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'immutable CLI snapshot');
      harness.setBuffers(createBuffer(target, 'preserved user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.coherence, 'diverged');
  assert.deepEqual(harness.comparisons, [{
    phase: 'postMutation',
    bufferText: 'preserved user edit',
    diskText: 'immutable CLI snapshot'
  }]);
  assert.equal(harness.currentBuffers()[0]?.text, 'preserved user edit');
  assert.equal(harness.currentBuffers()[0]?.isDirty, true);
  assert.equal(harness.diskText(target.project.manifestPath), 'immutable CLI snapshot');
  assert.deepEqual(harness.savedBuffers, []);
});

test('Compare or cancelled Reload never authorizes a later un-warned Auto Save overwrite', async () => {
  for (const recovery of ['compare', 'reload'] as const) {
    const harness = new MutationHarness();
    const target = createTarget(
      `C:\\work\\unwarned-${recovery}\\vba-project.json`
    );
    harness.setDisk(target, 'before');
    harness.setBuffers(createBuffer(target, 'before', 1, false));
    harness.recoveryChoices.push(recovery);
    harness.confirmReload = false;
    const coordinator = new ProjectManifestMutationCoordinator(harness.ports);

    const first = await coordinator.run({
      command: 'Reference Add',
      target,
      run: async () => {
        harness.setDisk(target, 'CLI snapshot');
        harness.setBuffers(createBuffer(target, 'user edit', 2, true));
        return { exitCode: 0, cancelled: false };
      }
    });
    assert.equal(first.coherence, 'diverged', recovery);
    assert.equal(
      harness.reports.some((report) => report.kind === 'keepEditingWarning'),
      false,
      recovery
    );

    harness.setDisk(target, 'user edit');
    harness.setBuffers(createBuffer(target, 'user edit', 3, false));
    let launches = 0;
    const blocked = await coordinator.run({
      command: 'Reference Remove',
      target,
      run: async () => {
        launches += 1;
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.deepEqual(blocked, { status: 'rejected', reason: 'divergence' });
    assert.equal(launches, 0, recovery);
  }
});

test('confirmed recovery focuses and reverts the same buffer before clearing divergence', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\reload\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  harness.recoveryChoices.push('reload');
  harness.confirmReload = true;
  harness.revertBehavior = async (selected) => {
    harness.setBuffers({
      ...selected,
      revision: 3,
      text: 'CLI snapshot',
      isDirty: false
    });
  };

  const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
    command: 'Reference Remove',
    target,
    run: async () => {
      harness.setDisk(target, 'CLI snapshot');
      harness.setBuffers(createBuffer(target, 'user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.coherence, 'coherent');
  assert.equal(harness.currentBuffers()[0]?.bufferId, 'manifest-buffer');
  assert.equal(harness.currentBuffers()[0]?.text, 'CLI snapshot');
  assert.ok(harness.reports.some((report) => report.kind === 'reloadCompleted'));
});

test('recovery reload refuses stale disk, stale editor, wrong active editor, buffer replacement, and a narrow disk race', async () => {
  for (const scenario of [
    'staleDisk',
    'staleEditor',
    'focusFailure',
    'wrongActive',
    'activeChangedAfterRevert',
    'replacementBuffer',
    'narrowRace'
  ] as const) {
    const harness = new MutationHarness();
    const target = createTarget(`C:\\work\\reload-${scenario}\\vba-project.json`);
    harness.setDisk(target, 'before');
    harness.setBuffers(createBuffer(target, 'before', 1, false));
    harness.recoveryChoices.push('reload');
    harness.confirmReload = true;
    if (scenario === 'staleDisk') {
      harness.confirmReloadBehavior = async () => {
        harness.setDisk(target, 'writer after confirmation');
        return true;
      };
    }
    if (scenario === 'staleEditor') {
      harness.confirmReloadBehavior = async () => {
        harness.setBuffers(createBuffer(target, 'edit after confirmation', 3, true));
        return true;
      };
    }
    if (scenario === 'wrongActive') {
      harness.revealBehavior = async () => {
        harness.activeIdentity = { canonicalPath: 'C:\\work\\other.txt' };
      };
    }
    if (scenario === 'focusFailure') {
      harness.revealBehavior = async () => {
        throw new Error('the manifest closed before it could be focused');
      };
    }
    harness.revertBehavior = async (selected) => {
      if (scenario === 'focusFailure') {
        assert.fail('revert must not run when reveal/focus fails');
      }
      if (scenario === 'narrowRace') {
        harness.setDisk(target, 'narrow-race writer');
        harness.setBuffers({
          ...selected,
          revision: 3,
          text: 'narrow-race writer',
          isDirty: false
        });
        return;
      }
      harness.setBuffers({
        ...selected,
        bufferId: scenario === 'replacementBuffer' ? 'replacement' : selected.bufferId,
        revision: 3,
        text: 'CLI snapshot',
        isDirty: false
      });
      if (scenario === 'activeChangedAfterRevert') {
        harness.activeIdentity = { canonicalPath: 'C:\\work\\other-after-revert.txt' };
      }
    };

    const result = await new ProjectManifestMutationCoordinator(harness.ports).run({
      command: 'Reference Add',
      target,
      run: async () => {
        harness.setDisk(target, 'CLI snapshot');
        harness.setBuffers(createBuffer(target, 'user edit', 2, true));
        return { exitCode: 0, cancelled: false };
      }
    });

    assert.equal(result.coherence, 'diverged', scenario);
    assert.ok(harness.reports.some((report) => report.kind === 'reloadRefused'), scenario);
  }
});

test('Keep Editing warns without mutation and a later warned save can clear the guard', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\keep\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  harness.recoveryChoices.push('keepEditing');
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);

  const first = await coordinator.run({
    command: 'Common Module Update',
    target,
    run: async () => {
      harness.setDisk(target, 'CLI snapshot');
      harness.setBuffers(createBuffer(target, 'kept user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });
  assert.equal(first.coherence, 'diverged');
  assert.equal(harness.currentBuffers()[0]?.text, 'kept user edit');
  assert.deepEqual(harness.savedBuffers, []);
  assert.ok(harness.reports.some((report) => report.kind === 'keepEditingWarning'));

  harness.setDisk(target, 'kept user edit');
  harness.setBuffers(createBuffer(target, 'kept user edit', 3, false));
  let launches = 0;
  const second = await coordinator.run({
    command: 'Common Module Update',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(second.status, 'completed');
  assert.equal(launches, 1);
});

test('Auto Save equality without a prior recovery action does not erase competing evidence', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\autosave\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'CLI snapshot');
      harness.setBuffers(createBuffer(target, 'user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });
  harness.setDisk(target, 'user edit');
  harness.setBuffers(createBuffer(target, 'user edit', 3, false));
  let launches = 0;

  const blocked = await coordinator.run({
    command: 'Reference Remove',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.deepEqual(blocked, { status: 'rejected', reason: 'divergence' });
  assert.equal(launches, 0);
});

test('closing a diverged buffer after fresh usable disk clears the mutation guard', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\close-recovery\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'after');
      harness.setBuffers(createBuffer(target, 'user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });
  harness.setBuffers();
  let launches = 0;

  const result = await coordinator.run({
    command: 'Reference Remove',
    target,
    run: async () => {
      launches += 1;
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(result.status, 'completed');
  assert.equal(launches, 1);
});

test('read-only commands report an explicit disk basis only while divergence blocks mutation', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\disk-basis\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  assert.equal(await coordinator.reportReadOnlyDiskBasis({
    command: 'Reference List',
    target
  }), false);
  await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'after');
      harness.setBuffers(createBuffer(target, 'user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });

  assert.equal(await coordinator.reportReadOnlyDiskBasis({
    command: 'Reference List',
    target
  }), true);
  assert.equal(harness.reports.at(-1)?.kind, 'readOnlyDiskBasis');
});

test('divergence identity survives canonical aliases and atomic file-identity replacement', async () => {
  const harness = new MutationHarness();
  const canonical = createTarget('C:\\work\\identity\\vba-project.json');
  const alias = createTarget('C:\\alias\\identity\\vba-project.json');
  harness.identities.set(canonical.project.manifestPath, {
    canonicalPath: 'C:\\canonical\\identity\\vba-project.json',
    objectIdentity: 'file-before-replace'
  });
  harness.identities.set(alias.project.manifestPath, {
    canonicalPath: 'C:\\canonical\\identity\\vba-project.json',
    objectIdentity: 'file-before-replace'
  });
  harness.setDisk(canonical, 'before');
  harness.setDisk(alias, 'before');
  harness.setBuffers(createBuffer(canonical, 'before', 1, false));
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  await coordinator.run({
    command: 'Reference Add',
    target: canonical,
    run: async () => {
      harness.setDisk(canonical, 'after');
      harness.setBuffers(createBuffer(canonical, 'user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });
  assert.equal(await coordinator.reportReadOnlyDiskBasis({
    command: 'Reference List',
    target: alias
  }), true);

  harness.identities.set(alias.project.manifestPath, {
    canonicalPath: 'C:\\canonical\\identity\\vba-project.json',
    objectIdentity: 'file-after-atomic-replace'
  });
  assert.equal(await coordinator.reportReadOnlyDiskBasis({
    command: 'Common Module List',
    target: alias
  }), true);
});

test('concurrent attempts leaving recovery recheck and reserve the busy guard atomically', async () => {
  const harness = new MutationHarness();
  const target = createTarget('C:\\work\\recovery-race\\vba-project.json');
  harness.setDisk(target, 'before');
  harness.setBuffers(createBuffer(target, 'before', 1, false));
  const coordinator = new ProjectManifestMutationCoordinator(harness.ports);
  await coordinator.run({
    command: 'Reference Add',
    target,
    run: async () => {
      harness.setDisk(target, 'after');
      harness.setBuffers(createBuffer(target, 'user edit', 2, true));
      return { exitCode: 0, cancelled: false };
    }
  });
  harness.setBuffers();
  const release = deferred<void>();
  const started = deferred<void>();
  let launches = 0;
  const attempt = (command: string) => coordinator.run({
    command,
    target,
    run: async () => {
      launches += 1;
      started.resolve();
      await release.promise;
      return { exitCode: 0, cancelled: false };
    }
  });

  const left = attempt('Reference Remove');
  const right = attempt('Common Module Update');
  await started.promise;
  release.resolve();
  const results = await Promise.all([left, right]);

  assert.equal(launches, 1);
  assert.deepEqual(
    results.map((result) => result.status).sort(),
    ['completed', 'rejected']
  );
  assert.equal(results.find((result) => result.status === 'rejected')?.reason, 'busy');
});

function createTarget(manifestPath: string): CommandPaletteTarget {
  const projectRoot = path.win32.dirname(manifestPath);
  const sourceRoot = path.win32.join(projectRoot, 'src', 'Book1');
  return {
    project: {
      projectRoot,
      manifestPath,
      projectName: 'One',
      primaryDocument: 'Book1',
      documents: [{
        name: 'Book1',
        sourcePath: 'src/Book1',
        sourceRoot,
        sourceRootIdentity: { canonicalPath: sourceRoot }
      }]
    },
    document: {
      name: 'Book1',
      sourcePath: 'src/Book1',
      sourceRoot,
      sourceRootIdentity: { canonicalPath: sourceRoot }
    }
  };
}

function createBuffer(
  target: CommandPaletteTarget,
  text: string,
  revision: number,
  isDirty: boolean,
  bufferId = 'manifest-buffer'
): ProjectManifestEditorSnapshot {
  return {
    filePath: target.project.manifestPath,
    bufferId,
    revision,
    text,
    isDirty
  };
}

function makeDiskUntrusted(
  harness: MutationHarness,
  target: CommandPaletteTarget,
  scenario: 'missing' | 'unreadable' | 'unusable'
): void {
  if (scenario === 'missing') {
    harness.removeDisk(target.project.manifestPath);
    return;
  }
  if (scenario === 'unreadable') {
    harness.beforeReadManifest = () => {
      const error = new Error('access denied') as NodeJS.ErrnoException;
      error.code = 'EACCES';
      throw error;
    };
    return;
  }
  harness.setProject(target.project.manifestPath, undefined);
}

class MutationHarness {
  public readonly identities = new Map<string, {
    canonicalPath: string;
    objectIdentity?: string;
  }>();
  public readonly reports: Array<{ kind: string; runningCommand?: string }> = [];
  public readonly savedBuffers: ProjectManifestEditorSnapshot[] = [];
  public readonly comparisons: Array<{
    phase: string;
    bufferText: string;
    diskText: string;
  }> = [];
  public readonly dirtyChoices: ProjectManifestDirtyPreflightChoice[] = [];
  public readonly mismatchChoices: ProjectManifestPreflightMismatchChoice[] = [];
  public readonly recoveryChoices: ProjectManifestRecoveryChoice[] = [];
  public readonly clock = new FakeClock();
  public readonly ports: ProjectManifestMutationPorts;
  private readonly disk = new Map<string, Uint8Array>();
  private readonly projects = new Map<string, CommandPaletteProjectTarget>();
  private readonly observers = new Set<
    (buffers: readonly ProjectManifestEditorSnapshot[]) => void
  >();
  private buffers: ProjectManifestEditorSnapshot[] = [];
  private getBuffersCount = 0;
  private readManifestCount = 0;
  public beforeGetBuffers: ((call: number) => void) | undefined;
  public beforeReadManifest: ((call: number, manifestPath: string) => void) | undefined;
  public saveBehavior: (
    buffer: ProjectManifestEditorSnapshot
  ) => Promise<boolean> = async () => false;
  public confirmReload = false;
  public confirmReloadBehavior: (() => Promise<boolean>) | undefined;
  public activeIdentity: { canonicalPath: string; objectIdentity?: string } | undefined;
  public revertBehavior: (
    buffer: ProjectManifestEditorSnapshot
  ) => Promise<void> = async () => undefined;
  public revealBehavior: ((
    buffer: ProjectManifestEditorSnapshot
  ) => Promise<void>) | undefined;

  public constructor() {
    this.ports = {
      resolvePathIdentity: async (filePath) =>
        this.identities.get(filePath) ?? { canonicalPath: filePath },
      readManifestBytes: async (manifestPath) => {
        this.readManifestCount += 1;
        this.beforeReadManifest?.(this.readManifestCount, manifestPath);
        const bytes = this.disk.get(manifestPath);
        if (bytes === undefined) {
          throw new Error(`missing manifest: ${manifestPath}`);
        }
        return Uint8Array.from(bytes);
      },
      decodeManifestBytes: (bytes) => new TextDecoder().decode(bytes),
      loadProjectTarget: async (manifestPath) => this.projects.get(manifestPath),
      getOpenBuffers: async () => {
        this.getBuffersCount += 1;
        this.beforeGetBuffers?.(this.getBuffersCount);
        return this.buffers.map((buffer) => ({ ...buffer }));
      },
      observeBuffers: (_identity, listener) => {
        this.observers.add(listener);
        return { dispose: () => this.observers.delete(listener) };
      },
      saveBuffer: async (buffer) => {
        this.savedBuffers.push({ ...buffer });
        return this.saveBehavior(buffer);
      },
      chooseDirtyPreflight: async () => this.dirtyChoices.shift() ?? 'cancel',
      choosePreflightMismatch: async () => this.mismatchChoices.shift() ?? 'cancel',
      chooseRecovery: async () => this.recoveryChoices.shift(),
      showComparison: async (comparison) => {
        this.comparisons.push({
          phase: comparison.phase,
          bufferText: comparison.buffer.text,
          diskText: comparison.disk.text
        });
      },
      confirmReload: async () => this.confirmReloadBehavior === undefined
        ? this.confirmReload
        : this.confirmReloadBehavior(),
      revealAndFocus: async (buffer) => {
        if (this.revealBehavior !== undefined) {
          await this.revealBehavior(buffer);
          return;
        }
        this.activeIdentity = this.identities.get(buffer.filePath) ?? {
          canonicalPath: buffer.filePath
        };
      },
      getActiveFileIdentity: async () => this.activeIdentity,
      revertBuffer: async (buffer) => this.revertBehavior(buffer),
      clock: this.clock,
      report: (report) => this.reports.push(report)
    };
  }

  public setDisk(target: CommandPaletteTarget, text: string): void {
    this.disk.set(target.project.manifestPath, new TextEncoder().encode(text));
    this.projects.set(target.project.manifestPath, target.project);
  }

  public setRawDisk(target: CommandPaletteTarget, bytes: readonly number[]): void {
    this.disk.set(target.project.manifestPath, Uint8Array.from(bytes));
    this.projects.set(target.project.manifestPath, target.project);
  }

  public setProject(
    manifestPath: string,
    project: CommandPaletteProjectTarget | undefined
  ): void {
    if (project === undefined) {
      this.projects.delete(manifestPath);
    } else {
      this.projects.set(manifestPath, project);
    }
  }

  public removeDisk(manifestPath: string): void {
    this.disk.delete(manifestPath);
  }

  public setBuffers(...buffers: ProjectManifestEditorSnapshot[]): void {
    this.buffers = buffers.map((buffer) => ({ ...buffer }));
    const snapshot = this.buffers.map((buffer) => ({ ...buffer }));
    for (const observer of this.observers) {
      observer(snapshot);
    }
  }

  public currentBuffers(): readonly ProjectManifestEditorSnapshot[] {
    return this.buffers.map((buffer) => ({ ...buffer }));
  }

  public diskText(manifestPath: string): string | undefined {
    const bytes = this.disk.get(manifestPath);
    return bytes === undefined ? undefined : new TextDecoder().decode(bytes);
  }
}

class FakeClock {
  private current = 0;
  private readonly timers: Array<{
    due: number;
    resolve(): void;
  }> = [];

  public now = (): number => this.current;

  public wait = (milliseconds: number): Promise<void> => new Promise((resolve) => {
    this.timers.push({ due: this.current + milliseconds, resolve });
  });

  public advance(milliseconds: number): void {
    this.current += milliseconds;
    const ready = this.timers.filter((timer) => timer.due <= this.current);
    for (const timer of ready) {
      this.timers.splice(this.timers.indexOf(timer), 1);
      timer.resolve();
    }
  }

  public get pendingTimers(): number {
    return this.timers.length;
  }
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
} {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolver) => {
    resolve = resolver;
  });
  return { promise, resolve };
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (predicate()) {
      return;
    }
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  assert.fail('condition did not become true');
}

function createPorts(
  project: CommandPaletteProjectTarget,
  bytes: Uint8Array,
  reports: string[]
): ProjectManifestMutationPorts {
  return {
    resolvePathIdentity: async (filePath) => ({ canonicalPath: filePath }),
    readManifestBytes: async () => bytes,
    decodeManifestBytes: () => '{"manifest":true}',
    loadProjectTarget: async () => project,
    getOpenBuffers: async () => [],
    observeBuffers: () => ({ dispose() {} }),
    saveBuffer: async () => false,
    chooseDirtyPreflight: async () => 'cancel',
    choosePreflightMismatch: async () => 'cancel',
    chooseRecovery: async () => undefined,
    showComparison: async () => undefined,
    confirmReload: async () => false,
    revealAndFocus: async () => undefined,
    getActiveFileIdentity: async () => undefined,
    revertBuffer: async () => undefined,
    clock: {
      now: () => 0,
      wait: async () => undefined
    },
    report: (report) => reports.push(report.kind)
  };
}
