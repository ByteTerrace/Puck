using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Answers the console's registered lines from a row's authority echoes.</summary>
public static class WorldDeferredVerbSettlement {
    /// <summary>Answers one echo from <paramref name="row"/>'s authority through the console's table
    /// (<see cref="WorldDeferredVerbEchoes.Answer"/>): a verdict for a line the console registered on that row settles
    /// it, prints a rebuild or undo verb's own line and counts a refusal; any other echo answers nothing.</summary>
    /// <param name="echoes">The console's table.</param>
    /// <param name="echo">The echo.</param>
    /// <param name="row">The row whose authority raised it, as its console link names it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="echoes"/> or <paramref name="row"/> is
    /// <see langword="null"/>.</exception>
    /// <remarks>A host subscribes each row's <see cref="WorldServer.EchoTap"/>: a registered verb nothing answers holds a
    /// settling session until the table evicts it.</remarks>
    public static void Answer(this WorldDeferredVerbEchoes echoes, in WorldEditEcho echo, string row) {
        ArgumentNullException.ThrowIfNull(argument: echoes);

        echoes.Answer(
            correlationId: echo.CorrelationId,
            grantTable: (echo.Kind == WorldEditEchoKind.GrantTable),
            local: (echo.ConnectionId == SubmissionEnvelope.LocalConnectionId),
            message: echo.Message,
            rejected: echo.Rejected,
            row: row
        );
    }
}
