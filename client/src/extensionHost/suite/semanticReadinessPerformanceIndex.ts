import {
  runSemanticReadinessPerformanceMeasurement
} from './semanticReadinessPerformance';

export async function run(): Promise<void> {
  await runSemanticReadinessPerformanceMeasurement();
}
