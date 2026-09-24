using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The narration sink every composition root binds so a headless script or canary keeps reading the exact
/// lines it read when the deterministic core wrote them straight to <see cref="Console.Error"/>, each narration framed
/// as one <see cref="ConsoleRecord"/>. Lives here rather than in Puck.World.Server because build/Architecture.props
/// denies that project a reference to <see cref="System.Console"/>.</summary>
public sealed class WorldConsoleNarrationSink : IWorldNarrationSink {
    /// <inheritdoc/>
    public void Narrate(in WorldNarration narration) =>
        ConsoleRecord.Write(
            record: narration.Text,
            writer: ((narration.Stream == WorldNarrationStream.Output)
                ? Console.Out
                : Console.Error)
        );
}
