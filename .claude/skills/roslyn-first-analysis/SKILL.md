---
name: roslyn-first-analysis
description: Analyze C# code with the compiler and Roslyn instead of grep/regex. Use whenever a question about this codebase is SEMANTIC — who uses/references a symbol, what implements an interface, is this dead code, is a rename/deletion safe, what's the real call graph or dependency shape — or before starting any multi-file grep-based investigation of C# structure. Grep is still right for orientation, literal strings, and non-C# files; this skill is for questions where text matching produces wrong answers.
---

# Roslyn-first C# analysis

This skill is **procedural only**: it tells you how to answer questions about
C# code accurately, never what the code should look like. The user's current
instruction outranks it; if it argues against a requested change, it is stale
— update it in the same change and say so.

**The core rule:** if the question contains *uses*, *references*,
*implements*, *overrides*, *dead*, *unused*, *rename*, or *safe to delete*
about C# code, grep output is **not evidence**. Text matching cannot see
overload resolution, `using` aliases, extension methods, generic
instantiation, or name collisions. The compiler and Roslyn can. This was
proven here: a token-matrix audit nearly deleted `AllocatorExtensions`
(~70–100 real call sites) because extension-method usage doesn't mention the
class name.

## Pick the cheapest tool that actually answers the question

| Question shape | Tool |
|---|---|
| Where is a file / a literal string / a config value? Orientation in unfamiliar code? Non-C# assets (HLSL, JSON, MSBuild)? | Grep/Glob — correct and fastest. No shame here. |
| "Did my rename/refactor break anything?" | `dotnet build` — the compiler is the cheapest full-semantic oracle, and this repo compiles warnings like IDE0005 as errors. Don't grep for stragglers; build. |
| Structure queries within files: list declarations, members, attributes, base lists; comment/trivia-aware scans | **Syntax-level Roslyn** — parse + walk, no MSBuild, ~2 s over all of `src/`. Template: [templates/syntax-query.cs](templates/syntax-query.cs) |
| Cross-project semantics: find all references, implementers, dead-code candidates, safe-rename impact | **Semantic Roslyn** via `MSBuildWorkspace` + `SymbolFinder`. Template: [templates/find-references.cs](templates/find-references.cs) |

## The pattern: .NET 10 file-based apps

Write a single `.cs` file in the **session scratchpad** (never in the repo)
and run it directly — no project scaffolding:

```powershell
dotnet run find-references.cs -- D:\...\Puck.slnx SdfProgramBuilder
```

Copy a template from `templates/` next to this skill and adapt it. Directives
that matter:

- `#:package Microsoft.CodeAnalysis.CSharp.Workspaces@*` (semantic) or just
  `Microsoft.CodeAnalysis.CSharp@*` (syntax-only)
- `#:property PublishAot=false` — required when anything reflects (JSON, MSBuildWorkspace)
- **Do NOT add `Microsoft.Build.Locator`** — it fails the file-based-app
  build (MSBL001), and modern `MSBuildWorkspace` doesn't need it (it loads
  MSBuild in an out-of-process build host). No `RegisterDefaults()` call.
- `MSBuildWorkspace.OpenSolutionAsync` handles this repo's `.slnx` directly.
- First run pays package restore + (for semantic) solution load; syntax-only
  scripts are ~2 s end-to-end. For one symbol in one project, pass the
  `.csproj` instead of the solution to load less.

Both templates were run against this repo and verified (2026-07-02).

## Known traps (each one caused a real near-miss here)

- **Extension-method classes look unused** to identifier-token counting —
  call sites never name the class. Use `SymbolFinder.FindReferencesAsync`.
- **Name collisions lie**: `ISdfFrameSource.CaptureFrame` is a method,
  `CaptureFrame` is also a type; `HostDocument` keeps string properties that
  shadow enum names by design. Token/grep counts conflate them; symbols don't.
- **XML-doc `cref`s live in trivia** — a syntax walk that skips trivia will
  call doc-referenced symbols unused. Pass `descendIntoTrivia: true` when
  scanning tokens.
- **Generated/vendored code**: skip `obj/`, `bin/` (the templates do), and
  remember CsWin32 output is `internal` generated code — absence from search
  results is not absence of use.
