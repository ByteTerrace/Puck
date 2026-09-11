using System.CommandLine;

namespace Puck.Cli.Official;

// The `puck official` verb: the local official-tree producer — build | serve | verify. No upload, no signing, no
// GitHub workflow; those are a separate, later concern. See the three sub-verbs for their own description text.
internal static class OfficialCommand {
    public static Command Create() {
        var command = new Command(description: """
            The local official tree producer: build, serve, and verify a puck.official.v1 tree.

            No sub-verb here uploads, signs, or drives a GitHub workflow — this verb's whole job is the local tree.
            """, name: "official");

        command.Subcommands.Add(item: OfficialBuildCommand.Create());
        command.Subcommands.Add(item: OfficialVerifyCommand.Create());
        command.Subcommands.Add(item: OfficialServeCommand.Create());

        return command;
    }
}
