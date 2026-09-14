# Puck Authoring Language for VS Code

IntelliSense for `.puck` files connects VS Code to the existing `puck lsp` language server. Open a Puck file to start completion suggestions, hover documentation, live diagnostics, document symbols, and formatting. Syntax highlighting, bracket matching, folding, and snippets are included.

## Setup

Install the Puck CLI and make its `puck` executable available on PATH, then install this extension. IntelliSense starts automatically in trusted workspaces. Press Ctrl+Space for suggestions, hover over a variable, template, built-in function, or keyword for documentation, or use Format Document.

Build the local CLI with `dotnet build src/Puck.Cli -c Release` from the repository root. For that assembly, set the following VS Code settings, replacing the path with your own absolute path:

```json
{
    "puck.server.command": "dotnet",
    "puck.server.args": ["D:/Source/ByteTerrace/Puck/src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll", "lsp"]
}
```

The command and arguments are passed directly without a shell. Changes restart the server automatically. You can also run **Puck: Restart Language Server**. Startup failures appear as an error message; the **Puck Language Server** output channel contains server logs. Remote workspaces run the CLI on the remote host.

Completion currently offers the server's keyword, section, function, and unit suggestions, with cartridge suggestions selected by document schema. It is not yet a complete context-sensitive semantic completion engine. See the [language-server documentation](../../src/Puck.World.Transpiler/README.md#editor-tooling) for server behavior.

## Formatting and source references

Puck files default to two-space indentation. User or workspace language settings can override `editor.tabSize`, `editor.insertSpaces`, and `editor.detectIndentation`; Format Document uses the active editor options. Objects put their properties on separate lines, while scalar arrays and vectors stay compact. `basis` suggestions point to `.puck` sources, which the world composer compiles when resolving inheritance.

## Syntax colors

Puck uses your selected theme’s syntax colors by default. Consistent syntax scopes distinguish properties, variables, keywords, functions, and literals without imposing a palette. It leaves your editor background and other languages alone. The same grammar drives syntax scopes and color application, including nested expressions, multiline raw strings, and interpolation.

An optional Puck palette is available by setting `puck.highlighting.consistentColors` to `true`:

| Role | Optional dark-palette color | Examples |
|---|---|---|
| Properties | Light blue | `palette`, `noise`, `position`, `exponent` |
| Variables and parameters | Neutral | `tile`, `tileIndex`, `meadowTiles` |
| Keywords and interpolation boundaries | Purple | `for`, `in`, `prototype`, `{expression}` inside an interpolated string |
| Functions | Soft gold | `filter`, `sine`, `cosine` |
| Types | Teal | `Prism`, `Superellipsoid` |
| Strings | Warm peach | Names, text, and the literal parts of interpolated strings |
| Numbers | Sage green | Coordinates, counts, and numeric quantities |
| Enum and boolean literals | Bright blue | `SmoothUnion`, `true`, `null` |
| Operators and parentheses | Muted neutral | `=>`, `+`, `>`, `()` |
| Comments | Muted green | `// ...` and `/* ... */` |
| Array delimiters | Theme bracket color 3 | `[]`, including indexing |
| Object and block delimiters | Theme bracket color 2 | `{}` outside string interpolation |

Property names share one style whether followed by a colon, an array, or an object. Loop sources are variables, and expressions inside interpolated strings retain their code colors. Capitalized bare identifiers use enum styling unless a declaration identifies them as a type; highlighting does not resolve symbol types.

`puck.highlighting.consistentColors` defaults to `false`. Fixed container colors remain enabled to distinguish arrays from objects, but draw from the active theme’s bracket palette and follow theme changes. Set `puck.highlighting.containerColors` to `false` to restore the theme’s usual nesting-depth coloring. Matching and guides remain available. Switching a document to another language clears Puck decorations; switching back reapplies them.

Customize any role under `workbench.colorCustomizations` using `puck.property`, `puck.variable`, `puck.keyword`, `puck.function`, `puck.type`, `puck.string`, `puck.number`, `puck.literal`, `puck.operator`, `puck.comment`, `puck.arrayDelimiter`, or `puck.objectDelimiter`.

## Build and install

From `editors/vscode`, run:

```sh
npm ci
npm run package
code --install-extension vscode-puck-1.0.0.vsix --force
```

The VSIX bundles the language client; the Puck CLI is installed separately. Packaging runs the build automatically.

## Verification

Run `npm test` for TextMate scope and delimiter-color regression checks. `test/extension-host.js` runs inside a VS Code extension development host, using its configured Puck CLI. It checks completion, hover, formatting, and open-document synchronization after a server restart. Launch VS Code with `--extensionDevelopmentPath` pointing at this directory and `--extensionTestsPath` pointing at that file, using an isolated user-data directory with the server settings above. Run `npm run build` first.
