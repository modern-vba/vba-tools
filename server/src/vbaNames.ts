export function sameName(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

export function unqualifiedTypeName(typeName: string): string {
  return typeName.split('.').at(-1) ?? typeName;
}
