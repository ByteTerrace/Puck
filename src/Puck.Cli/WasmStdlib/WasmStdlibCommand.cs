using System.Text;

using Puck.Scripting;

namespace Puck.Cli.WasmStdlib;

// The `puck wasm-stdlib` verb: writes every generated Rust source registered in
// Puck.Scripting.WasmStdlibSources.All to disk. Thin wrapper only: Puck.Scripting.WasmStdlibSources owns
// the registry — where each artifact lives and how to produce it
// (the two FixedQ4816 Rust-port files via Puck.Maths.FixedQ4816RustPort.EmitGenerated()/
// EmitVectors(), and the addon-ABI Rust mirror — wire enums, capability mask, layout constants — via
// AddonAbiRustPort.EmitGenerated()). Running this verb twice against an unchanged build must produce
// byte-identical files; nothing checks that today — the drift gate that compared the registry's output
// against what is committed left the build. Adding a future artifact is a one-line addition to the
// registry — this verb never changes.
// Exit 0 on success, 2 on a usage error, a missing repository root, or a missing destination directory.
internal static class WasmStdlibCommand {
    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"wasm-stdlib: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        // Every registry path names a repo convention (e.g. wasm/puck-stdlib/src), not an argument, so it
        // is anchored at the repository root — the same asymmetry `scan` documents for its own defaults.
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        foreach (var source in WasmStdlibSources.All) {
            var fullPath = Path.Combine(path1: repositoryRoot, path2: source.RelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(path: fullPath);

            if ((directory is null) || !Directory.Exists(path: directory)) {
                Console.Error.WriteLine(value: $"wasm-stdlib: destination directory not found for {source.RelativePath} (expected {CliPaths.ToDisplay(fullPath: (directory ?? fullPath))}).");

                return 2;
            }

            var contents = source.Emit();

            File.WriteAllText(contents: contents, encoding: encoding, path: fullPath);

            Console.Error.WriteLine(value: $"wasm-stdlib: wrote {CliPaths.ToDisplay(fullPath: fullPath)} ({contents.Length} chars).");
        }

        return 0;
    }

    private const string HelpText =
        """
        wasm-stdlib   regenerate the generated Rust sources of the WASM standard library

          -h / --help   this text

        Writes every artifact registered in Puck.Scripting.WasmStdlibSources.All by
        calling its Emit delegate directly — the FixedQ4816 Rust port (interval
        tables, polynomial coefficients, and known-answer vectors read from the live
        FixedQ4816 type) and the addon-ABI Rust mirror (wire enums, capability mask,
        and layout constants read from the live host types), never transcribed by hand.
        Writes every registered file unconditionally; running this verb twice against
        an unchanged build must produce byte-identical files, though no gate checks
        that today. Takes no other arguments.
        Never hand-edit a generated file. Adding a future artifact is a one-line
        addition to the registry, never a change to this verb.
        Exit codes: 0 wrote every file, 2 usage error, repository root not found, or a
        destination directory missing.
        """;
}
