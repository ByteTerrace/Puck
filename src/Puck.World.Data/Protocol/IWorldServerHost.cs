namespace Puck.World.Protocol;

/// <summary>
/// The server-side surface <see cref="LoopbackTransport"/> fronts — exactly the methods <c>Puck.World.Server.WorldServer</c>
/// exposes for a loopback client to submit through and attach a sink to. A separate shape from <see cref="IServerLink"/>
/// on purpose: <see cref="IServerLink"/> is the client-facing submit vocabulary (the taps and the Submit* naming a
/// recording observes), while this is the server-facing surface the transport adapts it onto — deliberately narrow
/// (three members, not one per submission kind), because every non-intent submission funnels through the one ordered
/// domain <see cref="Submit"/> drains; there is no per-kind Enqueue*/Apply* here for a transport to split traffic across.
/// Puck.World.Data cannot name <c>Puck.World.Server.WorldServer</c> directly — this interface is what lets
/// <see cref="LoopbackTransport"/> hold a server reference without the assembly ever naming the concrete type, so
/// <c>Puck.World.Server</c> stays a downstream consumer of <c>Puck.World.Data</c> rather than a dependency of it.
/// </summary>
public interface IWorldServerHost {
    /// <summary>Binds the client sink the server delivers each tick's snapshot to — a subscribe, not an overwrite: the
    /// server's output hub supports more than one attached sink (play-and-host), so a second call adds a second
    /// subscriber rather than displacing the first. The newly attached sink also immediately receives the live
    /// definition and a non-consuming primer snapshot of current state (see the implementation's own remarks).</summary>
    /// <param name="sink">The client sink.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed. Disposal must happen on the tick
    /// thread; a caller meant to stay attached for the process's whole lifetime deliberately never disposes it.</returns>
    IDisposable AttachSink(IClientSink sink);

    /// <summary>Enqueues one entity's intent for a tick — the per-tick intent buffer, separate from the ordered domain
    /// <see cref="Submit"/> drains (fold is arrival-order-independent, so intents need no envelope/completion machinery).</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    void EnqueueIntent(in IntentSubmission submission);

    /// <summary>Submits one envelope into the server's single ordered domain for every non-intent submission kind —
    /// command, grant, revoke, session, definition, mutation, undo, composition, lever, and query all drain through
    /// this one method, never a per-kind path (grant-then-warp must apply against the new table; a split queue could
    /// not guarantee that). A local caller (<see cref="LoopbackTransport"/>) enqueues and drains inline, on the tick
    /// thread, inside this same call — so <paramref name="completion"/> (when supplied) has already fired by the time
    /// <see cref="Submit"/> returns, preserving today's synchronous submit-and-check semantics and stdin FIFO order
    /// exactly. No submission returns a value directly; every envelope resolves to a typed
    /// <see cref="WorldSubmissionResult"/> instead.</summary>
    /// <param name="envelope">The envelope to submit.</param>
    /// <param name="completion">Invoked once with the envelope's typed result, or <see langword="null"/> when the
    /// caller does not need one (most command/grant/revoke/definition/mutation/undo/composition/lever submissions —
    /// their outcome is already reported loudly on stderr and through <c>WorldServer.EchoTap</c>).</param>
    void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null);
}
