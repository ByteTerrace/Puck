using Puck.Maths;
using System.Text.Json.Serialization;

namespace Puck.World.Protocol;

/// <summary>Authored logical channel names and independent register reach, projected only against the recipient's current mapping.</summary>
public readonly record struct WorldChannelSelection {
    private readonly string[]? m_channels;

    /// <summary>Copies and canonicalizes a bounded logical selection.</summary>
    [JsonConstructor]
    public WorldChannelSelection(IReadOnlyList<string> channels, ulong registers = 0) {
        if (channels.Count > WorldInputCatalog.MaximumLogicalChannels) { throw new ArgumentException(message: "channel selection exceeds 4096 names", paramName: nameof(channels)); }
        if (channels.Any(predicate: string.IsNullOrWhiteSpace)) { throw new ArgumentException(message: "channel selection contains an empty name", paramName: nameof(channels)); }
        m_channels = channels.Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();
        Registers = registers;
    }

    /// <summary>Canonical logical names, including currently inactive actions.</summary>
    public IReadOnlyList<string> Channels => Array.AsReadOnly(array: (m_channels ?? []));
    /// <summary>Independent target-register reach.</summary>
    public ulong Registers { get; }
    /// <summary>Whether the selection admits nothing.</summary>
    [JsonIgnore]
    public bool IsEmpty => ((Names.Length == 0) && (Registers == 0));

    /// <summary>Checks a semantic action, including text and currently inactive actions, without projecting it to a slot.</summary>
    public bool Contains(string name) {
        return (Names.BinarySearch(comparer: StringComparer.Ordinal, value: name) >= 0);
    }
    /// <summary>Projects names to live storage without allocating or inheriting another action's reused slot.</summary>
    public ChannelReachMask Project(WorldChannelTable table) {
        var bits = default(ChannelMask);

        foreach (var name in Names) {
            if (table.TryGetOrdinal(name: name, ordinal: out var ordinal)) { bits = bits.With(ordinal: ordinal); }
        }
        return new(Bits: bits, Registers: Registers);
    }
    /// <summary>Compares logical selections independently of collection identity.</summary>
    public bool Equals(WorldChannelSelection other) => ((Registers == other.Registers) && Names.SequenceEqual(other: other.Names, comparer: StringComparer.Ordinal));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Registers, SequenceHash.Of(Names));

    // The sorted, distinct names as a span, which is empty for the default value.
    private ReadOnlySpan<string> Names => m_channels;
}
