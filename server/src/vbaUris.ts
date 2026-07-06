import path from 'node:path';

export function isVbaSourceUri(uri: string): boolean {
  return /\.(bas|cls|frm)$/i.test(uriPathname(uri));
}

export function fallbackModuleIdentity(uri: string): string {
  const parsed_path = uriPathname(uri);
  const file_name = path.posix.basename(parsed_path);
  const extension = path.posix.extname(file_name);

  return file_name.slice(0, file_name.length - extension.length);
}

export function getFolderUri(uri: string): string {
  const parsed_path = uriPathname(uri);
  const folder_path = path.posix.dirname(parsed_path);

  return `file://${folder_path}`;
}

export function uriPathname(uri: string): string {
  if (uri.startsWith('file://')) {
    return new URL(uri).pathname;
  }

  return uri;
}

export function sameUri(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}
