namespace Puck.Hosting;

/// <summary>How often a render-graph instance refreshes: every <see cref="Divisor"/>-th presented frame, or at most
/// <see cref="Hertz"/> times a second. Exactly one of the two is positive.</summary>
/// <param name="Divisor">The instance renders at most once every this many presented frames, or 0 when
/// <see cref="Hertz"/> states the rate.</param>
/// <param name="Hertz">The most renders a second, or 0 when <see cref="Divisor"/> states the rate.</param>
public readonly record struct RenderGraphRefresh(int Divisor, int Hertz) {
    /// <summary>Gets the refresh that renders on every frame something visible reads the instance.</summary>
    public static RenderGraphRefresh EveryFrame => Every(divisor: 1);
    /// <summary>Gets whether exactly one of <see cref="Divisor"/> and <see cref="Hertz"/> is positive and the other is
    /// zero.</summary>
    public bool IsValid => (
        ((Divisor > 0) && (Hertz == 0)) ||
        ((Divisor == 0) && (Hertz > 0))
    );

    /// <summary>Creates a refresh that renders at most once every <paramref name="divisor"/> presented frames.</summary>
    /// <param name="divisor">The frame divisor, at least one.</param>
    /// <returns>The refresh.</returns>
    public static RenderGraphRefresh Every(int divisor) => new(
        Divisor: divisor,
        Hertz: 0
    );
    /// <summary>Creates a refresh that renders at most <paramref name="hertz"/> times a second.</summary>
    /// <param name="hertz">The rate, at least one.</param>
    /// <returns>The refresh.</returns>
    public static RenderGraphRefresh At(int hertz) => new(
        Divisor: 0,
        Hertz: hertz
    );
    /// <summary>Resolves the refresh to a frame divisor against the display's refresh rate. A rate becomes the smallest
    /// divisor that never renders faster than the rate asks, and a display whose rate is unknown (zero or negative)
    /// renders a rate-refreshed instance on every frame.</summary>
    /// <param name="displayHertz">The presented frames a second.</param>
    /// <returns>The divisor, at least one.</returns>
    /// <exception cref="InvalidOperationException">The refresh is not <see cref="IsValid"/>.</exception>
    public int ResolveDivisor(int displayHertz) {
        if (!IsValid) {
            throw new InvalidOperationException(message: $"Refresh divisor {Divisor} and hertz {Hertz} must name exactly one positive rate.");
        }
        if (Hertz == 0) {
            return Divisor;
        }
        if (displayHertz <= 0) {
            return 1;
        }

        return Math.Max(
            val1: 1,
            val2: ((int)((((long)displayHertz) + (Hertz - 1)) / Hertz))
        );
    }
}
/// <summary>One instance's read of another instance's output: an image the consumer shows through a footprint, or a
/// buffer it reads whenever it renders.</summary>
/// <param name="Producer">The name of the instance read.</param>
/// <param name="PreviousFrame">Whether the read takes the producer's previous completed frame instead of this frame's.
/// A read of the instance's own output is always a previous-frame read, through the planner's history resource,
/// whether or not it says so.</param>
/// <param name="Kind">What the consumer's input carries, which must be what the producer's output carries. An image
/// read demands the producer through the frame's footprints; a buffer read demands it on every frame the consumer
/// renders.</param>
public readonly record struct RenderGraphRead(string Producer, bool PreviousFrame = false, ShaderPipelineResourceKind Kind = ShaderPipelineResourceKind.Image);
/// <summary>One view rendered by a graph: the main camera, a pane, a game camera shown on a screen, or a nested world;
/// or world-scoped work, such as the SDF brick pool, whose output is a buffer the views read. Nesting is written as
/// reads: an instance that shows another's output reads it.</summary>
/// <param name="Name">The instance's unique name.</param>
/// <param name="Refresh">How often it refreshes.</param>
/// <param name="Passes">The passes one render of its graph records, the unit its cost is priced in. At least one.</param>
/// <param name="Reads">The instances whose output it may read.</param>
/// <param name="Output">What the output its consumers read carries. An instance whose output is a buffer renders at no
/// extent and costs no pass-pixels.</param>
/// <param name="ExternalPackage">The package id of the external producer that renders the instance through its own
/// submissions, or <see langword="null"/> for an instance that renders a graph. An external producer reads no other
/// instance, and hands its consumers only its latest completed output.</param>
public sealed record RenderGraphInstance(string Name, RenderGraphRefresh Refresh, int Passes, IReadOnlyList<RenderGraphRead> Reads, ShaderPipelineResourceKind Output = ShaderPipelineResourceKind.Image, string? ExternalPackage = null) {
    /// <summary>Gets how the instance renders: a graph of its own, or an external producer's submissions.</summary>
    public RenderGraphInstanceKind Kind => ((ExternalPackage is null)
        ? RenderGraphInstanceKind.Graph
        : RenderGraphInstanceKind.External);
}
/// <summary>How a render-graph instance renders.</summary>
public enum RenderGraphInstanceKind : byte {
    /// <summary>The instance renders a graph through its own node.</summary>
    Graph = 1,
    /// <summary>An external producer named by package id renders the instance through its own submissions, and hands
    /// its consumers its latest completed output.</summary>
    External = 2,
}
/// <summary>Why a set of render-graph instances was refused.</summary>
public enum RenderGraphInstanceRefusalCode : byte {
    /// <summary>An instance has no name.</summary>
    NameMissing = 1,
    /// <summary>Two instances share a name.</summary>
    NameDuplicated = 2,
    /// <summary>An instance's refresh names no rate, or both a divisor and a rate.</summary>
    RefreshInvalid = 3,
    /// <summary>An instance's pass count is less than one.</summary>
    PassesInvalid = 4,
    /// <summary>A read names no declared instance.</summary>
    ProducerUnknown = 5,
    /// <summary>An instance reads the same producer twice.</summary>
    ReadDuplicated = 6,
    /// <summary>Instances read each other within one frame, so no order renders every producer before its
    /// consumers.</summary>
    SameFrameCycle = 7,
    /// <summary>A read's kind is not what its producer's output carries: an image read of a buffer output, or a buffer
    /// read of an image output.</summary>
    KindMismatch = 8,
    /// <summary>An external producer declares a read: it renders through its own submissions, so no graph of its binds
    /// another instance's output.</summary>
    ExternalReads = 9,
    /// <summary>An instance reads an external producer's previous frame: the producer hands out only its latest
    /// completed output, which its next render overwrites.</summary>
    ExternalPreviousFrame = 10,
}
/// <summary>A refused set of render-graph instances.</summary>
/// <param name="Code">Why it was refused.</param>
/// <param name="Message">The refusal, naming what it concerns.</param>
/// <param name="Instances">The instances concerned; for <see cref="RenderGraphInstanceRefusalCode.SameFrameCycle"/>,
/// every instance in the cycle in read order; for <see cref="RenderGraphInstanceRefusalCode.KindMismatch"/> and
/// <see cref="RenderGraphInstanceRefusalCode.ExternalPreviousFrame"/>, the consumer, then the producer.</param>
public sealed record RenderGraphInstanceRefusal(RenderGraphInstanceRefusalCode Code, string Message, IReadOnlyList<string> Instances);
