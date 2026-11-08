---
name: tooling-files
description: "Maps shared repository tooling for files, paths, Git queries, project ownership and Markdown fences. Load before adding or changing a helper in these areas."
---

# Repository file and document helpers

Search by capability before adding a helper ([AGENTS.md](../../../AGENTS.md#core-rules)).
These are the shared homes; callers keep only their domain-specific decisions.

| Capability | Home |
|---|---|
| Lexical path containment and host path comparison | `PuckPaths.IsWithin`, `PuckPaths.Comparison` in `src/Puck.Abstractions/PuckPaths.cs` |
| Inspect path components for links, with an optional root | `CliTreeFiles.LinkedAncestor` in `src/Puck.Cli.Core/CliTreeFiles.cs` |
| Remove an owned directory tree without following descendant links | `ConfinedDirectory.RemoveTree` in `src/Puck.Confinement/ConfinedDirectory.cs` |
| Stage, flush, publish or discard complete replacement files | `AtomicFile`, including `PendingWrite`, in `src/Puck.Confinement/AtomicFile.cs` |
| Compare files or a file and bytes with bounded memory | `FileContent` in `src/Puck.Confinement/FileContent.cs` |
| Read source lines or nonempty process-output lines | `CliText` in `src/Puck.Cli.Core/CliText.cs` |
| Query Git, resolve common directories or commits, list unignored files | `CliGit` in `src/Puck.Cli.Core/CliGit.cs`; local reports use `ReadOnlyAsync` |
| Parse NUL-delimited worktree records | `CliWorktree.Parse` in `src/Puck.Cli.Core/CliWorktree.cs` |
| Find an owning project within an optional boundary | `CliProjects.FindOwningProject` in `src/Puck.Cli.Core/CliProjects.cs` |
| Read C# type declarations for project selection | `DeclarationsCommand.ReadTypes` in `src/Puck.Cli.Source/Analysis/DeclarationsCommand.cs` |
| Track Markdown code fences in link and quotation scans | `MarkdownFence` in `src/Puck.Cli.Source/Docs/MarkdownFence.cs` |
| Match compiler paths against analyzer allowlist identities | `AnalyzerAllowlistPath` in `src/Puck.Analyzers/AnalyzerAllowlistPath.cs` |

Lexical containment and ancestor inspection do not establish confinement against concurrent path replacement.
Deletion stays beneath the directory handle opened by `ConfinedDirectory.RemoveTree`; its internal traversal
refuses descendant links. The caller owns the selected root, whose ancestors are resolved by the OS.

Batch writers own pending writes until each is committed or disposed. `PendingWrite.Commit(overwrite: false)`
preserves a concurrent winner; replacing that with an existence check followed by an overwriting write does not.
Run [verification](../verification/SKILL.md)'s focused builds and laws for the changed boundary. Deletion and
persisted-data rewrites need the [hazards](../../../docs/development/hazards.md#silent-invariants) review.
