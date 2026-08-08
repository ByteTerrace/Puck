---
name: symbol-analysis
description: Answers semantic C# questions with compiler-backed evidence, including who calls or references a symbol, what implements or overrides it, whether code is used or safe to delete, whether a rename is safe, and what a file declares. Use before multi-file C# structure investigations and whenever a request asks who calls this, is this used, can I delete this, what implements this, or what breaks if I rename it. Text matching cannot resolve aliases, overloads, extension methods, or generic instantiations; use `puck references` or `puck declarations`. Use content-search for literals, configuration, and non-C# text.
---

# Symbol analysis

Use compiler-backed evidence for C# structure. Text matching cannot resolve
aliases, overloads, extensions, generic instantiations, or name collisions.
Treat repository-specific examples and observed load behavior as snapshots;
re-run the relevant command against the current tree before relying on them.

| Question | Command |
|---|---|
| References, implementers, overrides, derived types, dead code | `references` |
| Declarations, members, bases, attributes, documentation crefs | `declarations` |
| Literal/config/non-C# content | Route to `content-search` |
| Whether a refactor still compiles | Build the affected project |

## Start the CLI from a clean checkout

Run from the repository root. Prefer:

```text
dotnet src/Puck.Cli/publish/Puck.Cli.dll <verb> <arguments>
```

If the published assembly is absent:

```text
dotnet run --project src/Puck.Cli/Puck.Cli.csproj -c Release -- <verb> <arguments>
```

Never depend exclusively on ignored `publish/puck.exe`.

## Evidence rules

- `references` loads a project graph and resolves symbols. Narrowing with
  `--project` changes the closure and therefore the question.
- `declarations` parses syntax without a build and can see files no project
  compiles, but it cannot prove semantic usage.
- Documentation `cref` locations count as references unless deliberately
  excluded.
- A partial workspace load is not a clean answer. Use `--allow-partial` only
  when explicitly accepting and reporting incomplete evidence.
- Inspect ambiguity and resolved-definition output before acting on a short
  symbol name.

## Load the full reference selectively

Read [references/complete-reference.md](references/complete-reference.md) for
exact flags and output, solution/project closure traps, documentation behavior,
generated and inactive-code limits, verified edge cases, recipes, or the
Release-mode file-based Roslyn app fallback.
