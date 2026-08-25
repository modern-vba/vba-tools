import test from 'node:test';
import assert from 'node:assert/strict';

import { HostClassProjectionSnapshotEntry } from './hostClassProjectionLifecycle';
import { associateHostClassSources } from './hostClassSourceAssociation';

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
        text: 'VERSION 5.00\nATTRIBUTE vb_name = "invoiceform"\n'
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
    text: [
      'VERSION 5.00',
      'Begin VB.UserForm InvoiceForm',
      'End',
      'Attribute VB_Name = "ShadowedForm"',
      'Attribute VB_Name = "InvoiceForm"',
      'Option Explicit',
      ''
    ].join('\n')
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
    text: [
      'VERSION 5.00',
      'Begin VB.UserForm InvoiceForm',
      'End',
      'Attribute VB_Exposed = False',
      'Option Explicit',
      'Attribute VB_Name = "InvoiceForm"',
      ''
    ].join('\n')
  }], classes);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameInvalid');
});

test('HostClass association closes the class header at a malformed class attribute', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
    kind: 'form',
    text: [
      'VERSION 5.00',
      'Begin VB.UserForm InvoiceForm',
      'End',
      'Attribute VB_Name = "ShadowedForm"',
      'Attribute VB_Exposed = Maybe',
      'Attribute VB_Name = "InvoiceForm"',
      ''
    ].join('\n')
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
    text: 'Attribute\u00a0VB_Name = "InvoiceForm"\n'
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
    text: `Attribute VB_Name = "${name}"\n`
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
    text: 'Attribute\u3000VB_Name\u3000=\u3000"InvoiceForm"\n'
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
    text: 'Attribute VB_Name\u6ce8\u6587 = "InvoiceForm"\n'
  }], []);

  assert.deepEqual(result.associations, []);
  assert.equal(result.failures[0]?.reason, 'attributeVbNameMissing');
});

test('HostClass document association uses the last valid class-header VB_Name record', () => {
  const result = associateHostClassSources([{
    sourceUri: 'file:///C:/work/Invoices/src/Sheet1.document',
    kind: 'document',
    text: [
      'Attribute VB_Name = "ShadowedSheet"',
      'Attribute VB_Name = "Sheet1"',
      'Attribute VB_PredeclaredId = True',
      'Option Explicit',
      ''
    ].join('\n')
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
        text: 'Attribute VB_Name = "sheet1"\n'
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
        text: 'Attribute VB_Name = "InvoiceForm"\n'
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
