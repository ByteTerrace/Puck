using System.Text;

using Puck.World;

namespace Puck.Cli.Registry;

// The `puck registry` verb: writes docs/world-name-registry.md from WorldNameRegistry over the live document model,
// or under --check regenerates it in memory and compares — the same drift-detection shape `puck schema --check`
// and `puck architecture --map` establish. Both modes first fail on a name-shaped member the registry neither
// registers nor excludes, so a document field added without a registration cannot pass.
// Exit 0 wrote or matched, 1 check found drift or an uncovered member, 2 usage error or missing repository root.
internal static class RegistryCommand {
    private const string HelpText =
        """
        puck registry — the world name registry, generated and checked

        Usage: puck registry [--check]

        Options:
          --check      regenerate in memory and compare against docs/world-name-registry.md;
                       write nothing, exit 1 naming the first differing line, and exit 1 with
                       every name-shaped document member the registry neither registers nor
                       excludes
          -h, --help   this text

        Generated from Puck.World.WorldNameRegistry (src/Puck.World.Schema) over the same
        source-generated WorldJsonContext the engine loads a world document through: every
        document field carrying a state, zone, rule, table, pattern, topology, generator,
        field, or dynamics name, with the role it carries the name in. WorldModuleNamespace
        reads the same registry to prefix an aliased import's names at compose time.

        Written to: docs/world-name-registry.md.
        """;
    private const string RelativePath = "docs/world-name-registry.md";

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Flag(name: "check");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"registry: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

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

        var path = Path.Combine(path1: repositoryRoot, path2: RelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        var text = WorldNameRegistry.Render();

        if (!scanner.Has(name: "check")) {
            File.WriteAllText(path: path, contents: text, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.Out.WriteLine(value: $"registry: wrote {RelativePath} ({WorldNameRegistry.Sites.Count} sites).");

            return 0;
        }

        if (!File.Exists(path: path)) {
            Console.Error.WriteLine(value: $"registry: {RelativePath} is missing; run `puck registry` to write it.");

            return 1;
        }

        var onDisk = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n");

        if (string.Equals(a: onDisk, b: text, comparisonType: StringComparison.Ordinal)) {
            Console.Out.WriteLine(value: $"registry: {RelativePath} matches the model ({WorldNameRegistry.Sites.Count} sites).");

            return 0;
        }

        var expected = text.Split(separator: '\n');
        var actual = onDisk.Split(separator: '\n');
        var line = 0;

        while ((line < expected.Length) && (line < actual.Length) && string.Equals(a: expected[line], b: actual[line], comparisonType: StringComparison.Ordinal)) {
            line++;
        }

        Console.Error.WriteLine(value: $"registry: {RelativePath} disagrees with the model at line {(line + 1)}; run `puck registry` to rewrite it.");
        Console.Error.WriteLine(value: $"  on disk:   {((line < actual.Length) ? actual[line] : "(end of file)")}");
        Console.Error.WriteLine(value: $"  generated: {((line < expected.Length) ? expected[line] : "(end of file)")}");

        return 1;
    }
}
