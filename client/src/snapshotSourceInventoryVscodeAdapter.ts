import {
  SnapshotSourceInventoryCapture,
  SnapshotSourceInventoryHost,
  captureSnapshotSourceInventory
} from './snapshotSourceInventory';

export function createSnapshotSourceInventoryVscodeAdapter(
  host: SnapshotSourceInventoryHost
): SnapshotSourceInventoryCapture {
  return (sourceSetPath, cancellationToken) => captureSnapshotSourceInventory(
    sourceSetPath,
    host,
    cancellationToken
  );
}
