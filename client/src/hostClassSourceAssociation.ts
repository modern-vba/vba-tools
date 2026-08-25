import type {
  HostClassIdentity,
  HostClassProjectionSnapshotEntry
} from './hostClassProjectionLifecycle';

export interface HostClassSourceCandidate {
  readonly sourceUri: string;
  readonly kind: 'form' | 'document';
  readonly text: string;
}

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
    const metadata = readExplicitVbNameMetadata(source.text);
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

type VbNameMetadata =
  | { readonly state: 'missing' }
  | { readonly state: 'invalid'; readonly name?: string }
  | { readonly state: 'authoritative'; readonly name: string };

function readExplicitVbNameMetadata(text: string): VbNameMetadata {
  const lines = text.split(/\r\n|\n|\r/u);
  const vbNameLike = /^[ \t]*Attribute[ \t]+VB_Name(?=[ \t]|=|$)/iu;
  const validVbName = /^[ \t]*Attribute[ \t]+VB_Name[ \t]*=[ \t]*"([^"]+)"[ \t]*$/iu;
  const header = locateClassHeader(lines);

  const names: string[] = [];
  let invalid = header.invalid;
  let headerEnd = header.start;
  if (header.start >= 0) {
    while (headerEnd < lines.length) {
      const line = lines[headerEnd] as string;
      if (vbNameLike.test(line)) {
        const match = validVbName.exec(line);
        const name = match?.[1];
        if (name === undefined || !isPlausibleModuleName(name)) {
          invalid = true;
        } else {
          names.push(name);
        }
        headerEnd += 1;
        continue;
      }
      if (!isClassHeaderAttribute(line)) {
        break;
      }
      headerEnd += 1;
    }
  }

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index] as string;
    if (!vbNameLike.test(line)) {
      continue;
    }
    if (index < header.start || index >= headerEnd) {
      invalid = true;
    }
  }

  const name = names.at(-1);
  if (invalid) {
    return { state: 'invalid', ...(name === undefined ? {} : { name }) };
  }
  return name === undefined
    ? { state: 'missing' }
    : { state: 'authoritative', name };
}

function locateClassHeader(
  lines: readonly string[]
): { readonly start: number; readonly invalid: boolean } {
  const firstNonempty = lines.findIndex((line) => trimVbaLayout(line).length > 0);
  if (firstNonempty < 0) {
    return { start: -1, invalid: false };
  }
  if (/^[ \t]*Attribute(?=[ \t]|$)/iu.test(lines[firstNonempty] as string)) {
    return { start: firstNonempty, invalid: false };
  }
  if (!/^[ \t]*VERSION(?=[ \t]|$)/iu.test(lines[firstNonempty] as string)) {
    return { start: -1, invalid: true };
  }

  let designerDepth = 0;
  let sawDesignerBlock = false;
  for (let index = firstNonempty + 1; index < lines.length; index += 1) {
    const line = trimVbaLayout(lines[index] as string);
    if (line.length === 0) {
      continue;
    }
    if (/^Begin(?:Property)?(?:[ \t]|$)/iu.test(line)) {
      designerDepth += 1;
      sawDesignerBlock = true;
      continue;
    }
    if (designerDepth > 0) {
      if (/^End(?:Property)?$/iu.test(line)) {
        designerDepth -= 1;
      }
      continue;
    }
    if (/^Attribute(?=[ \t]|$)/iu.test(line)) {
      return { start: index, invalid: false };
    }
    if (!sawDesignerBlock && /^Object[ \t]*=/iu.test(line)) {
      continue;
    }
    return { start: -1, invalid: true };
  }

  return { start: -1, invalid: designerDepth !== 0 };
}

function isClassHeaderAttribute(line: string): boolean {
  return /^[ \t]*Attribute[ \t]+(?:VB_GlobalNameSpace|VB_Creatable)[ \t]*=[ \t]*False[ \t]*$/iu
    .test(line) ||
    /^[ \t]*Attribute[ \t]+(?:VB_PredeclaredId|VB_Exposed|VB_Customizable)[ \t]*=[ \t]*(?:True|False)[ \t]*$/iu
      .test(line);
}

function trimVbaLayout(value: string): string {
  return value.replace(/^[ \t]+|[ \t]+$/gu, '');
}

function isPlausibleModuleName(value: string): boolean {
  return [...value].length <= 31;
}

function createAttributeFailure(
  source: HostClassSourceCandidate,
  metadata: Exclude<VbNameMetadata, { readonly state: 'authoritative' }>
): HostClassSourceAssociationFailure {
  const invalid = metadata.state === 'invalid';
  return {
    sourceUri: source.sourceUri,
    sourceKind: source.kind,
    attributeVbName: metadata.state === 'invalid' ? metadata.name : undefined,
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
