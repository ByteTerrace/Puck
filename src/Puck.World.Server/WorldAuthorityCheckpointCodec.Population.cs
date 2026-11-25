using Puck.Maths;
using Puck.Networking;
using Puck.Commands;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    internal static void WriteTargetDesignation(WireWriter writer, WorldTargetDesignation target) {
        writer.WriteInt32(value: target.Index);
        writer.WriteFixedVector(value: target.Point);
    }
    internal static WorldTargetDesignation ReadTargetDesignation(ref WireReader reader) {
        var index = reader.ReadInt32();
        var point = reader.ReadFixedVector();

        return new WorldTargetDesignation(
            Index: index,
            Point: point
        );
    }

    /// <summary>Folds the simulation continuation of captured population entries: the value
    /// <see cref="WorldPopulation.AppendContinuationHash(ref Fnv1aHash, bool)"/> folds from the live slots the entries were
    /// captured from. Each slot's digest folds its sections' digests, the slots fold into segments, and what no slot
    /// digest holds (the resting bodies' contact-field observations and the profiles) folds beside the segments.</summary>
    /// <param name="hash">The hash to fold into.</param>
    /// <param name="entries">The entries, in slot order.</param>
    /// <param name="capacity">The population's slot count; every entry's index is below it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An entry's index is outside <paramref name="capacity"/>.</exception>
    public static void AppendPopulationEntries(ref Fnv1aHash hash, IReadOnlyList<WorldPopulationEntryCheckpoint> entries, int capacity) {
        ArgumentNullException.ThrowIfNull(argument: entries);

        var writer = new WireWriter();
        var digests = new ulong[capacity];
        var present = new bool[capacity];
        var beside = ContinuationBeside.Create();

        for (var index = 0; (index < entries.Count); index++) {
            var entry = entries[index];

            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: capacity, value: entry.Index);
            present[entry.Index] = true;
            digests[entry.Index] = ContinuationSlotDigest(entry: ref entry, held: 0U, heldDigests: default, work: null, writer: writer);
            beside.Add(entry: in entry, work: null, writer: writer);
        }
        FoldContinuationSegments(digests: digests, hash: ref hash, present: present);
        beside.Fold(hash: ref hash);
    }
    /// <summary>The digest of one section: its encoded bytes, hashed alone.</summary>
    /// <typeparam name="TSection">The section type.</typeparam>
    /// <param name="writer">The scratch writer the section is encoded into.</param>
    /// <param name="section">The section.</param>
    /// <returns>The digest.</returns>
    public static ulong SectionDigest<TSection>(WireWriter writer, in TSection section) where TSection : struct, IContinuationSection<TSection> {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.Reset();
        TSection.Write(section: in section, writer: writer);
        return Fnv1aHash.Compute(values: writer.WrittenSpan);
    }

    // One slot's continuation digest: the digests of its folded sections, in the continuation's order, and nothing
    // else. Nothing a section does not hold can reach it. A section a holder keeps (one bit of held per
    // WorldContinuationSectionKind) is folded by the digest its holder handed out, never encoded here; the entry's
    // value for it is not read.
    internal static ulong ContinuationSlotDigest(WireWriter writer, ref WorldPopulationEntryCheckpoint entry, WorldContinuationFoldWork? work, uint held, ReadOnlySpan<ulong> heldDigests) {
        var digester = new SectionDigester(hash: Fnv1aHash.Create(), held: held, heldDigests: heldDigests, work: work, writer: writer);

        WorldPopulationEntryCheckpoint.VisitSections(entry: ref entry, visitor: ref digester);
        return digester.Hash.Value;
    }
    // The digest of what a slot's checkpoint carries beside its sections, for a fold that compares one body's whole
    // image (a replay scope's): encoded afresh, cached nowhere.
    internal static ulong ContinuationTailDigest(WireWriter writer, in WorldPopulationEntryTail tail, WorldContinuationFoldWork? work) {
        writer.Reset();
        WriteTail(tail: in tail, writer: writer);
        if (work is not null) { work.TailBytes += writer.WrittenSpan.Length; }
        return Fnv1aHash.Compute(values: writer.WrittenSpan);
    }
    // The continuation's shape: the slot count, then one digest per segment of WorldPopulation.HashSegmentSlots slots,
    // each over its slots' presence and the present slots' digests.
    internal static void FoldContinuationSegments(ref Fnv1aHash hash, ReadOnlySpan<ulong> digests, ReadOnlySpan<bool> present) {
        hash.Add(value: ((uint)digests.Length));
        for (var first = 0; (first < digests.Length); first += WorldPopulation.HashSegmentSlots) {
            var segment = Fnv1aHash.Create();
            var end = Math.Min(val1: (first + WorldPopulation.HashSegmentSlots), val2: digests.Length);

            for (var index = first; (index < end); index++) {
                segment.Add(value: (present[index] ? ((byte)1) : ((byte)0)));
                if (present[index]) { segment.Add(value: digests[index]); }
            }
            hash.Add(value: segment.Value);
        }
    }

    /// <summary>The parts of a population's continuation that no slot digest folds, accumulated slot by slot in slot
    /// order and folded once, beside the segments. Each is a value a body's own state cannot key: a relation to the
    /// process's contact-field counters, or a profile its identity can change with no write to the body.</summary>
    /// <remarks>The parked rigid bodies observe the field together, so they fold as one bit; an asleep kit body folds
    /// its own slot and bit; an awake body's observation is current by construction and folds nothing. A profiled
    /// slot folds its slot and its projection's digest, taken from the projection's content on every fold.</remarks>
    internal struct ContinuationBeside {
        private bool m_parkedObservationCurrent;
        private uint m_asleepCount;
        private Fnv1aHash m_asleep;
        private uint m_profileCount;
        private Fnv1aHash m_profiles;

        internal static ContinuationBeside Create() => new() {
            m_asleep = Fnv1aHash.Create(),
            m_parkedObservationCurrent = true,
            m_profiles = Fnv1aHash.Create(),
        };
        internal void Add(WireWriter writer, in WorldPopulationEntryCheckpoint entry, WorldContinuationFoldWork? work) {
            if (entry.Body.Clocks.RigidSleepRootPlusOne != 0) {
                m_parkedObservationCurrent &= entry.Tail.ContactFieldObservationCurrent;
            } else if (entry.Body.Clocks.AsleepSinceTick != 0UL) {
                m_asleepCount++;
                m_asleep.Add(value: ((uint)entry.Index));
                m_asleep.Add(value: (entry.Tail.ContactFieldObservationCurrent ? ((byte)1) : ((byte)0)));
            }
            if (entry.Tail.Profile is { } profile) {
                writer.Reset();
                WorldIdentityProjectionWire.Write(projection: profile, writer: writer);
                m_profileCount++;
                m_profiles.Add(value: ((uint)entry.Index));
                m_profiles.Add(value: Fnv1aHash.Compute(values: writer.WrittenSpan));
                if (work is not null) {
                    work.Profiles++;
                    work.ProfileBytes += writer.WrittenSpan.Length;
                }
            }
        }
        internal readonly void Fold(ref Fnv1aHash hash) {
            hash.Add(value: (m_parkedObservationCurrent ? ((byte)1) : ((byte)0)));
            hash.Add(value: m_asleepCount);
            hash.Add(value: m_asleep.Value);
            hash.Add(value: m_profileCount);
            hash.Add(value: m_profiles.Value);
        }
    }

    private ref struct SectionDigester(WireWriter writer, Fnv1aHash hash, WorldContinuationFoldWork? work, uint held, ReadOnlySpan<ulong> heldDigests) : IContinuationSectionVisitor {
        private readonly ReadOnlySpan<ulong> m_heldDigests = heldDigests;

        internal Fnv1aHash Hash = hash;

        public void Visit<TSection>(ref TSection section) where TSection : struct, IContinuationSection<TSection> {
            if (!TSection.Folded) { return; }
            if ((held & (1U << ((int)TSection.Section))) != 0U) {
                Hash.Add(value: m_heldDigests[((int)TSection.Section)]);
                return;
            }
            Hash.Add(value: SectionDigest(section: in section, writer: writer));
            if (work is not null) {
                work.SectionEncodes[((int)TSection.Section)]++;
                work.SectionBytes[((int)TSection.Section)] += writer.WrittenSpan.Length;
            }
        }
    }
    private readonly struct SectionWriter(WireWriter writer) : IContinuationSectionVisitor {
        public void Visit<TSection>(ref TSection section) where TSection : struct, IContinuationSection<TSection> =>
            TSection.Write(section: in section, writer: writer);
    }
    private ref struct SectionReader : IContinuationSectionVisitor {
        internal WireReader Reader;

        public void Visit<TSection>(ref TSection section) where TSection : struct, IContinuationSection<TSection> =>
            section = TSection.Read(reader: ref Reader);
    }

    /// <summary>Encodes the exact continuation of one body a remote owner restores its prediction from
    /// (<see cref="Puck.World.Protocol.WorldOwnBody"/>): its population entry and the poses of the moving solids its step
    /// reads, each in the encoding a checkpoint holds them in.</summary>
    /// <param name="entry">The captured entry.</param>
    /// <param name="attached">The world's attached solid rows as the step left them.</param>
    /// <returns>The encoded continuation.</returns>
    public static byte[] EncodeOwnBody(in WorldPopulationEntryCheckpoint entry, IReadOnlyList<WorldAttachedSolidCheckpoint> attached) {
        var writer = new WireWriter();

        WritePopulationEntry(entry: entry, writer: writer);
        WriteAttachedSolids(rows: attached, writer: writer);
        return writer.ToArray();
    }
    /// <summary>Decodes a continuation <see cref="EncodeOwnBody"/> encoded, checking every section as a checkpoint's
    /// entries are checked.</summary>
    /// <param name="bytes">The encoded continuation.</param>
    /// <param name="entry">The entry, on success.</param>
    /// <param name="attached">The attached solid rows, on success.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the bytes decode to one whole continuation.</returns>
    public static bool TryDecodeOwnBody(ReadOnlySpan<byte> bytes, out WorldPopulationEntryCheckpoint entry, out WorldAttachedSolidCheckpoint[] attached, out string reason) {
        var reader = new WireReader(bytes: bytes);

        entry = ReadPopulationEntry(reader: ref reader);
        attached = ReadAttachedSolids(reader: ref reader);
        if (!reader.TryFinish(failure: out var failure)) {
            entry = default;
            attached = [];
            reason = $"own body: {failure}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
    private static void WriteAttachedSolids(WireWriter writer, IReadOnlyList<WorldAttachedSolidCheckpoint> rows) =>
        writer.WriteArray(items: rows, writeItem: static (w, row) => {
            w.WriteBoolean(value: row.Resolved); w.WriteFixedVector(value: row.Origin); w.WriteFixedQuaternion(value: row.Rotation); w.WriteInt64(value: row.Scale.Value);
        });
    private static WorldAttachedSolidCheckpoint[] ReadAttachedSolids(ref WireReader reader) => reader.ReadArray(field: "attached solids", maximum: MaxCollectionCount,
        readItem: static (ref WireReader r) => new WorldAttachedSolidCheckpoint(r.ReadBoolean(), r.ReadFixedVector(), r.ReadFixedQuaternion(), FixedQ4816.FromRawBits(value: r.ReadInt64())));
    private static void WritePopulationEntry(WireWriter writer, WorldPopulationEntryCheckpoint entry) {
        var sections = new SectionWriter(writer: writer);

        writer.WriteInt32(value: entry.Index);
        WorldPopulationEntryCheckpoint.VisitSections(entry: ref entry, visitor: ref sections);
        WriteTail(tail: in entry.Tail, writer: writer);
    }
    private static void WriteTail(WireWriter writer, in WorldPopulationEntryTail tail) {
        writer.WriteBoolean(value: tail.ContactFieldObservationCurrent);
        WorldIdentityProjectionWire.WriteOptional(
            projection: tail.Profile,
            writer: writer
        );
        WriteLongs(values: tail.Registers.Span, writer: writer);
    }
    private static WorldPopulationEntryCheckpoint ReadPopulationEntry(ref WireReader reader) {
        var entry = default(WorldPopulationEntryCheckpoint);

        entry.Index = reader.ReadInt32();

        var sections = new SectionReader { Reader = reader };

        WorldPopulationEntryCheckpoint.VisitSections(entry: ref entry, visitor: ref sections);
        reader = sections.Reader;
        entry.Tail = new(
            ContactFieldObservationCurrent: reader.ReadBoolean(),
            Profile: WorldIdentityProjectionWire.ReadOptional(reader: ref reader),
            Registers: ReadLongArray(field: "action state lane image", reader: ref reader)
        );
        if (reader.Failed) {
            return entry;
        }

        // What is consistent only across sections: every live scripted duration names the actor who authorized it.
        ReadOnlySpan<ulong> timerTicks = entry.Body.Action.TimerTicks;
        ReadOnlySpan<bool> pendingPress = entry.Body.Action.PendingPress;
        var timerActors = entry.Body.ScriptActors.Timers.AsSpan();
        var pendingActors = entry.Body.ScriptActors.Pending.AsSpan();

        for (var ordinal = 0; (ordinal < timerTicks.Length); ordinal++) {
            if ((timerTicks[ordinal] > 0UL) && (timerActors[ordinal] == default(Principal))) {
                reader.Fail(detail: "live scripted timer has no actor", refusal: WireRefusal.PayloadMalformed);
            }
            if (pendingPress[ordinal] && (pendingActors[ordinal] == default(Principal))) {
                reader.Fail(detail: "pending scripted press has no actor", refusal: WireRefusal.PayloadMalformed);
            }
        }
        foreach (ref readonly var segment in entry.Body.Tape.Segments.Span) {
            if (segment.Actor == default(Principal)) { reader.Fail(detail: "scripted tape segment has no actor", refusal: WireRefusal.PayloadMalformed); }
        }

        return entry;
    }
    private static byte[] EncodePopulation(WorldPopulationCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteInt32(value: section.SimulatedCount);
        writer.WriteInt32(value: section.Revision);
        writer.WriteByte(value: section.SeatKit);
        writer.WriteArray(
            items: section.Generations,
            writeItem: static (w, generation) => w.WriteInt32(value: generation)
        );
        writer.WriteArray(
            items: section.Entries,
            writeItem: WritePopulationEntry
        );
        writer.WriteArray(items: section.EventBodies, writeItem: static (w, index) => w.WriteInt32(value: index));
        writer.WriteArray(items: section.SleepingGravity, writeItem: static (w, row) => {
            w.WriteInt32(value: row.Body);
            w.WriteOptional(value: row.Acceleration, writeValue: static (v, acceleration) => v.WriteFixedVector(value: acceleration));
        });
        WriteAttachedSolids(rows: (section.AttachedSolids ?? []), writer: writer);

        return writer.ToArray();
    }
    private static bool TryDecodePopulation(byte[] bytes, out string reason, out WorldPopulationCheckpoint section) => TryDecodeSection(
        bytes: bytes,
        name: "population",
        read: ReadPopulation,
        reason: out reason,
        section: out section
    );
    private static WorldPopulationCheckpoint ReadPopulation(ref WireReader reader) {
        var simulatedCount = reader.ReadInt32();
        var revision = reader.ReadInt32();
        var seatKit = reader.ReadByte();
        var generations = reader.ReadArray(
            field: "population slot generations",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.IndexLimit
        );
        var entries = reader.ReadArray(
            field: "population entries",
            readItem: static (ref WireReader r) => ReadPopulationEntry(reader: ref r),
            maximum: MaxCollectionCount
        );
        var eventBodies = reader.ReadArray(field: "population event bodies", maximum: WorldBodiesLimits.IndexLimit,
            readItem: static (ref WireReader r) => r.ReadInt32());
        var gravity = reader.ReadArray(field: "population sleeping gravity", maximum: WorldBodiesLimits.IndexLimit,
            readItem: static (ref WireReader r) => (r.ReadInt32(), r.ReadOptional(readValue: static (ref WireReader v) => v.ReadFixedVector())));
        var attached = ReadAttachedSolids(reader: ref reader);

        return new WorldPopulationCheckpoint(
            AttachedSolids: attached,
            Entries: entries,
            EventBodies: eventBodies,
            Generations: generations,
            Revision: revision,
            SeatKit: seatKit,
            SimulatedCount: simulatedCount,
            SleepingGravity: gravity
        );
    }
    // ---- grants section ----

}
