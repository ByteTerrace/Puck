using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

// A primer and an ordinary tick may report the same tick number. Only one hub publication shares an index.
internal interface IWorldInterestSink {
    void DeliverSnapshot(in WorldSnapshot snapshot, WorldInterestFrame frame);
}

internal readonly record struct WorldBodyView(WorldInterestFrame Frame, WorldSinkDisclosure Disclosure) {
    public bool Contains(int body, int generation) => Frame.Contains(Disclosure, body, generation);
    public ReadOnlyMemory<EntitySnapshot> Project() => Frame.Project(Disclosure);
}

internal sealed class WorldInterestFrame {
    private readonly Dictionary<int, int> m_ordinals = [];
    private readonly List<EntitySnapshot> m_events = [];
    private readonly Dictionary<(Vector3 Origin, float Radius), EntitySnapshot[]> m_spheres = [];
    private readonly Dictionary<ReadOnlyMemory<EntitySnapshot>, WorldBodyImage> m_images = [];
    private readonly Dictionary<(WorldBodyImage Current, WorldBodyImage? Baseline), WorldBodyChanges> m_changes = [];
    private Dictionary<WorldSinkDisclosure, WorldDeclaredRoster.Declared[]> m_rosters = [];
    private Dictionary<WorldSinkDisclosure, WorldDeclaredRoster.Declared[]> m_priorRosters = [];
    private sealed record ContinuityImage(Dictionary<int, (int Generation, EntityContinuity Hint)> Events, ReadOnlyMemory<EntitySnapshot> Result);
    private readonly Dictionary<ReadOnlyMemory<EntitySnapshot>, List<ContinuityImage>> m_continuities = [];
    private FixedSweepGrid? m_grid;
    private FixedSweepSphere[] m_points = [];
    private int[] m_candidates = [];
    private bool m_indexed;
    private bool m_gridded;
    private ReadOnlyMemory<EntitySnapshot> m_entries;

    public (long Bodies, long Bytes) Encoded { get; private set; }

    public static WorldInterestFrame ForPrimer(in WorldSnapshot snapshot) {
        var frame = new WorldInterestFrame();
        frame.Begin(snapshot.Entries);
        return frame;
    }

    public void Begin(ReadOnlyMemory<EntitySnapshot> entries) {
        m_entries = entries;
        Encoded = default;
        m_indexed = m_gridded = false;
        m_images.Clear();
        m_changes.Clear();
        m_continuities.Clear();
        (m_rosters, m_priorRosters) = (m_priorRosters, m_rosters);
        m_rosters.Clear();
        m_spheres.Clear();
    }

    private void Index() {
        if (m_indexed) { return; }
        m_ordinals.Clear();
        m_events.Clear();
        var entries = m_entries.Span;
        for (var i = 0; i < entries.Length; i++) {
            ref readonly var entry = ref entries[i];
            m_ordinals.Add(entry.Index, i);
            if (entry.Continuity.Kind != EntityContinuityKind.Continuous) { m_events.Add(entry); }
        }
        m_indexed = true;
    }

    public ReadOnlySpan<EntitySnapshot> Continuity {
        get { Index(); return CollectionsMarshal.AsSpan(m_events); }
    }

    public bool Contains(WorldSinkDisclosure disclosure, int body, int generation) {
        Index();
        if (!m_ordinals.TryGetValue(body, out var ordinal)) { return false; }
        ref readonly var entry = ref m_entries.Span[ordinal];
        if (entry.Generation != generation || !entry.Active) { return false; }
        if (disclosure.IsFull) { return true; }
        return m_ordinals.TryGetValue(disclosure.ObserverBodyIndex, out var observer) &&
            disclosure.Policy.Discloses(entry, disclosure.ObserverBodyIndex, m_entries.Span[observer].Position);
    }

    public ReadOnlyMemory<EntitySnapshot> Project(WorldSinkDisclosure disclosure) {
        if (disclosure.IsFull) { return m_entries; }
        Index();
        if (!m_ordinals.TryGetValue(disclosure.ObserverBodyIndex, out var ordinal)) { return default; }
        if (disclosure.Policy.Mode == WorldObserverDisclosureMode.SelfOnly) { return m_entries.Slice(ordinal, 1); }
        if (disclosure.Policy.Radius is not { } radius) { return default; }
        var origin = m_entries.Span[ordinal].Position;
        var key = (origin, radius);
        if (m_spheres.TryGetValue(key, out var previous)) { return previous; }

        // Round both endpoints for the broad phase. The live policy tests the original positions.
        var reach = FixedQ4816.FromDouble((double)MathF.BitIncrement(radius) + 2d / FixedQ4816.One.Value);
        if (m_grid is null || m_grid.Capacity < m_entries.Length) {
            m_grid = new FixedSweepGrid(m_entries.Length);
            m_points = new FixedSweepSphere[m_grid.Capacity];
            m_candidates = new int[m_grid.Capacity];
            m_gridded = false;
        }
        if (!m_gridded) {
            for (var i = 0; i < m_entries.Length; i++) {
                m_points[i] = new(i, FixedVector3.FromVector3(m_entries.Span[i].Position), FixedQ4816.Zero);
            }
            m_grid.Rebuild(m_points.AsSpan(0, m_entries.Length), reach);
            m_gridded = true;
        }
        var bounds = reach == FixedQ4816.MaxValue ? FixedAabb.Everywhere : FixedAabb.Around(FixedVector3.FromVector3(origin), reach);
        var count = m_grid.Query(bounds, m_candidates);
        WorldTickWork.Process.Add(WorldTickWork.InterestCandidates, count);
        var selected = new List<EntitySnapshot>(count);
        foreach (var candidate in m_candidates.AsSpan(0, count)) {
            ref readonly var entry = ref m_entries.Span[candidate];
            if (disclosure.Policy.Discloses(entry, disclosure.ObserverBodyIndex, origin)) { selected.Add(entry); }
        }
        selected.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        var result = selected.ToArray();
        m_spheres.Add(key, result);
        return result;
    }

    public WorldDeclaredRoster.Declared[] Roster(WorldSinkDisclosure policy, ReadOnlySpan<EntitySnapshot> entries) {
        var audience = policy.IsFull ? WorldSinkDisclosure.Full : policy;
        if (!m_rosters.TryGetValue(audience, out var rows)) {
            m_priorRosters.TryGetValue(audience, out var previous);
            rows = WorldDeclaredRoster.Describe(entries, previous);
            m_rosters.Add(audience, rows);
        }
        return rows;
    }

    public ReadOnlyMemory<EntitySnapshot> ApplyContinuity(ReadOnlyMemory<EntitySnapshot> entries,
        Dictionary<int, (int Generation, EntityContinuity Hint)> hints) {
        if (!m_continuities.TryGetValue(entries, out var variants)) {
            variants = [];
            m_continuities.Add(entries, variants);
        }
        foreach (var variant in variants) {
            if (hints.Count == variant.Events.Count && hints.All(pair =>
                variant.Events.TryGetValue(pair.Key, out var prior) && prior == pair.Value)) { return variant.Result; }
        }
        var result = WorldProjectionSampler.ApplyContinuity(entries, hints);
        variants.Add(new(new(hints), result));
        return result;
    }

    public WorldBodyChanges Changes(WorldBodyImage image, WorldBodyImage? baseline) {
        var key = (image, baseline);
        if (!m_changes.TryGetValue(key, out var changes)) {
            changes = image.Encode(baseline);
            Encoded = (Encoded.Bodies + changes.Count, Encoded.Bytes + changes.Bytes.Length);
            m_changes.Add(key, changes);
        }
        return changes;
    }

    public WorldBodyImage Capture(ReadOnlyMemory<EntitySnapshot> entries) {
        if (!m_images.TryGetValue(entries, out var image)) {
            image = WorldBodyImage.Capture(entries);
            m_images.Add(entries, image);
        }
        return image;
    }
}
