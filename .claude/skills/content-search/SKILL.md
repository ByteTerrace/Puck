---
name: content-search
description: Searches repository file contents with Puck CLI `search` for literals, regex, multiline spans, counts, file lists, and text occurrences. Use whenever a task needs content search. Use symbol-analysis instead for semantic C# references, implementers, dead code, renames, or deletion safety. Prefer the Puck engine; if its clean-checkout bootstrap cannot run, use the documented explicit fallback and report that degraded path.
---

# Content search

Use Puck's search engine for repository content. It provides a ripgrep-shaped
interface with linear-time, leftmost-longest matching plus intersection,
complement, and lookaround. Reach for the Glob tool only when the question is
purely about path names — `--files` answers that inside the verb, over the same
walk a search uses. Use `symbol-analysis` when the question is semantic rather
than textual.

## Start the CLI from a clean checkout

Run from the repository root. Prefer the already-built portable assembly:

```text
dotnet src/Puck.Cli/publish/Puck.Cli.dll search <pattern> [path] [options]
```

If it is absent, use the tracked project:

```text
dotnet run --project src/Puck.Cli/Puck.Cli.csproj -c Release -- search <pattern> [path] [options]
```

Never depend exclusively on the ignored Windows launcher
`src/Puck.Cli/publish/puck.exe`. If neither command can run, content search may
fall back to the host's literal/regex search facility. State that fallback in
the result, preserve the requested path/glob/case semantics, and do not claim
support for Puck-only intersection, complement, or lookaround behavior.

## Rules that prevent wrong answers

- Search paths are repository-relative unless the task says otherwise.
- Use `-F` for literal text; otherwise the pattern is the Puck regex dialect.
- Add `-M 0` when all results are required; the default result cap is 250 and
  truncation is silent. Use `-s` for whole-file multiline span matching.
- Matching is case-sensitive by default. `-i` fixes the pattern and never the
  globs, and a stray case mismatch is the most common empty result.
- Globs are not ripgrep's. Brace sets and character classes are literal
  characters, and `**` requires an intermediate directory, so `src/**/*.cs`
  silently misses `src/foo.cs`. Write `-g "*.cs"` for an extension and
  `-g "src/**"` for a subtree, repeating `-g` instead of writing `{cs,md}`.
- Treat exit `0` as a match, `1` as no match, and `2` as an error. When a result
  surprises you, `--files` prints exactly which files the run would read.
- Text occurrences do not prove C# references, implementers, or deletion
  safety. Route those questions to `symbol-analysis`.
- Quote patterns for the active shell; do not silently rewrite regex syntax.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) when
you need exact flags, walk/glob behavior, engine semantics, syntax extensions,
cookbook patterns, shell escaping, or known limitations. Its detailed contract
is preserved rather than duplicated here.
