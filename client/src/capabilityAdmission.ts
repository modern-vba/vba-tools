export interface VbaDevCommandCapability {
  outputSchemaVersion: string;
}

export interface VbaDevCapabilities {
  toolVersion: string;
  contractVersion: string;
  featureVersions?: Record<string, string> | undefined;
  activeWindowsCodePage?: number | undefined;
  commands: Record<string, VbaDevCommandCapability>;
}

export interface RequiredVbaDevContract {
  contractVersion: string;
  featureVersions?: Record<string, string> | undefined;
  commandSchemaVersions: Record<string, string>;
}

export interface CapabilityRejection {
  readonly code: 'InvalidJson' | 'DuplicateProperty' | 'MissingCapability' | 'InvalidConsumedValue' | 'VersionMismatch';
  readonly path: readonly string[];
  readonly expected?: string;
  readonly actual?: string;
}

export type VbaDevCapabilityAdmission =
  | { readonly accepted: true; readonly facts: VbaDevCapabilities }
  | { readonly accepted: false; readonly rejection: CapabilityRejection };

const activeCodePageFeature = 'sourceSnapshot.activeWindowsCodePage';

/** Admits a complete raw capabilities response before callers select an executable. */
export function admitVbaDevCapabilities(raw: string, required: RequiredVbaDevContract): VbaDevCapabilityAdmission {
  if (hasUnpairedSurrogate(raw)) return rejected('InvalidJson', []);
  let parsed: unknown;
  let duplicate: string | undefined;
  try {
    duplicate = findDuplicateProperty(raw);
    parsed = JSON.parse(raw) as unknown;
  } catch {
    return rejected('InvalidJson', []);
  }
  if (duplicate !== undefined) return rejected('DuplicateProperty', [duplicate]);
  if (!isRecord(parsed)) return rejected('InvalidConsumedValue', []);
  for (const name of ['toolVersion', 'contractVersion']) {
    if (!Object.hasOwn(parsed, name)) return rejected('MissingCapability', [name]);
    if (!isConsumedString(parsed[name])) return rejected('InvalidConsumedValue', [name]);
  }
  if (!Object.hasOwn(parsed, 'commands')) return rejected('MissingCapability', ['commands']);
  const commands = parsed.commands;
  if (!isRecord(commands)) return rejected('InvalidConsumedValue', ['commands']);
  for (const [name, command] of Object.entries(commands)) {
    if (!isRecord(command)) return rejected('InvalidConsumedValue', ['commands', name]);
    if (!Object.hasOwn(command, 'outputSchemaVersion')) {
      return rejected('MissingCapability', ['commands', name, 'outputSchemaVersion']);
    }
    if (!isConsumedString(command.outputSchemaVersion)) {
      return rejected('InvalidConsumedValue', ['commands', name, 'outputSchemaVersion']);
    }
  }
  const features = Object.hasOwn(parsed, 'featureVersions') ? parsed.featureVersions : undefined;
  if (Object.hasOwn(parsed, 'featureVersions')) {
    if (!isRecord(features)) return rejected('InvalidConsumedValue', ['featureVersions']);
    for (const [name, version] of Object.entries(features)) {
      if (!isConsumedString(version)) return rejected('InvalidConsumedValue', ['featureVersions', name]);
    }
  }
  const activeCodePage = Object.hasOwn(parsed, 'activeWindowsCodePage') ? parsed.activeWindowsCodePage : undefined;
  if (Object.hasOwn(parsed, 'activeWindowsCodePage') &&
      (typeof activeCodePage !== 'number' || !Number.isSafeInteger(activeCodePage) || activeCodePage <= 0)) {
    return rejected('InvalidConsumedValue', ['activeWindowsCodePage']);
  }
  if (parsed.contractVersion !== required.contractVersion) {
    return rejected('VersionMismatch', ['contractVersion'], required.contractVersion, parsed.contractVersion as string);
  }
  for (const [name, version] of Object.entries(required.featureVersions ?? {})) {
    if (!isRecord(features) || !Object.hasOwn(features, name)) return rejected('MissingCapability', ['featureVersions', name]);
    if (features[name] !== version) return rejected('VersionMismatch', ['featureVersions', name], version, features[name] as string);
  }
  if (Object.hasOwn(required.featureVersions ?? {}, activeCodePageFeature) &&
      !Object.hasOwn(parsed, 'activeWindowsCodePage')) {
    return rejected('MissingCapability', ['activeWindowsCodePage']);
  }
  for (const [name, version] of Object.entries(required.commandSchemaVersions)) {
    if (!Object.hasOwn(commands, name)) return rejected('MissingCapability', ['commands', name]);
    const actual = (commands[name] as Record<string, string>).outputSchemaVersion;
    if (actual !== version) return rejected('VersionMismatch', ['commands', name, 'outputSchemaVersion'], version, actual);
  }
  const facts: VbaDevCapabilities = Object.freeze({
    toolVersion: parsed.toolVersion as string,
    contractVersion: parsed.contractVersion as string,
    commands: Object.freeze(Object.fromEntries(Object.entries(commands).map(([name, command]) =>
      [name, Object.freeze({ outputSchemaVersion: (command as Record<string, string>).outputSchemaVersion })]))),
    ...(features === undefined ? {} : { featureVersions: Object.freeze({ ...features as Record<string, string> }) }),
    ...(activeCodePage === undefined ? {} : { activeWindowsCodePage: activeCodePage as number })
  });
  return { accepted: true, facts };
}

function rejected(code: CapabilityRejection['code'], path: readonly string[], expected?: string, actual?: string): VbaDevCapabilityAdmission {
  return { accepted: false, rejection: { code, path, ...(expected === undefined ? {} : { expected }), ...(actual === undefined ? {} : { actual }) } };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isConsumedString(value: unknown): value is string {
  return typeof value === 'string' && !hasUnpairedSurrogate(value);
}

function hasUnpairedSurrogate(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return true;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return true;
    }
  }
  return false;
}

// Capture duplicate evidence before JSON.parse discards it. JSON.parse still owns grammar validation.
function findDuplicateProperty(raw: string): string | undefined {
  const stack: Array<{ object: boolean; key: boolean; names: Set<string> }> = [];
  let duplicate: string | undefined;
  for (let index = 0; index < raw.length; index++) {
    const character = raw[index];
    if (character === '"') {
      const start = index;
      for (index++; index < raw.length; index++) {
        if (raw[index] === '\\') index++;
        else if (raw[index] === '"') break;
      }
      const frame = stack.at(-1);
      if (frame?.object && frame.key) {
        const name = JSON.parse(raw.slice(start, index + 1)) as string;
        if (hasUnpairedSurrogate(name)) throw new SyntaxError('An object property name contains invalid Unicode.');
        if (frame.names.has(name)) duplicate ??= name;
        frame.names.add(name);
        frame.key = false;
      }
    } else if (character === '{' || character === '[') {
      stack.push({ object: character === '{', key: character === '{', names: new Set() });
    } else if (character === '}' || character === ']') {
      stack.pop();
    } else if (character === ',' && stack.at(-1)?.object) {
      stack.at(-1)!.key = true;
    }
  }
  return duplicate;
}
