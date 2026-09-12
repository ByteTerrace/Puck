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

## Build and install

From `editors/vscode`, run:

```sh
npm ci
npm run package
code --install-extension vscode-puck-1.0.0.vsix --force
```

The VSIX bundles the language client; the Puck CLI is installed separately. Packaging runs the build automatically.

## Verification

`test/extension-host.js` runs inside a VS Code extension development host, using its configured Puck CLI. It checks completion, hover, formatting, and open-document synchronization after a server restart. Launch VS Code with `--extensionDevelopmentPath` pointing at this directory and `--extensionTestsPath` pointing at that file, using an isolated user-data directory with the server settings above. Run `npm run build` first.
