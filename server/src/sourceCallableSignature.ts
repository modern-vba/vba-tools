import type { CallableSignature } from './hostDefinition';
import type { VbaDefinition } from './vbaSourceModel';

const C_TYPE_NAME_PATTERN = /[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?/;

export function parseSourceCallableParameterDefinitions(
  uri: string,
  line: string,
  lineIndex: number,
  parameterText: string,
  parameterStart: number
): VbaDefinition[] {
  const definitions: VbaDefinition[] = [];
  let search_offset = 0;

  for (const segment of parameterText.split(',')) {
    const segment_start = parameterStart + search_offset;
    const trimmed_segment = segment.trimStart();
    const match = /^(?:(?:Optional|ByVal|ByRef|ParamArray)\s+)*([A-Za-z_][A-Za-z0-9_]*)\b/i.exec(trimmed_segment);
    if (match !== null) {
      const name = match[1];
      const name_start = line.indexOf(name, segment_start);
      const type_match = new RegExp(`\\bAs\\s+(${C_TYPE_NAME_PATTERN.source})\\b`, 'i').exec(trimmed_segment);
      const passing_mode_match = /\b(ByVal|ByRef)\b/i.exec(trimmed_segment);
      const default_value_match = /=\s*(.+)\s*$/.exec(trimmed_segment);
      definitions.push({
        name,
        kind: 'parameter',
        visibility: 'local',
        uri,
        range: {
          start: { line: lineIndex, character: name_start },
          end: { line: lineIndex, character: name_start + name.length }
        },
        typeName: type_match?.[1],
        optional: /\bOptional\b/i.test(trimmed_segment),
        passingMode: passing_mode_match === null
          ? undefined
          : canonicalPassingMode(passing_mode_match[1]),
        isParamArray: /\bParamArray\b/i.test(trimmed_segment),
        defaultValue: default_value_match?.[1].trim()
      });
    }

    search_offset += segment.length + 1;
  }

  return definitions;
}

export function buildSourceCallableSignature(
  line: string,
  name: string,
  parameterDefinitions: VbaDefinition[]
): CallableSignature {
  const parameters = parameterDefinitions.map((parameter) => ({
    name: parameter.name,
    label: formatCallableParameterLabel(parameter),
    optional: parameter.optional,
    passingMode: parameter.passingMode,
    isParamArray: parameter.isParamArray,
    typeName: parameter.typeName,
    defaultValue: parameter.defaultValue
  }));
  const return_type_name = parseSourceCallableReturnTypeName(line);
  const return_suffix = return_type_name === undefined ? '' : ` As ${return_type_name}`;

  return {
    label: `${name}(${parameters.map((parameter) => parameter.label ?? parameter.name).join(', ')})${return_suffix}`,
    parameters,
    returnTypeName: return_type_name
  };
}

export function parseSourceCallableReturnTypeName(line: string): string | undefined {
  const return_match = new RegExp(`\\)\\s+As\\s+(${C_TYPE_NAME_PATTERN.source})\\b`, 'i').exec(line);
  return return_match?.[1];
}

function canonicalPassingMode(value: string): 'ByVal' | 'ByRef' {
  return value.toLowerCase() === 'byval' ? 'ByVal' : 'ByRef';
}

function formatCallableParameterLabel(parameter: VbaDefinition): string {
  const modifiers = [
    parameter.isParamArray === true ? 'ParamArray' : undefined,
    parameter.optional === true ? 'Optional' : undefined
  ].filter((modifier) => modifier !== undefined);

  return [...modifiers, parameter.name].join(' ');
}
