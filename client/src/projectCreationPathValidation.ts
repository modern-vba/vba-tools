export const projectCreationPathValidationReasons = {
  projectNameEmpty: 'projectNameEmpty',
  projectNameIllFormedUnicode: 'projectNameIllFormedUnicode',
  projectNameDotSegment: 'projectNameDotSegment',
  projectNameContainsPathSeparator: 'projectNameContainsPathSeparator',
  projectNameContainsWindowsInvalidCharacter: 'projectNameContainsWindowsInvalidCharacter',
  projectNameContainsUnicodeControlCharacter: 'projectNameContainsUnicodeControlCharacter',
  projectNameHasLeadingOrTrailingWhitespace: 'projectNameHasLeadingOrTrailingWhitespace',
  projectNameEndsWithDot: 'projectNameEndsWithDot',
  projectNameUsesReservedDeviceName: 'projectNameUsesReservedDeviceName',
  excelPathContainsUnsupportedCharacter: 'excelPathContainsUnsupportedCharacter',
  excelPathTooLong: 'excelPathTooLong'
} as const;

export type ProjectCreationPathValidationReason =
  typeof projectCreationPathValidationReasons[keyof typeof projectCreationPathValidationReasons];

export interface ProjectCreationPathValidationResult {
  readonly isValid: boolean;
  readonly reason: ProjectCreationPathValidationReason | null;
}

export const maximumExcelWorkbookPathUtf16CodeUnitLength = 218;

const reservedDeviceNames = new Set([
  'CON',
  'PRN',
  'AUX',
  'NUL',
  'COM1',
  'COM2',
  'COM3',
  'COM4',
  'COM5',
  'COM6',
  'COM7',
  'COM8',
  'COM9',
  'COM¹',
  'COM²',
  'COM³',
  'LPT1',
  'LPT2',
  'LPT3',
  'LPT4',
  'LPT5',
  'LPT6',
  'LPT7',
  'LPT8',
  'LPT9',
  'LPT¹',
  'LPT²',
  'LPT³'
]);

export function validateProjectName(candidate: string): ProjectCreationPathValidationResult {
  if (candidate.length === 0) {
    return rejected(projectCreationPathValidationReasons.projectNameEmpty);
  }

  if (!isWellFormedUtf16(candidate)) {
    return rejected(projectCreationPathValidationReasons.projectNameIllFormedUnicode);
  }

  if (candidate === '.' || candidate === '..') {
    return rejected(projectCreationPathValidationReasons.projectNameDotSegment);
  }

  if (candidate.includes('/') || candidate.includes('\\')) {
    return rejected(projectCreationPathValidationReasons.projectNameContainsPathSeparator);
  }

  if (/[<>:"|?*]/u.test(candidate)) {
    return rejected(projectCreationPathValidationReasons.projectNameContainsWindowsInvalidCharacter);
  }

  if (containsUnicodeControlCodeUnit(candidate)) {
    return rejected(projectCreationPathValidationReasons.projectNameContainsUnicodeControlCharacter);
  }

  if (isContractWhitespace(candidate.charCodeAt(0))
    || isContractWhitespace(candidate.charCodeAt(candidate.length - 1))) {
    return rejected(projectCreationPathValidationReasons.projectNameHasLeadingOrTrailingWhitespace);
  }

  if (candidate.endsWith('.')) {
    return rejected(projectCreationPathValidationReasons.projectNameEndsWithDot);
  }

  const firstDot = candidate.indexOf('.');
  const deviceNameCandidate = firstDot >= 0 ? candidate.slice(0, firstDot) : candidate;
  if (reservedDeviceNames.has(toAsciiUpperCase(deviceNameCandidate))) {
    return rejected(projectCreationPathValidationReasons.projectNameUsesReservedDeviceName);
  }

  return valid();
}

export function validateExcelWorkbookPath(candidate: string): ProjectCreationPathValidationResult {
  if (candidate.includes('[') || candidate.includes(']')) {
    return rejected(projectCreationPathValidationReasons.excelPathContainsUnsupportedCharacter);
  }

  if (candidate.length > maximumExcelWorkbookPathUtf16CodeUnitLength) {
    return rejected(projectCreationPathValidationReasons.excelPathTooLong);
  }

  return valid();
}

function valid(): ProjectCreationPathValidationResult {
  return { isValid: true, reason: null };
}

function rejected(reason: ProjectCreationPathValidationReason): ProjectCreationPathValidationResult {
  return { isValid: false, reason };
}

function isWellFormedUtf16(candidate: string): boolean {
  for (let index = 0; index < candidate.length; index++) {
    const codeUnit = candidate.charCodeAt(index);
    if (codeUnit >= 0xd800 && codeUnit <= 0xdbff) {
      if (index + 1 >= candidate.length) {
        return false;
      }

      const nextCodeUnit = candidate.charCodeAt(index + 1);
      if (nextCodeUnit < 0xdc00 || nextCodeUnit > 0xdfff) {
        return false;
      }

      index++;
    } else if (codeUnit >= 0xdc00 && codeUnit <= 0xdfff) {
      return false;
    }
  }

  return true;
}

function containsUnicodeControlCodeUnit(candidate: string): boolean {
  for (let index = 0; index < candidate.length; index++) {
    const codeUnit = candidate.charCodeAt(index);
    if (codeUnit <= 0x001f || (codeUnit >= 0x007f && codeUnit <= 0x009f)) {
      return true;
    }
  }

  return false;
}

function isContractWhitespace(codeUnit: number): boolean {
  return (codeUnit >= 0x0009 && codeUnit <= 0x000d)
    || codeUnit === 0x0020
    || codeUnit === 0x0085
    || codeUnit === 0x00a0
    || codeUnit === 0x1680
    || (codeUnit >= 0x2000 && codeUnit <= 0x200a)
    || (codeUnit >= 0x2028 && codeUnit <= 0x2029)
    || codeUnit === 0x202f
    || codeUnit === 0x205f
    || codeUnit === 0x3000;
}

function toAsciiUpperCase(value: string): string {
  let result = '';
  for (let index = 0; index < value.length; index++) {
    const codeUnit = value.charCodeAt(index);
    result += String.fromCharCode(
      codeUnit >= 0x0061 && codeUnit <= 0x007a ? codeUnit - 0x20 : codeUnit);
  }

  return result;
}
