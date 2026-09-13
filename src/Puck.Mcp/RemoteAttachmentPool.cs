using ModelContextProtocol.Protocol;
using Puck.Hosting;

namespace Puck.Mcp;

// Application attachments survive stateless HTTP requests, but never change owner or silently reconnect.
internal sealed class RemoteAttachmentPool : IAsyncDisposable {
    private readonly TimeProvider m_clock;
    private readonly RemoteMcpHost m_host;
    private readonly RemoteMcpAccessPolicy m_policy;
    private readonly RemoteMcpOptions m_options;

    private bool m_disposed;
    private int m_opening;
    private TaskCompletionSource? m_openingsDrained;
    private Task? m_sweep;

    public RemoteAttachmentPool(RemoteMcpOptions options, TimeProvider clock, RemoteMcpHost host, RemoteMcpAccessPolicy policy) {
        this.m_options = options; this.m_clock = clock; m_host = host; m_policy = policy;
        policy.Changed += RevokeRemoved;
    }

    internal async Task<string?> AttachAsync(RemoteMcpCaller caller, CancellationToken token) {
        var owner = caller.Subject;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );
            if ((m_attachments.Count + m_opening) >= 4) { return null; }
            var opening = m_openingByOwner.GetValueOrDefault(key: owner);

            if ((opening + m_attachments.Values.Count(predicate: attachment => (attachment.Owner == owner))) >= 2) { return null; }
            m_openingByOwner[owner] = (opening + 1);
            m_opening++;
            m_sweep ??= SweepAsync();
        }
        IControlSession? client = null;

        try {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(
                token1: token,
                token2: m_stop.Token
            );

            client = await m_host.AttachAsync(
                caller: caller,
                cancellationToken: stop.Token
            ).ConfigureAwait(continueOnCapturedContext: false);
            lock (m_gate) {
                ObjectDisposedException.ThrowIf(
                    condition: m_disposed,
                    instance: this
                );
                stop.Token.ThrowIfCancellationRequested();
                if (!m_policy.Allows(subject: owner)) { throw new UnauthorizedAccessException(message: "Operator grant revoked."); }
                var id = Guid.NewGuid().ToString(format: "N");

                m_attachments.Add(
                    key: id,
                    value: new(
                        owner,
                        client,
                        m_clock.GetTimestamp()
                    )
                );
                client = null;
                return id;
            }
        } finally {
            client?.Dispose();
            lock (m_gate) {
                if (--m_openingByOwner[owner] == 0) { m_openingByOwner.Remove(key: owner); }
                if (--m_opening == 0) { m_openingsDrained?.TrySetResult(); }
            }
        }
    }
    internal async ValueTask<CallToolResult> CallAsync(string owner, string id, CallToolRequestParams parameters, CancellationToken token) {
        Attachment attachment;

        lock (m_gate) {
            if (
                m_disposed ||
                !m_attachments.TryGetValue(
                key: id,
                value: out attachment!
            ) ||
                (attachment.Owner != owner)
            ) { return OperatorMcpServer.Error(
                message: "Unknown, closed or expired attachment. Attach explicitly before issuing new work.",
                unknown: false
            ); }
            if (attachment.Busy) { return OperatorMcpServer.Error(
                message: "Attachment busy; await its active operation.",
                unknown: false
            ); }
            if (m_clock.GetElapsedTime(startingTimestamp: attachment.LastUse).TotalSeconds >= m_options.IdleTimeoutSeconds) {
                m_attachments.Remove(key: id); attachment.Close();
                return OperatorMcpServer.Error(
                    message: "Attachment expired; attach explicitly before issuing new work.",
                    unknown: false
                );
            }
            attachment.Busy = true;
        }
        try {
            using var active = CancellationTokenSource.CreateLinkedTokenSource(
                token1: token,
                token2: attachment.Token
            );
            var result = await OperatorMcpServer.CallAsync(
                attachment.Client,
                parameters,
                active.Token,
                checked(++attachment.Sequence)
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (result.StructuredContent?.GetProperty(propertyName: "status").GetString() == "unknown") { Detach(
                id: id,
                owner: owner
            ); }
            return result;
        } catch (OperationCanceledException) { Detach(
            id: id,
            owner: owner
        ); throw; } finally { lock (m_gate) { attachment.Busy = false; attachment.LastUse = m_clock.GetTimestamp(); } }
    }
    internal bool Detach(string owner, string id) {
        lock (m_gate) {
            if (
                !m_attachments.TryGetValue(
                key: id,
                value: out var attachment
            ) ||
                (attachment.Owner != owner)
            ) { return false; }
            m_attachments.Remove(key: id);
            attachment.Close();
            return true;
        }
    }

    private void RevokeRemoved() {
        lock (m_gate) {
            foreach (var (id, attachment) in m_attachments) {
                if (!m_policy.Allows(subject: attachment.Owner)) { m_attachments.Remove(key: id); attachment.Close(); }
            }
        }
    }
    private async Task SweepAsync() {
        using var timer = new PeriodicTimer(
            period: TimeSpan.FromSeconds(seconds: 1),
            timeProvider: m_clock
        );

        try {
            while (await timer.WaitForNextTickAsync(cancellationToken: m_stop.Token).ConfigureAwait(continueOnCapturedContext: false)) {
                lock (m_gate) {
                    foreach (var (id, attachment) in m_attachments) {
                        if (
                            !attachment.Busy &&
                            (m_clock.GetElapsedTime(startingTimestamp: attachment.LastUse).TotalSeconds >= m_options.IdleTimeoutSeconds)
                        ) {
                            m_attachments.Remove(key: id); attachment.Close();
                        }
                    }
                }
            }
        } catch (OperationCanceledException) when (m_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync() {
        Task? sweep;
        Task openings;

        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            m_policy.Changed -= RevokeRemoved;
            m_stop.Cancel();
            foreach (var attachment in m_attachments.Values) { attachment.Close(); }
            m_attachments.Clear();
            sweep = m_sweep;
            openings = ((m_opening == 0)
                ? Task.CompletedTask
                : (m_openingsDrained ??= new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)).Task
            );
        }
        if (sweep is not null) { await sweep.ConfigureAwait(continueOnCapturedContext: false); }
        await openings.ConfigureAwait(continueOnCapturedContext: false);
        m_stop.Dispose();
    }

    private readonly Lock m_gate = new();
    private readonly Dictionary<string, Attachment> m_attachments = new(comparer: StringComparer.Ordinal);
    private readonly CancellationTokenSource m_stop = new();
    private readonly Dictionary<string, int> m_openingByOwner = new(comparer: StringComparer.Ordinal);

    private sealed class Attachment(string owner, IControlSession client, long lastUse) {
        private readonly CancellationTokenSource m_closed = new();

        internal string Owner { get; } = owner;
        internal IControlSession Client { get; } = client;
        internal long LastUse { get; set; } = lastUse;

        internal bool Busy { get; set; }
        internal CancellationToken Token => m_closed.Token;

        internal void Close() {
            // Run cancellation callbacks outside the pool lock.
            _ = m_closed.CancelAsync().ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            Client.Dispose();
        }

        internal long Sequence;
    }
}
