import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  Range,
  TestItem,
  Uri,
  workspace,
  tests
} from 'vscode';
import { createVscodeTestControllerAdapter } from '../../vscodeAdapters';

export async function runTestExplorerNavigationIntegrationTests(): Promise<void> {
  const controller = tests.createTestController(
    `vbaTools.navigation.${randomUUID()}`,
    'VBA Test Navigation Integration'
  );
  const sourcePath = join(tmpdir(), `Test_Module-${randomUUID()}.bas`);
  const sourceUri = Uri.file(sourcePath);
  try {
    await workspace.fs.writeFile(
      sourceUri,
      Buffer.from(
        'Attribute VB_Name = "Test_Module"\nOption Explicit\n\nPublic Sub Test_Passes()\nEnd Sub\n',
        'utf8'
      )
    );
    const adapter = createVscodeTestControllerAdapter(controller);
    const projectItem = adapter.createTestItem('project', 'Project') as TestItem;
    const documentItem = adapter.createTestItem('document', 'Book1') as TestItem;
    const moduleItem = adapter.createTestItem('module', 'Test_Module') as TestItem;
    const procedureItem = adapter.createTestItem(
      'procedure:Test_Module:Test_Passes',
      'Test_Passes',
      sourcePath,
      {
        start: { line: 3, character: 11 },
        end: { line: 3, character: 22 }
      }
    ) as TestItem;
    moduleItem.children.add(procedureItem);
    documentItem.children.add(moduleItem);
    projectItem.children.add(documentItem);
    controller.items.add(projectItem);

    const item = controller.items
      .get('project')
      ?.children.get('document')
      ?.children.get('module')
      ?.children.get('procedure:Test_Module:Test_Passes');

    assert.ok(item);
    assert.equal(item.uri?.fsPath.toLowerCase(), sourcePath.toLowerCase());
    assert.deepEqual(item.range, new Range(3, 11, 3, 22));
    assert.equal(controller.items.get('project')?.range, undefined);
    assert.equal(controller.items.get('project')?.children.get('document')?.range, undefined);
    assert.equal(
      controller.items.get('project')?.children.get('document')?.children.get('module')?.range,
      undefined
    );
    console.log('PASS Test Explorer exposes the procedure URI and declaration range through the VS Code Testing API');
  } finally {
    controller.dispose();
    await workspace.fs.delete(sourceUri, { useTrash: false });
  }
}
