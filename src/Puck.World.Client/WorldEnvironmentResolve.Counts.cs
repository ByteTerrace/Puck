using Puck.Abstractions.Counting;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve : IWorkCounterSource {
    /// <summary>The stable timeline work source shared by primary, routed and session presentations.</summary>
    public const string SourceName = "world.timeline";
    /// <summary>Counts actual document-to-environment resolves.</summary>
    public static readonly WorkKind EnvironmentResolves = new("world.timeline.environment-resolves", "resolves", WorkClass.Pacing);
    /// <summary>Counts the active rate integrals evaluated by presented frames.</summary>
    public static readonly WorkKind EvaluatedRates = new("world.timeline.evaluated-rates", "rates", WorkClass.Pacing);
    /// <summary>Counts comparisons selecting an integral's active piece.</summary>
    public static readonly WorkKind SearchedPieces = new("world.timeline.piece-searches", "comparisons", WorkClass.Pacing);
    /// <summary>Counts coefficient blends evaluating the active antiderivative.</summary>
    public static readonly WorkKind BlendedCoefficients = new("world.timeline.coefficient-blends", "blends", WorkClass.Pacing);
    /// <summary>Counts modular doublings including complete periods without multiplying large elapsed times.</summary>
    public static readonly WorkKind DoubledPeriods = new("world.timeline.period-doublings", "doublings", WorkClass.Pacing);
    /// <summary>Counts numeric domain checks whose resolved input changed.</summary>
    public static readonly WorkKind DomainChecks = new("world.timeline.domain-checks", "checks", WorkClass.Pacing);
    /// <summary>Counts changed closed-bound inputs clamped by the shared domain guard.</summary>
    public static readonly WorkKind DomainClamps = new("world.timeline.domain-clamps", "clamps", WorkClass.Pacing);
    /// <summary>Counts changed inputs retaining their last valid field or coupled pair.</summary>
    public static readonly WorkKind DomainHolds = new("world.timeline.domain-holds", "holds", WorkClass.Pacing);
    private static readonly WorkKind[] DeclaredKinds = [EnvironmentResolves, EvaluatedRates, SearchedPieces, BlendedCoefficients, DoubledPeriods, DomainChecks, DomainClamps, DomainHolds];
    /// <summary>The declared kinds in report order. Totals depend on presented frames; individual evaluations have
    /// deterministic counts for a supplied tick.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;
    /// <inheritdoc/>
    public string Name => SourceName;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => DeclaredKinds;
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) {
        if (kind == EnvironmentResolves) { value = Resolutions; }
        else if (kind == EvaluatedRates) { value = RateEvaluations; }
        else if (kind == SearchedPieces) { value = PieceSearches; }
        else if (kind == BlendedCoefficients) { value = CoefficientBlends; }
        else if (kind == DoubledPeriods) { value = PeriodDoublings; }
        else if (kind == DomainChecks) { value = Domains.Checks; }
        else if (kind == DomainClamps) { value = Domains.Clamps; }
        else if (kind == DomainHolds) { value = Domains.Holds; }
        else { value = 0L; return false; }
        return true;
    }
}
