using System.Diagnostics.CodeAnalysis;

namespace Puck.Commands;

/// <summary>
/// The typed, enumerable, scriptable face of "what can be toggled" — the composition root registers a
/// <see cref="FeatureSwitchDescriptor"/> per engine lever (each closing over the object that owns it), and the
/// <c>feature.*</c> verbs, the run-document <c>host.features</c> map, and the benchmark sweep all read and drive the
/// engine through this one surface. The registry holds no engine references itself; it is pure control-plane state.
/// </summary>
/// <remarks>
/// Registration happens once at composition, before the switches are used. Reads and writes thereafter run on the frame
/// thread, so the registry deliberately carries no locking — a caller that registers after the run has begun is a bug,
/// not a supported concurrency mode.
/// </remarks>
public sealed class FeatureSwitchRegistry {
    private readonly List<FeatureSwitchDescriptor> m_ordered = [];
    private readonly Dictionary<string, FeatureSwitchDescriptor> m_byName = new(comparer: StringComparer.Ordinal);

    /// <summary>The registered switches, in registration order.</summary>
    public IReadOnlyList<FeatureSwitchDescriptor> All => m_ordered;

    /// <summary>Registers a switch. The name must be unique.</summary>
    /// <param name="descriptor">The switch to register.</param>
    /// <exception cref="ArgumentException">A switch with the same <see cref="FeatureSwitchDescriptor.Name"/> is already
    /// registered.</exception>
    public void Register(FeatureSwitchDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(argument: descriptor);

        if (!m_byName.TryAdd(key: descriptor.Name, value: descriptor)) {
            throw new ArgumentException(
                message: $"A feature switch named '{descriptor.Name}' is already registered.",
                paramName: nameof(descriptor)
            );
        }

        m_ordered.Add(item: descriptor);
    }

    /// <summary>Looks up a switch by its exact name.</summary>
    /// <param name="name">The switch's dotted name.</param>
    /// <param name="descriptor">The resolved switch, or <see langword="null"/> when no switch has that name.</param>
    /// <returns>Whether a switch with <paramref name="name"/> is registered.</returns>
    public bool TryGet(string name, [MaybeNullWhen(returnValue: false)] out FeatureSwitchDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return m_byName.TryGetValue(key: name, value: out descriptor);
    }

    /// <summary>Captures every switch's current value.</summary>
    /// <returns>A snapshot suitable for a later <see cref="Restore"/>.</returns>
    public FeatureSwitchSnapshot Snapshot() {
        var values = new Dictionary<string, string>(capacity: m_ordered.Count, comparer: StringComparer.Ordinal);

        foreach (var descriptor in m_ordered) {
            values[descriptor.Name] = descriptor.Get();
        }

        return new FeatureSwitchSnapshot(Values: values);
    }

    /// <summary>Re-applies a previously captured snapshot, restoring each switch to the value it held. Best-effort: a
    /// switch not present in the snapshot is left untouched, and a value the switch now rejects is skipped.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    public void Restore(FeatureSwitchSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(argument: snapshot);

        foreach (var descriptor in m_ordered) {
            if (snapshot.Values.TryGetValue(key: descriptor.Name, value: out var value)) {
                _ = descriptor.Set(arg: value);
            }
        }
    }
}
