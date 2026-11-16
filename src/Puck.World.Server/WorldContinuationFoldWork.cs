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
            if (SectionEncodes[section] == 0L) { continue; }
            bytes += SectionBytes[section];
            WorldTickWork.Process.Add(amount: SectionEncodes[section], kind: WorldTickWork.SectionEncodes[section]);
            WorldTickWork.Process.Add(amount: SectionBytes[section], kind: WorldTickWork.SectionBytes[section]);
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
