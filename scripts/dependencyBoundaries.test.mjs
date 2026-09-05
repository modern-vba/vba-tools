import test from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { verifyDependencyBoundaries } from './dependencyBoundaries.mjs';

const devProject = 'tools/vba-dev/src/VbaDev.Domain/VbaDev.Domain.csproj';
const serverProject = 'tools/vba-language-server/src/VbaLanguageServer.Cli/VbaLanguageServer.Cli.csproj';

async function repository(t, files) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-boundaries-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  for (const [name, contents] of Object.entries(files)) {
    const file = path.join(root, name);
    await fs.mkdir(path.dirname(file), { recursive: true });
    await fs.writeFile(file, contents, 'utf8');
  }
  return root;
}

function projectReference(from, to, attributes = '') {
  const relative = path.posix.relative(path.posix.dirname(from), to).replaceAll('/', '\\');
  return `<Project><ItemGroup><ProjectReference Include="${relative}" ${attributes} /></ItemGroup></Project>`;
}

test('VbaDev cannot reference a language-server project', async (t) => {
  const root = await repository(t, {
    [devProject]: projectReference(devProject, serverProject),
    [serverProject]: '<Project />'
  });

  await assert.rejects(verifyDependencyBoundaries({ root }),
    /VbaDev\.Domain\.csproj.*ProjectReference.*VbaLanguageServer\.Cli\.csproj/s);
});

test('project metadata remains a foundation even for build-order-only consumer references', async (t) => {
  const metadata = 'tools/vba-project-metadata/src/VbaTools.ProjectMetadata/Metadata.csproj';
  const root = await repository(t, {
    [metadata]: projectReference(metadata, serverProject, 'ReferenceOutputAssembly="false"'),
    [serverProject]: '<Project />'
  });

  await assert.rejects(verifyDependencyBoundaries({ root }),
    /VbaTools\.ProjectMetadata must not depend on VbaLanguageServer/);
});

test('production and test owners cannot restore reverse or foundation-to-consumer references', async (t) => {
  for (const [from, to] of [
    ['tools/vba-dev/tests/VbaDev.Tests/Test.csproj', 'tools/vba-debug-adapter/tests/VbaDebugAdapter.Tests/Test.csproj'],
    [devProject, 'client/src/Extension.csproj'],
    [devProject, 'src/Extension.csproj'],
    [devProject, 'tools/vba-integration-tests/Integration.csproj'],
    ['tools/vba-syntax/src/VbaTools.Syntax/Syntax.csproj', devProject],
    ['tools/vba-syntax/tests/VbaTools.Syntax.Tests/Tests.csproj', serverProject],
    ['tools/vba-protocol-framing/src/Framing.csproj', 'tools/vba-debug-adapter/src/Adapter.csproj']
  ]) {
    await t.test(`${from} -> ${to}`, async (t) => {
      const root = await repository(t, {
        [from]: projectReference(from, to, 'ReferenceOutputAssembly="false"'),
        [to]: '<Project />'
      });
      await assert.rejects(verifyDependencyBoundaries({ root }), /Dependency boundaries failed/);
    });
  }
});

test('build declarations cannot hide reverse dependencies in metadata or linked source', async (t) => {
  const relative = '../../../vba-language-server/src/VbaLanguageServer.Cli';
  for (const [name, declaration, kind] of [
    ['Test.csproj', `<ProjectReference Include='${relative}/VbaLanguageServer.Cli.csproj'><ReferenceOutputAssembly>false</ReferenceOutputAssembly></ProjectReference>`, 'ProjectReference'],
    ['Directory.Build.props', `<ProjectReference Condition="'$(Configuration)' == 'Release'" Include="${relative}/VbaLanguageServer.Cli.csproj" />`, 'ProjectReference'],
    ['Build.targets', `<Reference Include="Consumer"><HintPath>${relative}/bin/Release/VbaLanguageServer.Cli.dll</HintPath></Reference>`, 'HintPath'],
    ['Test.csproj', '<Reference Include="VbaLanguageServer.Cli" />', 'Reference'],
    ['Test.csproj', `<Compile Include="${relative}/Contract.cs" Link="Contract.cs" />`, 'Compile'],
    ['Test.csproj', `<Compile Include="${relative}/Contract.cs"><Link>Contract.cs</Link></Compile>`, 'Compile'],
    ['Test.csproj', `<Compile Include="$(MSBuildThisFileDirectory)${relative}/Contract.cs" Link="Contract.cs" />`, 'Compile'],
    ['Test.csproj', `<Compile Include="${relative}/**/*.cs" Link="%(Filename)%(Extension)" />`, 'Compile']
  ]) {
    await t.test(`${name}: ${declaration}`, async (t) => {
      const root = await repository(t, {
        [`tools/vba-dev/src/VbaDev.Domain/${name}`]: `<Project><ItemGroup>${declaration}</ItemGroup></Project>`
      });
      await assert.rejects(verifyDependencyBoundaries({ root }), new RegExp(kind));
    });
  }
});

test('source imports and qualified product contracts preserve the same direction', async (t) => {
  for (const [source, contents] of [
    ['tools/vba-dev/src/Illegal.cs', 'using VbaLanguageServer;'],
    ['tools/vba-dev/tests/Illegal.cs', 'global using Contract = global::VbaDebugAdapter.Contract;'],
    ['tools/vba-dev/src/Illegal.cs', 'class X { global::VbaLanguageServer.Project Contract; }'],
    ['tools/vba-syntax/src/Illegal.cs', 'using static VbaDev.Domain.Contract;'],
    ['tools/vba-protocol-framing/tests/Illegal.cs', 'using VbaTools.TypeLibRegistry;'],
    ['tools/vba-syntax/src/illegal.ts', 'import type { Contract } from "../../vba-dev/src/contract";'],
    ['tools/vba-dev/src/illegal.ts', 'export { Contract } from "../../vba-language-server/src/contract";'],
    ['tools/vba-dev/src/illegal.mjs', 'const contract = await import("../../vba-debug-adapter/src/contract.mjs");'],
    ['tools/vba-syntax/src/illegal.ts', 'import * as vscode from "vscode";'],
    ['tools/vba-syntax/src/illegal.ts', 'const contract = require("../../../vba-dev-contract.json");']
  ]) {
    await t.test(contents, async (t) => {
      const root = await repository(t, { [source]: contents });
      await assert.rejects(verifyDependencyBoundaries({ root }), /(?:contract|import)/i);
    });
  }
});

test('allowed dependencies remain inventoried, including build-order-only references and neutral integration tests', async (t) => {
  const syntax = 'tools/vba-syntax/src/VbaTools.Syntax/Syntax.csproj';
  const framing = 'tools/vba-protocol-framing/src/Framing.csproj';
  const integration = 'tools/vba-integration-tests/tests/Integration.csproj';
  const root = await repository(t, {
    [syntax]: projectReference(syntax, framing),
    [framing]: '<Project />',
    [devProject]: projectReference(devProject, syntax),
    [serverProject]: projectReference(serverProject, devProject, 'ReferenceOutputAssembly="false"'),
    [integration]: projectReference(integration, devProject),
    'tools/vba-integration-tests/tests/Integration.cs': 'using VbaDev.Domain; class Test { string Executable = "vba-language-server.exe"; }',
    'tools/vba-syntax/src/Syntax.cs': 'using VbaTools.ContentLengthFraming; namespace VbaTools.Syntax;',
    'tools/vba-dev/src/Build.props': '<Project><ItemGroup><Reference Include="Own"><HintPath>VbaDev.Domain.dll</HintPath></Reference><Compile Include="Own.cs" /></ItemGroup></Project>',
    'client/src/contract.ts': 'import { contract } from "../../vba-dev-contract.json";'
  });

  const { dependencies } = await verifyDependencyBoundaries({ root });
  assert.equal(dependencies.find((dependency) => dependency.source === serverProject).referenceOutputAssembly, false);
  assert.deepEqual(new Set(dependencies.map((dependency) => dependency.kind)),
    new Set(['ProjectReference', 'Reference', 'HintPath', 'Compile', 'product contract', 'import']));
});

test('unresolved build inputs cannot silently bypass protected owners', async (t) => {
  const root = await repository(t, {
    [devProject]: '<Project><ItemGroup><ProjectReference Include="$(ConsumerProject)" ReferenceOutputAssembly="false" /></ItemGroup></Project>'
  });

  await assert.rejects(verifyDependencyBoundaries({ root }), /unresolved.*ConsumerProject/i);
});

test('linked Compile globs are checked against every included repository source', async (t) => {
  const root = await repository(t, {
    [devProject]: '<Project><ItemGroup><Compile Include="../../../**/Contract.cs" Link="Contract.cs" /></ItemGroup></Project>',
    'tools/vba-language-server/src/Contract.cs': 'namespace Contracts; class Contract {}'
  });

  await assert.rejects(verifyDependencyBoundaries({ root }), /Compile.*tools\/vba-language-server\/src\/Contract\.cs/);
});

test('absolute references into consumer projects keep repository ownership', async (t) => {
  const root = await repository(t, { [serverProject]: '<Project />' });
  const source = path.join(root, devProject);
  await fs.mkdir(path.dirname(source), { recursive: true });
  await fs.writeFile(source, `<Project><ItemGroup><ProjectReference Include="${path.join(root, serverProject)}" /></ItemGroup></Project>`, 'utf8');

  await assert.rejects(verifyDependencyBoundaries({ root }), /VbaDev must not depend on VbaLanguageServer/);
});

test('comments, fixture literals, friend-assembly names, and generated outputs do not create dependencies', async (t) => {
  const root = await repository(t, {
    [devProject]: '<Project><!-- <ProjectReference Include="../../../vba-language-server/Server.csproj" /> --></Project>',
    'tools/vba-syntax/src/Syntax.cs': [
      '// using VbaDev.Domain;',
      '/* using VbaDebugAdapter; */',
      '[assembly: InternalsVisibleTo("VbaLanguageServer.Tests")]',
      'class Fixture {',
      '  string Ordinary = "using VbaDev.Domain;";',
      '  string Verbatim = @"using VbaDev.Domain; ""quoted""";',
      '  string Raw = """ using VbaLanguageServer; "literal" """;',
      '}'
    ].join('\n'),
    'tools/vba-syntax/src/fixture.ts': [
      '// import "vscode";',
      '/* export * from "vba-dev"; */',
      'const fixture = `import "vscode";`;',
      'import { local } from "./local";'
    ].join('\n'),
    'tools/vba-dev/src/obj/Generated.cs': 'using VbaLanguageServer;',
    'tools/vba-syntax/bin/Generated.cs': 'using VbaDev;',
    'node_modules/package/project.csproj': projectReference('node_modules/package/project.csproj', serverProject)
  });

  const { dependencies } = await verifyDependencyBoundaries({ root });
  assert.deepEqual(dependencies, [{ source: 'tools/vba-syntax/src/fixture.ts', kind: 'import', target: 'tools/vba-syntax/src/local', targetOwner: 'VbaTools.Syntax' }]);
});

test('linked source outside product folders is checked in the compiling foundation context', async (t) => {
  const source = 'tools/vba-syntax/src/VbaTools.Syntax/Syntax.csproj';
  const root = await repository(t, {
    [source]: '<Project><ItemGroup><Compile Include="../../../../shared/Contract.cs" Link="Contract.cs" /></ItemGroup></Project>',
    'shared/Contract.cs': 'using VbaDev.Domain;'
  });

  await assert.rejects(verifyDependencyBoundaries({ root }), /Syntax\.csproj.*shared\/Contract\.cs.*VbaDev\.Domain/s);
});

test('a consumer assembly copied outside its product folder is rejected by HintPath identity', async (t) => {
  const root = await repository(t, {
    [devProject]: '<Project><ItemGroup><Reference Include="Copied"><HintPath>../../../../lib/vba-language-server.dll</HintPath></Reference></ItemGroup></Project>'
  });

  await assert.rejects(verifyDependencyBoundaries({ root }), /HintPath.*lib\/vba-language-server\.dll/);
});
