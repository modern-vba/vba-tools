import { runPreviewPerformanceMeasurement } from './previewPerformance';

export async function run(): Promise<void> {
  await runPreviewPerformanceMeasurement();
}
