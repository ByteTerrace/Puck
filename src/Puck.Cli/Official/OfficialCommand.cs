using System.CommandLine;

namespace Puck.Cli.Official;

// The `puck official` verb: the local official-tree producer — build | serve | verify. No upload, no signing, no
// GitHub workflow; those are a separate, later concern. See the three sub-verbs for their own description text.
internal static class OfficialCommand {
    public static Command Create() {
        var command = new Command(
            description: "Build, serve, and verify a local puck.official.manifest.v1 tree.",
            name: "official"
        );

        command.Detail(detail: "No subcommand uploads, signs, or drives a GitHub workflow: this verb's whole job is the local tree.");

        command.Subcommands.Add(item: OfficialBuildCommand.Create());
        command.Subcommands.Add(item: OfficialVerifyCommand.Create());
        command.Subcommands.Add(item: OfficialServeCommand.Create());

        return command;
    }
}
