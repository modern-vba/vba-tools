# vba-debug-adapter

`vba-debug-adapter` is the separately versioned, self-contained Windows x64
debug companion bundled with the VBA Tools extension. It owns native VBE debug
sessions and delegates snapshot workbook creation to the exact compatible
`vba-dev` executable selected for that session.

The extension manages this executable for normal use. Run the following command
only when inspecting its machine-readable compatibility contract:

```text
vba-debug-adapter capabilities --format json
```

See the root [Debug in the VBE](../../README.md#debug-in-the-vbe) guidance for
the supported user workflow.
