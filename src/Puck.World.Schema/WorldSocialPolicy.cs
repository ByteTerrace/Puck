using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>Authored limits and learning coefficients for a directed social-memory bank. This describes memory,
/// not perception: its caller must decide which events an observer can actually witness or receive.</summary>
/// <param name="Dimensions">Independent, uniquely named impression dimensions. Values are normalized to [-1,1].</param>
/// <param name="ImpressionCapacity">Total retained (observer, subject, dimension) entries, not body capacity.</param>
/// <param name="ImpressionsPerObserver">Maximum retained entries for one observer, across subjects and dimensions.</param>
/// <param name="ReceiptCapacity">Exact duplicate-evidence ledger capacity. Unexpired receipts are never evicted.</param>
/// <param name="EvidenceAttemptsPerTick">Maximum ingestion attempts, including invalid and duplicate attempts.</param>
/// <param name="ExpiredReceiptsPerTick">Maximum expired ledger entries reclaimed when the engine clock advances.</param>
/// <param name="EvidenceLifetimeSeconds">Positive exact engine-tick admission age. Older evidence is refused.</param>
/// <param name="ReliabilityDimension">Optional [0,1] dimension used to assess a report's source, independently of affection.</param>
/// <param name="UnfamiliarReliability">Fallback source reliability in [0,1], blended with learned reliability by confidence.</param>
/// <param name="DirectWeight">Non-negative weight of a direct observation of quality one, at most 1024.</param>
/// <param name="ReportWeight">Non-negative report weight before quality and source reliability, at most 1024.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSocialPolicy(
    IReadOnlyList<WorldSocialDimension> Dimensions,
    int ImpressionCapacity = 65536,
    int ImpressionsPerObserver = 256,
    int ReceiptCapacity = 65536,
    int EvidenceAttemptsPerTick = 1024,
    int ExpiredReceiptsPerTick = 1024,
    decimal EvidenceLifetimeSeconds = 60,
    string? ReliabilityDimension = null,
    decimal UnfamiliarReliability = 0.5m,
    decimal DirectWeight = 1,
    decimal ReportWeight = 0.5m
);

/// <summary>One contextual belief's authored bounds and learning behavior. Confidence is a game heuristic, not a
/// calibrated probability. This is not a personality or mood model.</summary>
/// <param name="Name">Non-empty dimension name; contexts, attempts, and outcomes may use distinct dimensions.</param>
/// <param name="Minimum">Lower normalized bound, at least -1.</param>
/// <param name="Maximum">Upper normalized bound, at most 1, strictly greater than Minimum.</param>
/// <param name="Baseline">Value of an unknown impression, inside the bounds.</param>
/// <param name="PriorWeight">Positive inertia toward the current estimate, at most 1024.</param>
/// <param name="WeightCapacity">Maximum accumulated evidence weight, positive and at most 65536.</param>
/// <param name="LearningRate">[0,1] multiplier on value learning; zero locks the value but not evidence/confidence.</param>
/// <param name="MaximumChange">[0,2] maximum absolute value change per accepted event or direct-observation upgrade.</param>
/// <param name="ConflictThreshold">[0,2] distance from the current estimate above which fresh evidence is conflicting.</param>
/// <param name="ConflictGain">[0,1] maximum uncertainty increase, scaled by effective evidence strength.</param>
/// <param name="ConsistencyGain">[0,1] maximum uncertainty decrease for consistent, independent evidence.</param>
/// <param name="FollowUpBoost">[0,16] extra direct-evidence weight per unit uncertainty; does not boost repeated events.</param>
/// <param name="ConfidenceDecaySeconds">Exact engine-tick duration for linear evidence-weight decay; zero disables it.</param>
/// <param name="RecoverySeconds">Exact engine-tick duration for linear value recovery to baseline; zero disables it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSocialDimension(
    WorldCellName Name,
    decimal Minimum = -1,
    decimal Maximum = 1,
    decimal Baseline = 0,
    decimal PriorWeight = 4,
    decimal WeightCapacity = 64,
    decimal LearningRate = 1,
    decimal MaximumChange = 0.25m,
    decimal ConflictThreshold = 0.5m,
    decimal ConflictGain = 0.25m,
    decimal ConsistencyGain = 0.125m,
    decimal FollowUpBoost = 1,
    decimal ConfidenceDecaySeconds = 0,
    decimal RecoverySeconds = 0
);

/// <summary>Immutable, validated fixed-point social policy. Construction copies the authoring list; subsequent
/// author-side mutation cannot change a running memory bank.</summary>
public sealed class CompiledWorldSocialPolicy {
    /// <summary>Representation ceiling on retained impressions and evidence receipts, independently.</summary>
    public const int MaximumEntries = 1048576;
    /// <summary>Representation ceiling on independent named dimensions.</summary>
    public const int MaximumDimensions = 64;

    private readonly CompiledWorldSocialDimension[] m_dimensions;
    private readonly Dictionary<string, int> m_names;
    /// <summary>The canonical source policy used to validate checkpoint ownership, not a cryptographic identity.</summary>
    public string Identity { get; }
    /// <summary>Total impression capacity.</summary>
    public int ImpressionCapacity { get; }
    /// <summary>Per-observer impression capacity, counting each dimension separately.</summary>
    public int ImpressionsPerObserver { get; }
    /// <summary>Exact evidence receipt capacity.</summary>
    public int ReceiptCapacity { get; }
    /// <summary>Ingestion attempt ceiling per clock boundary.</summary>
    public int EvidenceAttemptsPerTick { get; }
    /// <summary>Expired receipt reclamation ceiling per clock boundary.</summary>
    public int ExpiredReceiptsPerTick { get; }
    /// <summary>Maximum original event age in engine ticks, inclusive.</summary>
    public ulong EvidenceLifetimeTicks { get; }
    /// <summary>Source-reliability dimension ordinal, or -1 when no learned reliability is used.</summary>
    public int ReliabilityDimension { get; }
    /// <summary>Raw Q48.16 unfamiliar source reliability.</summary>
    public long UnfamiliarReliability { get; }
    /// <summary>Raw Q48.16 direct-observation weight.</summary>
    public long DirectWeight { get; }
    /// <summary>Raw Q48.16 report weight before source reliability.</summary>
    public long ReportWeight { get; }
    /// <summary>The copied, immutable compiled dimensions in declaration order.</summary>
    public ReadOnlySpan<CompiledWorldSocialDimension> Dimensions => m_dimensions;

    /// <summary>Validates and compiles a policy. Decimal values use the ordinary Q48.16 literal conversion;
    /// positive weights must remain positive after conversion.</summary>
    /// <param name="policy">The authored policy.</param>
    /// <returns>A detached compiled policy.</returns>
    /// <exception cref="ArgumentNullException">The policy is null.</exception>
    /// <exception cref="ArgumentException">A name, bound, capacity, weight, or duration is invalid.</exception>
    public static CompiledWorldSocialPolicy Compile(WorldSocialPolicy policy) {
        ArgumentNullException.ThrowIfNull(policy);
        return new(policy);
    }

    /// <summary>Resolves a dimension without allocation. An unknown name returns -1.</summary>
    /// <param name="name">The exact ordinal-case dimension name.</param>
    /// <returns>The dimension ordinal or -1.</returns>
    public int FindDimension(string name) => m_names.TryGetValue(name, out var ordinal) ? ordinal : -1;

    private CompiledWorldSocialPolicy(WorldSocialPolicy source) {
        static void Require(bool condition, string field) {
            if (!condition) { throw new ArgumentException($"social policy {field} is invalid", nameof(source)); }
        }
        static long Raw(decimal value) => WorldStateNumericLiteral.ToFixed(value).Value;
        Require(source.Dimensions is { Count: > 0 and <= MaximumDimensions }, "dimensions");
        Require(source.ImpressionCapacity is > 0 and <= MaximumEntries, "impressionCapacity");
        Require(source.ImpressionsPerObserver > 0 && source.ImpressionsPerObserver <= source.ImpressionCapacity, "impressionsPerObserver");
        Require(source.ReceiptCapacity is > 0 and <= MaximumEntries, "receiptCapacity");
        Require(source.EvidenceAttemptsPerTick is > 0 and <= MaximumEntries, "evidenceAttemptsPerTick");
        Require(source.ExpiredReceiptsPerTick is > 0 and <= MaximumEntries, "expiredReceiptsPerTick");
        Require(FixedTickConversion.TryDurationEngineTicksExact(source.EvidenceLifetimeSeconds, out var lifetime) && lifetime > 0, "evidenceLifetimeSeconds");
        Require(source.UnfamiliarReliability is >= 0 and <= 1, "unfamiliarReliability");
        Require(source.DirectWeight is >= 0 and <= 1024 && source.ReportWeight is >= 0 and <= 1024, "evidence weights");
        Require((source.DirectWeight == 0 || Raw(source.DirectWeight) > 0) && (source.ReportWeight == 0 || Raw(source.ReportWeight) > 0), "quantized evidence weights");
        m_dimensions = new CompiledWorldSocialDimension[source.Dimensions!.Count];
        m_names = new(StringComparer.Ordinal);
        for (var index = 0; index < m_dimensions.Length; index++) {
            var d = source.Dimensions[index];
            Require(d is not null && !string.IsNullOrWhiteSpace(d.Name.Value) && m_names.TryAdd(d.Name.Value, index), "dimension names");
            Require(d!.Minimum >= -1 && d.Maximum <= 1 && d.Minimum < d.Maximum && d.Baseline >= d.Minimum && d.Baseline <= d.Maximum, $"{d.Name} bounds");
            Require(d.PriorWeight is > 0 and <= 1024 && d.WeightCapacity is > 0 and <= 65536, $"{d.Name} weights");
            Require(d.LearningRate is >= 0 and <= 1 && d.MaximumChange is >= 0 and <= 2, $"{d.Name} learning");
            Require(d.ConflictThreshold is >= 0 and <= 2 && d.ConflictGain is >= 0 and <= 1 && d.ConsistencyGain is >= 0 and <= 1 && d.FollowUpBoost is >= 0 and <= 16, $"{d.Name} uncertainty");
            Require(FixedTickConversion.TryDurationEngineTicksExact(d.ConfidenceDecaySeconds, out var decay), $"{d.Name} confidenceDecaySeconds");
            Require(FixedTickConversion.TryDurationEngineTicksExact(d.RecoverySeconds, out var recovery), $"{d.Name} recoverySeconds");
            Require(Raw(d.Minimum) < Raw(d.Maximum) && Raw(d.PriorWeight) > 0 && Raw(d.WeightCapacity) > 0, $"{d.Name} quantized bounds/weights");
            m_dimensions[index] = new(d.Name.Value, Raw(d.Minimum), Raw(d.Maximum), Raw(d.Baseline),
                Raw(d.PriorWeight), Raw(d.WeightCapacity), Raw(d.LearningRate), Raw(d.MaximumChange),
                Raw(d.ConflictThreshold), Raw(d.ConflictGain), Raw(d.ConsistencyGain), Raw(d.FollowUpBoost), decay, recovery);
        }
        ReliabilityDimension = source.ReliabilityDimension is null ? -1 : FindDimension(source.ReliabilityDimension);
        Require(source.ReliabilityDimension is null || (ReliabilityDimension >= 0 && m_dimensions[ReliabilityDimension].Minimum >= 0), "reliabilityDimension must name a [0,1] dimension");
        ImpressionCapacity = source.ImpressionCapacity; ImpressionsPerObserver = source.ImpressionsPerObserver;
        ReceiptCapacity = source.ReceiptCapacity; EvidenceAttemptsPerTick = source.EvidenceAttemptsPerTick;
        ExpiredReceiptsPerTick = source.ExpiredReceiptsPerTick; EvidenceLifetimeTicks = lifetime;
        UnfamiliarReliability = Raw(source.UnfamiliarReliability); DirectWeight = Raw(source.DirectWeight); ReportWeight = Raw(source.ReportWeight);
        Identity = JsonSerializer.Serialize(source, WorldJsonContext.Default.WorldSocialPolicy);
    }
}

/// <summary>One immutable compiled dimension; all signed numeric coefficients are raw Q48.16, and durations are engine ticks.</summary>
public readonly record struct CompiledWorldSocialDimension(
    string Name, long Minimum, long Maximum, long Baseline, long PriorWeight, long WeightCapacity,
    long LearningRate, long MaximumChange, long ConflictThreshold, long ConflictGain, long ConsistencyGain,
    long FollowUpBoost, ulong ConfidenceDecayTicks, ulong RecoveryTicks
);
