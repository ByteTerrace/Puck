---
type: llm
weight: 1
---

The response's single search command reads both `src` and `docs` at every depth, including files directly under each directory, and searches the literal `PuckRootCommand` in `.cs` and `.md` files only.

PASS if its command names the trees as path arguments (`... PuckRootCommand src docs`, a `grep -r` or `rg` over `src docs`, a loop over both), or uses per-extension subtree globs such as `-g "src/**.cs"`. Example: `dotnet lane-cli/Puck.Cli.dll search -F -l -M 0 -g "*.cs" -g "*.md" PuckRootCommand src docs`, or `grep -rlF --include="*.cs" --include="*.md" PuckRootCommand src docs`.

FAIL if it covers only one tree, searches a different pattern, uses a `puck search` glob shaped like `src/**/*.cs` that needs an intermediate directory and so misses `src/X.cs`, or combines subtree globs with extension globs in one `puck search` (repeated `-g` flags OR together, so the second example also lists every other file under `src` and `docs` and every `.cs` or `.md` file anywhere). Examples: `puck search PuckRootCommand -g "src/**/*.cs" -g "docs/**/*.md"`, or `puck search -F -l -g "src/**" -g "docs/**" -g "*.cs" -g "*.md" PuckRootCommand`.
