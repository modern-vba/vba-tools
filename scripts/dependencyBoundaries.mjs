import { promises as fs } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const ignoredDirectories = new Set(['.git', '.vs', 'node_modules', 'bin', 'obj', 'out', 'coverage']);
// These are explicitly designated foundations, not every directory outside a product.
// The integration-test owner may consume public products and is not a foundation.
const foundationOwners = new Set(['VbaTools.Syntax', 'VbaTools.ContentLengthFraming']);
const consumerOwners = new Set(['VbaDev', 'VbaLanguageServer', 'VbaDebugAdapter', 'VscodeExtension', 'IntegrationTests']);

function owner(file) {
  const normalized = file.toLowerCase();
  if (normalized.startsWith('tools/vba-dev/')) return 'VbaDev';
  if (normalized.startsWith('tools/vba-language-server/')) return 'VbaLanguageServer';
  if (normalized.startsWith('tools/vba-debug-adapter/')) return 'VbaDebugAdapter';
  if (normalized.startsWith('client/') || normalized.startsWith('src/')) return 'VscodeExtension';
  if (normalized.startsWith('tools/vba-syntax/')) return 'VbaTools.Syntax';
  if (normalized.startsWith('tools/vba-protocol-framing/')) return 'VbaTools.ContentLengthFraming';
  if (normalized.startsWith('tools/vba-integration-tests/')) return 'IntegrationTests';
  return undefined;
}

function forbidden(sourceOwner, targetOwner) {
  return (sourceOwner === 'VbaDev' && consumerOwners.has(targetOwner) && targetOwner !== 'VbaDev')
    || (foundationOwners.has(sourceOwner) && consumerOwners.has(targetOwner));
}

function contractOwner(name) {
  if (/^(VbaDev\b|VbaTools\.TypeLibRegistry\b|vba-dev\b)/i.test(name)) return 'VbaDev';
  if (/^(VbaLanguageServer\b|vba-language-server\b)/i.test(name)) return 'VbaLanguageServer';
  if (/^(VbaDebugAdapter\b|vba-debug-adapter\b)/i.test(name)) return 'VbaDebugAdapter';
  if (/^(VscodeExtension\b|vscode\b)/i.test(name)) return 'VscodeExtension';
  return undefined;
}

function decodeXml(value) {
  return value.replace(/&#(x[0-9a-f]+|[0-9]+);/gi, (_, number) =>
    String.fromCodePoint(number[0].toLowerCase() === 'x' ? parseInt(number.slice(1), 16) : Number(number)))
    .replace(/&(quot|apos|lt|gt|amp);/g, (_, name) =>
      ({ quot: '"', apos: "'", lt: '<', gt: '>', amp: '&' })[name]);
}

function attribute(tag, name) {
  const match = tag.match(new RegExp(`\\b${name}\\s*=\\s*(["'])([\\s\\S]*?)\\1`, 'i'));
  return match ? decodeXml(match[2]) : undefined;
}

function resolveInput(source, input, root) {
  const value = input.trim().replaceAll('\\', '/')
    .replaceAll('$(MSBuildThisFileDirectory)', './')
    .replaceAll('$(MSBuildProjectDirectory)', '.');
  return path.isAbsolute(value)
    ? path.relative(root, value).replaceAll('\\', '/')
    : path.posix.normalize(path.posix.join(path.posix.dirname(source), value));
}

function buildDependencies(source, text, root) {
  const dependencies = [];
  const xml = text.replace(/<!--[\s\S]*?-->/g, '');
  for (const match of xml.matchAll(/<(ProjectReference|Compile|Reference)\b([^>]*?)(?:\/>|>([\s\S]*?)<\/\1\s*>)/gi)) {
    const [, kind, attributes, body = ''] = match;
    const include = attribute(attributes, 'Include');
    if (!include) continue;
    for (const input of include.split(';')) {
      if (kind.toLowerCase() === 'reference') {
        dependencies.push({ source, kind: 'Reference', target: input, targetOwner: contractOwner(input) });
        const hintPath = body.match(/<HintPath\b[^>]*>([\s\S]*?)<\/HintPath\s*>/i)?.[1];
        if (hintPath) {
          const target = resolveInput(source, decodeXml(hintPath), root);
          dependencies.push({ source, kind: 'HintPath', target,
            targetOwner: owner(target) ?? contractOwner(path.posix.basename(target)) });
        }
      } else {
        const dependency = { source, kind, target: resolveInput(source, input, root) };
        if (kind.toLowerCase() === 'projectreference') {
          const outputAssembly = attribute(attributes, 'ReferenceOutputAssembly')
            ?? body.match(/<ReferenceOutputAssembly\b[^>]*>([\s\S]*?)<\/ReferenceOutputAssembly\s*>/i)?.[1];
          dependency.referenceOutputAssembly = outputAssembly?.trim().toLowerCase() !== 'false';
        }
        dependencies.push(dependency);
      }
    }
  }
  return dependencies;
}

// Keep literal positions so JS imports can be read without treating comments,
// fixture strings, C# raw strings, or InternalsVisibleTo names as dependencies.
function sourceTokens(text) {
  const literals = [];
  let code = '';
  for (let index = 0; index < text.length;) {
    const start = index;
    if (text.startsWith('//', index)) {
      const end = text.indexOf('\n', index);
      index = end < 0 ? text.length : end;
    } else if (text.startsWith('/*', index)) {
      const end = text.indexOf('*/', index + 2);
      index = end < 0 ? text.length : end + 2;
    } else if ('"\'`'.includes(text[index])) {
      const quote = text[index++];
      const raw = quote === '"' ? text.slice(start).match(/^"{3,}/)?.[0] : undefined;
      const verbatim = quote === '"' && text[start - 1] === '@';
      if (raw) {
        const end = text.indexOf(raw, start + raw.length);
        index = end < 0 ? text.length : end + raw.length;
      } else {
        while (index < text.length) {
          if (!verbatim && text[index] === '\\') { index += 2; continue; }
          if (verbatim && text.startsWith('""', index)) { index += 2; continue; }
          if (text[index++] === quote) break;
        }
      }
      literals.push({ start, value: text.slice(start + 1, index - 1) });
    } else {
      code += text[index++];
      continue;
    }
    code += text.slice(start, index).replace(/[^\r\n]/g, ' ');
  }
  return { code, literals };
}

function sourceDependencies(source, text, root) {
  const { code, literals } = sourceTokens(text);
  const dependencies = [];
  if (source.endsWith('.cs')) {
    const contract = /\b(?:VbaDev|VbaLanguageServer|VbaDebugAdapter|VscodeExtension|VbaTools\s*\.\s*TypeLibRegistry)\b(?:\s*\.\s*\w+)*/g;
    for (const match of code.matchAll(contract)) {
      const target = match[0].replace(/\s/g, '');
      dependencies.push({ source, kind: 'product contract', target, targetOwner: contractOwner(target) });
    }
  } else {
    for (const literal of literals) {
      const prefix = code.slice(0, literal.start);
      if (!/(?:\b(?:import|export)[^;]*?\bfrom\s*|\b(?:import|require)\s*\(\s*|\bimport\s*)$/.test(prefix)) continue;
      const target = literal.value.startsWith('.') || path.isAbsolute(literal.value)
        ? resolveInput(source, literal.value, root) : literal.value;
      const targetOwner = owner(target) ?? contractOwner(path.posix.basename(target));
      dependencies.push({ source, kind: 'import', target, targetOwner });
    }
  }
  return dependencies;
}

async function filesUnder(directory, prefix = '') {
  const files = [];
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const relative = `${prefix}${entry.name}`;
    if (entry.isDirectory() && !ignoredDirectories.has(entry.name)) {
      files.push(...await filesUnder(path.join(directory, entry.name), `${relative}/`));
    } else if (entry.isFile()) {
      files.push(relative);
    }
  }
  return files.sort();
}

/**
 * Inspect repository declarations without building products or loading assemblies.
 * Conditional declarations are all checked. This is not an MSBuild evaluator or
 * a C#/TypeScript compiler: runtime reflection and computed imports are outside
 * its scope, and unresolved protected-owner build inputs must be made explicit.
 */
export async function verifyDependencyBoundaries({ root = process.cwd() } = {}) {
  const dependencies = [];
  const violations = [];
  const files = await filesUnder(root);
  for (const source of files) {
    const buildFile = /\.(csproj|props|targets)$/i.test(source);
    const codeFile = /\.(cs|[cm]?[jt]sx?)$/i.test(source) && owner(source);
    if (!buildFile && !codeFile) continue;
    const text = await fs.readFile(path.join(root, source), 'utf8');
    const declared = buildFile ? buildDependencies(source, text, root) : sourceDependencies(source, text, root);
    const pending = declared.flatMap((dependency) => /[*?]/.test(dependency.target)
      ? [dependency, ...files.filter((file) => path.posix.matchesGlob(file.toLowerCase(), dependency.target.toLowerCase()))
        .map((target) => ({ ...dependency, target }))]
      : [dependency]);
    for (const dependency of pending) {
      if (dependency.kind.toLowerCase() === 'compile' && files.includes(dependency.target)) {
        const linked = await fs.readFile(path.join(root, dependency.target), 'utf8');
        pending.push(...sourceDependencies(dependency.target, linked, root).map((contract) => ({
          ...contract, source, kind: `Compile ${dependency.target}: ${contract.kind}`
        })));
      }
      dependencies.push(dependency);
      const targetOwner = dependency.targetOwner ?? owner(dependency.target);
      if ((owner(source) === 'VbaDev' || foundationOwners.has(owner(source)))
        && /[$@%]\(/.test(dependency.target)) {
        violations.push(`${source}: ${dependency.kind} has an unresolved build input: ${dependency.target}`);
      }
      if (forbidden(owner(source), targetOwner)) {
        violations.push(`${source}: ${dependency.kind} -> ${dependency.target} (${owner(source)} must not depend on ${targetOwner})`);
      }
    }
  }
  if (violations.length) throw new Error(`Dependency boundaries failed:\n${violations.join('\n')}`);
  return { dependencies };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    const result = await verifyDependencyBoundaries({ root: process.argv[2] ?? process.cwd() });
    console.log(`Dependency boundaries passed (${result.dependencies.length} declared dependencies).`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
