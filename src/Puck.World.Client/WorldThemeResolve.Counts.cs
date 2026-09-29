using Puck.Abstractions.Counting;

namespace Puck.World.Client;

public sealed partial class WorldThemeResolve : IWorkCounterSource {
    /// <summary>The theme's presentation work source.</summary>
    public const string SourceName = "presentation.theme";
    /// <summary>Counts changed-input numeric domain checks.</summary>
    public static readonly WorkKind DomainChecks = new("presentation.theme.domain-checks", "checks", WorkClass.Pacing);
    /// <summary>Counts changed inputs clamped to a closed bound.</summary>
    public static readonly WorkKind DomainClamps = new("presentation.theme.domain-clamps", "clamps", WorkClass.Pacing);
    /// <summary>Counts changed non-finite inputs retaining their last valid value.</summary>
    public static readonly WorkKind DomainHolds = new("presentation.theme.domain-holds", "holds", WorkClass.Pacing);
    private static readonly WorkKind[] Kinds = [DomainChecks, DomainClamps, DomainHolds];
    /// <inheritdoc/>
    public string Name => SourceName;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        if (kind == DomainChecks) { value = Domains.Checks; }
        else if (kind == DomainClamps) { value = Domains.Clamps; }
        else if (kind == DomainHolds) { value = Domains.Holds; }
        else { value = 0L; return false; }
        return true;
    }
}
