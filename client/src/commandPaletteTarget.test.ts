import assert from 'node:assert/strict';
import test from 'node:test';
import * as path from 'node:path';

import {
  CommandPaletteProjectTarget,
  CommandPaletteTarget,
  CommandPaletteTargetResolutionOptions,
  parseCommandPaletteManifestSelectionProjection,
  retainExactCommandPaletteTarget,
  resolveCommandPaletteTarget,
  selectInitialCommandPaletteDocumentFocus
} from './commandPaletteTarget';

const windowsPath = path.win32;

test('selection projection reads only disk selection-critical manifest fields', () => {
  const projection = parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: {
      Book1: {
        sourcePath: 'src/Book1',
        templatePath: 42,
        futureProperty: { acceptedByTheNarrowProjection: true }
      }
    },
    futureRootProperty: true
  }));

  assert.deepEqual(projection, {
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: [{ name: 'Book1', sourcePath: 'src/Book1' }]
  });
});

test('selection projection rejects malformed selection fields without narrowing complete manifest validation', () => {
  assert.equal(parseCommandPaletteManifestSelectionProjection('{broken'), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: '   ',
    documents: { Book1: { sourcePath: 'src/Book1' } }
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: []
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: { '': { sourcePath: 'src/Book1' } }
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 2,
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: { Book1: { sourcePath: 'src/Book1' } }
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: '   ',
    primaryDocument: 'Book1',
    documents: { Book1: { sourcePath: 'src/Book1' } }
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: { Book1: { sourcePath: '   ' } }
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: 'Missing',
    documents: { Book1: { sourcePath: 'src/Book1' } }
  })), undefined);
  assert.equal(parseCommandPaletteManifestSelectionProjection(JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: {
      Book1: { sourcePath: 'src/Book1' },
      book1: { sourcePath: 'src/Book2' }
    }
  })), undefined);
});

test('a usable nearest on-disk manifest selects its project before workspace candidates', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Nearest');
  const manifestPath = windowsPath.join(projectRoot, 'vba-project.json');
  let workspaceSearches = 0;
  let projectChoices = 0;

  const target = await resolveCommandPaletteTarget({
    scope: 'project',
    snapshot: {
      activeFilePath: windowsPath.join(projectRoot, 'src', 'Book1', 'Module1.bas'),
      activeEditorFilePath: windowsPath.join(projectRoot, 'src', 'Book1', 'Module1.bas'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    },
    workspaceRoots: ['C:\\work'],
    fileExists: async (candidate) => candidate === manifestPath,
    findProjectManifests: async () => {
      workspaceSearches += 1;
      return [windowsPath.join('C:\\work', 'Other', 'vba-project.json')];
    },
    readTextFile: async (candidate) => {
      assert.equal(candidate, manifestPath);
      return manifestText();
    },
    resolvePathIdentity: async (candidate) => ({ canonicalPath: windowsPath.resolve(candidate) }),
    chooseProject: async () => {
      projectChoices += 1;
      return undefined;
    },
    chooseDocument: async () => undefined,
    showErrorMessage: async () => undefined
  });

  assert.equal(target?.project.projectRoot, projectRoot);
  assert.equal(target?.project.manifestPath, manifestPath);
  assert.equal(target?.document, undefined);
  assert.equal(workspaceSearches, 0);
  assert.equal(projectChoices, 0);
});

test('an unusable nearest manifest fails closed without falling through', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Broken');
  const manifestPath = windowsPath.join(projectRoot, 'vba-project.json');
  const errors: string[] = [];
  let workspaceSearches = 0;
  let projectChoices = 0;

  const target = await resolveCommandPaletteTarget({
    scope: 'project',
    snapshot: {
      activeFilePath: windowsPath.join(projectRoot, 'src', 'Module1.bas'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    },
    workspaceRoots: ['C:\\work'],
    fileExists: async (candidate) => candidate === manifestPath,
    findProjectManifests: async () => {
      workspaceSearches += 1;
      return [windowsPath.join('C:\\work', 'Valid', 'vba-project.json')];
    },
    readTextFile: async () => '{"schemaVersion":1}',
    resolvePathIdentity: async (candidate) => ({ canonicalPath: windowsPath.resolve(candidate) }),
    chooseProject: async () => {
      projectChoices += 1;
      return undefined;
    },
    chooseDocument: async () => undefined,
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  });

  assert.equal(target, undefined);
  assert.equal(workspaceSearches, 0);
  assert.equal(projectChoices, 0);
  assert.equal(errors.length, 1);
  assert.match(errors[0]!, /Broken[\\/]vba-project\.json/);
  assert.match(errors[0]!, /cannot be used for Command Palette targeting/i);
});

test('without a containing manifest one usable workspace project is automatic', async () => {
  const validRoot = windowsPath.join('C:\\work', 'Valid');
  const brokenRoot = windowsPath.join('C:\\work', 'Broken');
  let projectChoices = 0;
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'project',
    snapshot: emptySnapshot(),
    findProjectManifests: async () => [
      windowsPath.join(brokenRoot, 'vba-project.json'),
      windowsPath.join(validRoot, 'vba-project.json')
    ],
    readTextFile: async (manifestPath) => manifestPath.startsWith(validRoot)
      ? manifestText()
      : '{"schemaVersion":1}',
    chooseProject: async () => {
      projectChoices += 1;
      return undefined;
    }
  }));

  assert.equal(target?.project.projectRoot, validRoot);
  assert.equal(projectChoices, 0);
});

test('multiple usable workspace projects require an explicit invocation-local choice', async () => {
  const firstRoot = windowsPath.join('C:\\work', 'First');
  const secondRoot = windowsPath.join('C:\\work', 'Second');
  let candidatesSeen: readonly CommandPaletteProjectTarget[] = [];
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'project',
    snapshot: emptySnapshot(),
    findProjectManifests: async () => [
      windowsPath.join(firstRoot, 'vba-project.json'),
      windowsPath.join(secondRoot, 'vba-project.json')
    ],
    chooseProject: async (candidates) => {
      candidatesSeen = candidates;
      return candidates[1];
    }
  }));

  assert.deepEqual(candidatesSeen.map((candidate) => candidate.projectRoot), [firstRoot, secondRoot]);
  assert.equal(target?.project.projectRoot, secondRoot);
});

test('zero usable projects reports one failure and project choice cancellation is silent', async () => {
  const errors: string[] = [];
  const noProject = await resolveCommandPaletteTarget(createOptions({
    scope: 'project',
    snapshot: emptySnapshot(),
    findProjectManifests: async () => [windowsPath.join('C:\\work', 'Broken', 'vba-project.json')],
    readTextFile: async () => '{broken',
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  }));
  assert.equal(noProject, undefined);
  assert.equal(errors.length, 1);
  assert.match(errors[0]!, /could not select a workbook-backed project/i);

  errors.length = 0;
  const cancelled = await resolveCommandPaletteTarget(createOptions({
    scope: 'project',
    snapshot: emptySnapshot(),
    findProjectManifests: async () => [
      windowsPath.join('C:\\work', 'First', 'vba-project.json'),
      windowsPath.join('C:\\work', 'Second', 'vba-project.json')
    ],
    chooseProject: async () => undefined,
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  }));
  assert.equal(cancelled, undefined);
  assert.deepEqual(errors, []);
});

test('an active non-primary exported source selects its exact manifest document', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'document',
    snapshot: {
      activeEditorFilePath: windowsPath.join(projectRoot, 'src', 'Book2', 'Module2.cls'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    },
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText({
      Book1: { sourcePath: 'src/Book1' },
      Book2: { sourcePath: 'src/Book2' }
    })
  }));

  assert.equal(target?.document?.name, 'Book2');
});

test('a sole manifest document is automatic when no active source is eligible', async () => {
  let documentChoices = 0;
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'document',
    snapshot: {
      activeEditorFilePath: windowsPath.join('C:\\notes', 'README.md'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    },
    chooseDocument: async () => {
      documentChoices += 1;
      return undefined;
    }
  }));

  assert.equal(target?.document?.name, 'Book1');
  assert.equal(documentChoices, 0);
});

test('visible source ownership sets multi-document QuickPick focus without accepting it', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  let initiallyFocused = '';
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'document',
    snapshot: {
      activeEditorFilePath: windowsPath.join('C:\\notes', 'README.md'),
      visibleEditorFilePaths: [
        windowsPath.join(projectRoot, 'src', 'Book2', 'Module2.frm'),
        windowsPath.join(projectRoot, 'src', 'Book2', 'Feature.bas')
      ],
      openDocumentFilePaths: [windowsPath.join(projectRoot, 'src', 'Book1', 'Module1.bas')]
    },
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText({
      Book1: { sourcePath: 'src/Book1' },
      Book2: { sourcePath: 'src/Book2' }
    }),
    chooseDocument: async (documents, focused) => {
      initiallyFocused = focused.name;
      return documents[0];
    }
  }));

  assert.equal(initiallyFocused, 'Book2');
  assert.equal(target?.document?.name, 'Book1');
});

test('document focus gives active ownership priority and uses primary for empty evidence', () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const book1 = {
    name: 'Book1',
    sourcePath: 'src/Book1',
    sourceRoot: windowsPath.join(projectRoot, 'src', 'Book1'),
    sourceRootIdentity: { canonicalPath: windowsPath.join(projectRoot, 'src', 'Book1') }
  };
  const book2 = {
    name: 'Book2',
    sourcePath: 'src/Book2',
    sourceRoot: windowsPath.join(projectRoot, 'src', 'Book2'),
    sourceRootIdentity: { canonicalPath: windowsPath.join(projectRoot, 'src', 'Book2') }
  };
  const project = {
    projectRoot,
    manifestPath: windowsPath.join(projectRoot, 'vba-project.json'),
    projectName: 'MultiBook',
    primaryDocument: 'Book1',
    documents: [book1, book2]
  };

  assert.equal(
    selectInitialCommandPaletteDocumentFocus(project, [book2], [book1], [book1]),
    book2
  );
  assert.equal(
    selectInitialCommandPaletteDocumentFocus(project, [], [], []),
    book1
  );
});

test('mixed visible evidence falls to unanimous open evidence, then primary document', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const documents = {
    Book1: { sourcePath: 'src/Book1' },
    Book2: { sourcePath: 'src/Book2' }
  };
  const focused: string[] = [];
  const base = {
    scope: 'document' as const,
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText(documents),
    chooseDocument: async (_documents: Parameters<CommandPaletteTargetResolutionOptions['chooseDocument']>[0], initial: Parameters<CommandPaletteTargetResolutionOptions['chooseDocument']>[1]) => {
      focused.push(initial.name);
      return undefined;
    }
  };

  await resolveCommandPaletteTarget(createOptions({
    ...base,
    snapshot: {
      visibleEditorFilePaths: [
        windowsPath.join(projectRoot, 'src', 'Book1', 'One.bas'),
        windowsPath.join(projectRoot, 'src', 'Book2', 'Two.bas')
      ],
      openDocumentFilePaths: [
        windowsPath.join(projectRoot, 'src', 'Book2', 'Two.bas'),
        windowsPath.join(projectRoot, 'src', 'Book2', 'Three.cls')
      ]
    }
  }));
  await resolveCommandPaletteTarget(createOptions({
    ...base,
    snapshot: {
      visibleEditorFilePaths: [],
      openDocumentFilePaths: [
        windowsPath.join(projectRoot, 'src', 'Book1', 'One.bas'),
        windowsPath.join(projectRoot, 'src', 'Book2', 'Two.bas')
      ]
    }
  }));

  assert.deepEqual(focused, ['Book2', 'Book1']);
});

test('document choice cancellation returns no target', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'document',
    snapshot: emptySnapshot(),
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText({
      Book1: { sourcePath: 'src/Book1' },
      Book2: { sourcePath: 'src/Book2' }
    }),
    chooseDocument: async () => undefined
  }));

  assert.equal(target, undefined);
});

test('absolute and parent-relative source roots never choose among workspace projects', async () => {
  const firstRoot = windowsPath.join('C:\\work', 'First');
  const secondRoot = windowsPath.join('C:\\work', 'Second');
  const sharedRoot = windowsPath.join('C:\\shared', 'SecondSources');
  let projectChoices = 0;
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'document',
    snapshot: {
      activeEditorFilePath: windowsPath.join(sharedRoot, 'Module2.bas'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    },
    findProjectManifests: async () => [
      windowsPath.join(firstRoot, 'vba-project.json'),
      windowsPath.join(secondRoot, 'vba-project.json')
    ],
    readTextFile: async (manifestPath) => manifestPath.startsWith(firstRoot)
      ? manifestText({ Book1: { sourcePath: '..\\SharedForFirst' } })
      : manifestText({ Book2: { sourcePath: sharedRoot } }, 'Book2'),
    chooseProject: async (candidates) => {
      projectChoices += 1;
      return candidates[0];
    }
  }));

  assert.equal(projectChoices, 1);
  assert.equal(target?.project.projectRoot, firstRoot);
  assert.equal(target?.document?.name, 'Book1');
});

test('active frx and non-VBA files do not authorize document selection', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const selectedByChooser: string[] = [];
  for (const extension of ['.frx', '.txt']) {
    const target = await resolveCommandPaletteTarget(createOptions({
      scope: 'document',
      snapshot: {
        activeEditorFilePath: windowsPath.join(projectRoot, 'src', 'Book2', `Module2${extension}`),
        visibleEditorFilePaths: [],
        openDocumentFilePaths: []
      },
      findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
      readTextFile: async () => manifestText({
        Book1: { sourcePath: 'src/Book1' },
        Book2: { sourcePath: 'src/Book2' }
      }),
      chooseDocument: async (documents) => {
        selectedByChooser.push(extension);
        return documents[0];
      }
    }));
    assert.equal(target?.document?.name, 'Book1');
  }

  assert.deepEqual(selectedByChooser, ['.frx', '.txt']);
});

test('an eligible active source with unresolvable identity fails instead of falling into a chooser', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const activeSource = windowsPath.join(projectRoot, 'src', 'Book2', 'Module2.bas');
  let documentChoices = 0;
  const errors: string[] = [];
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'document',
    snapshot: {
      activeEditorFilePath: activeSource,
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    },
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText({
      Book1: { sourcePath: 'src/Book1' },
      Book2: { sourcePath: 'src/Book2' }
    }),
    resolvePathIdentity: async (candidate) => {
      if (candidate === activeSource) {
        throw new Error('identity unavailable');
      }
      return { canonicalPath: candidate };
    },
    chooseDocument: async () => {
      documentChoices += 1;
      return undefined;
    },
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  }));

  assert.equal(target, undefined);
  assert.equal(documentChoices, 0);
  assert.equal(errors.length, 1);
  assert.match(errors[0]!, /active source ownership cannot be resolved/i);
});

for (const overlapCase of [
  {
    name: 'equal roots',
    first: 'src/Shared',
    second: 'src/Shared',
    identity: undefined
  },
  {
    name: 'nested roots',
    first: 'src/Shared',
    second: 'src/Shared/Nested',
    identity: undefined
  },
  {
    name: 'case-aliased roots',
    first: 'src/Shared',
    second: 'SRC/shared',
    identity: undefined
  },
  {
    name: 'junction-aliased roots',
    first: 'src/Physical',
    second: 'src/Junction',
    identity: (candidate: string) => ({
      canonicalPath: candidate.endsWith('Junction')
        ? candidate.replace(/Junction$/u, 'Physical')
        : candidate
    })
  },
  {
    name: 'symbolic-link-aliased roots',
    first: 'src/Physical',
    second: 'src/SymbolicLink',
    identity: (candidate: string) => ({
      canonicalPath: candidate.endsWith('SymbolicLink')
        ? candidate.replace(/SymbolicLink$/u, 'Physical')
        : candidate
    })
  },
  {
    name: 'filesystem-object-aliased roots',
    first: 'src/FirstSpelling',
    second: 'src/SecondSpelling',
    identity: (candidate: string) => ({
      canonicalPath: candidate,
      objectIdentity: 'volume-7:file-42'
    })
  }
] as const) {
  test(`DocumentSourceSetIsolation rejects ${overlapCase.name} before document choice`, async () => {
    let documentChoices = 0;
    const errors: string[] = [];
    const target = await resolveCommandPaletteTarget(createOptions({
      scope: 'document',
      snapshot: emptySnapshot(),
      readTextFile: async () => manifestText({
        Book1: { sourcePath: overlapCase.first },
        Book2: { sourcePath: overlapCase.second }
      }),
      resolvePathIdentity: async (candidate) => overlapCase.identity?.(candidate) ?? ({
        canonicalPath: candidate
      }),
      chooseDocument: async () => {
        documentChoices += 1;
        return undefined;
      },
      showErrorMessage: async (message) => {
        errors.push(message);
      }
    }));

    assert.equal(target, undefined);
    assert.equal(documentChoices, 0);
    assert.equal(errors.length, 1);
    assert.match(errors[0]!, /could not select a workbook-backed project/i);
  });
}

test('an unresolvable document source root makes the selection projection unusable', async () => {
  const target = await resolveCommandPaletteTarget(createOptions({
    scope: 'project',
    snapshot: emptySnapshot(),
    readTextFile: async () => manifestText({
      Book1: { sourcePath: 'src/Missing' }
    }),
    resolvePathIdentity: async () => {
      throw new Error('path does not exist');
    }
  }));

  assert.equal(target, undefined);
});

test('repeated invocations use the newly captured active context without remembered targets', async () => {
  const projectRoot = windowsPath.join('C:\\work', 'Multi');
  const shared = {
    scope: 'document' as const,
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText({
      Book1: { sourcePath: 'src/Book1' },
      Book2: { sourcePath: 'src/Book2' }
    })
  };

  const first = await resolveCommandPaletteTarget(createOptions({
    ...shared,
    snapshot: {
      activeEditorFilePath: windowsPath.join(projectRoot, 'src', 'Book1', 'One.bas'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    }
  }));
  const second = await resolveCommandPaletteTarget(createOptions({
    ...shared,
    snapshot: {
      activeEditorFilePath: windowsPath.join(projectRoot, 'src', 'Book2', 'Two.bas'),
      visibleEditorFilePaths: [],
      openDocumentFilePaths: []
    }
  }));

  assert.equal(first?.document?.name, 'Book1');
  assert.equal(second?.document?.name, 'Book2');
});

test('exact target retention accepts only the same project and physical document identity', () => {
  const selected = createExactTarget();
  const refreshedDocument = {
    ...selected.document!,
    sourceRoot: 'C:\\canonical\\Book1',
    sourceRootIdentity: {
      canonicalPath: 'C:\\canonical\\Book1',
      objectIdentity: 'volume-1:file-42'
    }
  };
  const refreshedProject: CommandPaletteProjectTarget = {
    ...selected.project,
    documents: [refreshedDocument]
  };

  const retained = retainExactCommandPaletteTarget(selected, refreshedProject);

  assert.equal(retained?.project, refreshedProject);
  assert.equal(retained?.document, refreshedDocument);
});

test('exact target retention rejects project, document, and source identity changes', () => {
  const selected = createExactTarget();
  const changedDocument = {
    ...selected.document!,
    sourceRoot: 'C:\\other\\Book1',
    sourceRootIdentity: { canonicalPath: 'C:\\other\\Book1' }
  };

  for (const refreshed of [
    { ...selected.project, projectName: 'Retargeted' },
    { ...selected.project, documents: [] },
    { ...selected.project, documents: [changedDocument] },
    { ...selected.project, manifestPath: 'C:\\work\\Other\\vba-project.json' }
  ]) {
    assert.equal(retainExactCommandPaletteTarget(selected, refreshed), undefined);
  }
});

function manifestText(
  documents: Record<string, { sourcePath: string }> = {
    Book1: { sourcePath: 'src/Book1' }
  },
  primaryDocument = 'Book1'
): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName: 'MultiBook',
    primaryDocument,
    documents
  });
}

function createExactTarget(): CommandPaletteTarget {
  const projectRoot = 'C:\\work\\Project';
  const manifestPath = windowsPath.join(projectRoot, 'vba-project.json');
  const document = {
    name: 'Book1',
    sourcePath: 'src/Book1',
    sourceRoot: windowsPath.join(projectRoot, 'src', 'Book1'),
    sourceRootIdentity: {
      canonicalPath: windowsPath.join(projectRoot, 'src', 'Book1'),
      objectIdentity: 'volume-1:file-42'
    }
  };
  return {
    project: {
      projectRoot,
      manifestPath,
      projectName: 'Project',
      primaryDocument: 'Book1',
      documents: [document]
    },
    document
  };
}

function emptySnapshot(): CommandPaletteTargetResolutionOptions['snapshot'] {
  return {
    visibleEditorFilePaths: [],
    openDocumentFilePaths: []
  };
}

function createOptions(
  overrides: Partial<CommandPaletteTargetResolutionOptions>
): CommandPaletteTargetResolutionOptions {
  const projectRoot = windowsPath.join('C:\\work', 'Project');
  return {
    scope: 'project',
    snapshot: emptySnapshot(),
    workspaceRoots: ['C:\\work'],
    fileExists: async () => false,
    findProjectManifests: async () => [windowsPath.join(projectRoot, 'vba-project.json')],
    readTextFile: async () => manifestText(),
    resolvePathIdentity: async (candidate) => ({
      canonicalPath: windowsPath.resolve(candidate)
    }),
    chooseProject: async (candidates) => candidates[0],
    chooseDocument: async (documents) => documents[0],
    showErrorMessage: async () => undefined,
    ...overrides
  };
}
