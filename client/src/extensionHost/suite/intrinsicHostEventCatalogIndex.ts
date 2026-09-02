import {
  runIntrinsicHostEventCatalogIntegrationTests
} from './intrinsicHostEventCatalogIntegration';

export async function run(): Promise<void> {
  await runIntrinsicHostEventCatalogIntegrationTests();
}
