import type {
  HostClassIdentity,
  HostClassProjectionSnapshotEntry
} from './hostClassProjectionLifecycle';
import type {
  HostClassModuleIdentityMetadata,
  HostClassSourceCandidate
} from './hostClassSourceMetadata';

export type { HostClassSourceCandidate } from './hostClassSourceMetadata';

export interface HostClassSourceAssociation {
  readonly sourceUri: string;
  readonly sourceKind: 'form' | 'document';
  readonly attributeVbName: string;
  readonly projectionIdentity: HostClassIdentity;
  readonly authority: HostClassProjectionSnapshotEntry['authority'];
}

export interface HostClassSourceAssociationFailure {
  readonly sourceUri: string;
  readonly sourceKind: 'form' | 'document';
  readonly attributeVbName: string | undefined;
  readonly candidateProjectionIdentity: HostClassIdentity | undefined;
  readonly reason:
    | 'attributeVbNameMissing'
    | 'attributeVbNameInvalid'
    | 'projectionIdentityMismatch'
    | 'componentKindMismatch';
  readonly message: string;
  readonly guidance: string;
}

export interface HostClassSourceAssociationResult {
  readonly associations: readonly HostClassSourceAssociation[];
  readonly failures: readonly HostClassSourceAssociationFailure[];
}

export function associateHostClassSources(
  sources: readonly HostClassSourceCandidate[],
  classes: readonly HostClassProjectionSnapshotEntry[],
  classEnumerationComplete = true
): HostClassSourceAssociationResult {
  const classesByIdentity = new Map(
    classes.map((entry) => [hostClassIdentityKey(entry.identity), entry])
  );
  const classesByName = new Map(
    classes.map((entry) => [entry.identity.name.toLowerCase(), entry])
  );
  const associations: HostClassSourceAssociation[] = [];
  const failures: HostClassSourceAssociationFailure[] = [];

  for (const source of sources) {
    const metadata = source.moduleIdentity;
    if (metadata.state !== 'authoritative') {
      failures.push(createAttributeFailure(source, metadata));
      continue;
    }

    const attributeVbName = metadata.name;
    const projection = classesByIdentity.get(
      hostClassIdentityKey({ name: attributeVbName, kind: source.kind })
    );
    if (projection === undefined) {
      const incompatibleKind = classesByName.get(attributeVbName.toLowerCase());
      if (incompatibleKind === undefined && !classEnumerationComplete) {
        continue;
      }
      failures.push({
        sourceUri: source.sourceUri,
        sourceKind: source.kind,
        attributeVbName,
        candidateProjectionIdentity: incompatibleKind?.identity,
        reason: incompatibleKind === undefined
          ? 'projectionIdentityMismatch'
          : 'componentKindMismatch',
        message: incompatibleKind === undefined
          ? `No ${source.kind} host-class projection matches Attribute VB_Name "${attributeVbName}".`
          : `Attribute VB_Name "${attributeVbName}" matches a ${incompatibleKind.identity.kind} projection, not a ${source.kind} projection.`,
        guidance: 'Re-export the source or repair its explicit Attribute VB_Name metadata.'
      });
      continue;
    }

    associations.push({
      sourceUri: source.sourceUri,
      sourceKind: source.kind,
      attributeVbName,
      projectionIdentity: projection.identity,
      authority: projection.authority
    });
  }

  return { associations, failures };
}

function createAttributeFailure(
  source: HostClassSourceCandidate,
  metadata: Exclude<HostClassModuleIdentityMetadata, { readonly state: 'authoritative' }>
): HostClassSourceAssociationFailure {
  const invalid = metadata.state === 'invalid';
  return {
    sourceUri: source.sourceUri,
    sourceKind: source.kind,
    attributeVbName: undefined,
    candidateProjectionIdentity: undefined,
    reason: invalid ? 'attributeVbNameInvalid' : 'attributeVbNameMissing',
    message: invalid
      ? 'The source has malformed or misplaced Attribute VB_Name metadata.'
      : 'The source has no authoritative Attribute VB_Name value.',
    guidance: 'Re-export the source or repair its explicit Attribute VB_Name metadata.'
  };
}

function hostClassIdentityKey(identity: HostClassIdentity): string {
  return `${identity.kind}\u0000${identity.name.toLowerCase()}`;
}
