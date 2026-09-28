using System.CommandLine;

using Puck.World;

namespace Puck.Cli.Registry;

// The `puck registry` verb: writes docs/world-name-registry.md from WorldNameRegistry over the live document model,
// or under --check regenerates it in memory and compares — the same drift-detection shape `puck schema --check`
// and `puck architecture --map` establish. Both modes first fail on a name-shaped member the registry neither
// registers nor excludes, so a document field added without a registration cannot pass.
// Exit 0 wrote or matched, 1 check found drift or an uncovered member, 2 usage error or missing repository root.
internal static class RegistryCommand {
    private const string RelativePath = "docs/world-name-registry.md";

    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var uncovered = WorldNameRegistry.Uncovered;

        if (uncovered.Count > 0) {
            Console.Error.WriteLine(value: $"registry: {uncovered.Count} name-shaped member(s) neither registered nor excluded in WorldNameRegistry:");

            foreach (var member in uncovered) {
                Console.Error.WriteLine(value: $"  {member}");
            }

            return 1;
        }

        return (CliGeneratedFile.WriteOrCheck(
            check: check,
            detail: $" ({WorldNameRegistry.Sites.Count} sites)",
            relativePath: RelativePath,
            repositoryRoot: repositoryRoot,
            source: "the model",
            text: WorldNameRegistry.Render(),
            verb: "registry"
        )
            ? 0
            : 1);
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Regenerate in memory and compare against docs/world-name-registry.md; write nothing, and exit 1 naming the first differing line.",
        description: """
        The world name registry, generated and checked.

        Generated from Puck.World.WorldNameRegistry (src/Puck.World.Schema) over the same
        source-generated WorldJsonContext the engine loads a world document through: every
        document field carrying a state, zone, rule, table, pattern, topology, generator,
        field, or dynamics name, with the role it carries the name in. WorldModuleNamespace
        reads the same registry to prefix an aliased import's names at compose time.

        Both modes first exit 1 with every name-shaped document member the registry neither
        registers nor excludes, so a document field added without a registration cannot pass.

        Written to: docs/world-name-registry.md.
        """,
        name: "registry",
        run: Run
    );
}
