using System.Text.Json;
using Puck.Overlays;
using Puck.SdfVm;

namespace Puck.World;

/// <summary>
/// The unified overlay's <see cref="IOverlayFrameSources"/> implementation for Puck.World — adapts the binder's
/// <see cref="WorldFrameSource"/> vocabulary (camera/view/probe/capture) to the opaque integer keys a HUD
/// <c>Frame</c> element's overlay slot addresses its source by. <see cref="WorldHudFeed"/> assigns a key to every
/// declared <c>Frame</c> element's source through <see cref="KeyFor"/> on the structure rebuild; the compositor's
/// per-produced-frame slot table calls <see cref="TryAcquire"/> to resolve that key's live lease. A single-threaded
/// class: both calls run on the render thread, the same discipline every other binder-owned feed already keeps.
/// </summary>
internal sealed class WorldOverlayFrameSources : IOverlayFrameSources {
    private readonly WorldScreenBinder m_binder;

    // The cached-structure key table, keyed by canonical wire form plus seat rather than the source record alone: a
    // Camera.Controls.Vendor list must compare structurally, and a bare Camera source with no
    // authored Seat (record-equal across every seat's identity panel) still needs a DISTINCT key per seat — the
    // enclosing seat scope KeyFor's caller passes is part of the source's identity here, not a resolve-time detail.
    private readonly Stack<int> m_freeKeys = new();
    private readonly Dictionary<(string Source, int Seat), int> m_keys = new();
    private readonly List<SourceEntry?> m_sources = [];

    private readonly OverlayFrameSourceGeneration m_generation;

    public WorldOverlayFrameSources(WorldScreenBinder binder) {
        m_binder = binder;
        m_generation = new OverlayFrameSourceGeneration(
            release: ReleaseActiveKey,
            retain: RetainKey
        );
    }

    private void ReleaseActiveKey(int key) {
        var entry = m_sources[key]!;

        m_binder.ReleaseFrameSource(source: entry.Source, seat: entry.Seat);
        TryRecycle(key: key);
    }
    private void RetainKey(int key) {
        var entry = m_sources[key]!;

        m_binder.RetainFrameSource(source: entry.Source, seat: entry.Seat);
    }
    private void TryRecycle(int key) {
        if (
            m_generation.IsActive(key: key) ||
            (m_sources[key] is not { } entry) ||
            (entry.StructureReferences != 0) ||
            !entry.Leases.IsIdle
        ) {
            return;
        }

        m_binder.ForgetFrameSource(source: entry.Source, seat: entry.Seat);
        _ = m_keys.Remove(key: (entry.StructuralSource, entry.Seat));
        m_sources[key] = null;
        m_freeKeys.Push(item: key);
    }

    /// <summary>Resolves a key for one cached HUD element's <see cref="WorldFrameSource"/> in a seat scope. The key
    /// remains valid until that cached structure calls <see cref="ReleaseStructureKey"/>; the active generation
    /// separately retains its producer only while a visible HUD panel names it.</summary>
    /// <param name="source">The frame source a HUD element names.</param>
    /// <param name="seat">The 1-based enclosing seat scope — the owning identity panel's slot+1 for a player-scope
    /// element, or 1 for a world-scope element. A camera source's own authored <c>Seat</c>, when present, still
    /// wins; this is only the fallback and the disambiguator between two seats' otherwise-identical bare sources.</param>
    /// <returns>The key <see cref="TryAcquire"/> resolves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public int KeyFor(WorldFrameSource source, int seat) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var structuralSource = JsonSerializer.Serialize(
            value: source,
            jsonTypeInfo: WorldJsonContext.Default.WorldFrameSource
        );
        var entry = (structuralSource, seat);

        if (m_keys.TryGetValue(
            key: entry,
            value: out var existing
        )) {
            m_sources[existing]!.StructureReferences++;

            return existing;
        }

        var key = (m_freeKeys.TryPop(result: out var recycled) ? recycled : m_sources.Count);
        var sourceEntry = new SourceEntry(
            binder: m_binder,
            key: key,
            onIdle: TryRecycle,
            seat: seat,
            source: source,
            structuralSource: structuralSource
        );

        if (key == m_sources.Count) {
            m_sources.Add(item: sourceEntry);
        } else {
            m_sources[key] = sourceEntry;
        }

        m_keys[entry] = key;

        return key;
    }
    /// <summary>Releases one cached HUD element's logical use of a key. Producer ownership is tracked separately by
    /// the active generation and outstanding GPU leases; the slot is recycled only after all three reach zero.</summary>
    public void ReleaseStructureKey(int key) {
        if (
            (key < 0) ||
            (key >= m_sources.Count) ||
            (m_sources[key] is not { StructureReferences: > 0 } entry)
        ) {
            return;
        }

        entry.StructureReferences--;
        TryRecycle(key: key);
    }
    /// <summary>Begins the visible HUD source set for one produced frame.</summary>
    public void BeginGeneration() => m_generation.BeginGeneration();
    /// <summary>Ends the visible HUD source set and releases sources absent from it.</summary>
    public void EndGeneration() => m_generation.EndGeneration();
    /// <summary>Marks one stable key as used by a visible world- or seat-scope HUD panel this generation.</summary>
    /// <param name="key">The key returned by <see cref="KeyFor"/>.</param>
    public void MarkActive(int key) => m_generation.MarkActive(key: key);
    /// <inheritdoc/>
    public bool TryAcquire(int key, out OverlayFrameLease lease) {
        if (
            (key < 0) ||
            (key >= m_sources.Count) ||
            !m_generation.IsActive(key: key) ||
            (m_sources[key] is not { } entry) ||
            !m_binder.TryAcquireFrame(
                source: entry.Source,
                seat: entry.Seat,
                frame: out var frame
            )
        ) {
            lease = default;

            return false;
        }

        lease = entry.Leases.Retain(frame: in frame);

        return true;
    }

    private sealed class SourceEntry {
        public FrameLeaseRelay Leases { get; }
        public int Seat { get; }
        public WorldFrameSource Source { get; }
        public string StructuralSource { get; }
        public int StructureReferences { get; set; } = 1;

        public SourceEntry(
            WorldScreenBinder binder,
            int key,
            Action<int> onIdle,
            WorldFrameSource source,
            string structuralSource,
            int seat
        ) {
            Leases = new FrameLeaseRelay(binder: binder, key: key, onIdle: onIdle, seat: seat, source: source);
            Seat = seat;
            Source = source;
            StructuralSource = structuralSource;
        }
    }
    // OverlayFrameSlots can hold this key once in the current frame and once pending retirement from the previous
    // frame. Each acquisition adds a binder reference, so a generation ending before the prior overlay fence retires
    // cannot dispose the capture/view/camera resource that pass still samples.
    private sealed class FrameLeaseRelay {
        private readonly WorldScreenBinder m_binder;
        private readonly Action<int> m_release;
        private readonly Action<int> m_onIdle;
        private readonly LeaseSlot[] m_slots = new LeaseSlot[2];
        private readonly WorldFrameSource m_source;
        private readonly int m_key;
        private readonly int m_seat;

        public FrameLeaseRelay(WorldScreenBinder binder, int key, Action<int> onIdle, WorldFrameSource source, int seat) {
            m_binder = binder;
            m_key = key;
            m_onIdle = onIdle;
            m_release = Release;
            m_seat = seat;
            m_source = source;
        }

        public OverlayFrameLease Retain(in SdfScreenSourceFrame frame) {
            for (var token = 0; (token < m_slots.Length); token++) {
                if (m_slots[token].Active) {
                    continue;
                }

                m_binder.RetainFrameSource(seat: m_seat, source: m_source);
                m_slots[token] = new LeaseSlot(
                    Active: true,
                    Release: frame.Release,
                    ReleaseToken: frame.ReleaseToken
                );

                return new OverlayFrameLease(
                    ImageViewHandle: frame.ImageViewHandle,
                    Release: m_release,
                    ReleaseToken: token
                );
            }

            frame.Release?.Invoke(obj: frame.ReleaseToken);

            throw new InvalidOperationException(message: "A HUD frame source has more than two overlay frames awaiting retirement.");
        }

        public bool IsIdle => (!m_slots[0].Active && !m_slots[1].Active);

        private void Release(int token) {
            if ((token < 0) || (token >= m_slots.Length) || !m_slots[token].Active) {
                return;
            }

            var slot = m_slots[token];

            m_slots[token] = default;

            try {
                slot.Release?.Invoke(obj: slot.ReleaseToken);
            } finally {
                m_binder.ReleaseFrameSource(seat: m_seat, source: m_source);
                m_onIdle(m_key);
            }
        }

        private readonly record struct LeaseSlot(bool Active, Action<int>? Release, int ReleaseToken);
    }
}
