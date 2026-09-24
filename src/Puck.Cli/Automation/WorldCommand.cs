using System.CommandLine;

namespace Puck.Cli.Automation;

/// <summary><c>puck world</c> — prepare hosted world documents, or probe a running world's QUIC endpoint.</summary>
internal static class WorldCommand {
    /// <summary>Creates the <c>world</c> verb; <paramref name="clock"/> bounds its release and probe work.</summary>
    /// <param name="clock">The CLI host's clock.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) =>
        new(
            description: "Prepare hosted documents, manage releases, or probe a QUIC endpoint.",
            name: "world"
        ) { WorldPrepareCommand.Create(), WorldReleaseCommand.Create(clock: clock), WorldProbeCommand.Create(clock: clock) };
}
