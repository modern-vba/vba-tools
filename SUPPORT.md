# Support

## Bugs, Feature Requests, and Questions

Use [GitHub Issues](https://github.com/modern-vba/vba-tools/issues) for product
bugs, feature requests, and general usage questions. Check the
[README](README.md) first for current setup requirements and known platform
boundaries.

## Security Reports

Report suspected vulnerabilities through
[GitHub private vulnerability reporting](https://github.com/modern-vba/vba-tools/security/advisories/new).
Do not include vulnerability details in a public issue.

## Diagnostic Information

Include enough information to reproduce and diagnose the problem:

- VBA Tools extension version;
- `vba-dev --version` output;
- VS Code version;
- Windows version and architecture;
- desktop Excel version when workbook automation is involved;
- the command or editor action that failed, its full output, and relevant logs;
- a minimal exported VBA source or project manifest when it can be shared
  safely.

Remove workbook data, credentials, and other sensitive information before
posting publicly.

## Supported Environment

The initial Marketplace package targets Windows x64 (`win32-x64`). Editor-only
language, formatting, and navigation features do not require Excel. Workbook
build, test, publish, export, doctor, and native VBE debugging commands require
desktop Excel and trusted access to the VBA project object model.

## Service Level

This project is community maintained. No response-time or service-level
commitment is offered.
