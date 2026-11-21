namespace Puck.World.Client;

/// <summary>What a row's signal did between two readings.</summary>
[Flags]
public enum SignalEdge : byte {
    /// <summary>The signal held, or this is its first reading.</summary>
    None = 0,
    /// <summary>The signal's value differs from the last reading.</summary>
    Changed = 1,
    /// <summary>The signal went from zero or below to above zero.</summary>
    Rose = 2,
}

/// <summary>
/// Edge detection for the signals authored rows name (a haptic row's trigger, a key light's flash, a highlight's
/// trigger): each row id remembers its last reading, and <see cref="Observe(string, float)"/> reports what moved since.
/// A row's first reading, and the first after the tracker is cleared or bound to another mirror lifetime, only seeds it
/// and reports <see cref="SignalEdge.None"/>, so a world that boots with a signal already high never fires on it.
/// </summary>
/// <remarks>Presentation only and single-threaded: the owning director reads it on the pump thread.</remarks>
public sealed class WorldSignalEdges {
    private readonly Dictionary<string, float> m_last = new(comparer: StringComparer.Ordinal);

    private int m_lifetime = -1;
    private WorldStateMirror? m_mirror;

    /// <summary>Forgets every row's last reading, so each seeds again on its next observation.</summary>
    public void Clear() {
        m_last.Clear();
        m_lifetime = -1;
        m_mirror = null;
    }
    /// <summary>Follows the mirror the signals are read from: readings taken from another mirror, or from the same one
    /// after its lifetime moved (a document installed), no longer compare with the new ones, so they are forgotten.</summary>
    /// <param name="mirror">The mirror the next observations read.</param>
    public void Bind(WorldStateMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        if (ReferenceEquals(objA: m_mirror, objB: mirror) && (m_lifetime == mirror.Lifetime)) {
            return;
        }

        m_last.Clear();
        m_mirror = mirror;
        m_lifetime = mirror.Lifetime;
    }
    /// <summary>Records a row's reading and reports what moved since its last.</summary>
    /// <param name="id">The row's id.</param>
    /// <param name="value">The signal's value at this tick.</param>
    /// <returns>The edge between the last reading and this one, or <see cref="SignalEdge.None"/> on the first.</returns>
    public SignalEdge Observe(string id, float value) {
        ArgumentNullException.ThrowIfNull(argument: id);

        if (!m_last.TryGetValue(key: id, value: out var last)) {
            m_last[id] = value;

            return SignalEdge.None;
        }

        m_last[id] = value;

        return (
            ((value != last) ? SignalEdge.Changed : SignalEdge.None) |
            (((last <= 0f) && (value > 0f)) ? SignalEdge.Rose : SignalEdge.None)
        );
    }
    /// <summary>Reads a row's authored signal at the delivered tick and reports what moved since its last reading,
    /// binding to <paramref name="mirror"/> first.</summary>
    /// <param name="mirror">The mirror the signal reads.</param>
    /// <param name="id">The row's id.</param>
    /// <param name="signal">The row's authored signal.</param>
    /// <returns>The edge between the last reading and this one.</returns>
    public SignalEdge Observe(WorldStateMirror mirror, string id, in BindableScalar signal) {
        Bind(mirror: mirror);

        return Observe(
            id: id,
            value: mirror.Scalar(delivered: true, fallback: 0f, scalar: in signal)
        );
    }
    /// <summary>Reads a row's authored signal for one holder at the delivered tick and reports what moved since its
    /// last reading: a <c>$body</c> key reads the body the lease is bound to (<see cref="WorldStateLease.Scalar"/>).
    /// The lease must already be bound.</summary>
    /// <param name="lease">The holder's reads, bound to its mirror and body.</param>
    /// <param name="id">The row's id.</param>
    /// <param name="signal">The row's authored signal.</param>
    /// <returns>The edge between the last reading and this one; <see cref="SignalEdge.None"/> for an unbound
    /// lease.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lease"/> or <paramref name="id"/> is
    /// <see langword="null"/>.</exception>
    public SignalEdge Observe(WorldStateLease lease, string id, in BindableScalar signal) {
        ArgumentNullException.ThrowIfNull(argument: lease);

        if (lease.Mirror is not { } mirror) {
            return SignalEdge.None;
        }

        Bind(mirror: mirror);

        return Observe(
            id: id,
            value: lease.Scalar(delivered: true, fallback: 0f, scalar: in signal)
        );
    }
}
