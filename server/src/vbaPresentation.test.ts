import { deepEqual, equal } from 'node:assert/strict';
import test from 'node:test';

import type { CallableParameter, HostDefinition } from './hostDefinition';
import type { DocumentationComment, VbaDefinition } from './vbaSourceModel';
import {
  completionKindForHostDefinition,
  completionKindForVbaDefinition,
  getHostDefinitionDetail,
  getParameterDocumentation,
  renderCallableParameterMetadata,
  renderDocumentationComment,
  renderHostDefinitionHover,
  renderSignatureDocumentation,
  renderSourceCallableParameterMetadata,
  semanticTokenModifiersForHostDefinition,
  semanticTokenModifiersForVbaDefinition,
  semanticTokenTypeForHostDefinition,
  semanticTokenTypeForVbaDefinition
} from './vbaPresentation';

test('VbaPresentation renders documentation comments and parameter lookup metadata', () => {
  const documentation: DocumentationComment = {
    brief: ['Creates', 'a value.'],
    details: ['Used by signature help and hover.'],
    params: [
      'name The display name.',
      'count Number of values.'
    ],
    returns: 'Created value.'
  };

  equal(
    renderDocumentationComment(documentation),
    'Creates a value.\n\nUsed by signature help and hover.\n\n'
      + '@param name The display name.\n@param count Number of values.\n@return Created value.'
  );
  equal(
    renderSignatureDocumentation(documentation),
    'Creates a value.\n\n@return Created value.'
  );
  equal(getParameterDocumentation(documentation).get('name'), 'The display name.');
  equal(getParameterDocumentation(documentation).get('count'), 'Number of values.');
});

test('VbaPresentation maps source definitions to completion and semantic token metadata', () => {
  equal(completionKindForVbaDefinition(createVbaDefinition('constant')), 'constant');
  equal(semanticTokenTypeForVbaDefinition(createVbaDefinition('constant')), 'variable');
  deepEqual(semanticTokenModifiersForVbaDefinition(createVbaDefinition('constant')), ['readonly']);

  equal(completionKindForVbaDefinition(createVbaDefinition('sub')), 'function');
  equal(semanticTokenTypeForVbaDefinition(createVbaDefinition('typeField')), 'property');
  equal(semanticTokenTypeForVbaDefinition(createVbaDefinition('event')), 'event');
  equal(semanticTokenModifiersForVbaDefinition(createVbaDefinition('local')), undefined);
});

test('VbaPresentation renders host definitions with application context', () => {
  const definition: HostDefinition = {
    name: 'xlUp',
    kind: 'constant',
    hostApplication: 'excel',
    parentName: 'XlDirection',
    documentation: 'Moves up.',
    value: '-4162'
  };

  equal(getHostDefinitionDetail(definition), 'Excel.XlDirection.xlUp');
  equal(renderHostDefinitionHover(definition), 'Excel.XlDirection.xlUp\n\nValue: -4162\n\nMoves up.');
  equal(completionKindForHostDefinition(definition), 'constant');
  equal(semanticTokenTypeForHostDefinition(definition), 'variable');
  deepEqual(semanticTokenModifiersForHostDefinition(definition), ['readonly']);
});

test('VbaPresentation renders callable parameter metadata consistently', () => {
  const optional_parameter: CallableParameter = {
    name: 'count',
    optional: true,
    typeName: 'Long',
    defaultValue: '1'
  };

  equal(renderCallableParameterMetadata(optional_parameter), 'Long Optional. Default: 1.');
  equal(renderSourceCallableParameterMetadata(optional_parameter), 'Long Optional. Default: 1.');
  equal(renderSourceCallableParameterMetadata({ name: 'required', typeName: 'String' }), undefined);
});

function createVbaDefinition(kind: VbaDefinition['kind']): VbaDefinition {
  return {
    name: `Test${kind}`,
    kind,
    visibility: 'public',
    uri: 'file:///test.bas',
    range: {
      start: { line: 0, character: 0 },
      end: { line: 0, character: 1 }
    }
  };
}
