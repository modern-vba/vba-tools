import { ordinalIgnoreCaseCanonicalCodePoints } from './ordinalIgnoreCaseData.g';

export function ordinalIgnoreCaseKey(value: string): string {
  let key = '';
  for (const character of value) {
    const codePoint = character.codePointAt(0)!;
    const canonical = ordinalIgnoreCaseCanonicalCodePoints.get(codePoint);
    key += canonical === undefined ? character : String.fromCodePoint(canonical);
  }
  return key;
}

/** Returns the first conflicting pair with both original spellings preserved. */
export function findOrdinalIgnoreCaseDuplicate(values: readonly string[]): readonly [string, string] | undefined {
  const originalValuesByKey = new Map<string, string>();
  for (const value of values) {
    const key = ordinalIgnoreCaseKey(value);
    const originalValue = originalValuesByKey.get(key);
    if (originalValue !== undefined) {
      return [originalValue, value];
    }
    originalValuesByKey.set(key, value);
  }
  return undefined;
}
