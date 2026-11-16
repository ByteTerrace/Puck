using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a holder hands out a cached digest only under the version it was taken at or over
/// content equal to the content it was taken from; its write side moves its version and notes its slot once per
/// publish; the notes publish every written slot, partition by partition; and a derived answer is recomputed exactly
/// when a holder it reads moved.</summary>
public sealed class VersionedHolderLawTests {
    private struct Pose : IVersionedSection {
        public long X;
        public long Y;
    }
    private record struct Named(string Name, ImmutableArray<int> Rows) : IVersionedSection;

    // Counts its encodes in the scratch so a law can tell a cached answer from a fresh one.
    private sealed class Scratch {
        public int Encodes;
    }
    private readonly struct PoseCodec : ISectionCodec<Pose, Scratch> {
        public static ulong Digest(Scratch writer, in Pose section, out int bytes) {
            writer.Encodes++;
            bytes = 16;
            return unchecked((((ulong)section.X) * 1_000_003UL) + ((ulong)section.Y));
        }
    }
    private readonly struct TableCodec : ISectionTableCodec<long, Scratch> {
        public static ulong Digest(Scratch writer, ReadOnlySpan<long> elements, out int bytes) {
            var digest = ((ulong)elements.Length);

            writer.Encodes++;
            bytes = (elements.Length * 8);
            foreach (var element in elements) { digest = unchecked((digest * 31UL) + ((ulong)element)); }
            return digest;
        }
    }
    private readonly struct NamedCodec : ISectionCodec<Named, Scratch> {
        public static ulong Digest(Scratch writer, in Named section, out int bytes) {
            writer.Encodes++;
            bytes = (section.Name?.Length ?? 0);
            return unchecked((((ulong)(section.Name?.Length ?? 0)) * 131UL) + ((ulong)(section.Rows.IsDefault ? 0 : section.Rows.Length)));
        }
    }
    private readonly struct Sum : IDerivation<Pose, long>, IDerivation<Pose, Pose, long> {
        public static long Derive(in Pose section) => (section.X + section.Y);
        public static long Derive(in Pose first, in Pose second) => (first.X + second.X);
    }

    // THE LAW: a section written back to the value it held hands out its cached digest and counts one filtered
    // compare. The first move after the first digest must encode, since the holder keeps the bytes a compare needs
    // only from its second encode on, and it encodes to the digest it had; every same-value write after that is
    // answered by the compare. The red leg is a holder that trusts the version alone and encodes on every write.
    [Fact]
    public void ASectionWrittenBackToItsValueHandsOutItsCachedDigestAndCountsOneFilteredCompare() {
        var holder = new Versioned<Pose>(binding: new SlotBinding(), value: new Pose { X = 3, Y = 4 });
        var scratch = new Scratch();
        var work = default(SectionDigestWork);
        var first = holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work);

        Assert.Equal(expected: first, actual: holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: 1, actual: scratch.Encodes);

        holder.Write.X = 3;
        Assert.Equal(expected: first, actual: holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: (2, 1L, 0L), actual: (scratch.Encodes, work.UnchangedEncodes, work.FilteredCompares));

        holder.Write.X = 3;
        Assert.Equal(expected: first, actual: holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: (2, 1L, 1L, ((long)Unsafe.SizeOf<Pose>())), actual: (scratch.Encodes, work.UnchangedEncodes, work.FilteredCompares, work.ComparedBytes));

        holder.Write.X = 5;
        Assert.NotEqual(expected: first, actual: holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: (3, 3L, 48L, 1L), actual: (scratch.Encodes, work.Encodes, work.EncodedBytes, work.FilteredCompares));
    }
    // THE LAW: a table holder compares its count and its elements, and a record holder compares by the section's
    // own equality, under which an immutable array equals only itself.
    [Fact]
    public void ATableAndARecordHolderFilterASameValueWriteToo() {
        var binding = new SlotBinding();
        var table = new VersionedArray<long>(binding: binding);
        var named = new VersionedRecord<Named>(binding: binding, value: new Named(Name: "a", Rows: [1, 2]));
        var scratch = new Scratch();
        var work = default(SectionDigestWork);

        table.Resize(count: 2)[1] = 7;
        var tableDigest = table.Digest<TableCodec, Scratch>(writer: scratch, work: ref work);

        table.Write[1] = 7;
        _ = table.Digest<TableCodec, Scratch>(writer: scratch, work: ref work);
        table.Write[1] = 7;
        Assert.Equal(expected: tableDigest, actual: table.Digest<TableCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: (2, 1L), actual: (scratch.Encodes, work.FilteredCompares));
        table.Resize(count: 3);
        Assert.NotEqual(expected: tableDigest, actual: table.Digest<TableCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: 3, actual: table.Read.Length);
        Assert.Equal(expected: 7L, actual: table.Read[1]);

        work = default;
        scratch.Encodes = 0;
        var namedDigest = named.Digest<NamedCodec, Scratch>(writer: scratch, work: ref work);
        var rows = named.Read.Rows;

        named.Write = new Named(Name: "a", Rows: rows);
        _ = named.Digest<NamedCodec, Scratch>(writer: scratch, work: ref work);
        named.Write = new Named(Name: "a", Rows: rows);
        Assert.Equal(expected: namedDigest, actual: named.Digest<NamedCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Equal(expected: (2, 1L), actual: (scratch.Encodes, work.FilteredCompares));
        named.Write = new Named(Name: "a", Rows: [1, 2]);
        _ = named.Digest<NamedCodec, Scratch>(writer: scratch, work: ref work);
        Assert.Equal(expected: (3, 1L), actual: (scratch.Encodes, work.FilteredCompares));
    }
    // THE LAW: under the notes' checked mode a digest read compares what it hands out with a fresh encoding, names
    // the first difference by slot, section and version, and heals. The section is written around the door here, as
    // only a test assembly may, so its version stands still over changed bytes.
    [Fact]
    public void ACheckedReadNamesAStaleDigestAndHeals() {
        var notes = new SlotNotes(count: 8, partitionSlots: 4);
        var binding = new SlotBinding();
        var holder = new Versioned<Pose>(binding: binding, value: new Pose { X = 1 });
        var scratch = new Scratch();
        var work = default(SectionDigestWork);

        binding.Bind(notes: notes, slot: 5);
        var before = holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work);

        Unsafe.AsRef(source: in holder.Read).X = 9;
        Assert.Equal(expected: before, actual: holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work));

        notes.Checked = true;
        var healed = holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work);

        Assert.NotEqual(expected: before, actual: healed);
        Assert.Contains(expectedSubstring: "slot 5 section Pose", actualString: notes.StaleDigest, comparisonType: StringComparison.Ordinal);
        notes.ClearStaleDigest();
        Assert.Equal(expected: healed, actual: holder.Digest<PoseCodec, Scratch>(writer: scratch, work: ref work));
        Assert.Null(@object: notes.StaleDigest);
    }
    // THE LAW: notes written across partitions publish every written slot once, partition by partition in ascending
    // order, however many holders of a slot were written and however often; a second publish moves nothing; a
    // reader following the versions is handed exactly the written slots. An unbound owner notes nothing.
    [Fact]
    public void NotesWrittenAcrossPartitionsPublishEverySlotOnceInPartitionOrder() {
        var notes = new SlotNotes(count: 12, partitionSlots: 4);
        var slots = new[] { 9, 2, 6, 1 };
        var bindings = new SlotBinding[slots.Length];
        var poses = new Versioned<Pose>[slots.Length];
        var tables = new VersionedArray<long>[slots.Length];
        var loose = new Versioned<Pose>(binding: new SlotBinding());
        var moved = new List<int>();
        var place = notes.Versions.Sequence;

        for (var owner = 0; (owner < slots.Length); owner++) {
            bindings[owner] = new SlotBinding();
            bindings[owner].Bind(notes: notes, slot: slots[owner]);
            poses[owner] = new Versioned<Pose>(binding: bindings[owner]);
            tables[owner] = new VersionedArray<long>(binding: bindings[owner]);
        }
        Assert.Equal(expected: 3, actual: notes.PartitionCount);

        for (var owner = 0; (owner < slots.Length); owner++) {
            poses[owner].Write.X = owner;
            poses[owner].Write.Y = owner;
            tables[owner].Resize(count: 1)[0] = owner;
        }
        loose.Write.X = 1;
        Assert.Equal(expected: 0UL, actual: notes.Versions[9]);

        Assert.Equal(expected: 4, actual: notes.Publish());
        Assert.True(condition: notes.Versions.TryReadMoves(into: moved, place: ref place));
        // Most recently moved first: partition 2 (slot 9) published last, partition 0 (slots 2 then 1, in write order) first.
        Assert.Equal(expected: [9, 6, 1, 2], actual: moved);
        foreach (var slot in slots) { Assert.Equal(expected: 1UL, actual: notes.Versions[slot]); }
        Assert.Equal(expected: 0UL, actual: notes.Versions[0]);

        Assert.Equal(expected: 0, actual: notes.Publish());
        moved.Clear();
        Assert.True(condition: notes.Versions.TryReadMoves(into: moved, place: ref place));
        Assert.Empty(collection: moved);

        poses[0].Write.X = 2;
        notes.Note(slot: 11);
        Assert.Equal(expected: 2, actual: notes.Publish());
        Assert.Equal(expected: (2UL, 1UL), actual: (notes.Versions[9], notes.Versions[11]));

        notes.Reset(count: 4);
        Assert.False(condition: notes.Versions.TryReadMoves(into: moved, place: ref place));
        Assert.Equal(expected: 1, actual: notes.PartitionCount);
    }
    // THE LAW: a derived answer is computed when a holder it reads is another holder or has moved, and is returned
    // as kept otherwise. The red leg is a view keyed by one holder's version that misses the second's.
    [Fact]
    public void ADerivedAnswerIsRecomputedExactlyWhenAHolderItReadsMoved() {
        var binding = new SlotBinding();
        var first = new Versioned<Pose>(binding: binding, value: new Pose { X = 2, Y = 3 });
        var second = new Versioned<Pose>(binding: binding, value: new Pose { X = 10 });
        var one = new Derived<long>();
        var two = new Derived<long>();

        Assert.Equal(expected: 5L, actual: one.Read<Pose, Sum>(holder: first));
        Assert.Equal(expected: 5L, actual: one.Read<Pose, Sum>(holder: first));
        Assert.Equal(expected: 1L, actual: one.Computes);
        first.Write.Y = 4;
        Assert.Equal(expected: 6L, actual: one.Read<Pose, Sum>(holder: first));
        Assert.Equal(expected: 10L, actual: one.Read<Pose, Sum>(holder: second));
        Assert.Equal(expected: 3L, actual: one.Computes);

        Assert.Equal(expected: 12L, actual: two.Read<Pose, Pose, Sum>(first: first, second: second));
        Assert.Equal(expected: 12L, actual: two.Read<Pose, Pose, Sum>(first: first, second: second));
        second.Write.X = 20;
        Assert.Equal(expected: 22L, actual: two.Read<Pose, Pose, Sum>(first: first, second: second));
        first.Write.X = 3;
        Assert.Equal(expected: 23L, actual: two.Read<Pose, Pose, Sum>(first: first, second: second));
        Assert.Equal(expected: 3L, actual: two.Computes);
    }
}
