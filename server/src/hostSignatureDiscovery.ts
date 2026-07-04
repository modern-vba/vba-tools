import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

import {
  C_SUPPORTED_HOST_APPLICATIONS,
  formatHostApplicationName
} from './officeHostCatalog';
import type {
  CallableParameter,
  CallableSignature,
  HostApplication,
  HostDefinition
} from './vbaProject';

const execFileAsync = promisify(execFile);

export type HostTypeLibraryPowerShellRunner = (script: string) => Promise<string>;
export type HostSignaturePowerShellRunner = HostTypeLibraryPowerShellRunner;

export async function discoverHostDefinitionsFromTypeLibrary(
  hostApplication: HostApplication,
  runPowerShell: HostTypeLibraryPowerShellRunner = runPowerShellScript
): Promise<HostDefinition[]> {
  const stdout = await runPowerShell(createTypeLibraryDiscoveryScript(hostApplication));
  const parsed = stripNullProperties(JSON.parse(stdout) as unknown);
  if (!isHostDefinitionArray(parsed)) {
    throw new Error(`${formatHostApplicationName(hostApplication)} Type Library discovery returned an invalid host catalog.`);
  }

  return cloneHostDefinitionsWithApplication(parsed, hostApplication);
}

export async function discoverHostSignaturesFromTypeLibrary(
  hostApplication: HostApplication,
  runPowerShell: HostSignaturePowerShellRunner = runPowerShellScript
): Promise<HostDefinition[]> {
  return discoverHostDefinitionsFromTypeLibrary(hostApplication, runPowerShell);
}

async function runPowerShellScript(script: string): Promise<string> {
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script],
    {
      maxBuffer: 1024 * 1024 * 20,
      timeout: 30000,
      windowsHide: true
    }
  );

  return stdout;
}

function createTypeLibraryDiscoveryScript(hostApplication: HostApplication): string {
  const prog_id = progIdForHostApplication(hostApplication);
  const type_lib_id = typeLibraryIdForHostApplication(hostApplication) ?? '';
  return String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ELEMDESC = System.Runtime.InteropServices.ComTypes.ELEMDESC;
using FUNCDESC = System.Runtime.InteropServices.ComTypes.FUNCDESC;
using ITypeInfo = System.Runtime.InteropServices.ComTypes.ITypeInfo;
using ITypeLib = System.Runtime.InteropServices.ComTypes.ITypeLib;
using INVOKEKIND = System.Runtime.InteropServices.ComTypes.INVOKEKIND;
using PARAMFLAG = System.Runtime.InteropServices.ComTypes.PARAMFLAG;
using TYPEATTR = System.Runtime.InteropServices.ComTypes.TYPEATTR;
using TYPEDESC = System.Runtime.InteropServices.ComTypes.TYPEDESC;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;
using VARDESC = System.Runtime.InteropServices.ComTypes.VARDESC;
using VARKIND = System.Runtime.InteropServices.ComTypes.VARKIND;

public sealed class HostDefinitionDto {
  public string name { get; set; }
  public string kind { get; set; }
  public string documentation { get; set; }
  public string value { get; set; }
  public string typeName { get; set; }
  public CallableSignatureDto signature { get; set; }
  public List<HostDefinitionDto> members { get; set; }
}

public sealed class CallableSignatureDto {
  public string label { get; set; }
  public List<CallableParameterDto> parameters { get; set; }
  public string returnTypeName { get; set; }
  public string documentation { get; set; }
}

public sealed class CallableParameterDto {
  public string name { get; set; }
  public string label { get; set; }
  public string documentation { get; set; }
  public bool? optional { get; set; }
  public string passingMode { get; set; }
  public bool? isParamArray { get; set; }
  public string typeName { get; set; }
  public string defaultValue { get; set; }
}

public static class HostTypeLibraryReader {
  [DllImport("oleaut32.dll", PreserveSig = false)]
  private static extern void LoadRegTypeLib(ref Guid rguid, ushort major, ushort minor, int lcid, out ITypeLib typeLib);

  public static HostDefinitionDto[] ReadFromProgId(string progId, string fallbackTypeLibText) {
    string clsidText = ReadRegistryDefault(progId + "\\CLSID");
    string typeLibText = null;
    string versionText = null;
    if (!String.IsNullOrEmpty(clsidText)) {
      typeLibText = ReadRegistryDefault("CLSID\\" + clsidText + "\\TypeLib");
      versionText = ReadRegistryDefault("CLSID\\" + clsidText + "\\Version");
    }
    if (String.IsNullOrEmpty(typeLibText)) {
      typeLibText = fallbackTypeLibText;
    }
    if (String.IsNullOrEmpty(typeLibText)) {
      throw new InvalidOperationException("No Type Library is registered for " + progId + ".");
    }
    if (String.IsNullOrEmpty(versionText)) {
      versionText = ReadLatestTypeLibVersion(typeLibText);
    }
    if (String.IsNullOrEmpty(versionText)) {
      throw new InvalidOperationException("No Type Library version is registered for " + progId + ".");
    }

    string[] versionParts = versionText.Split('.');
    ushort major = UInt16.Parse(versionParts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    ushort minor = versionParts.Length > 1
      ? UInt16.Parse(versionParts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
      : (ushort)0;
    Guid typeLibId = new Guid(typeLibText);
    ITypeLib typeLib;
    LoadRegTypeLib(ref typeLibId, major, minor, 0, out typeLib);

    List<HostDefinitionDto> definitions = new List<HostDefinitionDto>();
    int count = typeLib.GetTypeInfoCount();
    for (int index = 0; index < count; index += 1) {
      ITypeInfo typeInfo;
      typeLib.GetTypeInfo(index, out typeInfo);
      IntPtr typeAttrPointer;
      typeInfo.GetTypeAttr(out typeAttrPointer);
      TYPEATTR typeAttr = (TYPEATTR)Marshal.PtrToStructure(typeAttrPointer, typeof(TYPEATTR));
      try {
        if (typeAttr.typekind == TYPEKIND.TKIND_ENUM) {
          HostDefinitionDto enumDefinition = ReadEnum(typeInfo, typeAttr);
          if (enumDefinition != null) {
            definitions.Add(enumDefinition);
          }
          continue;
        }

        if (typeAttr.typekind == TYPEKIND.TKIND_MODULE) {
          definitions.AddRange(ReadStandaloneConstants(typeInfo, typeAttr));
          continue;
        }

        if (typeAttr.typekind != TYPEKIND.TKIND_DISPATCH && typeAttr.typekind != TYPEKIND.TKIND_INTERFACE) {
          continue;
        }

        string typeName = NormalizeTypeName(GetDocumentationName(typeInfo, -1));
        if (String.IsNullOrEmpty(typeName)) {
          continue;
        }

        HostDefinitionDto definition = new HostDefinitionDto();
        definition.name = typeName;
        definition.kind = "class";
        definition.documentation = GetDocumentationText(typeInfo, -1);
        definition.members = ReadMembers(typeInfo, typeAttr);
        definitions.Add(definition);
      } finally {
        typeInfo.ReleaseTypeAttr(typeAttrPointer);
      }
    }

    return definitions.ToArray();
  }

  private static HostDefinitionDto ReadEnum(ITypeInfo typeInfo, TYPEATTR typeAttr) {
    string enumName = NormalizeTypeName(GetDocumentationName(typeInfo, -1));
    if (String.IsNullOrEmpty(enumName)) {
      return null;
    }

    HostDefinitionDto definition = new HostDefinitionDto();
    definition.name = enumName;
    definition.kind = "enum";
    definition.documentation = GetDocumentationText(typeInfo, -1);
    definition.members = ReadConstantVariables(typeInfo, typeAttr, "enumMember");
    return definition;
  }

  private static List<HostDefinitionDto> ReadStandaloneConstants(ITypeInfo typeInfo, TYPEATTR typeAttr) {
    return ReadConstantVariables(typeInfo, typeAttr, "constant");
  }

  private static List<HostDefinitionDto> ReadConstantVariables(ITypeInfo typeInfo, TYPEATTR typeAttr, string kind) {
    List<HostDefinitionDto> constants = new List<HostDefinitionDto>();
    for (int index = 0; index < typeAttr.cVars; index += 1) {
      IntPtr varDescPointer;
      typeInfo.GetVarDesc(index, out varDescPointer);
      VARDESC varDesc = (VARDESC)Marshal.PtrToStructure(varDescPointer, typeof(VARDESC));
      try {
        if (varDesc.varkind != VARKIND.VAR_CONST) {
          continue;
        }

        string name = NormalizeTypeName(GetDocumentationName(typeInfo, varDesc.memid));
        if (String.IsNullOrEmpty(name)) {
          continue;
        }

        HostDefinitionDto constant = new HostDefinitionDto();
        constant.name = name;
        constant.kind = kind;
        constant.documentation = GetDocumentationText(typeInfo, varDesc.memid);
        constant.value = GetConstantValue(varDesc);
        constant.typeName = GetTypeName(typeInfo, varDesc.elemdescVar.tdesc);
        constants.Add(constant);
      } finally {
        typeInfo.ReleaseVarDesc(varDescPointer);
      }
    }

    return constants;
  }

  private static List<HostDefinitionDto> ReadMembers(ITypeInfo typeInfo, TYPEATTR typeAttr) {
    List<HostDefinitionDto> members = new List<HostDefinitionDto>();
    for (int index = 0; index < typeAttr.cFuncs; index += 1) {
      IntPtr funcDescPointer;
      typeInfo.GetFuncDesc(index, out funcDescPointer);
      FUNCDESC funcDesc = (FUNCDESC)Marshal.PtrToStructure(funcDescPointer, typeof(FUNCDESC));
      try {
        string[] names = new string[Math.Max(funcDesc.cParams + 1, 1)];
        int nameCount;
        typeInfo.GetNames(funcDesc.memid, names, names.Length, out nameCount);
        if (nameCount == 0 || String.IsNullOrEmpty(names[0])) {
          continue;
        }
        if (IsInfrastructureMember(names[0]) ||
            funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYPUT ||
            funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYPUTREF) {
          continue;
        }

        string returnTypeName = GetTypeName(typeInfo, funcDesc.elemdescFunc.tdesc);
        bool isProperty = funcDesc.invkind == INVOKEKIND.INVOKE_PROPERTYGET;
        HostDefinitionDto member = new HostDefinitionDto();
        member.name = names[0];
        member.kind = isProperty ? "property" : "function";
        member.documentation = GetDocumentationText(typeInfo, funcDesc.memid);
        member.typeName = returnTypeName;
        if (!isProperty) {
          member.signature = BuildSignature(member.name, member.documentation, returnTypeName, typeInfo, funcDesc, names, nameCount);
        }

        members.Add(member);
      } finally {
        typeInfo.ReleaseFuncDesc(funcDescPointer);
      }
    }

    return members;
  }

  private static bool IsInfrastructureMember(string memberName) {
    return memberName == "QueryInterface" ||
      memberName == "AddRef" ||
      memberName == "Release" ||
      memberName == "GetTypeInfoCount" ||
      memberName == "GetTypeInfo" ||
      memberName == "GetIDsOfNames" ||
      memberName == "Invoke";
  }

  private static CallableSignatureDto BuildSignature(
    string memberName,
    string documentation,
    string returnTypeName,
    ITypeInfo typeInfo,
    FUNCDESC funcDesc,
    string[] names,
    int nameCount
  ) {
    List<CallableParameterDto> parameters = new List<CallableParameterDto>();
    int elemDescSize = Marshal.SizeOf(typeof(ELEMDESC));
    for (int parameterIndex = 0; parameterIndex < funcDesc.cParams; parameterIndex += 1) {
      IntPtr elemDescPointer = new IntPtr(funcDesc.lprgelemdescParam.ToInt64() + (elemDescSize * parameterIndex));
      ELEMDESC elemDesc = (ELEMDESC)Marshal.PtrToStructure(elemDescPointer, typeof(ELEMDESC));
      string parameterName = parameterIndex + 1 < nameCount ? names[parameterIndex + 1] : "Arg" + (parameterIndex + 1).ToString();
      bool optional = (elemDesc.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FOPT) == PARAMFLAG.PARAMFLAG_FOPT;

      CallableParameterDto parameter = new CallableParameterDto();
      parameter.name = parameterName;
      parameter.optional = optional ? (bool?)true : null;
      parameter.label = optional ? "Optional " + parameterName : parameterName;
      parameter.typeName = GetTypeName(typeInfo, elemDesc.tdesc);
      parameter.defaultValue = GetDefaultValue(elemDesc);
      parameters.Add(parameter);
    }

    CallableSignatureDto signature = new CallableSignatureDto();
    signature.parameters = parameters;
    signature.returnTypeName = returnTypeName;
    signature.documentation = documentation;
    signature.label = memberName + "(" + String.Join(", ", parameters.ConvertAll(p => p.label ?? p.name).ToArray()) + ")" +
      (String.IsNullOrEmpty(returnTypeName) ? "" : " As " + returnTypeName);
    return signature;
  }

  private static string GetConstantValue(VARDESC varDesc) {
    if (varDesc.varkind != VARKIND.VAR_CONST || varDesc.desc.lpvarValue == IntPtr.Zero) {
      return null;
    }

    try {
      object value = Marshal.GetObjectForNativeVariant(varDesc.desc.lpvarValue);
      return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    } catch (ArgumentException) {
      return null;
    }
  }

  private static string GetDefaultValue(ELEMDESC elemDesc) {
    if ((elemDesc.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FHASDEFAULT) != PARAMFLAG.PARAMFLAG_FHASDEFAULT ||
        elemDesc.desc.paramdesc.lpVarValue == IntPtr.Zero) {
      return null;
    }

    try {
      object value = Marshal.GetObjectForNativeVariant(elemDesc.desc.paramdesc.lpVarValue);
      return value == null ? null : value.ToString();
    } catch (ArgumentException) {
      return null;
    }
  }

  private static string GetTypeName(ITypeInfo ownerTypeInfo, TYPEDESC typeDesc) {
    VarEnum vt = (VarEnum)typeDesc.vt;
    if (vt == VarEnum.VT_VOID || vt == VarEnum.VT_HRESULT) {
      return null;
    }
    if (vt == VarEnum.VT_PTR || vt == VarEnum.VT_SAFEARRAY) {
      TYPEDESC nested = (TYPEDESC)Marshal.PtrToStructure(typeDesc.lpValue, typeof(TYPEDESC));
      return GetTypeName(ownerTypeInfo, nested);
    }
    if (vt == VarEnum.VT_USERDEFINED) {
      ITypeInfo referencedTypeInfo;
      ownerTypeInfo.GetRefTypeInfo(unchecked((int)typeDesc.lpValue.ToInt64()), out referencedTypeInfo);
      return NormalizeTypeName(GetDocumentationName(referencedTypeInfo, -1));
    }

    switch (vt) {
      case VarEnum.VT_BSTR: return "String";
      case VarEnum.VT_BOOL: return "Boolean";
      case VarEnum.VT_I2: return "Integer";
      case VarEnum.VT_I4: return "Long";
      case VarEnum.VT_INT: return "Long";
      case VarEnum.VT_R4: return "Single";
      case VarEnum.VT_R8: return "Double";
      case VarEnum.VT_DATE: return "Date";
      case VarEnum.VT_CY: return "Currency";
      case VarEnum.VT_VARIANT: return "Variant";
      case VarEnum.VT_DISPATCH: return "Object";
      case VarEnum.VT_UNKNOWN: return "Object";
      default: return vt.ToString().Replace("VT_", "");
    }
  }

  private static string GetDocumentationName(ITypeInfo typeInfo, int memid) {
    string name;
    string doc;
    int helpContext;
    string helpFile;
    typeInfo.GetDocumentation(memid, out name, out doc, out helpContext, out helpFile);
    return name;
  }

  private static string GetDocumentationText(ITypeInfo typeInfo, int memid) {
    string name;
    string doc;
    int helpContext;
    string helpFile;
    typeInfo.GetDocumentation(memid, out name, out doc, out helpContext, out helpFile);
    return String.IsNullOrEmpty(doc) ? null : doc;
  }

  private static string NormalizeTypeName(string name) {
    if (String.IsNullOrEmpty(name)) {
      return name;
    }

    return name.StartsWith("_") ? name.Substring(1) : name;
  }

  private static string ReadRegistryDefault(string subKeyName) {
    using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(subKeyName)) {
      if (key == null) {
        return null;
      }

      object value = key.GetValue(null);
      return value == null ? null : value.ToString();
    }
  }

  private static string ReadLatestTypeLibVersion(string typeLibText) {
    using (RegistryKey key = Registry.ClassesRoot.OpenSubKey("TypeLib\\" + typeLibText)) {
      if (key == null) {
        return null;
      }

      string[] versions = key.GetSubKeyNames();
      Array.Sort(versions);
      return versions.Length == 0 ? null : versions[versions.Length - 1];
    }
  }
}
'@
[HostTypeLibraryReader]::ReadFromProgId('${prog_id}', '${type_lib_id}') | ConvertTo-Json -Depth 12 -Compress
`;
}

function typeLibraryIdForHostApplication(hostApplication: HostApplication): string | undefined {
  switch (hostApplication) {
    case 'excel':
      return '{00020813-0000-0000-C000-000000000046}';
    case 'word':
      return '{00020905-0000-0000-C000-000000000046}';
    case 'powerpoint':
      return '{91493440-5A91-11CF-8700-00AA0060263B}';
    case 'access':
      return undefined;
  }
}

function progIdForHostApplication(hostApplication: HostApplication): string {
  switch (hostApplication) {
    case 'excel':
      return 'Excel.Application';
    case 'word':
      return 'Word.Application';
    case 'powerpoint':
      return 'PowerPoint.Application';
    case 'access':
      return 'Access.Application';
  }
}

function cloneHostDefinitionsWithApplication(
  definitions: HostDefinition[],
  hostApplication: HostApplication
): HostDefinition[] {
  return definitions.map((definition) => cloneHostDefinitionWithApplication(definition, hostApplication));
}

function cloneHostDefinitionWithApplication(
  definition: HostDefinition,
  hostApplication: HostApplication
): HostDefinition {
  const clone: HostDefinition = {
    ...definition,
    hostApplication
  };
  if (definition.members !== undefined) {
    clone.members = definition.members.map((member) =>
      cloneHostDefinitionWithApplication(member, hostApplication)
    );
  }

  return clone;
}

function stripNullProperties(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(stripNullProperties);
  }

  if (typeof value !== 'object' || value === null) {
    return value;
  }

  return Object.fromEntries(
    Object.entries(value)
      .filter(([, entry_value]) => entry_value !== null)
      .map(([key, entry_value]) => [key, stripNullProperties(entry_value)])
  );
}

function isHostDefinitionArray(value: unknown): value is HostDefinition[] {
  return Array.isArray(value) && value.every(isHostDefinition);
}

function isHostDefinition(value: unknown): value is HostDefinition {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<HostDefinition>;
  return typeof candidate.name === 'string'
    && (candidate.kind === undefined || isHostDefinitionKind(candidate.kind))
    && (candidate.hostApplication === undefined || isHostApplication(candidate.hostApplication))
    && (candidate.parentName === undefined || typeof candidate.parentName === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string')
    && (candidate.value === undefined || typeof candidate.value === 'string')
    && (candidate.typeName === undefined || typeof candidate.typeName === 'string')
    && (candidate.signature === undefined || isCallableSignature(candidate.signature))
    && (candidate.members === undefined || isHostDefinitionArray(candidate.members));
}

function isCallableSignature(value: unknown): value is CallableSignature {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<CallableSignature>;
  return typeof candidate.label === 'string'
    && Array.isArray(candidate.parameters)
    && candidate.parameters.every(isCallableParameter)
    && (candidate.returnTypeName === undefined || typeof candidate.returnTypeName === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string');
}

function isCallableParameter(value: unknown): value is CallableParameter {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<CallableParameter>;
  return typeof candidate.name === 'string'
    && (candidate.label === undefined || typeof candidate.label === 'string')
    && (candidate.documentation === undefined || typeof candidate.documentation === 'string')
    && (candidate.optional === undefined || typeof candidate.optional === 'boolean')
    && (candidate.passingMode === undefined || candidate.passingMode === 'ByVal' || candidate.passingMode === 'ByRef')
    && (candidate.isParamArray === undefined || typeof candidate.isParamArray === 'boolean')
    && (candidate.typeName === undefined || typeof candidate.typeName === 'string')
    && (candidate.defaultValue === undefined || typeof candidate.defaultValue === 'string');
}

function isHostDefinitionKind(value: unknown): boolean {
  return value === 'class'
    || value === 'property'
    || value === 'function'
    || value === 'enum'
    || value === 'enumMember'
    || value === 'constant';
}

function isHostApplication(value: unknown): value is HostApplication {
  return typeof value === 'string'
    && (C_SUPPORTED_HOST_APPLICATIONS as readonly string[]).includes(value);
}
