import type { HostDefinition } from './vbaProject';

const bundledExcelHostDefinitions: HostDefinition[] = [
  {
    name: 'Application',
    documentation: 'Represents the Microsoft Excel application.',
    members: [
      { name: 'ActiveWorkbook', documentation: 'Returns the active workbook.' },
      { name: 'ActiveSheet', documentation: 'Returns the active sheet.' },
      { name: 'Workbooks', documentation: 'Returns the Workbooks collection.' }
    ]
  },
  {
    name: 'Workbook',
    documentation: 'Represents an Excel workbook.',
    members: [
      { name: 'Name', documentation: 'Returns the workbook name.' },
      { name: 'Worksheets', documentation: 'Returns the Worksheets collection.' }
    ]
  },
  {
    name: 'Worksheet',
    documentation: 'Represents an Excel worksheet.',
    members: [
      { name: 'Name', documentation: 'Returns the worksheet name.' },
      { name: 'Range', documentation: 'Returns a Range object.' },
      { name: 'Cells', documentation: 'Returns the Cells collection.' }
    ]
  },
  {
    name: 'Range',
    documentation: 'Represents a cell, row, column, selection, or block of cells.',
    members: [
      { name: 'Address', documentation: 'Returns the range address.' },
      { name: 'Value', documentation: 'Returns or sets the range value.' },
      { name: 'Value2', documentation: 'Returns or sets the range value without Currency and Date data types.' }
    ]
  }
];

export function getBundledExcelHostDefinitions(): HostDefinition[] {
  return bundledExcelHostDefinitions.map(cloneHostDefinition);
}

function cloneHostDefinition(definition: HostDefinition): HostDefinition {
  return {
    ...definition,
    members: definition.members?.map(cloneHostDefinition)
  };
}
