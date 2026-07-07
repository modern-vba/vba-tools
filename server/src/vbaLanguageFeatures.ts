import { resolveName } from './nameResolution';
import type { SourcePosition, SourceRange } from './sourceRange';
import { getIdentifierRangesInCode } from './vbaIdentifierSource';
import {
  findModule,
  sameDefinitionLocation
} from './vbaProjectIndex';
import type { VbaProject } from './vbaProjectModel';
import { isIdentifierName } from './vbaText';
import { sameUri } from './vbaUris';
import type { DefinitionLocation } from './vbaSourceModel';

export interface LanguageFeatureRequest {
  uri: string;
  position: SourcePosition;
}

export interface RenameEdit {
  uri: string;
  range: SourceRange;
  newText: string;
}

export function getDefinition(
  project: VbaProject,
  request: LanguageFeatureRequest
): DefinitionLocation | undefined {
  const resolution = resolveName(project, request);
  return resolution?.source === 'vba' ? resolution.definition : undefined;
}

export function getRenameTarget(
  project: VbaProject,
  request: LanguageFeatureRequest
): DefinitionLocation | undefined {
  const resolution = resolveName(project, request);
  return resolution?.source === 'vba' ? resolution.definition : undefined;
}

export function getRenameEdits(
  project: VbaProject,
  request: LanguageFeatureRequest,
  newName: string
): RenameEdit[] {
  if (!isIdentifierName(newName)) {
    return [];
  }

  const target = getRenameTarget(project, request);
  if (target === undefined) {
    return [];
  }

  const target_module = findModule(project, target.uri);
  if (target_module === undefined) {
    return [];
  }

  const edits: RenameEdit[] = [];
  for (const module of project.modules.filter((candidate) =>
    sameUri(candidate.folderUri, target_module.folderUri)
  )) {
    for (let line_index = 0; line_index < module.lines.length; line_index += 1) {
      for (const range of getIdentifierRangesInCode(module.lines[line_index], line_index)) {
        const resolution = resolveName(project, {
          uri: module.uri,
          position: range.start
        });
        if (resolution?.source === 'vba' && sameDefinitionLocation(resolution.definition, target)) {
          edits.push({
            uri: module.uri,
            range,
            newText: newName
          });
        }
      }
    }
  }

  return edits;
}
