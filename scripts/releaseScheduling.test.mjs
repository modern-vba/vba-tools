import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import yaml from 'js-yaml';

const load = async (name) => yaml.load(await readFile(`.github/workflows/${name}.yml`, 'utf8'));
const normalize = (condition) => condition?.replaceAll('github.event_name', 'inputs.release_event').replace(/\s+/g, ' ').trim();

test('scheduler probe retains the production dependency graph and conditions', async () => {
  const release = await load('release');
  const probe = await load('release-scheduling-probe');
  for (const [name, job] of Object.entries(release.jobs)) {
    assert.deepEqual(probe.jobs[name].needs, job.needs, `${name} dependencies`);
    if (name === 'validate') {
      assert.equal(job.if, undefined);
      assert.equal(probe.jobs[name].if, "inputs.blocked_job != 'validate'");
    } else if (name === 'publish') {
      assert.equal(normalize(probe.jobs[name].if),
        `inputs.blocked_job != 'publish' && (${normalize(job.if)})`);
    } else {
      assert.equal(normalize(probe.jobs[name].if), normalize(job.if), `${name} condition`);
    }
    assert.equal(job['continue-on-error'], undefined, `${name} production failure is authoritative`);
  }
  assert.equal(probe.jobs.legacy_finalize.if, undefined);
  assert.deepEqual(probe.jobs.legacy_finalize.needs, release.jobs.finalize.needs);
});

test('scheduling probes have no publication, identity, checkout or write surface', async () => {
  const probe = await load('release-scheduling-probe');
  const caller = await load('release-scheduling-validation');
  assert.deepEqual(probe.permissions, {});
  assert.deepEqual(probe.on, { workflow_call: { inputs: {
    release_event: { required: true, type: 'string' },
    blocked_job: { default: 'none', type: 'string' },
    failure_job: { default: 'none', type: 'string' }
  } } });
  for (const job of Object.values(probe.jobs)) {
    assert.equal(job.environment, undefined);
    assert.equal(job.permissions, undefined);
    assert.equal(job['continue-on-error'], undefined);
    assert.equal(job['timeout-minutes'], 2);
    for (const step of job.steps) {
      assert.equal(step.uses, undefined);
      assert.doesNotMatch(step.run, /\b(?:gh|az|vsce|curl|wget|fetch)\b|secrets\.|github\.token/);
    }
  }
  assert.deepEqual(caller.permissions, { contents: 'read' });
  assert.deepEqual(caller.on, { pull_request: {
    branches: ['main'],
    paths: ['.github/workflows/release*.yml', 'scripts/releaseScheduling.test.mjs']
  } });
  assert.deepEqual(caller.jobs.scheduler.permissions, {});
  assert.equal(caller.jobs.scheduler.secrets, undefined);
  assert.equal(caller.jobs.scheduler.uses, './.github/workflows/release-scheduling-probe.yml');
  assert.deepEqual(caller.jobs.scheduler.strategy.matrix.include, [
    { release_event: 'push', blocked_job: 'none', failure_job: 'none' },
    { release_event: 'workflow_dispatch', blocked_job: 'none', failure_job: 'none' },
    { release_event: 'push', blocked_job: 'validate', failure_job: 'none' },
    { release_event: 'workflow_dispatch', blocked_job: 'publish', failure_job: 'none' }
  ]);
});

test('finalization admits only successful prerequisites and a non-cancelled run', async () => {
  const { jobs: { finalize } } = await load('release');
  assert.equal(typeof finalize.if, 'string', 'An explicit status guard is required');
  assert.match(finalize.if, /!cancelled\(\)/);
  assert.doesNotMatch(finalize.if, /always\(\)|success\(\)/);
  const statuses = ['success', 'failure', 'cancelled', 'skipped', 'unknown', ''];
  // Typed Boolean/string evaluation of the actual condition, not an emulator
  // of GitHub's implicit ancestor scheduling. The hosted probe covers that.
  for (const cancelled of [false, true]) {
    for (const validation of statuses) {
      for (const publication of statuses) {
        const allowed = runInNewContext(finalize.if, {
          cancelled: () => cancelled,
          needs: { validate: { result: validation }, publish: { result: publication } }
        }, { timeout: 100 });
        assert.equal(allowed, !cancelled && validation === 'success' && publication === 'success',
          JSON.stringify({ cancelled, validation, publication }));
      }
    }
  }
});
