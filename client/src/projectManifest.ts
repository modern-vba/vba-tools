export interface WorkbookBackedProjectManifest {
  projectName: string;
  primaryDocument: string;
  documents: readonly WorkbookBackedProjectDocument[];
}

export interface WorkbookBackedProjectDocument {
  name: string;
  sourcePath: string;
}

export function parseProjectManifest(json: string): WorkbookBackedProjectManifest | undefined {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return undefined;
  }

  if (!isRecord(parsed)) {
    return undefined;
  }

  const projectName = parsed.projectName;
  const primaryDocument = parsed.primaryDocument;
  const documentsValue = parsed.documents;
  if (parsed.schemaVersion !== 1 ||
      typeof projectName !== 'string' ||
      typeof primaryDocument !== 'string' ||
      !isRecord(documentsValue)) {
    return undefined;
  }

  const documents: WorkbookBackedProjectDocument[] = [];
  for (const [name, document] of Object.entries(documentsValue)) {
    if (name.length === 0 || !isRecord(document) || typeof document.sourcePath !== 'string') {
      return undefined;
    }

    documents.push({ name, sourcePath: document.sourcePath });
  }

  if (!documents.some((document) => document.name.toLowerCase() === primaryDocument.toLowerCase())) {
    return undefined;
  }

  return {
    projectName,
    primaryDocument,
    documents
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
