namespace Puck.Cli.Official;

// The `puck official` verb: the local official-tree producer — build | serve | verify. No upload, no signing, no
// GitHub workflow; those are a separate, later concern. See the three sub-verbs for their own usage text.
internal static class OfficialCommand {
    private const string HelpText =
        """
        puck official — the local official tree producer: build, serve, and verify a puck.official.v1 tree

        Usage: puck official <build|verify|serve> [args ...]

        Sub-verbs:
          build    write a puck.official.v1 tree from the shipped engine, world documents, and their assets
          verify   re-hash and re-check a puck.official.v1 tree's own claims
          serve    a minimal static HTTP server over a tree, for a dev-server client to fetch from

        Run 'puck official <sub-verb> -h' for sub-verb-specific usage.

        No sub-verb here uploads, signs, or drives a GitHub workflow — this verb's whole job is the local tree.
        """;

    public static int Run(string[] args) {
        if ((args.Length == 0) || (args[0] is "-h" or "--help")) {
            Console.Out.WriteLine(value: HelpText);

            return ((args.Length == 0) ? 2 : 0);
        }

        var rest = args[1..];

        return args[0] switch {
            "build" => OfficialBuildCommand.Run(args: rest),
            "verify" => OfficialVerifyCommand.Run(args: rest),
            "serve" => OfficialServeCommand.Run(args: rest),
            var unknown => Unknown(name: unknown),
        };
    }
    private static int Unknown(string name) {
        Console.Error.WriteLine(value: $"official: unknown sub-verb '{name}'. Run 'puck official -h' for the list.");

        return 2;
    }
}
