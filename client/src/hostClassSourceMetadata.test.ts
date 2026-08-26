import test from 'node:test';
import assert from 'node:assert/strict';

import {
  resolveHostClassSourceMetadata
} from './hostClassSourceMetadata';

test('host-class source metadata is resolved by the parser-owned batch request', async () => {
  const sources = [{
    sourceUri: 'file:///C:/work/InvoiceForm.frm',
    kind: 'form' as const,
    text: 'Attribute VB_Name = "InvoiceForm"\r\n'
  }];
  let sentMethod: string | undefined;
  let sentParameters: unknown;

  const resolved = await resolveHostClassSourceMetadata({
    sendRequest: async (method, parameters) => {
      sentMethod = method;
      sentParameters = parameters;
      return {
        sources: [{
          sourceUri: sources[0].sourceUri,
          kind: sources[0].kind,
          state: 'authoritative',
          name: 'InvoiceForm'
        }]
      };
    }
  }, sources);

  assert.equal(sentMethod, 'vba/moduleIdentityMetadata');
  assert.deepEqual(sentParameters, { sources });
  assert.deepEqual(resolved, [{
    sourceUri: sources[0].sourceUri,
    kind: sources[0].kind,
    moduleIdentity: {
      state: 'authoritative',
      name: 'InvoiceForm'
    }
  }]);
});
