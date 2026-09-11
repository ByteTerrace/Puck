using System.CommandLine;

namespace Puck.Cli.Automation;

/// <summary><c>puck world</c> — prepare hosted world documents, or probe a running world's QUIC endpoint.</summary>
internal static class WorldCommand {
    public static Command Create() =>
        new(description: "Prepare hosted documents or probe a QUIC endpoint.", name: "world") { WorldPrepareCommand.Create(), WorldProbeCommand.Create() };
}
