import type { HostApplication, HostDefinition } from './hostDefinition';
import { C_OFFICE_HOST_CATALOG_SNAPSHOT } from './generated/officeHostCatalogSnapshot';
import {
  C_SUPPORTED_HOST_APPLICATIONS,
  cloneHostDefinitionsWithApplication
} from './hostDefinitionCatalog';

export { C_SUPPORTED_HOST_APPLICATIONS } from './hostDefinitionCatalog';

export const C_DEFAULT_MAIN_HOST_APPLICATION: HostApplication = 'excel';

const C_HOST_APPLICATION_DISPLAY_NAMES: Record<HostApplication, string> = {
  excel: 'Excel',
  word: 'Word',
  powerpoint: 'PowerPoint',
  access: 'Access'
};

export interface HostApplicationSelectionOptions {
  mainHostApplication?: HostApplication;
  additionalHostApplications?: HostApplication[];
}

export interface HostApplicationSelection {
  mainHostApplication: HostApplication;
  additionalHostApplications: HostApplication[];
  enabledHostApplications: HostApplication[];
}

const bundledHostDefinitionsByApplication: Partial<Record<HostApplication, HostDefinition[]>> = {
  excel: [
    {
      name: 'Application',
      kind: 'class',
      documentation: 'Represents the Microsoft Excel application.',
      members: [
        { name: 'ActiveWorkbook', kind: 'property', documentation: 'Returns the active workbook.' },
        { name: 'ActiveSheet', kind: 'property', documentation: 'Returns the active sheet.' },
        { name: 'Workbooks', kind: 'property', documentation: 'Returns the Workbooks collection.' }
      ]
    },
    {
      name: 'Workbook',
      kind: 'class',
      documentation: 'Represents an Excel workbook.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the workbook name.' },
        { name: 'Worksheets', kind: 'property', documentation: 'Returns the Worksheets collection.' }
      ]
    },
    {
      name: 'Worksheet',
      kind: 'class',
      documentation: 'Represents an Excel worksheet.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the worksheet name.' },
        { name: 'Range', kind: 'function', documentation: 'Returns a Range object.' },
        { name: 'Cells', kind: 'property', documentation: 'Returns the Cells collection.' }
      ]
    },
    {
      name: 'Range',
      kind: 'class',
      documentation: 'Represents a cell, row, column, selection, or block of cells.',
      members: [
        { name: 'Address', kind: 'property', documentation: 'Returns the range address.' },
        { name: 'Value', kind: 'property', documentation: 'Returns or sets the range value.' },
        { name: 'Value2', kind: 'property', documentation: 'Returns or sets the range value without Currency and Date data types.' }
      ]
    }
  ],
  word: [
    {
      name: 'Application',
      kind: 'class',
      documentation: 'Represents the Microsoft Word application.',
      members: [
        { name: 'ActiveDocument', kind: 'property', documentation: 'Returns the active document.' },
        { name: 'Documents', kind: 'property', documentation: 'Returns the Documents collection.' },
        { name: 'Selection', kind: 'property', documentation: 'Returns the current selection.' }
      ]
    },
    {
      name: 'Document',
      kind: 'class',
      documentation: 'Represents a Word document.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the document name.' },
        { name: 'Range', kind: 'function', documentation: 'Returns a Range object.' }
      ]
    },
    {
      name: 'Range',
      kind: 'class',
      documentation: 'Represents a contiguous area in a Word document.',
      members: [
        { name: 'Text', kind: 'property', documentation: 'Returns or sets the range text.' },
        { name: 'Start', kind: 'property', documentation: 'Returns or sets the starting character position.' },
        { name: 'End', kind: 'property', documentation: 'Returns or sets the ending character position.' }
      ]
    },
    {
      name: 'Selection',
      kind: 'class',
      documentation: 'Represents the current selection in Word.',
      members: [
        { name: 'Text', kind: 'property', documentation: 'Returns or sets the selected text.' },
        { name: 'Range', kind: 'property', documentation: 'Returns the selection range.' }
      ]
    }
  ],
  powerpoint: [
    {
      name: 'Application',
      kind: 'class',
      documentation: 'Represents the Microsoft PowerPoint application.',
      members: [
        { name: 'ActivePresentation', kind: 'property', documentation: 'Returns the active presentation.' },
        { name: 'Presentations', kind: 'property', documentation: 'Returns the Presentations collection.' },
        { name: 'SlideShowWindows', kind: 'property', documentation: 'Returns the SlideShowWindows collection.' }
      ]
    },
    {
      name: 'Presentation',
      kind: 'class',
      documentation: 'Represents a PowerPoint presentation.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the presentation name.' },
        { name: 'Slides', kind: 'property', documentation: 'Returns the Slides collection.' },
        { name: 'SlideShowSettings', kind: 'property', documentation: 'Returns the slide show settings.' }
      ]
    },
    {
      name: 'Slide',
      kind: 'class',
      documentation: 'Represents a PowerPoint slide.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the slide name.' },
        { name: 'Shapes', kind: 'property', documentation: 'Returns the Shapes collection.' }
      ]
    },
    {
      name: 'Shape',
      kind: 'class',
      documentation: 'Represents a shape on a PowerPoint slide.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the shape name.' },
        { name: 'TextFrame', kind: 'property', documentation: 'Returns the text frame for the shape.' },
        { name: 'Visible', kind: 'property', documentation: 'Returns or sets whether the shape is visible.' }
      ]
    }
  ],
  access: [
    {
      name: 'Application',
      kind: 'class',
      documentation: 'Represents the Microsoft Access application.',
      members: [
        { name: 'CurrentDb', kind: 'function', documentation: 'Returns the current database object.' },
        { name: 'DoCmd', kind: 'property', documentation: 'Returns the DoCmd object.' },
        { name: 'Forms', kind: 'property', documentation: 'Returns the Forms collection.' },
        { name: 'Reports', kind: 'property', documentation: 'Returns the Reports collection.' }
      ]
    },
    {
      name: 'DoCmd',
      kind: 'class',
      documentation: 'Provides methods for running Access actions.',
      members: [
        { name: 'OpenForm', kind: 'function', documentation: 'Opens a form.' },
        { name: 'OpenReport', kind: 'function', documentation: 'Opens a report.' },
        { name: 'Close', kind: 'function', documentation: 'Closes an Access object.' }
      ]
    },
    {
      name: 'Form',
      kind: 'class',
      documentation: 'Represents an Access form.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the form name.' },
        { name: 'RecordSource', kind: 'property', documentation: 'Returns or sets the source of records for the form.' },
        { name: 'Controls', kind: 'property', documentation: 'Returns the Controls collection.' }
      ]
    },
    {
      name: 'Report',
      kind: 'class',
      documentation: 'Represents an Access report.',
      members: [
        { name: 'Name', kind: 'property', documentation: 'Returns the report name.' },
        { name: 'RecordSource', kind: 'property', documentation: 'Returns or sets the source of records for the report.' },
        { name: 'Controls', kind: 'property', documentation: 'Returns the Controls collection.' }
      ]
    }
  ]
};

export function getBundledHostDefinitions(options: HostApplicationSelectionOptions = {}): HostDefinition[] {
  return createHostApplicationSelection(options).enabledHostApplications
    .flatMap((hostApplication) => getBundledHostDefinitionsForApplication(hostApplication));
}

export function createHostApplicationSelection(
  options: HostApplicationSelectionOptions = {}
): HostApplicationSelection {
  const main_host_application = options.mainHostApplication ?? C_DEFAULT_MAIN_HOST_APPLICATION;
  const additional_host_applications = uniqueHostApplications(
    (options.additionalHostApplications ?? []).filter((hostApplication) =>
      hostApplication !== main_host_application
    )
  );

  return {
    mainHostApplication: main_host_application,
    additionalHostApplications: additional_host_applications,
    enabledHostApplications: [main_host_application, ...additional_host_applications]
  };
}

export function getBundledHostDefinitionsForApplication(hostApplication: HostApplication): HostDefinition[] {
  return cloneHostDefinitionsWithApplication(
    C_OFFICE_HOST_CATALOG_SNAPSHOT[hostApplication]
      ?? bundledHostDefinitionsByApplication[hostApplication]
      ?? [],
    hostApplication
  );
}

export function getBundledExcelHostDefinitions(): HostDefinition[] {
  return getBundledHostDefinitionsForApplication('excel');
}

export function getBundledWordHostDefinitions(): HostDefinition[] {
  return getBundledHostDefinitionsForApplication('word');
}

export function formatHostApplicationName(hostApplication: HostApplication): string {
  return C_HOST_APPLICATION_DISPLAY_NAMES[hostApplication];
}

function uniqueHostApplications(hostApplications: HostApplication[]): HostApplication[] {
  const seen = new Set<HostApplication>();
  const result: HostApplication[] = [];

  for (const host_application of hostApplications) {
    if (seen.has(host_application)) {
      continue;
    }

    seen.add(host_application);
    result.push(host_application);
  }

  return result;
}
