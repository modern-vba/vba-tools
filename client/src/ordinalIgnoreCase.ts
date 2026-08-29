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
