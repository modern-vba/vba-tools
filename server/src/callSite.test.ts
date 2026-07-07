import { deepEqual, equal, ok } from 'node:assert/strict';
import test from 'node:test';

import {
  getCallSiteAt,
  getCallStatementSegments
} from './callSite';

test('CallSite statement segmentation follows LogicalSource statement boundaries', () => {
  const line = 'Log "a:b", Named:=Value: Call Target(Left("x:y", 1)): Debug.Print 1 \' : ignored';

  deepEqual(
    getCallStatementSegments(line).map((segment) => segment.text.trim()),
    [
      'Log "a:b", Named:=Value',
      'Call Target(Left("x:y", 1))',
      'Debug.Print 1'
    ]
  );
});

test('CallSite resolves continued explicit Call expressions', () => {
  const lines = [
    'Call Factory.Build( _',
    '  firstArg, secondArg:=42'
  ];

  const expression = getCallSiteAt(lines, {
    line: 1,
    character: lines[1].length
  });

  ok(expression);
  equal(expression.name, 'Build');
  equal(expression.activeParameter, 1);
  equal(expression.namedArgumentName, 'secondArg');
  deepEqual(
    expression.chain?.segments.map((segment) => segment.name),
    ['Factory', 'Build']
  );
});
