using System.CommandLine;

using Puck.Scripting;

namespace Puck.Cli.WasmStdlib;

// The `puck wasm-stdlib` verb: writes every generated Rust source registered in
// Puck.Scripting.WasmStdlibSources.All, or under --check compares each against the file on disk. Thin wrapper
// only: Puck.Scripting.WasmStdlibSources owns the registry — where each artifact lives and how to produce it
// (the two FixedQ4816 Rust-port files via Puck.Maths.FixedQ4816RustPort.EmitGenerated()/
// EmitVectors(), and the addon-ABI Rust mirror — wire enums, capability mask, layout constants — via
// AddonAbiRustPort.EmitGenerated()). Adding a future artifact is a one-line addition to
// the registry — this verb never changes.
// Exit 0 wrote or matched, 1 check found drift, 2 on a missing repository root.
public static class WasmStdlibCommand {
    private static int Run(bool check) {
        // Every registry path names a repo convention (e.g. wasm/puck-stdlib/src), not an argument, so it
        // is anchored at the repository root — the same asymmetry `scan` documents for its own defaults.
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        return (CliGeneratedFile.WriteOrCheckAll(
            check: check,
            detail: "",
            eachFile: true,
            files: [.. WasmStdlibSources.All.Select(selector: static source => new GeneratedFile(RelativePath: source.RelativePath, Text: source.Emit()))],
            repositoryRoot: repositoryRoot,
            source: "the live FixedQ4816 and addon-ABI types",
            verb: "wasm-stdlib"
        )
            ? 0
            : 1);
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Regenerate every Rust source in memory and compare it, and its staged copy, against the file on disk; write nothing, and exit 1 naming the first differing line of each stale file.",
        description: "Regenerate the generated Rust sources of the WASM standard library, or check them.",
        detail: """
        Writes every artifact registered in Puck.Scripting.WasmStdlibSources.All by calling its Emit delegate
        directly — the FixedQ4816 Rust port (interval tables, polynomial coefficients, and known-answer vectors
        read from the live FixedQ4816 type) and the addon-ABI Rust mirror (wire enums, capability mask, and
        layout constants read from the live host types), never transcribed by hand. A stale port is a
        determinism risk for WASM guests, so CI runs --check on every change.

        Never hand-edit a generated file. Adding a future artifact is a one-line addition to the registry, never
        a change to this verb.

        Exit codes: 0 wrote or matched every file, 1 --check found a file missing, stale or unstaged,
        2 repository root not found.
        """,
        name: "wasm-stdlib",
        run: Run
    );
}
