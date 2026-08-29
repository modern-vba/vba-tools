import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  type DoctorCommandOptions,
  FirstRunDoctorPromptState,
  runDoctorCommand,
  promptForFirstRunDoctor
} from './doctorCommand';
import { VbaDebugAdapterCompatibilityError } from './debugAdapter';
import { VbaDevCompatibilityError } from './devtool';

test('Doctor command validates the CLI and invokes doctor with an explicit project root', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const notifications: string[] = [];
  const diagnosticRefreshes: Array<{ scopeKey: string; output: string }> = [];
  const targetScopes: string[] = [];

  const result = await runDoctorCommand({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: path.join('D:', 'tools', 'vba-dev.exe'),
    activeFilePath: path.join(projectRoot, 'src', 'Book1', 'Module1.bas'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: async (scope) => {
      targetScopes.push(scope);
      return {
        project: {
          projectRoot,
          manifestPath: path.join(projectRoot, 'vba-project.json'),
          projectName: 'BookProject',
          primaryDocument: 'Book1',
          documents: []
        }
      };
    },
    capabilitiesProcess: async (file, args) => {
      calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            doctor: { outputSchemaVersion: '1.0' }
          },
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    },
    startProcess: (file, args) => {
      calls.push({ file, args });
      return {
        onStdout: (listener) => listener(JSON.stringify({
          schemaVersion: '1.0',
          toolVersion: '0.1.0',
          scope: 'project',
          project: projectRoot,
          status: 'fail',
          complete: true,
          checks: [{
            id: 'project.manifest',
            status: 'fail',
            message: 'Project manifest is missing.',
            durationMilliseconds: 0,
            details: {}
          }, ...projectEnvironmentChecks()]
        })),
        onStderr: () => undefined,
        onExit: (listener) => listener(1, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    diagnosticReporter: {
      refresh: (scopeKey, value) => {
        diagnosticRefreshes.push({ scopeKey, output: value });
        return [];
      }
    },
    showErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    },
    requiredContract: {
      contractVersion: '1.0',
      commandSchemaVersions: {
        doctor: '1.0'
      }
    }
  });

  assert.ok(result);
  assert.equal(result.projectRoot, projectRoot);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['doctor', '--format', 'json', '--project', projectRoot]
  ]);
  assert.deepEqual(targetScopes, ['project']);
  assert.match(output.join(''), /\[FAIL\] project\.manifest: Project manifest is missing\./);
  assert.match(notifications[0], /Doctor found blocking issues/);
  assert.equal(diagnosticRefreshes.length, 1);
  assert.equal(diagnosticRefreshes[0].scopeKey, `project:${projectRoot}`);
  assert.match(
    diagnosticRefreshes[0].output,
    /\[FAIL\] project\.manifest: Project manifest is missing\. \(0 ms\)/
  );
  assert.match(
    diagnosticRefreshes[0].output,
    /\[PASS\] excel\.processCleanup: excel\.processCleanup passed\. \(0 ms\)/
  );
});

test('Doctor runs VBE debugging after a failed project diagnostic', async () => {
  const fixture = createAggregateDoctorFixture({
    projectExitCode: 1,
    projectStdout: '[FAIL] Project manifest: missing\n'
  });

  await runDoctorCommand(fixture.options);

  assert.deepEqual(fixture.invocations, [
    `project:doctor --format json --project ${fixture.projectRoot}`,
    'adapter:doctor --format json --cancellation-transport stdin-v1'
  ]);
  assert.match(
    fixture.output.join(''),
    /\[FAIL\] project\.manifest: Project manifest is invalid\./
  );
  assert.doesNotMatch(fixture.output.join(''), /Doctor command infrastructure failure/);
});

test('Doctor rejects a vba-dev diagnostic for a different project', async () => {
  const differentProject = path.join('C:', 'work', 'DifferentProject');
  const fixture = createAggregateDoctorFixture({
    projectReport: {
      schemaVersion: '1.0',
      toolVersion: '0.1.0',
      scope: 'project',
      project: differentProject,
      status: 'pass',
      complete: true,
      checks: [{
        id: 'project.manifest',
        status: 'pass',
        message: 'Project manifest is valid.',
        durationMilliseconds: 0,
        details: {}
      }, ...projectEnvironmentChecks()]
    }
  });

  await runDoctorCommand(fixture.options);

  assert.deepEqual(fixture.invocations, [
    `project:doctor --format json --project ${fixture.projectRoot}`,
    'adapter:doctor --format json --cancellation-transport stdin-v1'
  ]);
  assert.match(fixture.output.join(''), /project identity.*does not match/i);
  assert.match(fixture.output.join(''), /Doctor command infrastructure failure/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor labels project and VBE output and renders every adapter check', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  const projectHeading = output.indexOf('Project automation');
  const projectResult = output.indexOf('[PASS] project.manifest');
  const vbeHeading = output.indexOf('VBE debugging');
  assert.ok(projectHeading >= 0);
  assert.ok(projectResult > projectHeading);
  assert.ok(vbeHeading > projectResult);
  for (const id of adapterDoctorCheckIds) {
    assert.match(
      output,
      new RegExp(`\\[PASS\\] ${escapeRegExp(id)}: ${escapeRegExp(id)} passed\\. \\(0 ms\\)`)
    );
  }
});

test('Doctor consumes a valid nonzero adapter diagnostic as one blocking result', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: failingAdapterDoctorReport()
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /\[FAIL\] vbide\.access: Trusted VBIDE access is unavailable\./);
  assert.doesNotMatch(output, /Doctor command infrastructure failure/);
  assert.equal(fixture.notifications.length, 1);
  assert.match(fixture.notifications[0], /blocking issues/i);
});

test('Doctor classifies malformed adapter output as command infrastructure failure', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterStdout: '{not-json',
    adapterStderr: 'adapter diagnostic log\n'
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /invalid JSON/);
  assert.match(output, /\{not-json/);
  assert.match(output, /adapter diagnostic log/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor classifies an adapter signal exit without JSON as infrastructure failure', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: null,
    adapterSignal: 'SIGTERM',
    adapterStdout: '',
    adapterStderr: 'adapter terminated unexpectedly\n'
  });

  const result = await runDoctorCommand(fixture.options);

  assert.ok(result);
  assert.equal(result.cancelled, false);
  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /invalid JSON/);
  assert.match(output, /adapter terminated unexpectedly/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor classifies an incomplete adapter diagnostic as command infrastructure failure', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: incompleteAdapterDoctorReport()
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /incomplete/i);
  assert.doesNotMatch(output, /Overall: PASS \(incomplete\)/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects an adapter diagnostic from a different tool version', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: {
      ...passingAdapterDoctorReport() as Record<string, unknown>,
      toolVersion: '9.9.9'
    }
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /toolVersion 9\.9\.9/);
  assert.match(output, /0\.1\.0 is required/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects project scope in an adapter diagnostic', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: {
      ...passingAdapterDoctorReport() as Record<string, unknown>,
      scope: 'project'
    }
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /must not contain scope/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a project in an adapter diagnostic', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: {
      ...passingAdapterDoctorReport() as Record<string, unknown>,
      project: path.join('C:', 'work', 'BookProject')
    }
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /must not contain project/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a document in an adapter diagnostic', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: {
      ...passingAdapterDoctorReport() as Record<string, unknown>,
      document: 'Book1'
    }
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /must not contain document/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects an adapter diagnostic missing a required check', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string }>;
  };
  report.checks = report.checks.filter((check) => check.id !== 'workspace.deletion');
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /missing required check workspace\.deletion/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects duplicate adapter check identifiers', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string }>;
  };
  report.checks.push({ ...report.checks[0] });
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /duplicate check platform\.windows/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects required adapter checks reported out of order', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string }>;
  };
  [report.checks[0], report.checks[1]] = [report.checks[1], report.checks[0]];
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /required checks in their stable order/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects an overall pass that hides a failed adapter check', async () => {
  const report = failingAdapterDoctorReport() as Record<string, unknown>;
  report.status = 'pass';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /overall status pass does not match fail/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects an overall pass that hides an unverified adapter check', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string; status: string }>;
  };
  const startupCheck = report.checks.find((check) => check.id === 'excel.startup');
  assert.ok(startupCheck);
  startupCheck.status = 'unverified';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /overall status pass does not match unverified/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects an overall pass that hides an adapter warning', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string; status: string }>;
  };
  const closeCheck = report.checks.find((check) => check.id === 'excel.processClose');
  assert.ok(closeCheck);
  closeCheck.status = 'warning';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /overall status pass does not match warning/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects an overall warning when every adapter check passes', async () => {
  const report = passingAdapterDoctorReport() as Record<string, unknown>;
  report.status = 'warning';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /overall status warning does not match pass checks/);
  assert.doesNotMatch(output, /Overall: WARNING/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a skipped adapter check without an earlier blocker', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string; status: string }>;
  };
  const workspaceCheck = report.checks.find((check) => check.id === 'workspace.session');
  assert.ok(workspaceCheck);
  workspaceCheck.status = 'skipped';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /skipped check workspace\.session has no earlier blocker/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects resumed readiness checks after an adapter blocker', async () => {
  const report = passingAdapterDoctorReport() as {
    status: string;
    checks: Array<{ id: string; status: string }>;
  };
  report.status = 'fail';
  const workspaceCheck = report.checks.find((check) => check.id === 'workspace.session');
  assert.ok(workspaceCheck);
  workspaceCheck.status = 'fail';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /readiness check excel\.startup must be skipped after an earlier blocker/);
  assert.doesNotMatch(output, /Overall: FAIL/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects skipped terminal cleanup after Windows validation passed', async () => {
  const report = failingAdapterDoctorReport() as {
    checks: Array<{ id: string; status: string }>;
  };
  for (const id of [
    'vbe.breakpointCleanup',
    'excel.processClose',
    'workspace.deletion'
  ]) {
    const cleanupCheck = report.checks.find((check) => check.id === id);
    assert.ok(cleanupCheck);
    cleanupCheck.status = 'skipped';
  }
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(
    output,
    /cleanup check vbe\.breakpointCleanup cannot be skipped after platform\.windows passed/
  );
  assert.doesNotMatch(output, /Overall: FAIL/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor accepts the non-Windows diagnostic with every later check skipped', async () => {
  const report = passingAdapterDoctorReport() as {
    status: string;
    checks: Array<{ id: string; status: string }>;
  };
  report.status = 'fail';
  for (const [index, check] of report.checks.entries()) {
    check.status = index === 0 ? 'fail' : 'skipped';
  }
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Overall: FAIL \(complete\)/);
  assert.match(output, /\[SKIPPED\] workspace\.deletion/);
  assert.doesNotMatch(output, /Doctor command infrastructure failure/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a complete diagnostic without a conclusive Windows classification', async () => {
  const report = passingAdapterDoctorReport() as {
    status: string;
    checks: Array<{ id: string; status: string }>;
  };
  report.status = 'warning';
  report.checks[0].status = 'warning';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /platform\.windows must report pass or fail/);
  assert.doesNotMatch(output, /Overall: WARNING/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a required check executed after Windows classification failed', async () => {
  const report = passingAdapterDoctorReport() as {
    status: string;
    checks: Array<{ id: string; status: string }>;
  };
  report.status = 'fail';
  report.checks[0].status = 'fail';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 1,
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(
    output,
    /required check workspace\.session must be skipped after platform\.windows failed/
  );
  assert.doesNotMatch(output, /Overall: FAIL/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a passing adapter diagnostic with a nonzero exit', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 7,
    adapterReport: passingAdapterDoctorReport()
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /overall status pass requires exit code 0, received 7/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a failing adapter diagnostic with a zero exit', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterExitCode: 0,
    adapterReport: failingAdapterDoctorReport()
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /overall status fail requires a nonzero exit code/);
  assert.doesNotMatch(output, /Overall: FAIL/);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor rejects a fractional adapter check duration', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string; durationMilliseconds: number }>;
  };
  report.checks[0].durationMilliseconds = 0.5;
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /nonnegative safe-integer durationMilliseconds/);
  assertOnlyProjectOverallPass(output);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor renders an empty adapter check remediation string', async () => {
  const report = passingAdapterDoctorReport() as {
    checks: Array<{ id: string; remediation?: string }>;
  };
  report.checks[0].remediation = '';
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Overall: PASS \(complete\)/);
  assert.match(output, /  Remediation: \n/);
  assert.doesNotMatch(output, /Doctor command infrastructure failure/);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor reports an actionable debug-adapter resolution failure after project output', async () => {
  const configuredPath = path.join('D:', 'broken', 'vba-debug-adapter.exe');
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  fixture.options.vbaDebugAdapterResolver = {
    resolve: async () => {
      throw new VbaDebugAdapterCompatibilityError(
        `vba-debug-adapter at '${configuredPath}' reports incompatible protocolVersion.`
      );
    }
  };

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.ok(output.indexOf('[PASS] project.manifest') < output.indexOf('VBE debugging'));
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, new RegExp(escapeRegExp(configuredPath)));
  assert.match(output, /incompatible protocolVersion/);
  assert.deepEqual(fixture.invocations, [
    `project:doctor --format json --project ${fixture.projectRoot}`
  ]);
  assert.equal(fixture.notifications.length, 1);
});

test('Doctor independently cancels VBE debugging after project Doctor completes', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let adapterClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterStdout: ((value: string) => void) | undefined;
  let adapterCancellationRequests = 0;
  let adapterKills = 0;
  let signalAdapterStarted: (() => void) | undefined;
  const adapterStarted = new Promise<void>((resolve) => {
    signalAdapterStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startDebugAdapterProcess = (_file, args) => {
    fixture.invocations.push(`adapter:${args.join(' ')}`);
    signalAdapterStarted?.();
    return {
      onStdout: (listener) => {
        adapterStdout = listener;
      },
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        adapterClose = listener;
      },
      requestCancellation: async () => {
        adapterCancellationRequests += 1;
        adapterStdout?.(JSON.stringify(cancelledAdapterDoctorReport()));
        adapterClose?.(1, null);
      },
      kill: () => {
        adapterKills += 1;
        adapterClose?.(null, 'SIGTERM');
      }
    };
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await adapterStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.equal(adapterCancellationRequests, 1);
  assert.equal(adapterKills, 0);
  assert.deepEqual(fixture.invocations, [
    `project:doctor --format json --project ${fixture.projectRoot}`,
    'adapter:doctor --format json --cancellation-transport stdin-v1'
  ]);
  assert.match(fixture.output.join(''), /VBE debugging command cancelled\./);
  assert.match(fixture.output.join(''), /Overall: UNVERIFIED \(incomplete\)/);
  assert.doesNotMatch(fixture.output.join(''), /VbaDev command cancelled\./);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor still notifies when cooperative cancellation reports failed terminal cleanup', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let adapterClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterStdout: ((value: string) => void) | undefined;
  let signalAdapterStarted: (() => void) | undefined;
  const adapterStarted = new Promise<void>((resolve) => {
    signalAdapterStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startDebugAdapterProcess = () => {
    signalAdapterStarted?.();
    return {
      onStdout: (listener) => {
        adapterStdout = listener;
      },
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        adapterClose = listener;
      },
      requestCancellation: async () => {
        adapterStdout?.(JSON.stringify(cancelledAdapterDoctorReportWithCleanupFailure()));
        adapterClose?.(1, null);
      },
      kill: () => undefined
    };
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await adapterStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.match(fixture.output.join(''), /\[FAIL\] workspace\.deletion/);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor rejects incomplete cancellation output without an unverified check', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let adapterClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterStdout: ((value: string) => void) | undefined;
  let signalAdapterStarted: (() => void) | undefined;
  const adapterStarted = new Promise<void>((resolve) => {
    signalAdapterStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startDebugAdapterProcess = () => {
    signalAdapterStarted?.();
    return {
      onStdout: (listener) => {
        adapterStdout = listener;
      },
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        adapterClose = listener;
      },
      requestCancellation: async () => {
        adapterStdout?.(JSON.stringify(incompleteAdapterDoctorReport()));
        adapterClose?.(1, null);
      },
      kill: () => undefined
    };
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await adapterStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, false);
  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /incomplete diagnostic must include an unverified check/);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor still notifies for a project blocker when VBE debugging is cancelled', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectExitCode: 1,
    projectStdout: '[FAIL] Project manifest is invalid.\n'
  });
  let adapterExit: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterStdout: ((value: string) => void) | undefined;
  let signalAdapterStarted: (() => void) | undefined;
  const adapterStarted = new Promise<void>((resolve) => {
    signalAdapterStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startDebugAdapterProcess = () => ({
    onStdout: (listener) => {
      adapterStdout = listener;
    },
    onStderr: () => undefined,
    onExit: (listener) => {
      adapterExit = listener;
      signalAdapterStarted?.();
    },
    requestCancellation: async () => {
      adapterStdout?.(JSON.stringify(cancelledAdapterDoctorReport()));
      adapterExit?.(1, null);
    },
    kill: () => {
      adapterExit?.(null, 'SIGTERM');
    }
  });

  const runningDoctor = runDoctorCommand(fixture.options);
  await adapterStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor surfaces malformed output after cooperative VBE cancellation', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let adapterClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterStdout: ((value: string) => void) | undefined;
  let adapterKills = 0;
  let signalAdapterStarted: (() => void) | undefined;
  const adapterStarted = new Promise<void>((resolve) => {
    signalAdapterStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startDebugAdapterProcess = () => {
    signalAdapterStarted?.();
    return {
      onStdout: (listener) => {
        adapterStdout = listener;
      },
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        adapterClose = listener;
      },
      requestCancellation: async () => {
        adapterStdout?.('{malformed');
        adapterClose?.(1, null);
      },
      kill: () => {
        adapterKills += 1;
      }
    };
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await adapterStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, false);
  assert.equal(adapterKills, 0);
  const output = fixture.output.join('');
  assert.match(output, /Doctor command infrastructure failure/);
  assert.match(output, /invalid JSON/);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor treats failed cooperative cancellation delivery as infrastructure failure', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let adapterClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterStdout: ((value: string) => void) | undefined;
  let signalAdapterStarted: (() => void) | undefined;
  const adapterStarted = new Promise<void>((resolve) => {
    signalAdapterStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startDebugAdapterProcess = () => {
    signalAdapterStarted?.();
    return {
      onStdout: (listener) => {
        adapterStdout = listener;
      },
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        adapterClose = listener;
      },
      requestCancellation: async () => {
        adapterStdout?.(JSON.stringify(cancelledAdapterDoctorReport()));
        adapterClose?.(1, null);
        throw new Error('write EPIPE');
      },
      kill: () => undefined
    };
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await adapterStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, false);
  const output = fixture.output.join('');
  assert.match(output, /cancellation request could not be delivered/);
  assert.match(output, /Doctor command infrastructure failure/);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor independently cancels project Doctor before VBE debugging starts', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture();
  let projectExit: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let projectKills = 0;
  let adapterResolutions = 0;
  let signalProjectStarted: (() => void) | undefined;
  const projectStarted = new Promise<void>((resolve) => {
    signalProjectStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startProcess = (_file, args) => {
    fixture.invocations.push(`project:${args.join(' ')}`);
    signalProjectStarted?.();
    return {
      onStdout: () => undefined,
      onStderr: () => undefined,
      onExit: (listener) => {
        projectExit = listener;
      },
      kill: () => {
        projectKills += 1;
        projectExit?.(null, 'SIGTERM');
      }
    };
  };
  fixture.options.vbaDebugAdapterResolver = {
    resolve: async () => {
      adapterResolutions += 1;
      throw new Error('adapter must not resolve after project cancellation');
    }
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await projectStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.equal(projectKills, 1);
  assert.equal(adapterResolutions, 0);
  assert.deepEqual(fixture.invocations, [
    `project:doctor --format json --project ${fixture.projectRoot}`
  ]);
  assert.doesNotMatch(fixture.output.join(''), /VBE debugging/);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor still notifies when project cancellation returns a failed diagnostic after cleanup', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({ projectExitCode: 1 });
  const failedReport = getProjectDoctorStdout(
    { projectExitCode: 1 },
    fixture.projectRoot
  );
  let projectStdout: ((value: string) => void) | undefined;
  let projectClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let adapterResolutions = 0;
  let signalProjectStarted: (() => void) | undefined;
  const projectStarted = new Promise<void>((resolve) => {
    signalProjectStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.startProcess = (_file, args) => {
    fixture.invocations.push(`project:${args.join(' ')}`);
    signalProjectStarted?.();
    return {
      onStdout: (listener) => {
        projectStdout = listener;
      },
      onStderr: () => undefined,
      onExit: () => undefined,
      onClose: (listener) => {
        projectClose = listener;
      },
      kill: () => {
        projectStdout?.(failedReport);
        projectClose?.(1, null);
      }
    };
  };
  fixture.options.vbaDebugAdapterResolver = {
    resolve: async () => {
      adapterResolutions += 1;
      throw new Error('adapter must not resolve after project cancellation');
    }
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await projectStarted;
  cancellationToken.cancel();
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.equal(adapterResolutions, 0);
  assert.match(fixture.output.join(''), /\[FAIL\] project\.manifest/);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor does not start VBE debugging after cancellation during adapter resolution', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  const originalResolver = fixture.options.vbaDebugAdapterResolver;
  assert.ok(originalResolver);
  const compatibleAdapter = await originalResolver.resolve();
  let releaseResolution: ((value: typeof compatibleAdapter) => void) | undefined;
  let signalResolutionStarted: (() => void) | undefined;
  const resolutionStarted = new Promise<void>((resolve) => {
    signalResolutionStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.vbaDebugAdapterResolver = {
    resolve: () => {
      signalResolutionStarted?.();
      return new Promise((resolve) => {
        releaseResolution = resolve;
      });
    }
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await resolutionStarted;
  cancellationToken.cancel();
  releaseResolution?.(compatibleAdapter);
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.deepEqual(fixture.invocations, [
    `project:doctor --format json --project ${fixture.projectRoot}`
  ]);
  assert.match(fixture.output.join(''), /VBE debugging command cancelled\./);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor preserves cancellation when adapter resolution later fails', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let rejectResolution: ((reason: Error) => void) | undefined;
  let signalResolutionStarted: (() => void) | undefined;
  const resolutionStarted = new Promise<void>((resolve) => {
    signalResolutionStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.vbaDebugAdapterResolver = {
    resolve: () => {
      signalResolutionStarted?.();
      return new Promise((_resolve, reject) => {
        rejectResolution = reject;
      });
    }
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await resolutionStarted;
  cancellationToken.cancel();
  rejectResolution?.(new Error('adapter resolution failed after cancellation'));
  const result = await runningDoctor;

  assert.ok(result);
  assert.equal(result.cancelled, true);
  assert.match(fixture.output.join(''), /VBE debugging command cancelled\./);
  assert.doesNotMatch(fixture.output.join(''), /Doctor command infrastructure failure/);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor returns on cancellation while adapter resolution remains pending', async () => {
  const cancellationToken = new TestCancellationToken();
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  let signalResolutionStarted: (() => void) | undefined;
  const resolutionStarted = new Promise<void>((resolve) => {
    signalResolutionStarted = resolve;
  });
  fixture.options.cancellationToken = cancellationToken;
  fixture.options.vbaDebugAdapterResolver = {
    resolve: () => {
      signalResolutionStarted?.();
      return new Promise<never>(() => undefined);
    }
  };

  const runningDoctor = runDoctorCommand(fixture.options);
  await resolutionStarted;
  cancellationToken.cancel();
  const outcome = await Promise.race([
    runningDoctor.then((result) => ({ kind: 'result' as const, result })),
    new Promise<{ kind: 'timeout' }>((resolve) => {
      setTimeout(() => resolve({ kind: 'timeout' }), 25);
    })
  ]);

  assert.equal(outcome.kind, 'result');
  if (outcome.kind !== 'result') {
    return;
  }
  assert.ok(outcome.result);
  assert.equal(outcome.result.cancelled, true);
  assert.match(fixture.output.join(''), /VBE debugging command cancelled\./);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor resolves and runs the configured adapter without an injected resolver', async () => {
  const configuredPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const adapterInvocations: Array<{ file: string; args: readonly string[] }> = [];
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n'
  });
  delete fixture.options.vbaDebugAdapterResolver;
  fixture.options.configuredDebugAdapterPath = configuredPath;
  fixture.options.requiredDebugAdapterContract = {
    contractVersion: '1.0',
    protocolVersion: '1.1',
    transports: ['stdio'],
    sessionIdFormat: 'lowercase-hex-32',
    commands: ['cleanup', 'doctor'],
    commandSchemaVersions: { doctor: '1.0' },
    featureVersions: { 'doctor.stdinCancellation': '1.0' },
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
  };
  fixture.options.debugAdapterCapabilitiesProcess = async (file, args) => {
    adapterInvocations.push({ file, args });
    return {
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        ...fixture.options.requiredDebugAdapterContract
      }),
      stderr: ''
    };
  };
  fixture.options.startDebugAdapterProcess = (file, args) => {
    adapterInvocations.push({ file, args });
    return {
      onStdout: (listener) => listener(JSON.stringify(passingAdapterDoctorReport())),
      onStderr: () => undefined,
      onExit: (listener) => listener(0, null),
      kill: () => undefined
    };
  };

  await runDoctorCommand(fixture.options);

  assert.deepEqual(adapterInvocations, [
    { file: configuredPath, args: ['capabilities', '--format', 'json'] },
    {
      file: configuredPath,
      args: [
        'doctor',
        '--format',
        'json',
        '--cancellation-transport',
        'stdin-v1'
      ]
    }
  ]);
  assert.match(fixture.output.join(''), /VBE debugging/);
  assert.match(fixture.output.join(''), /\[PASS\] workspace\.deletion/);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor renders a unique additive adapter check with its troubleshooting details', async () => {
  const report = passingAdapterDoctorReport() as {
    status: string;
    checks: Array<Record<string, unknown>>;
    [key: string]: unknown;
  };
  report.status = 'warning';
  report.futureMetadata = { producer: 'future-adapter' };
  report.checks.splice(1, 0, {
    id: 'future.additive',
    status: 'warning',
    message: 'A future diagnostic warning.',
    durationMilliseconds: 3,
    remediation: 'Review the future diagnostic.',
    details: { code: 'FUTURE001' },
    futureCheckMetadata: true
  });
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterReport: report
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Overall: WARNING \(complete\)/);
  assert.match(output, /\[WARNING\] future\.additive: A future diagnostic warning\. \(3 ms\)/);
  assert.match(output, /Remediation: Review the future diagnostic\./);
  assert.match(output, /Details: \{"code":"FUTURE001"\}/);
  assert.match(output, /\[PASS\] workspace\.deletion/);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor retains adapter stderr logging without blocking a valid passing report', async () => {
  const fixture = createAggregateDoctorFixture({
    projectStdout: '[PASS] Project manifest\n',
    adapterStderr: 'adapter diagnostic log\n'
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /adapter diagnostic log/);
  assert.match(output, /Overall: PASS \(complete\)/);
  assert.doesNotMatch(output, /Doctor command infrastructure failure/);
  assert.deepEqual(fixture.notifications, []);
});

test('Doctor shows one blocking notification when both diagnostics fail', async () => {
  const fixture = createAggregateDoctorFixture({
    projectExitCode: 1,
    projectStdout: '[FAIL] Project manifest is invalid.\n',
    adapterExitCode: 1,
    adapterReport: failingAdapterDoctorReport()
  });

  await runDoctorCommand(fixture.options);

  const output = fixture.output.join('');
  assert.match(output, /Project automation/);
  assert.match(output, /\[FAIL\] project\.manifest: Project manifest is invalid\./);
  assert.match(output, /VBE debugging/);
  assert.match(output, /\[FAIL\] vbide\.access/);
  assert.deepEqual(fixture.notifications, [
    'VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.'
  ]);
});

test('Doctor reports configured and effective vba-dev paths after bundled fallback', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const configuredPath = path.join('D:', 'old', 'vba-dev.exe');
  const effectivePath = path.join('C:', 'extension', 'bin', 'vba-dev.exe');
  const output: string[] = [];
  const capabilities = {
    toolVersion: '0.1.0',
    contractVersion: '1.0',
    commands: {
      doctor: { outputSchemaVersion: '1.0' }
    },
    debugAdapter: {
      protocolVersion: '1.0',
      transport: 'stdio',
      command: 'debug-adapter'
    }
  };

  await runDoctorCommand({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => ({
        configuredPath,
        bundledPath: effectivePath,
        executablePath: effectivePath,
        source: 'bundled',
        configuredFailure: 'contractVersion 0.9 is incompatible',
        capabilities
      })
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: async (scope) => {
      assert.equal(scope, 'project');
      return {
        project: {
          projectRoot,
          manifestPath: path.join(projectRoot, 'vba-project.json'),
          projectName: 'BookProject',
          primaryDocument: 'Book1',
          documents: []
        }
      };
    },
    startProcess: (file) => {
      assert.equal(file, effectivePath);
      return {
        onStdout: (listener) => listener('[PASS] Project manifest\n'),
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  });

  assert.match(output.join(''), /vba-dev executable fallback:/);
  assert.match(output.join(''), new RegExp(`Configured: ${escapeRegExp(configuredPath)}`));
  assert.match(output.join(''), new RegExp(`Effective: ${escapeRegExp(effectivePath)}`));
});

test('Doctor stops without another notification when companion resolution failure was already reported', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  let processStarts = 0;
  const notifications: string[] = [];

  const result = await runDoctorCommand({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => {
        throw new VbaDevCompatibilityError('no compatible vba-dev', true);
      }
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: async () => ({
      project: {
        projectRoot,
        manifestPath: path.join(projectRoot, 'vba-project.json'),
        projectName: 'BookProject',
        primaryDocument: 'Book1',
        documents: []
      }
    }),
    startProcess: () => {
      processStarts += 1;
      throw new Error('Doctor must not start');
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    }
  });

  assert.equal(result, undefined);
  assert.equal(processStarts, 0);
  assert.deepEqual(notifications, []);
});

test('First-run doctor prompt can run doctor once for the workspace', async () => {
  const state = new MemoryPromptState();
  let doctorRuns = 0;

  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => 'Run Doctor',
    runDoctor: async () => {
      doctorRuns += 1;
    }
  });
  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => {
      throw new Error('prompt should be suppressed after the first prompt');
    },
    runDoctor: async () => {
      doctorRuns += 1;
    }
  });

  assert.equal(doctorRuns, 1);
});

test('First-run doctor prompt supports a workspace do-not-ask-again choice', async () => {
  const state = new MemoryPromptState();
  let prompts = 0;

  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => {
      prompts += 1;
      return "Don't Ask Again";
    },
    runDoctor: async () => {
      throw new Error('doctor should not run when the user suppresses the prompt');
    }
  });
  await promptForFirstRunDoctor({
    workspaceState: state,
    showInformationMessage: async () => {
      prompts += 1;
      return 'Run Doctor';
    },
    runDoctor: async () => undefined
  });

  assert.equal(prompts, 1);
  assert.equal(state.get(FirstRunDoctorPromptState.Suppress), true);
});

interface AggregateDoctorFixtureConfig {
  projectExitCode?: number;
  projectReport?: unknown;
  projectStdout?: string;
  projectStderr?: string;
  adapterExitCode?: number | null;
  adapterSignal?: string | null;
  adapterReport?: unknown;
  adapterStdout?: string;
  adapterStderr?: string;
}

function createAggregateDoctorFixture(
  config: AggregateDoctorFixtureConfig = {}
): {
  projectRoot: string;
  invocations: string[];
  output: string[];
  notifications: string[];
  options: DoctorCommandOptions;
} {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const vbaDevPath = path.join('C:', 'extension', 'bin', 'vba-dev.exe');
  const debugAdapterPath = path.join(
    'C:',
    'extension',
    'bin',
    'vba-debug-adapter.exe'
  );
  const invocations: string[] = [];
  const output: string[] = [];
  const notifications: string[] = [];
  const projectStdout = getProjectDoctorStdout(config, projectRoot);
  const options: DoctorCommandOptions = {
    extensionRoot: path.join('C:', 'extension'),
    vbaDevResolver: {
      resolve: async () => ({
        bundledPath: vbaDevPath,
        executablePath: vbaDevPath,
        source: 'bundled',
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            doctor: { outputSchemaVersion: '1.0' }
          },
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }
      })
    },
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: debugAdapterPath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          protocolVersion: '1.1',
          transports: ['stdio'],
          sessionIdFormat: 'lowercase-hex-32',
          commands: ['cleanup', 'doctor'],
          commandSchemaVersions: { doctor: '1.0' },
          featureVersions: { 'doctor.stdinCancellation': '1.0' },
          requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
        }
      })
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: async (scope) => {
      assert.equal(scope, 'project');
      return {
        project: {
          projectRoot,
          manifestPath: path.join(projectRoot, 'vba-project.json'),
          projectName: 'BookProject',
          primaryDocument: 'Book1',
          documents: []
        }
      };
    },
    startProcess: (_file, args) => {
      invocations.push(`project:${args.join(' ')}`);
      return {
        onStdout: (listener) => {
          listener(projectStdout);
        },
        onStderr: (listener) => {
          if (config.projectStderr !== undefined) {
            listener(config.projectStderr);
          }
        },
        onExit: (listener) => listener(config.projectExitCode ?? 0, null),
        kill: () => undefined
      };
    },
    startDebugAdapterProcess: (_file, args) => {
      invocations.push(`adapter:${args.join(' ')}`);
      return {
        onStdout: (listener) => listener(
          config.adapterStdout ?? JSON.stringify(
            config.adapterReport ?? passingAdapterDoctorReport()
          )
        ),
        onStderr: (listener) => {
          if (config.adapterStderr !== undefined) {
            listener(config.adapterStderr);
          }
        },
        onExit: (listener) => listener(
          config.adapterExitCode === undefined ? 0 : config.adapterExitCode,
          config.adapterSignal ?? null
        ),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    }
  };
  return { projectRoot, invocations, output, notifications, options };
}

function getProjectDoctorStdout(
  config: AggregateDoctorFixtureConfig,
  projectRoot: string
): string {
  if (config.projectReport !== undefined) {
    return JSON.stringify(config.projectReport);
  }
  if (
    config.projectStdout !== undefined &&
    !config.projectStdout.startsWith('[PASS]') &&
    !config.projectStdout.startsWith('[FAIL]')
  ) {
    return config.projectStdout;
  }

  const failed = (config.projectExitCode ?? 0) !== 0;
  return JSON.stringify({
    schemaVersion: '1.0',
    toolVersion: '0.1.0',
    scope: 'project',
    project: projectRoot,
    status: failed ? 'fail' : 'pass',
    complete: true,
    checks: [{
      id: 'project.manifest',
      status: failed ? 'fail' : 'pass',
      message: failed ? 'Project manifest is invalid.' : 'Project manifest is valid.',
      durationMilliseconds: 0,
      details: {}
    }, ...projectEnvironmentChecks()]
  });
}

function projectEnvironmentChecks(): Array<Record<string, unknown>> {
  const detailNames: Record<string, string> = {
    'platform.windows': 'isWindows',
    'excel.comStartup': 'dedicatedInstanceStarted',
    'excel.processOwnership': 'ownedByInvocation',
    'excel.vbideProjectAccess': 'projectAccessSucceeded',
    'excel.processCleanup': 'ownedProcessReleased'
  };
  return Object.entries(detailNames).map(([id, detailName]) => ({
    id,
    status: 'pass',
    message: `${id} passed.`,
    durationMilliseconds: 0,
    details: { [detailName]: true }
  }));
}

class MemoryPromptState {
  private readonly values = new Map<string, unknown>();

  public get<T>(key: string): T | undefined {
    return this.values.get(key) as T | undefined;
  }

  public async update(key: string, value: unknown): Promise<void> {
    this.values.set(key, value);
  }
}

class TestCancellationToken {
  public isCancellationRequested = false;
  private readonly listeners = new Set<() => void>();

  public onCancellationRequested(listener: () => void): { dispose(): void } {
    this.listeners.add(listener);
    return {
      dispose: () => this.listeners.delete(listener)
    };
  }

  public cancel(): void {
    this.isCancellationRequested = true;
    for (const listener of [...this.listeners]) {
      listener();
    }
  }
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function assertOnlyProjectOverallPass(output: string): void {
  assert.equal(output.match(/Overall: PASS/g)?.length, 1);
}

const adapterDoctorCheckIds = [
  'platform.windows',
  'workspace.session',
  'excel.startup',
  'excel.processOwnership',
  'workbook.fixtureCreation',
  'workbook.open',
  'vbide.access',
  'vbe.commandContext',
  'vbe.breakpoint',
  'vbe.breakMode',
  'vbe.continue',
  'vbe.procedureCompletion',
  'vbe.breakpointCleanup',
  'excel.processClose',
  'workspace.deletion'
] as const;

function passingAdapterDoctorReport(): unknown {
  return {
    schemaVersion: '1.0',
    toolVersion: '0.1.0',
    status: 'pass',
    complete: true,
    checks: adapterDoctorCheckIds.map((id) => ({
      id,
      status: 'pass',
      message: `${id} passed.`,
      durationMilliseconds: 0
    }))
  };
}

function failingAdapterDoctorReport(): unknown {
  return {
    schemaVersion: '1.0',
    toolVersion: '0.1.0',
    status: 'fail',
    complete: true,
    checks: adapterDoctorCheckIds.map((id, index) => ({
      id,
      status: id === 'vbide.access'
        ? 'fail'
        : index > adapterDoctorCheckIds.indexOf('vbide.access') &&
          index < adapterDoctorCheckIds.indexOf('vbe.breakpointCleanup')
          ? 'skipped'
          : 'pass',
      message: id === 'vbide.access'
        ? 'Trusted VBIDE access is unavailable.'
        : `${id} completed.`,
      durationMilliseconds: 0
    }))
  };
}

function incompleteAdapterDoctorReport(): unknown {
  return {
    ...passingAdapterDoctorReport() as Record<string, unknown>,
    complete: false
  };
}

function cancelledAdapterDoctorReport(): unknown {
  const report = failingAdapterDoctorReport() as {
    status: string;
    complete: boolean;
    checks: Array<{ id: string; status: string }>;
  };
  report.status = 'unverified';
  report.complete = false;
  const blockedCheck = report.checks.find((check) => check.id === 'vbide.access');
  if (blockedCheck !== undefined) {
    blockedCheck.status = 'unverified';
  }
  return report;
}

function cancelledAdapterDoctorReportWithCleanupFailure(): unknown {
  const report = cancelledAdapterDoctorReport() as {
    status: string;
    checks: Array<{ id: string; status: string; message: string }>;
  };
  report.status = 'fail';
  const cleanupCheck = report.checks.find((check) => check.id === 'workspace.deletion');
  if (cleanupCheck !== undefined) {
    cleanupCheck.status = 'fail';
    cleanupCheck.message = 'Temporary workspace deletion failed.';
  }
  return report;
}
