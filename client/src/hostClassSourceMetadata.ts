export const ModuleIdentityMetadataMethod = 'vba/moduleIdentityMetadata';

export type HostClassSourceKind = 'form' | 'document';

export interface HostClassSourceText {
  readonly sourceUri: string;
  readonly kind: HostClassSourceKind;
  readonly text: string;
}

export type HostClassModuleIdentityMetadata =
  | { readonly state: 'missing' }
  | { readonly state: 'invalid' }
  | { readonly state: 'authoritative'; readonly name: string };

export interface HostClassSourceCandidate {
  readonly sourceUri: string;
  readonly kind: HostClassSourceKind;
  readonly moduleIdentity: HostClassModuleIdentityMetadata;
}

interface ModuleIdentityMetadataBatchRequest {
  readonly sources: readonly HostClassSourceText[];
}

export interface ModuleIdentityMetadataLanguageClient {
  sendRequest(
    method: string,
    parameters: ModuleIdentityMetadataBatchRequest
  ): Promise<unknown>;
}

export async function resolveHostClassSourceMetadata(
  languageClient: ModuleIdentityMetadataLanguageClient,
  sources: readonly HostClassSourceText[]
): Promise<readonly HostClassSourceCandidate[]> {
  const response = await languageClient.sendRequest(
    ModuleIdentityMetadataMethod,
    { sources }
  );
  if (!isRecord(response)) {
    throw new Error('The VBA language server returned invalid ModuleIdentity metadata.');
  }
  const responseSources = response.sources;
  if (!Array.isArray(responseSources)) {
    throw new Error('The VBA language server returned invalid ModuleIdentity metadata.');
  }
  if (responseSources.length !== sources.length) {
    throw new Error('The VBA language server returned incomplete ModuleIdentity metadata.');
  }

  return sources.map((source, index) => {
    const result = responseSources[index];
    if (!isRecord(result)
      || result.sourceUri !== source.sourceUri
      || result.kind !== source.kind) {
      throw new Error('The VBA language server returned mismatched ModuleIdentity metadata.');
    }

    const moduleIdentity = readModuleIdentity(result);
    return {
      sourceUri: source.sourceUri,
      kind: source.kind,
      moduleIdentity
    };
  });
}

function readModuleIdentity(
  result: Record<string, unknown>
): HostClassModuleIdentityMetadata {
  if (result.state === 'authoritative'
    && typeof result.name === 'string'
    && result.name.length > 0) {
    return { state: 'authoritative', name: result.name };
  }
  if ((result.state === 'missing' || result.state === 'invalid')
    && result.name === null) {
    return { state: result.state };
  }

  throw new Error('The VBA language server returned invalid ModuleIdentity metadata.');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
