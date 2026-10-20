using System.CommandLine;
using System.Text;

using Puck.Cli.Creation;
using Puck.Cli.Mesh;
using Puck.Cli.Registry;
using Puck.Cli.Test;
using Puck.Cli.Transpiler;
using Puck.Cli.Vocabulary;

namespace Puck.Cli;

/// <summary>
/// The world-authoring verbs this assembly holds, and the startup every process that runs them shares. The <c>puck</c>
/// root composes <see cref="Verbs"/> beside every other verb, and the assembly also runs on its own as the world
/// compiler a verb starts in a process of its own (<c>puck parity</c>'s tree compile).
/// </summary>
public static class WorldsRoot {
    /// <summary>This assembly's file name: the world compiler, beside the <c>puck</c> CLI that composes it.</summary>
    public const string CompilerAssembly = "Puck.Cli.Worlds.dll";

    /// <summary>Creates this assembly's verbs.</summary>
    /// <returns>compile, creation, decompile, embed, lint, lsp, mesh, migrate, registry, test, verbs and vocabulary.</returns>
    public static IEnumerable<Command> Verbs() => [
        CompileCommand.Create(),
        CreationCommand.Create(),
        DecompileCommand.Create(),
        EmbedCommand.Create(),
        LintCommand.Create(),
        LspCommand.Create(),
        MeshCommand.Create(),
        PuckMigrateCommand.Create(),
        RegistryCommand.Create(),
        TestCommand.Create(),
        VerbsCommand.Create(),
        VocabularyCommand.Create(),
    ];
    /// <summary>Prepares the process before any verb runs.</summary>
    public static void Start() {
        // Every verb emits source text — matched lines, comment records, drift reports — so the streams carry
        // whatever the tree contains. Without this the host falls back to the machine's console code page and
        // quietly substitutes non-ASCII (an em dash becomes '-', a math symbol becomes '?'), which corrupts the
        // JSONL data streams and makes output machine-dependent. The setter suppresses the byte-order mark.
        Console.OutputEncoding = Encoding.UTF8;
        // A world source the CLI compiles for a document (a basis or an import `puck test` stages, a composition a verb
        // reads) is compiled once across processes, sharing the game's persisted compile cache. The per-user directory is
        // named at this entry point, as the game names it at its own.
        Puck.World.Transpiler.Composition.WorldCompileCache.Shared.Persist(directory: Puck.Abstractions.PuckUserDirectory.Resolve(name: "compilations"));
    }
}
