using System.Buffers.Binary;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

// Entries are in stable slot order. A retained baseline owns its register values too.
internal sealed class WorldBodyImage(ReadOnlyMemory<EntitySnapshot> entries) {
    public ReadOnlyMemory<EntitySnapshot> Entries { get; } = entries;

    public static WorldBodyImage Capture(ReadOnlyMemory<EntitySnapshot> source) {
        var owned = source.ToArray();
        for (var i = 0; i < owned.Length; i++) {
            if (!owned[i].State.IsEmpty) { owned[i] = owned[i] with { State = owned[i].State.ToArray() }; }
        }
        return new(owned);
    }

    public WorldBodyChanges Encode(WorldBodyImage? baseline) {
        var before = (baseline?.Entries ?? default).Span;
        var writer = new WireWriter();
        var removed = new List<int>();
        int old = 0, count = 0;
        foreach (ref readonly var entry in Entries.Span) {
            while (old < before.Length && before[old].Index < entry.Index) { removed.Add(before[old++].Index); }
            if (old < before.Length && before[old].Index == entry.Index) {
                ref readonly var held = ref before[old++];
                if ((held with { State = entry.State }) == entry && held.State.Span.SequenceEqual(entry.State.Span)) { continue; }
            }
            WorldFederationCodec.WriteEntity(writer, entry);
            count++;
        }
        while (old < before.Length) { removed.Add(before[old++].Index); }
        WorldTickWork.Process.Add(WorldTickWork.InterestBodies, count);
        WorldTickWork.Process.Add(WorldTickWork.InterestBytes, writer.WrittenMemory.Length);
        return new(count, writer.WrittenMemory, removed.ToArray());
    }
}

internal readonly record struct WorldBodyChanges(int Count, ReadOnlyMemory<byte> Bytes, int[] Removed);

// Both ends retain the last N snapshots (other record kinds consume no slot). Only the tick thread installs
// images. The wire reader can acknowledge one exact stamp previously installed by this stream.
internal sealed class WorldBodyHistory {
    private readonly (WorldStreamStamp Stamp, WorldBodyImage? Image)[] m_sent =
        new (WorldStreamStamp, WorldBodyImage?)[WorldFederationProjectionSink.PendingDeliveryLimit];
    private WorldStreamStamp m_ack;
    private int m_next;

    public void Remember(WorldStreamStamp stamp, WorldBodyImage image) {
        lock (m_sent) {
            m_sent[m_next] = (stamp, image);
            m_next = (m_next + 1) % m_sent.Length;
        }
    }

    public void Clear() {
        lock (m_sent) {
            Array.Clear(m_sent);
            m_ack = default;
            m_next = 0;
        }
    }

    public void Acknowledge(WorldStreamStamp stamp) {
        lock (m_sent) {
            if (stamp.Ordinal > m_ack.Ordinal && Find(stamp) is not null) { m_ack = stamp; }
        }
    }

    internal WorldBodyImage? Find(WorldStreamStamp stamp) {
        foreach (var slot in m_sent) {
            if (slot.Stamp == stamp) { return slot.Image; }
        }
        return null;
    }

    public (long Ordinal, WorldBodyImage? Image) Baseline(WorldStreamStamp next) {
        lock (m_sent) {
            return m_ack.Stream == next.Stream && m_ack.Generation == next.Generation && Find(m_ack) is { } held
                ? (m_ack.Ordinal, held) : default;
        }
    }

}

/// <summary>Reconstructs a federation stream's body table against the acknowledged image named by each frame.</summary>
public sealed class WorldSnapshotReceiver {
    private readonly WorldBodyHistory m_history = new();

    /// <summary>Applies a full or delta body image after the stream ingress has admitted its stamp.</summary>
    public bool TryReceive(WorldStreamStamp stamp, bool delta, ReadOnlySpan<byte> payload, out WorldSnapshot snapshot) {
        snapshot = default;
        var removed = ReadOnlySpan<byte>.Empty;
        WorldBodyImage? baseline = null;
        if (delta) {
            if (payload.Length < 12) { return false; }
            var ordinal = BinaryPrimitives.ReadInt64LittleEndian(payload);
            var count = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
            if (ordinal >= stamp.Ordinal || (uint)count > WorldBodiesLimits.CapacityCeiling ||
                count > (payload.Length - 12) / sizeof(int)) { return false; }
            baseline = m_history.Find(stamp with { Ordinal = ordinal });
            if (baseline is null) { return false; }
            removed = payload.Slice(12, count * sizeof(int));
            payload = payload[(12 + removed.Length)..];
        }
        if (!WorldFederationCodec.TryDecodeSnapshot(payload, out snapshot, out _)) { return false; }

        var updates = snapshot.Entries.Span;
        var last = -1;
        foreach (ref readonly var entry in updates) {
            if (entry.Index <= last || (uint)entry.Index >= WorldBodiesLimits.CapacityCeiling) { return false; }
            last = entry.Index;
        }
        if (baseline is null) {
            m_history.Remember(stamp, new WorldBodyImage(snapshot.Entries));
            return true;
        }
        if (updates.IsEmpty && removed.IsEmpty) {
            m_history.Remember(stamp, baseline);
            snapshot = snapshot with { Entries = baseline.Entries };
            return true;
        }
        var before = baseline.Entries.Span;
        var entries = new List<EntitySnapshot>(Math.Max(before.Length, updates.Length));
        var changed = 0;
        foreach (var held in before) {
            while (changed < updates.Length && updates[changed].Index < held.Index) { entries.Add(updates[changed++]); }
            if (!removed.IsEmpty && BinaryPrimitives.ReadInt32LittleEndian(removed) == held.Index) {
                if (changed < updates.Length && updates[changed].Index == held.Index) { return false; }
                removed = removed[sizeof(int)..];
            } else {
                entries.Add(changed < updates.Length && updates[changed].Index == held.Index ? updates[changed++] : held);
            }
        }
        if (!removed.IsEmpty) { return false; }
        entries.AddRange(updates[changed..]);
        var image = new WorldBodyImage(entries.ToArray());
        m_history.Remember(stamp, image);
        snapshot = snapshot with { Entries = image.Entries };
        return true;
    }
}
