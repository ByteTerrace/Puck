# Puck Authoring Language for VS Code

Rich language support, syntax highlighting, bracket matching, and snippets for the **Puck World Authoring DSL** (`.puck` and `.world.puck`).

## Features

- **Syntax Highlighting**: Comprehensive TextMate grammar covering directives (`schema`, `basis`, `let`, `import`, `export`), engine sections (`host`, `views`, `camera`, `seatRig`, `motion`, `rules`, `state`, `solids`, `materials`), WebAssembly addons (`addon`, `request`, `watchMemory`), physical units (`s`, `hz`, `deg`, `rad`, `m`, `px`), hexadecimal numbers, and color literals (`#RRGGBB`).
- **Opinionated Puck Formatting**: Built to enforce Egyptian/K&R bracing (`{` on the declaration line) and standard 4-space indentation matching Puck's C# `.editorconfig` baseline.
- **Bracket Matching & Auto-Closing**: Automatic pair management for `{}`, `[]`, `()`, and `""`.
- **Code Folding**: Quick collapse/expand across world sections, templates, and `#region` blocks.
- **Snippets**: Productivity snippets for `world`, `host`, `views`, `seatRig`, `addon`, `template`, `let`, and `import`.

## Private Marketplace Installation

This extension is hosted on our internal **Puck VS Marketplace** (`mcr.microsoft.com/vsmarketplace/vscode-private-marketplace`):

```bash
# Package into VSIX
puck packaging vscode

# Install locally
code --install-extension editors/vscode/vscode-puck-1.0.0.vsix
```
