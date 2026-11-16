namespace Puck.World.Server;

/// <summary>What one continuation fold encoded, accumulated while it runs and published to
/// <see cref="WorldTickWork"/> once when it ends, so a fold counts per section without a counter write per section per
/// slot. A population owns one and reuses it from fold to fold.</summary>
internal sealed class WorldContinuationFoldWork {
    private static readonly int SectionCount = Enum.GetValues<WorldContinuationSectionKind>().Length;

    /// <summary>The bytes each section encoded to, by <see cref="WorldContinuationSectionKind"/>.</summary>
    internal readonly long[] SectionBytes = new long[SectionCount];
    /// <summary>The times each section was encoded, by <see cref="WorldContinuationSectionKind"/>.</summary>
    internal readonly long[] SectionEncodes = new long[SectionCount];
    /// <summary>What the digest reads of the sections kept behind holders did, by <see cref="WorldContinuationSectionKind"/>.</summary>
    internal readonly SectionDigestWork[] Held = new SectionDigestWork[SectionCount];
    /// <summary>The scratch one slot's held digests are read into, by <see cref="WorldContinuationSectionKind"/>.</summary>
    internal readonly ulong[] HeldDigests = new ulong[SectionCount];

    /// <summary>The slots whose sections were encoded.</summary>
    internal long Slots;
    /// <summary>The profile projections encoded beside the slots.</summary>
    internal long Profiles;
    /// <summary>The bytes those projections encoded to.</summary>
    internal long ProfileBytes;
    /// <summary>The bytes a replay scope's tails encoded to.</summary>
    internal long TailBytes;

    /// <summary>Adds what the fold encoded to the tick work ledger and clears this for the next fold.</summary>
    internal void Publish() {
        var bytes = (ProfileBytes + TailBytes);

        for (var section = 0; (section < SectionCount); section++) {
            ref var held = ref Held[section];
            var encodes = (SectionEncodes[section] + held.Encodes);
            var encoded = (SectionBytes[section] + held.EncodedBytes);

            if (encodes != 0L) {
                bytes += encoded;
                WorldTickWork.Process.Add(amount: encodes, kind: WorldTickWork.SectionEncodes[section]);
                WorldTickWork.Process.Add(amount: encoded, kind: WorldTickWork.SectionBytes[section]);
            }
            if ((held.ComparedBytes | held.FilteredCompares | held.UnchangedEncodes) != 0L) {
                WorldTickWork.Process.Add(amount: held.ComparedBytes, kind: WorldTickWork.SectionComparedBytes[section]);
                WorldTickWork.Process.Add(amount: held.FilteredCompares, kind: WorldTickWork.SectionFilteredCompares[section]);
                WorldTickWork.Process.Add(amount: held.UnchangedEncodes, kind: WorldTickWork.SectionUnchangedEncodes[section]);
            }
            held = default;
            SectionBytes[section] = 0L;
            SectionEncodes[section] = 0L;
        }
        WorldTickWork.Process.Add(amount: Slots, kind: WorldTickWork.ContinuationEncodes);
        WorldTickWork.Process.Add(amount: bytes, kind: WorldTickWork.ContinuationBytes);
        WorldTickWork.Process.Add(amount: Profiles, kind: WorldTickWork.ContinuationProfiles);
        WorldTickWork.Process.Add(amount: ProfileBytes, kind: WorldTickWork.ContinuationProfileBytes);
        WorldTickWork.Process.Add(amount: TailBytes, kind: WorldTickWork.ContinuationTailBytes);
        Slots = 0L;
        Profiles = 0L;
        ProfileBytes = 0L;
        TailBytes = 0L;
    }
}
