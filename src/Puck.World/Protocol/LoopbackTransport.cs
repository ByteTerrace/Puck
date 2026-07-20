using Puck.World.Server;

namespace Puck.World.Protocol;

/// <summary>The in-process transport binding one client to one <see cref="WorldServer"/>: commands, session requests,
/// and queries apply synchronously at submit (the host's command-apply window immediately precedes the tick's step, so
/// FIFO order and read-after-write are preserved — a byte transport would buffer to the same boundary), per-tick
/// intents and live world edits (mutations, definition swaps, journal undo) buffer to the step, and the produced
/// <see cref="WorldSnapshot"/> is pushed to the bound <see cref="IClientSink"/>. Single-threaded on the host tick; no
/// byte serialization.</summary>
internal sealed class LoopbackTransport : IServerLink {
    private readonly WorldServer m_server;

    /// <summary>Initializes a new instance of the <see cref="LoopbackTransport"/> class over the server it fronts.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public LoopbackTransport(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        m_server = server;
    }

    /// <summary>An optional record tap invoked with every submitted intent before it reaches the server — the seam the
    /// replay tape captures the live per-tick intent stream through. <see langword="null"/> (the default) is a free
    /// pass-through; set only while a recording is armed.</summary>
    public Action<IntentSubmission>? IntentTap { get; set; }

    /// <summary>An optional record tap invoked with every authority command before it applies — the command half of the
    /// captured per-tick server-input stream. <see langword="null"/> (the default) is a free pass-through.</summary>
    public Action<WorldCommand>? CommandTap { get; set; }

    /// <summary>Binds the client sink the server delivers each tick's snapshot to.</summary>
    /// <param name="sink">The client sink.</param>
    public void Bind(IClientSink sink) {
        m_server.AttachSink(sink: sink);
    }

    /// <inheritdoc/>
    public void SubmitIntent(in IntentSubmission submission) {
        IntentTap?.Invoke(obj: submission);
        m_server.EnqueueIntent(submission: in submission);
    }

    /// <inheritdoc/>
    public void SubmitCommand(WorldCommand command) {
        CommandTap?.Invoke(obj: command);
        m_server.ApplyCommand(command: command);
    }

    /// <inheritdoc/>
    public SessionReply SubmitSession(SessionRequest request) {
        return m_server.ApplySession(request: request);
    }

    /// <inheritdoc/>
    public QueryAnswer Query(WorldQuery query) {
        return m_server.Answer(query: query);
    }

    /// <inheritdoc/>
    public void SubmitDefinition(WorldDefinition definition, WorldPrincipal principal) {
        m_server.EnqueueDefinition(definition: definition, principal: principal);
    }

    /// <inheritdoc/>
    public void SubmitWorldMutation(WorldMutation mutation) {
        m_server.EnqueueMutation(mutation: mutation);
    }

    /// <inheritdoc/>
    public void SubmitUndo(int count, WorldPrincipal principal) {
        m_server.EnqueueUndo(count: count, principal: principal);
    }

    /// <inheritdoc/>
    public void SubmitGrant(WorldGrant grant) {
        m_server.Grant(grant: grant);
    }

    /// <inheritdoc/>
    public void SubmitRevoke(WorldGrant grant) {
        m_server.Revoke(grant: grant);
    }

    /// <inheritdoc/>
    public void SubmitComposition(WorldComposition composition, WorldPrincipal principal) {
        m_server.ApplyComposition(composition: composition, principal: principal);
    }
}
