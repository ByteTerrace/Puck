using Puck.Abstractions.Counting;

namespace Puck.World.Client;

public sealed partial class WorldThemeResolve : IWorkCounterSource {
    /// <summary>The theme's presentation work source.</summary>
    public const string SourceName = "presentation.theme";

    /// <summary>Counts changed-input numeric domain checks.</summary>
    public static readonly WorkKind DomainChecks = new(name: "presentation.theme.domain-checks", unit: "checks", workClass: WorkClass.Pacing);
    /// <summary>Counts changed inputs clamped to a closed bound.</summary>
    public static readonly WorkKind DomainClamps = new(name: "presentation.theme.domain-clamps", unit: "clamps", workClass: WorkClass.Pacing);
    /// <summary>Counts changed non-finite inputs retaining their last valid value.</summary>
    public static readonly WorkKind DomainHolds = new(name: "presentation.theme.domain-holds", unit: "holds", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] Kinds = [DomainChecks, DomainClamps, DomainHolds];

    /// <inheritdoc/>
    public string Name => SourceName;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;

    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        if (kind == DomainChecks) { value = Domains.Checks; } else if (kind == DomainClamps) { value = Domains.Clamps; } else if (kind == DomainHolds) { value = Domains.Holds; } else { value = 0L; return false; }
        return true;
    }
}
