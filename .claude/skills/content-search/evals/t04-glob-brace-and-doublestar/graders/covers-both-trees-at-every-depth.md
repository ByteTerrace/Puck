---
type: llm
weight: 1
---

The response's `puck search` command (or one shell line wrapping it) reads both `src` and `docs` at every depth, including files directly under each directory, and searches the literal `PuckRootCommand`.

PASS if it names the trees as path arguments (`... PuckRootCommand src docs`, or a loop over `src docs`), or uses subtree globs such as `-g "src/**"`, or searches `.` and keeps only `src/` and `docs/` paths. Example: `dotnet lane-cli/Puck.Cli.dll search -F -l -M 0 -g "*.cs" -g "*.md" PuckRootCommand src docs`.

FAIL if it covers only one tree, uses a glob shaped like `src/**/*.cs` that needs an intermediate directory and so misses `src/X.cs`, uses a brace set such as `*.{cs,md}`, or searches a different pattern. Example: `puck search PuckRootCommand -g "src/**/*.cs" -g "docs/**/*.md"`.
