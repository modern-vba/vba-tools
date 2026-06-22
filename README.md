# VBA Language Server

VBA-LanguageServer provides editor intelligence for exported VBA source files in
Visual Studio Code.

## Formatting

The extension provides `Format Document` for `.bas`, `.cls`, and `.frm` files.
Formatting is opt-in through normal VS Code settings; the extension does not
change user or workspace settings during activation.

To format on save for VBA files, set this extension as the language-specific
formatter and enable `editor.formatOnSave`:

```json
{
  "[vba]": {
    "editor.defaultFormatter": "tkmr-akhs.vba-language-server",
    "editor.formatOnSave": true
  }
}
```

Formatting normalizes VBA keyword and intrinsic word casing, normalizes resolved
reference casing to the matching `VbaDefinition` or `HostDefinition`, and rewrites
leading whitespace according to VBA block depth. It does not rename declarations,
edit sibling files, or rewrite comments and strings.
