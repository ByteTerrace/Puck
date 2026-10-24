using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>A check-specific uncertainty budget, in metres. Snap reach is deliberately absent.</summary>
/// <param name="CollisionMargin">Authored collision expansion.</param>
/// <param name="ContactSkin">Solver clearance.</param>
/// <param name="Numerical">Fixed-point and transform arithmetic uncertainty.</param>
/// <param name="BakeHalfDiagonal">Half the rendered bake cell diagonal, when a baked mesh is compared.</param>
public sealed record WorldDiagnosticBudget(double CollisionMargin, double ContactSkin, double Numerical, double BakeHalfDiagonal = 0) {
    /// <summary>Total explained uncertainty.</summary>
    public double Total => Math.Abs(CollisionMargin) + Math.Abs(ContactSkin) + Math.Abs(Numerical) + Math.Abs(BakeHalfDiagonal);
}

/// <summary>The source and matching-state identity of one diagnostic observation.</summary>
/// <param name="Snapshot">Unique report snapshot id.</param>
/// <param name="Tick">Completed simulation tick.</param>
/// <param name="Frame">Rendered frame, absent for an authoritative headless audit.</param>
/// <param name="Instance">Explicit view instance, or primary.</param>
/// <param name="Subject">Placement or body identity.</param>
/// <param name="Revision">Collision/document revision actually examined.</param>
/// <param name="Stamp">Authored stamp id.</param>
/// <param name="Creation">Authored creation id.</param>
/// <param name="Node">Authored shape name or id.</param>
/// <param name="Operation">Authored operation.</param>
public sealed record WorldDiagnosticIdentity(long Snapshot, ulong Tick, long? Frame, string Instance, string Subject,
    long Revision, string Stamp, string Creation, string Node, string Operation) {
    /// <summary>The authored world entry point; stamp, creation and node identify the source within its composition.</summary>
    public string? SourceFile { get; init; }
}

/// <summary>One causal cluster. This record is shared by text, JSON, log and capture consumers.</summary>
/// <param name="Id">Stable finding id.</param>
/// <param name="View">Diagnostic category.</param>
/// <param name="Severity">red, amber or unknown.</param>
/// <param name="Source">The effective provider and source.</param>
/// <param name="Check">Named contract.</param>
/// <param name="Expected">Expected relation.</param>
/// <param name="Observed">Measured separation in metres; signed for field/support checks, unsigned for nearest mesh distance.</param>
/// <param name="Budget">Explained error components.</param>
/// <param name="Witness">Worst witness in world space.</param>
/// <param name="Normal">Witness normal.</param>
/// <param name="Identity">Snapshot and authored identity.</param>
/// <param name="FirstSeen">First observed tick.</param>
/// <param name="LastSeen">Most recent observed tick.</param>
/// <param name="ActiveTicks">Elapsed active ticks, never a count of report requests.</param>
/// <param name="Resolved">Whether the check was evaluated and no longer found this cause.</param>
/// <param name="Visibility">in-view, occluded, off-screen or unknown.</param>
/// <param name="Details">Causal measurements and skipped prerequisites.</param>
public sealed record WorldDiagnosticFinding(string Id, string View, string Severity, string Source, string Check,
    string Expected, double? Observed, WorldDiagnosticBudget Budget, Vector3 Witness, Vector3 Normal,
    WorldDiagnosticIdentity Identity, ulong FirstSeen = 0, ulong LastSeen = 0, ulong ActiveTicks = 0,
    bool Resolved = false, string Visibility = "unknown", IReadOnlyDictionary<string, string>? Details = null) {
    /// <summary>The units of the separation and budget.</summary>
    public string Units => "m";
    /// <summary>The stable presentation anchor; measurements always retain their own current witness.</summary>
    public Vector3? PinAnchor { get; init; }
    /// <summary>The exact rendered mesh revision, absent when only the authored representation was checked.</summary>
    public long? MeshRevision { get; init; }
    /// <summary>The other end of a measured support gap, when present.</summary>
    public Vector3? SurfaceWitness { get; init; }
    /// <summary>The coverage of the check snapshot, retained when this finding is picked alone.</summary>
    public WorldDiagnosticCoverage? Coverage { get; init; }
    /// <summary>Serializes a picked finding with the same schema as its report.</summary>
    /// <returns>The structured causal record.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, WorldDiagnosticReport.JsonOptions);
    /// <summary>The causal grouping key; screen position never changes identity.</summary>
    [JsonIgnore]
    public string Cause => $"{View}|{Identity.Instance}|{Identity.Subject}|{Source}|{Check}";
}

/// <summary>Coverage explicitly distinguishes a sampled audit from a complete surface proof.</summary>
/// <param name="Tested">Number of evaluated witnesses.</param>
/// <param name="Eligible">Number of eligible witnesses in this audit.</param>
/// <param name="Skipped">Reasons and counts of unevaluated inputs.</param>
/// <param name="Complete">Whether all required representations and witnesses were evaluated.</param>
public sealed record WorldDiagnosticCoverage(int Tested, int Eligible, IReadOnlyDictionary<string, int> Skipped, bool Complete) {
    /// <summary>Per-comparison witness counts, distinct from clustered finding counts. Unknown samples are never clean.</summary>
    public IReadOnlyDictionary<string, WorldDiagnosticSampleCounts> Comparisons { get; init; } = new Dictionary<string, WorldDiagnosticSampleCounts>();
}

/// <summary>Mutually exclusive outcomes of sampled witnesses for one comparison.</summary>
/// <param name="Red">Certified contract violations.</param>
/// <param name="Clean">Evaluated witnesses within their own precision budgets.</param>
/// <param name="Unknown">Witnesses without certified correspondence or required state.</param>
public sealed record WorldDiagnosticSampleCounts(int Red, int Clean, int Unknown);

/// <summary>An immutable diagnostic snapshot, including incomplete coverage.</summary>
/// <param name="Snapshot">Snapshot identity.</param>
/// <param name="View">Requested view.</param>
/// <param name="Tick">Completed tick.</param>
/// <param name="Findings">Active and retained resolved findings.</param>
/// <param name="Coverage">The tested population and missing prerequisites.</param>
public sealed record WorldDiagnosticReport(long Snapshot, string View, ulong Tick,
    IReadOnlyList<WorldDiagnosticFinding> Findings, WorldDiagnosticCoverage Coverage) {
    internal static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, WriteIndented = true,
    };
    /// <summary>Serializes the same record used by every diagnostic consumer.</summary>
    /// <returns>The JSON report.</returns>
    public string ToJson() => JsonSerializer.Serialize(value: this, options: JsonOptions);
    /// <summary>A concise severity and coverage summary.</summary>
    /// <returns>The console/banner summary.</returns>
    public string Summary() => $"{View}: red={Findings.Count(f => !f.Resolved && f.Severity == "red")} amber={Findings.Count(f => !f.Resolved && f.Severity == "amber")} unknown={Findings.Count(f => !f.Resolved && f.Severity == "unknown")} tested={Coverage.Tested}/{Coverage.Eligible} coverage={(Coverage.Complete ? "complete" : "incomplete")}";
}

/// <summary>Stable cause clustering and witness hysteresis, independent of rendering and analysis scheduling.</summary>
public sealed class WorldDiagnosticTracker {
    private readonly Dictionary<string, WorldDiagnosticFinding> m_causes = new(StringComparer.Ordinal);
    private long m_nextId;

    /// <summary>Publishes evaluated causes, retaining transient resolved evidence. Incomplete checks never resolve an old cause.</summary>
    /// <param name="view">The category evaluated.</param>
    /// <param name="tick">Completed tick.</param>
    /// <param name="observations">Raw witnesses, possibly many per cause.</param>
    /// <param name="complete">Whether missing causes can be marked resolved.</param>
    /// <param name="evaluated">Individually evaluated causes that may resolve even when another provider is incomplete.</param>
    /// <returns>One finding per cause.</returns>
    public IReadOnlyList<WorldDiagnosticFinding> Update(string view, ulong tick,
        IEnumerable<WorldDiagnosticFinding> observations, bool complete, Func<WorldDiagnosticFinding, bool>? evaluated = null) {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations.OrderByDescending(f => Math.Abs(f.Observed ?? 0))) {
            if (!active.Add(observation.Cause)) {
                continue;
            }
            var next = observation;
            if (m_causes.TryGetValue(observation.Cause, out var previous)) {
                // A new anchor must improve by at least the check's own precision, or ten percent of the excess.
                var oldExcess = Math.Max(0, Math.Abs(previous.Observed ?? 0) - previous.Budget.Total);
                var newExcess = Math.Max(0, Math.Abs(next.Observed ?? 0) - next.Budget.Total);
                if (!previous.Resolved && previous.Identity.Revision == next.Identity.Revision &&
                    newExcess <= oldExcess + Math.Max(next.Budget.Numerical, oldExcess * 0.1)) {
                    next = next with { PinAnchor = (previous.PinAnchor ?? previous.Witness) +
                        (next.Identity.Subject.StartsWith("body:", StringComparison.Ordinal) ? next.Witness - previous.Witness : Vector3.Zero) };
                }
                next = next with { Id = previous.Id, FirstSeen = previous.Resolved ? tick : previous.FirstSeen,
                    LastSeen = tick, ActiveTicks = previous.Resolved ? 1 : tick - Math.Min(tick, previous.FirstSeen) + 1 };
            } else {
                next = next with { Id = (++m_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FirstSeen = tick, LastSeen = tick, ActiveTicks = 1 };
            }
            m_causes[observation.Cause] = next;
        }
        foreach (var (key, value) in m_causes.ToArray()) {
                if (value.View == view && !active.Contains(key)) {
                    if (complete || evaluated?.Invoke(value) == true || value.Check == "not-evaluated") {
                        m_causes[key] = value with { Resolved = true };
                    } else if (!value.Resolved) {
                        // Keep the last measured snapshot, but never present an unevaluated current state as red or clean.
                        m_causes[key] = value with { Severity = "unknown" };
                    }
                }
        }
        return m_causes.Values.Where(f => f.View == view).OrderBy(f => f.Resolved)
            .ThenBy(f => f.Severity == "red" ? 0 : f.Severity == "amber" ? 1 : 2)
            .ThenByDescending(f => Math.Max(0, Math.Abs(f.Observed ?? 0) - f.Budget.Total))
            .ThenBy(f => f.Id, StringComparer.Ordinal).ToArray();
    }
}
