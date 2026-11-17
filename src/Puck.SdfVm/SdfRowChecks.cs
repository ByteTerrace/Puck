using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>Opt-in independent row comparisons and the witnesses that force their guarded paths. This diagnostic
/// state belongs to presentation and survives replacement tables after device loss.</summary>
public static class SdfRowChecks {
    private static readonly WorkKindRegistry Registry = new();

    /// <summary>Gets or sets whether all tables run the independent pack, generation, seed and device comparisons.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Counts records compared to a pure packing of the producer row.</summary>
    public static WorkKind Packed { get; } = Declare(name: "pack.compared");
    /// <summary>Counts differing packed records.</summary>
    public static WorkKind PackMismatch { get; } = Declare(name: "pack.mismatched");
    /// <summary>Counts independently checked generation transitions.</summary>
    public static WorkKind Ledger { get; } = Declare(name: "ledger.compared");
    /// <summary>Counts incorrect generation transitions.</summary>
    public static WorkKind LedgerMismatch { get; } = Declare(name: "ledger.mismatched");
    /// <summary>Counts new generations checked for a pending previous-matrix seed.</summary>
    public static WorkKind Seeds { get; } = Declare(name: "seeds.compared");
    /// <summary>Counts missing seeds.</summary>
    public static WorkKind SeedMismatch { get; } = Declare(name: "seeds.mismatched");
    /// <summary>Counts device records compared against the host bytes at recording.</summary>
    public static WorkKind Device { get; } = Declare(name: "device.compared");
    /// <summary>Counts differing device records.</summary>
    public static WorkKind DeviceMismatch { get; } = Declare(name: "device.mismatched");
    /// <summary>Counts rows staged absent for refused geometry.</summary>
    public static WorkKind RefusedAbsent { get; } = Declare(name: "refused.absent");
    /// <summary>Counts rows returning live from absence with a new seed.</summary>
    public static WorkKind ReturnedLive { get; } = Declare(name: "absent.returned-live");
    /// <summary>Counts memory trims serviced by tables.</summary>
    public static WorkKind Trims { get; } = Declare(name: "trims");
    /// <summary>Counts mesh regions shrunk after relocation.</summary>
    public static WorkKind Shrinks { get; } = Declare(name: "region.shrinks");
    /// <summary>Counts whole-table cursor answers, including a new table's first answer.</summary>
    public static WorkKind TablesWhole { get; } = Declare(name: "cursor.tables.whole");
    /// <summary>Counts deliberately uncommitted table-consumer windows.</summary>
    public static WorkKind Skipped { get; } = Declare(name: "cursor.tables.skipped");
    /// <summary>Gets all comparison and forcing counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new("sdf.rows.check", Registry.Kinds);

    private static WorkKind Declare(string name) => Registry.Declare(name: $"sdf.rows.check.{name}", unit: "count", workClass: WorkClass.Pacing);
}
