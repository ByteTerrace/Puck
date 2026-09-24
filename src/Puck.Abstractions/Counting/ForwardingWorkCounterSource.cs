namespace Puck.Abstractions.Counting;

/// <summary>
/// A stable <see cref="IWorkCounterSource"/> in front of the counting instances an owner creates and retires over its
/// life — an arena rebuilt when its world's definition changes, a search rebuilt with it, or a frame source for each
/// offscreen view that comes and goes beside the primary one. A reader registers the forwarder once, and its totals
/// never go down: it reads the sum of its target (<see cref="Retarget"/>) and every attached instance
/// (<see cref="Attach"/>), and an instance that retires has its totals carried forward.
/// <para>
/// Retargeting, attaching, detaching and reading take one lock, so a read never sees an instance without the carried
/// totals, or the carried totals twice. A retired instance must not be counted into afterwards; what it counts after it
/// retires is not read.
/// </para>
/// </summary>
public sealed class ForwardingWorkCounterSource : IWorkCounterSource {
    private readonly List<IWorkCounterSource> m_attached = [];

    private readonly long[] m_carried;

    private readonly Lock m_gate = new();

    private readonly WorkKind[] m_kinds;

    private IWorkCounterSource? m_target;

    /// <summary>Initializes a new instance of the <see cref="ForwardingWorkCounterSource"/> class with no target and no
    /// attached instance: every declared kind reads zero until one is given.</summary>
    /// <param name="name">The source's name, which every instance it forwards to shares.</param>
    /// <param name="kinds">The kinds it forwards, in the order a report lists them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a dotted work name.</exception>
    public ForwardingWorkCounterSource(string name, ReadOnlySpan<WorkKind> kinds) {
        Name = WorkKind.RequireSourceName(
            name: name,
            paramName: nameof(name)
        );
        m_kinds = kinds.ToArray();
        m_carried = new long[m_kinds.Length];
    }

    /// <inheritdoc/>
    public string Name { get; }
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds =>
        m_kinds;

    /// <summary>Makes <paramref name="target"/> the one instance the owner replaces, first carrying forward everything
    /// the current target counted. Retargeting to the current target changes nothing.</summary>
    /// <param name="target">The owner's new counting instance, which must carry this forwarder's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> carries another source's name.</exception>
    public void Retarget(IWorkCounterSource target) {
        RequireSameName(
            paramName: nameof(target),
            source: target
        );

        lock (m_gate) {
            if (ReferenceEquals(
                objA: m_target,
                objB: target
            )) {
                return;
            }

            if (m_target is { } retiring) {
                Carry(retiring: retiring);
            }

            m_target = target;
        }
    }
    /// <summary>Adds <paramref name="instance"/> to the instances the forwarder sums beside its target, until
    /// <see cref="Detach"/>. Attaching an instance already attached changes nothing.</summary>
    /// <param name="instance">A counting instance the owner runs alongside the others, which must carry this
    /// forwarder's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instance"/> carries another source's name.</exception>
    public void Attach(IWorkCounterSource instance) {
        RequireSameName(
            paramName: nameof(instance),
            source: instance
        );

        lock (m_gate) {
            if (!m_attached.Contains(item: instance)) {
                m_attached.Add(item: instance);
            }
        }
    }
    /// <summary>Retires an attached instance: carries forward everything it counted and stops reading it. An instance
    /// not attached is ignored.</summary>
    /// <param name="instance">The attached instance the owner is retiring.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    public void Detach(IWorkCounterSource instance) {
        ArgumentNullException.ThrowIfNull(instance);

        lock (m_gate) {
            if (m_attached.Remove(item: instance)) {
                Carry(retiring: instance);
            }
        }
    }
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        var index = Array.IndexOf(
            array: m_kinds,
            value: kind
        );

        if (index < 0) {
            value = 0L;

            return false;
        }

        lock (m_gate) {
            var total = m_carried[index];

            if (m_target is { } target) {
                total = checked((total + Read(
                    instance: target,
                    kind: kind
                )));
            }

            foreach (var instance in m_attached) {
                total = checked((total + Read(
                    instance: instance,
                    kind: kind
                )));
            }

            value = total;
        }

        return true;
    }

    private static long Read(IWorkCounterSource instance, WorkKind kind) {
        _ = instance.TryRead(
            kind: kind,
            value: out var value
        );

        return value;
    }
    // Folds a retiring instance's totals into the carried ones; the caller holds the lock.
    private void Carry(IWorkCounterSource retiring) {
        for (var index = 0; (index < m_kinds.Length); index++) {
            m_carried[index] = checked((m_carried[index] + Read(
                instance: retiring,
                kind: m_kinds[index]
            )));
        }
    }
    private void RequireSameName(IWorkCounterSource source, string paramName) {
        ArgumentNullException.ThrowIfNull(
            argument: source,
            paramName: paramName
        );

        if (!string.Equals(
            a: source.Name,
            b: Name,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new ArgumentException(
                message: $"Work source '{source.Name}' cannot stand in for '{Name}'.",
                paramName: paramName
            );
        }
    }
}
