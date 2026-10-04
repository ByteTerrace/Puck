using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>The residency cache's deterministic host schedule counts.</summary>
public static class SdfIndirectWork {
    /// <summary>The host counter source.</summary>
    public const string SourceName = "sdf.indirect";

    /// <summary>The scheduled ray total.</summary>
    public static WorkKind Rays { get; } = Kind(name: "indirect.rays.scheduled", unit: "rays");
    /// <summary>The scheduled probe stratum total.</summary>
    public static WorkKind Probes { get; } = Kind(name: "indirect.probes.scheduled", unit: "probes");
    /// <summary>The complete vocabulary, including reserved zero source and receiver rows.</summary>
    public static WorkKind[] Kinds { get; } = [Rays, Probes,
        .. new[] { "demand", "geometry", "light", "shadow", "screen", "converge" }.Select(selector: reason => Kind(name: $"indirect.probes.scheduled.{reason}", unit: "probes")),
        .. new[] { "near", "room", "world" }.SelectMany(selector: level => new[] { "allocated", "evicted", "refused" }.Select(selector: action => Kind(name: $"indirect.bricks.{action}.{level}", unit: "bricks"))),
        Kind(name: "indirect.sweeps.completed", unit: "sweeps"), Kind(name: "indirect.sweeps.restarted", unit: "sweeps"),
        Kind(name: "indirect.proofs.issued", unit: "proofs"), Kind(name: "indirect.proofs.reused", unit: "proofs"), Kind(name: "indirect.proofs.deferred", unit: "proofs")];

    private static WorkKind Kind(string name, string unit) => new(name: name, unit: unit, workClass: WorkClass.Deterministic);
}
