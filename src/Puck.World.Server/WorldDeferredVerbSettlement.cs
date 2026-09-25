using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Settles a submitted line with the tick-boundary verdict of the edit it started.</summary>
public static class WorldDeferredVerbSettlement {
    /// <summary>Takes the verb a local submission registered for this verdict and settles its line with it.</summary>
    /// <param name="echoes">The pending-verb table.</param>
    /// <param name="echo">The verdict.</param>
    /// <returns>The per-verb line the verdict settled with, <c>[&lt;verb&gt;: …]</c>, or <see langword="null"/> when
    /// the verdict answers no locally registered submission.</returns>
    /// <remarks>A host subscribes each table to its owning server's <see cref="WorldServer.EchoTap"/>: a
    /// registered verb nothing settles holds a settling session until the table evicts it. A
    /// <see cref="WorldEditEchoKind.GrantTable"/> echo never settles a registered verb: a rebuild replays its
    /// document's grants under its own correlation, and those echoes narrate that replay, not the rebuild's
    /// verdict.</remarks>
    public static string? Settle(this WorldDeferredVerbEchoes echoes, in WorldEditEcho echo) {
        ArgumentNullException.ThrowIfNull(argument: echoes);

        if (
            (echo.ConnectionId != SubmissionEnvelope.LocalConnectionId) ||
            (echo.Kind == WorldEditEchoKind.GrantTable) ||
            !echoes.TryTake(
                correlationId: echo.CorrelationId,
                settlement: out var settlement,
                verb: out var verb
            )
        ) {
            return null;
        }

        var verdict = $"[{verb}: {echo.Message}]";

        settlement!.Settle(result: (echo.Rejected
            ? CommandResult.Error(output: verdict)
            : new CommandResult(Output: verdict)
        ));

        return verdict;
    }
}
