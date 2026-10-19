---
name: content-search
description: "Searches Puck repo text with puck search, not grep or rg: files or lines mentioning a verb, flag or key, regex, contains-A-not-B, counts, uncapped file lists. C# usages go to symbol-analysis."
---

# Content search

`puck search` is the repository's content search: a ripgrep-shaped verb over a
linear-time, leftmost-longest regex engine with intersection, complement and
lookaround. Use it in place of Grep, `rg` and `grep` for anything inside the
checkout. Reach for the Glob tool only when the question is purely about path
names: `--files` answers that inside the verb, over the same walk a search
uses. Use `symbol-analysis` when the question is semantic rather than textual.

## Start the CLI from a clean checkout

Run from the repository root with the checkout's own CLI build, never a
published copy or the global `puck` tool, both of which lag the head. Copy
`src/Puck.Cli/bin/Release/net11.0/` to a scratch directory named for your lane
and run the copy, so the running tool never locks the build output another
build needs:

```text
dotnet <lane>-cli/Puck.Cli.dll search <pattern> [path] [options]
```

If that build is absent, build it (`dotnet build src/Puck.Cli -c Release
-nodeReuse:false`) or run the tracked project directly:

```text
dotnet run --project src/Puck.Cli/Puck.Cli.csproj -c Release -- search <pattern> [path] [options]
```

If neither command can run, fall back to the host's literal or regex search.
Say in the result that it came from a degraded fallback, keep the requested
path, glob and case semantics, and never claim Puck-only intersection,
complement or lookaround behavior for it. A "contains A but not B" condition
still has to be checked: use a second filter or per-line inspection with the
host tool.

## Rules that prevent wrong answers

- Search paths are repository-relative unless the task says otherwise.
- Use `-F` for literal text; otherwise the pattern is the Puck regex dialect.
- Add `-M 0` when all results are required; the default result cap is 250 and
  truncation is silent. `-l` lists files, `-c` counts matching lines per file,
  and `-s` matches across whole-file text and prints line ranges.
- Matching is case-sensitive by default. `-i` fixes the pattern and never the
  globs, and a stray case mismatch is the most common empty result.
- Globs are not ripgrep's. Brace sets and character classes are literal
  characters, and `**` requires an intermediate directory, so `src/Puck.Cli/**/*.cs`
  silently misses `src/Puck.Cli/Program.cs`. Write `-g "*.cs"` for an extension and
  `-g "src/**"` for a subtree, repeating `-g` instead of writing `{cs,md}`.
- A pattern that starts with `-` needs `--` before it, or it is parsed as a
  flag.
- Treat exit `0` as a match, `1` as no match, and `2` as an error. When a result
  surprises you, `--files` prints exactly which files the run would read.
- Text occurrences do not prove C# references, implementers, overrides, rename
  safety or deletion safety. Route those questions to `symbol-analysis`, whose
  verbs are `puck references` and `puck declarations`.
- Quote patterns for the active shell; do not silently rewrite regex syntax.
- The walk has no extension filter, so it reads `.puck` DSL sources by
  default. A cartridge `.puck` source and its committed `.cartridge.json` twin
  can each hold a fact the other lacks: a `let`/template name or unexpanded
  `for`/lambda body exists only in `.puck`; a lowered default or basis-merged
  value exists only in the compiled JSON. Search both before concluding a value
  is absent. A world `.puck` source compiles into `bin/`, which the walk
  skips, so only the source is searched.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) when
you need exact flags, walk and glob behavior, engine semantics, syntax
extensions, cookbook patterns (contains A but not B, files without a pattern,
multiline blocks, whole-word alternations), shell escaping, or known
limitations.

## Related skills

| Question | Skill |
|---|---|
| Who references, implements, overrides or can delete a C# symbol (`puck references`, `puck declarations`) | `symbol-analysis` |
| `.puck` name resolution and diagnostics | `puck-dsl` |
