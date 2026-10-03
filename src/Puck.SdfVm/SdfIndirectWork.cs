using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>The residency cache's deterministic host schedule counts.</summary>
public static class SdfIndirectWork {
    /// <summary>The host counter source.</summary>
    public const string SourceName = "sdf.indirect";

    /// <summary>The scheduled ray total.</summary>
    public static WorkKind Rays { get; } = Kind("indirect.rays.scheduled", "rays");
    /// <summary>The scheduled probe stratum total.</summary>
    public static WorkKind Probes { get; } = Kind("indirect.probes.scheduled", "probes");
    /// <summary>The complete vocabulary, including reserved zero source and receiver rows.</summary>
    public static WorkKind[] Kinds { get; } = [Rays, Probes,
        .. new[] { "demand", "geometry", "light", "shadow", "screen", "converge" }.Select(reason => Kind($"indirect.probes.scheduled.{reason}", "probes")),
        .. new[] { "near", "room", "world" }.SelectMany(level => new[] { "allocated", "evicted", "refused" }.Select(action => Kind($"indirect.bricks.{action}.{level}", "bricks"))),
        Kind("indirect.sweeps.completed", "sweeps"), Kind("indirect.sweeps.restarted", "sweeps"),
        Kind("indirect.proofs.issued", "proofs"), Kind("indirect.proofs.reused", "proofs"), Kind("indirect.proofs.deferred", "proofs")];

    private static WorkKind Kind(string name, string unit) => new(name: name, unit: unit, workClass: WorkClass.Deterministic);
}
