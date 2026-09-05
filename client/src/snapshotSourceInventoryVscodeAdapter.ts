import {
  SnapshotSourceInventoryCapture,
  SnapshotSourceInventoryHost,
  captureSnapshotSourceInventory
} from './snapshotSourceInventory';

export function createSnapshotSourceInventoryVscodeAdapter(
  host: SnapshotSourceInventoryHost
): SnapshotSourceInventoryCapture {
  return (sourceSetPath, cancellationToken, activeWindowsCodePage) => captureSnapshotSourceInventory(
    sourceSetPath,
    activeWindowsCodePage === undefined ? host : {
      ...host, getActiveWindowsCodePage: () => activeWindowsCodePage
    },
    cancellationToken
  );
}
