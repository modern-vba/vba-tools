import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  collectHostClassProjectionFormSources
} from './hostClassProjectionSourceCollection';

test('HostClass form collection merges dirty-only open sources and overlays disk bytes', async () => {
  const sourceSetPath = path.resolve('workspace', 'Project', 'src', 'Book1');
  const overlayPath = path.join(sourceSetPath, 'Overlay.frm');
  const dirtyOnlyPath = path.join(sourceSetPath, 'DirtyOnly.frm');
  const ordinaryClassPath = path.join(sourceSetPath, 'OrdinaryClass.cls');
  const diskReads: string[] = [];
  let sentMethod: string | undefined;
  let sentParameters: unknown;

  const sources = await collectHostClassProjectionFormSources(
    sourceSetPath,
    [{
      filePath: overlayPath,
      sourceUri: 'file:///workspace/Project/src/Book1/Overlay.frm'
    }, {
      filePath: ordinaryClassPath,
      sourceUri: 'file:///workspace/Project/src/Book1/OrdinaryClass.cls'
    }],
    [{
      scheme: 'file',
      filePath: overlayPath,
      sourceUri: 'file:///workspace/Project/src/Book1/Overlay.frm',
      text: 'Attribute VB_Name = "OverlayOpen"\n'
    }, {
      scheme: 'file',
      filePath: dirtyOnlyPath,
      sourceUri: 'file:///workspace/Project/src/Book1/DirtyOnly.frm',
      text: 'Attribute VB_Name = "DirtyOnly"\n'
    }, {
      scheme: 'untitled',
      filePath: path.join(sourceSetPath, 'Ignored.frm'),
      sourceUri: 'untitled:Ignored.frm',
      text: 'Attribute VB_Name = "Ignored"\n'
    }, {
      scheme: 'file',
      filePath: path.resolve('workspace', 'Other', 'Outside.frm'),
      sourceUri: 'file:///workspace/Other/Outside.frm',
      text: 'Attribute VB_Name = "Outside"\n'
    }],
    async (source) => {
      diskReads.push(source.filePath);
      return 'Attribute VB_Name = "OverlayDisk"\n';
    },
    {
      sendRequest: async (method, parameters) => {
        sentMethod = method;
        sentParameters = parameters;
        return {
          sources: [
            {
              sourceUri: 'file:///workspace/Project/src/Book1/DirtyOnly.frm',
              kind: 'form',
              state: 'authoritative',
              name: 'DirtyOnly'
            },
            {
              sourceUri: 'file:///workspace/Project/src/Book1/Overlay.frm',
              kind: 'form',
              state: 'authoritative',
              name: 'OverlayOpen'
            }
          ]
        };
      }
    }
  );

  assert.deepEqual(sources, [{
    sourceUri: 'file:///workspace/Project/src/Book1/DirtyOnly.frm',
    kind: 'form',
    moduleIdentity: { state: 'authoritative', name: 'DirtyOnly' }
  }, {
    sourceUri: 'file:///workspace/Project/src/Book1/Overlay.frm',
    kind: 'form',
    moduleIdentity: { state: 'authoritative', name: 'OverlayOpen' }
  }]);
  assert.equal(sentMethod, 'vba/moduleIdentityMetadata');
  assert.deepEqual(sentParameters, {
    sources: [{
      sourceUri: 'file:///workspace/Project/src/Book1/DirtyOnly.frm',
      kind: 'form',
      text: 'Attribute VB_Name = "DirtyOnly"\n'
    }, {
      sourceUri: 'file:///workspace/Project/src/Book1/Overlay.frm',
      kind: 'form',
      text: 'Attribute VB_Name = "OverlayOpen"\n'
    }]
  });
  assert.deepEqual(diskReads, []);
});
