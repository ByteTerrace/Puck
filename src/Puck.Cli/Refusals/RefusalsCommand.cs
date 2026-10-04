using System.CommandLine;

namespace Puck.Cli.Refusals;

// The `puck refusals` verb: prints the source census of the refusals world.refusals lists (RefusalCensus) as the header
// world.refusals prints over the whole catalog, and the projects it counted. The census canary holds the running
// World's reflective scan to the same census. Exit 0 counted, 2 missing repository root.
internal static class RefusalsCommand {
    private static int Run() {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        Console.WriteLine(value: $"{RefusalCensus.Header(census: RefusalCensus.Count(repositoryRoot: repositoryRoot))}]");
        Console.WriteLine(value: $"refusals: counted from {RefusalCensus.Projects.Count} project(s): {string.Join(separator: ", ", values: RefusalCensus.Projects)}");

        return 0;
    }

    public static Command Create() {
        var command = new Command(
            description: "Count the refusals world.refusals lists, from source.",
            name: "refusals"
        );

        command.SetAction(action: _ => Run());
        command.Detail(detail: """
            Counts every enum member tagged [Refusal(...)] in the projects whose assemblies the World's
            refusal catalog anchors, and the distinct doors they name, by reading the sources, never by
            loading the World. Prints the count as the header world.refusals prints over the whole
            catalog. The refusal-catalog-census canary holds the running World's reflective scan to this
            census, so a door whose assembly drops out of either side shows as a disagreement.
            """);

        return command;
    }
}
