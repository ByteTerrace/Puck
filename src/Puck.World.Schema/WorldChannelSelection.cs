using System.Text.Json.Serialization;

namespace Puck.World.Protocol;

/// <summary>Authored logical channel names and independent register reach, projected only against the recipient's current mapping.</summary>
public readonly record struct WorldChannelSelection {
    private readonly IReadOnlyList<string>? m_channels;

    /// <summary>Copies and canonicalizes a bounded logical selection.</summary>
    [JsonConstructor]
    public WorldChannelSelection(IReadOnlyList<string> channels, ulong registers = 0) {
        if (channels.Count > WorldInputCatalog.MaximumLogicalChannels) { throw new ArgumentException("channel selection exceeds 4096 names", nameof(channels)); }
        if (channels.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("channel selection contains an empty name", nameof(channels)); }
        m_channels = Array.AsReadOnly(channels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Registers = registers;
    }

    /// <summary>Canonical logical names, including currently inactive actions.</summary>
    public IReadOnlyList<string> Channels => m_channels ?? [];
    /// <summary>Independent target-register reach.</summary>
    public ulong Registers { get; }
    /// <summary>Whether the selection admits nothing.</summary>
    [JsonIgnore]
    public bool IsEmpty => Channels.Count == 0 && Registers == 0;

    /// <summary>Checks a semantic action, including text and currently inactive actions, without projecting it to a slot.</summary>
    public bool Contains(string name) {
        var low = 0;
        var high = Channels.Count - 1;
        while (low <= high) {
            var middle = low + ((high - low) / 2);
            var order = StringComparer.Ordinal.Compare(Channels[middle], name);
            if (order == 0) { return true; }
            if (order < 0) { low = middle + 1; } else { high = middle - 1; }
        }
        return false;
    }

    /// <summary>Projects names to live storage without allocating or inheriting another action's reused slot.</summary>
    public ChannelReachMask Project(WorldChannelTable table) {
        var bits = default(ChannelMask);
        for (var index = 0; index < Channels.Count; index++) {
            if (table.TryGetOrdinal(Channels[index], out var ordinal)) { bits = bits.With(ordinal); }
        }
        return new(bits, Registers);
    }
    /// <summary>Compares logical selections independently of collection identity.</summary>
    public bool Equals(WorldChannelSelection other) => Registers == other.Registers && Channels.SequenceEqual(other.Channels, StringComparer.Ordinal);
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(Registers);
        foreach (var name in Channels) { hash.Add(name, StringComparer.Ordinal); }
        return hash.ToHashCode();
    }
}
