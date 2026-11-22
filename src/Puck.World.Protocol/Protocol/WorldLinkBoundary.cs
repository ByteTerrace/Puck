using System.Collections.Concurrent;
using Puck.Abstractions.Pacing;

namespace Puck.World.Protocol;

/// <summary>Shares one pump window and one ingress order across the host's local loopback links.</summary>
public sealed class WorldLinkBoundary : IHostTickBoundary {
    [ThreadStatic] private static WorldLinkBoundary? OpenWindow;

    private readonly ConcurrentQueue<Action> m_waiting = new();
    private int m_pumpThread;

    // Before registration, boot can submit synchronously. Once the loop runs, only its open window can submit.
    internal bool IsOpen => ReferenceEquals(OpenWindow, this) || Volatile.Read(ref m_pumpThread) == 0;

    internal void Enqueue(Action submission) => m_waiting.Enqueue(submission);

    /// <inheritdoc/>
    public void RegisterPump() {
        if (Interlocked.CompareExchange(ref m_pumpThread, Environment.CurrentManagedThreadId, 0) != 0) {
            throw new InvalidOperationException("The link already has a registered pump.");
        }
    }
    /// <inheritdoc/>
    public void UnregisterPump() {
        RequirePump();
        Volatile.Write(ref m_pumpThread, 0);
    }
    /// <inheritdoc/>
    public void Open(bool drain) {
        RequirePump();
        if (OpenWindow is not null) {
            throw new InvalidOperationException("A link boundary window is already open on this thread.");
        }

        OpenWindow = this;
        if (!drain) { return; }

        try {
            while (m_waiting.TryDequeue(out var submission)) { submission(); }
        } catch {
            Close();
            throw;
        }
    }
    /// <inheritdoc/>
    public void Close() => OpenWindow = null;

    private void RequirePump() {
        if (Volatile.Read(ref m_pumpThread) != Environment.CurrentManagedThreadId) {
            throw new InvalidOperationException("Only the registered pump can open the link boundary window.");
        }
    }
}
