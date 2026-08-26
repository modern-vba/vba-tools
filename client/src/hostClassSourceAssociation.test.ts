import test from 'node:test';
import assert from 'node:assert/strict';

import { HostClassProjectionSnapshotEntry } from './hostClassProjectionLifecycle';
import { associateHostClassSources } from './hostClassSourceAssociation';

test('HostClass association rejects parser-owned invalid module identity metadata', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/CDecl.frm',
    kind: 'form',
    moduleIdentity: { state: 'invalid' }
  }], [{
    identity: { name: 'CDecl', kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }]);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameInvalid');
});

test('HostClass source association uses explicit VB_Name case-insensitively instead of file name', () => {
  const classes: readonly HostClassProjectionSnapshotEntry[] = [
    {
      identity: {
        name: 'InvoiceForm',
        kind: 'form'
      },
      authority: 'current',
      projection: {
        intrinsicEventSourceName: 'UserForm',
        events: []
      }
    }
  ];

  const result = associateHostClassSources(
    [
      {
        sourceUri: 'file:///C:/work/Invoices/src/Unrelated.frm',
        kind: 'form',
        moduleIdentity: { state: 'authoritative', name: 'invoiceform' }
      }
    ],
    classes
  );

  assert.deepEqual(result, {
    associations: [
      {
        sourceUri: 'file:///C:/work/Invoices/src/Unrelated.frm',
        sourceKind: 'form',
        attributeVbName: 'invoiceform',
        projectionIdentity: {
          name: 'InvoiceForm',
          kind: 'form'
        },
        authority: 'current'
      }
    ],
    failures: []
  });
});

test('HostClass form association uses the last valid class-header VB_Name record', () => {
  const classes: readonly HostClassProjectionSnapshotEntry[] = [{
    identity: { name: 'InvoiceForm', kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }];

  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'authoritative', name: 'InvoiceForm' }
  }], classes);

  assert.deepEqual(result.associations.map((association) => ({
    attributeVbName: association.attributeVbName,
    projectionIdentity: association.projectionIdentity
  })), [{
    attributeVbName: 'InvoiceForm',
    projectionIdentity: { name: 'InvoiceForm', kind: 'form' }
  }]);
  assert.deepEqual(result.failures, []);
});

test('HostClass form association rejects a misplaced body VB_Name record', () => {
  const classes: readonly HostClassProjectionSnapshotEntry[] = [{
    identity: { name: 'InvoiceForm', kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }];

  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'invalid' }
  }], classes);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameInvalid');
});

test('HostClass association closes the class header at a malformed class attribute', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'invalid' }
  }], [{
    identity: { name: 'InvoiceForm', kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }]);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameInvalid');
});

test('HostClass association does not treat NBSP as Attribute grammar whitespace', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'invalid' }
  }], [{
    identity: { name: 'InvoiceForm', kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }]);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameInvalid');
});

test('HostClass association preserves NBSP inside an explicit VB_Name value', () => {
  const name = 'Invoice\u00a0Form';
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'authoritative', name }
  }], [{
    identity: { name, kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }]);

  assert.equal(result.associations[0]?.attributeVbName, name);
  assert.deepEqual(result.failures, []);
});

test('HostClass association accepts MS-VBAL ideographic layout whitespace', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'authoritative', name: 'InvoiceForm' }
  }], [{
    identity: { name: 'InvoiceForm', kind: 'form' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'UserForm',
      events: []
    }
  }]);

  assert.equal(result.associations[0]?.attributeVbName, 'InvoiceForm');
  assert.deepEqual(result.failures, []);
});

test('HostClass association does not confuse a longer attribute identifier with VB_Name', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'missing' }
  }], []);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameMissing');
});

test('HostClass document association uses the last valid class-header VB_Name record', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/Sheet1.document',
    kind: 'document',
    moduleIdentity: { state: 'authoritative', name: 'Sheet1' }
  }], [{
    identity: { name: 'Sheet1', kind: 'document' },
    authority: 'current',
    projection: {
      intrinsicEventSourceName: 'Worksheet',
      events: []
    }
  }]);

  assert.equal(result.associations[0]?.attributeVbName, 'Sheet1');
  assert.deepEqual(result.failures, []);
});

test('HostClass source association reports a same-name incompatible component kind', () => {
  const classes: readonly HostClassProjectionSnapshotEntry[] = [
    {
      identity: {
        name: 'Sheet1',
        kind: 'document'
      },
      authority: 'current',
      projection: {
        intrinsicEventSourceName: 'Worksheet',
        events: []
      }
    }
  ];

  const result = associateHostClassSources(
    [
      {
        sourceUri: 'file:///C:/work/Invoices/src/Sheet1.frm',
        kind: 'form',
        moduleIdentity: { state: 'authoritative', name: 'sheet1' }
      }
    ],
    classes
  );

  assert.deepEqual(result, {
    associations: [],
    failures: [
      {
        sourceUri: 'file:///C:/work/Invoices/src/Sheet1.frm',
        sourceKind: 'form',
        attributeVbName: 'sheet1',
        candidateProjectionIdentity: {
          name: 'Sheet1',
          kind: 'document'
        },
        reason: 'componentKindMismatch',
        message: 'Attribute VB_Name "sheet1" matches a document projection, not a form projection.',
        guidance: 'Re-export the source or repair its explicit Attribute VB_Name metadata.'
      }
    ]
  });
});

test('HostClass incomplete enumeration leaves an absent source identity indeterminate', () => {
  const result = associateHostClassSources(
    [
      {
        sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
        kind: 'form',
        moduleIdentity: { state: 'authoritative', name: 'InvoiceForm' }
      }
    ],
    [],
    false
  );

  assert.deepEqual(result, {
    associations: [],
    failures: []
  });
});
