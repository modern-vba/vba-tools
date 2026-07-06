export type HostDefinitionKind = 'class' | 'property' | 'function' | 'enum' | 'enumMember' | 'constant';
export type HostApplication = 'excel' | 'word' | 'powerpoint' | 'access';

export interface CallableParameter {
  name: string;
  label?: string;
  documentation?: string;
  optional?: boolean;
  passingMode?: 'ByVal' | 'ByRef';
  isParamArray?: boolean;
  typeName?: string;
  defaultValue?: string;
}

export interface CallableSignature {
  label: string;
  parameters: CallableParameter[];
  returnTypeName?: string;
  documentation?: string;
}

export interface HostDefinition {
  name: string;
  kind?: HostDefinitionKind;
  hostApplication?: HostApplication;
  parentName?: string;
  documentation?: string;
  value?: string;
  typeName?: string;
  signature?: CallableSignature;
  members?: HostDefinition[];
}
