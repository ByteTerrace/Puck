using System.Text.Json;
using Puck.Commands;

namespace Puck.Hosting;

/// <summary>How often a render-graph instance refreshes: every <see cref="Divisor"/>-th presented frame, at most
/// <see cref="Hertz"/> times a second (a cap counted in presented frames), or at least <see cref="Floor"/> times a second
/// (a floor held by presentation time, <see cref="PresentationRate.Holds"/>). Exactly one of the three is positive.</summary>
/// <param name="Divisor">The instance renders at most once every this many presented frames, or 0 when another member
/// states the rate.</param>
/// <param name="Hertz">The most renders a second, or 0 when another member states the rate.</param>
/// <param name="Floor">The fewest renders a second, or 0 when another member states the rate.</param>
public readonly record struct RenderGraphRefresh(int Divisor, int Hertz, int Floor = 0) {
    /// <summary>Gets the refresh that renders on every frame something visible reads the instance.</summary>
    public static RenderGraphRefresh EveryFrame => Every(divisor: 1);
    /// <summary>Gets whether exactly one of <see cref="Divisor"/>, <see cref="Hertz"/> and <see cref="Floor"/> is positive
    /// and the others are zero.</summary>
    public bool IsValid => (
        ((Divisor > 0) && (Hertz == 0) && (Floor == 0)) ||
        ((Divisor == 0) && (Hertz > 0) && (Floor == 0)) ||
        ((Divisor == 0) && (Hertz == 0) && (Floor > 0))
    );

    /// <summary>Creates a refresh that renders at most once every <paramref name="divisor"/> presented frames.</summary>
    /// <param name="divisor">The frame divisor, at least one.</param>
    /// <returns>The refresh.</returns>
    public static RenderGraphRefresh Every(int divisor) => new(
        Divisor: divisor,
        Hertz: 0
    );
    /// <summary>Creates a refresh that renders at most <paramref name="hertz"/> times a second.</summary>
    /// <param name="hertz">The cap, at least one.</param>
    /// <returns>The refresh.</returns>
    public static RenderGraphRefresh AtMost(int hertz) => new(
        Divisor: 0,
        Hertz: hertz
    );
    /// <summary>Creates a refresh that renders at least <paramref name="hertz"/> times a second of presentation time and
    /// holds its latest render between.</summary>
    /// <param name="hertz">The floor, at least one.</param>
    /// <returns>The refresh.</returns>
    public static RenderGraphRefresh AtLeast(int hertz) => new(
        Divisor: 0,
        Floor: hertz,
        Hertz: 0
    );
    /// <summary>Resolves a divisor or a cap to a frame divisor against the display's refresh rate
    /// (<see cref="PresentationRate.FramesAtMost"/>); a floor, which presentation time paces rather than frames, resolves
    /// to one.</summary>
    /// <param name="displayHertz">The presented frames a second, or zero or less when unknown.</param>
    /// <returns>The divisor, at least one.</returns>
    /// <exception cref="InvalidOperationException">The refresh is not <see cref="IsValid"/>.</exception>
    public int ResolveDivisor(int displayHertz) {
        if (!IsValid) {
            throw new InvalidOperationException(message: $"Refresh divisor {Divisor}, hertz {Hertz} and floor {Floor} must name exactly one positive rate.");
        }
        if (Divisor > 0) {
            return Divisor;
        }

        return ((Hertz > 0)
            ? new PresentationRate(DisplayHertz: Math.Max(val1: 0, val2: displayHertz), GridHertz: 0d).FramesAtMost(hertz: Hertz)
            : 1);
    }
    /// <summary>Returns whether the refresh holds an instance's latest render through this frame: a divisor or a cap while
    /// fewer presented frames than its divisor have passed since that render, a floor while
    /// <see cref="PresentationRate.Holds"/> does. An instance that has never rendered is never held.</summary>
    /// <param name="rate">The frame's presentation rate.</param>
    /// <param name="frames">The presented frames since the instance last rendered, or a negative number when it never
    /// has.</param>
    /// <param name="elapsedSeconds">The presentation time since the instance last rendered.</param>
    /// <param name="frameSeconds">The latest frame's span.</param>
    /// <returns><see langword="true"/> when the refresh holds the instance this frame.</returns>
    /// <exception cref="InvalidOperationException">The refresh is not <see cref="IsValid"/>.</exception>
    public bool Holds(PresentationRate rate, long frames, double elapsedSeconds, double frameSeconds) {
        var divisor = ResolveDivisor(displayHertz: rate.DisplayHertz);

        if (frames < 0) {
            return false;
        }

        return ((Floor > 0)
            ? rate.Holds(elapsedSeconds: elapsedSeconds, frameSeconds: frameSeconds, hertz: Floor)
            : (frames < divisor));
    }
}
/// <summary>One instance's read of another instance's output: an image the consumer shows through a footprint, or a
/// buffer it reads whenever it renders.</summary>
/// <param name="Producer">The name of the instance read.</param>
/// <param name="PreviousFrame">Whether the read takes the producer's previous completed frame instead of this frame's,
/// so it orders nothing. A read of the instance's own output is always a previous-frame read, whether or not it says so:
/// a graph instance's through the planner's history resource, and an external producer's through the latest output it
/// completed before it renders. A previous-frame read of another external producer binds that producer's latest
/// completed output when the reader renders.</param>
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
/// submissions, or <see langword="null"/> for an instance that renders a graph. An external producer reads only images,
/// each instance's latest completed output handed to it as it produces, and hands its consumers its latest completed
/// output. A package spelled <c>source.&lt;producer id&gt;</c>
/// (<see cref="SourcePackage"/>) makes the instance an image source (<see cref="IsSource"/>).</param>
/// <param name="Settings">A source instance's settings object, which its producer opens the image with, or
/// <see langword="null"/> for its producer's defaults. Only a source carries settings.</param>
public sealed record RenderGraphInstance(string Name, RenderGraphRefresh Refresh, int Passes, IReadOnlyList<RenderGraphRead> Reads, ShaderPipelineResourceKind Output = ShaderPipelineResourceKind.Image, string? ExternalPackage = null, IReadOnlyDictionary<string, JsonElement>? Settings = null) {
    /// <summary>The prefix of every source instance's package id: <c>source.</c> then the producer's id.</summary>
    public const string SourcePackagePrefix = "source.";

    /// <summary>An authored pixel extent, independent of reader footprints and display quantization. Null uses the
    /// scheduler's ordinary footprint sizing. Sources keep their producer-negotiated extent instead.</summary>
    public RenderGraphPixelExtent? OutputExtent { get; init; }
    /// <summary>Gets the instance's identity as a displayed source: a producer handle for a source instance, whose hit
    /// ends at its pixels, and an instance handle for any other, whose hit continues into its camera. Either names the
    /// instance.</summary>
    public SourceHandle Handle => (IsSource
        ? SourceHandle.Producer(name: Name)
        : SourceHandle.Instance(name: Name)
    );
    /// <summary>Gets whether the instance is an image source: an external instance whose package is
    /// <see cref="SourcePackage"/> of a producer id. A source reads nothing, is scheduled at its producer's cadence and
    /// negotiated extent (<see cref="RenderGraphSourceState"/>) rather than a refresh and a footprint, and renders at most
    /// once a frame however many instances read it.</summary>
    public bool IsSource => (ExternalPackage?.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: SourcePackagePrefix
    ) ?? false);
    /// <summary>Gets how the instance renders: a graph of its own, or an external producer's submissions.</summary>
    public RenderGraphInstanceKind Kind => ((ExternalPackage is null)
        ? RenderGraphInstanceKind.Graph
        : RenderGraphInstanceKind.External);
    /// <summary>Gets the id of the producer a source instance names, or <see langword="null"/> for any other
    /// instance.</summary>
    public string? SourceProducer => (IsSource
        ? ExternalPackage![SourcePackagePrefix.Length..]
        : null
    );

    /// <summary>Returns the package id a source instance of a producer names.</summary>
    /// <param name="producer">The producer's registered id.</param>
    /// <returns><c>source.&lt;producer&gt;</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="producer"/> is empty.</exception>
    public static string SourcePackage(string producer) {
        ArgumentException.ThrowIfNullOrEmpty(argument: producer);

        return (SourcePackagePrefix + producer);
    }
    /// <summary>Creates a source instance: an external instance of <see cref="SourcePackage"/> of a producer, which reads
    /// nothing, records one pass, is paced by its cadence rather than a refresh, and carries its settings.</summary>
    /// <param name="name">The instance's unique name.</param>
    /// <param name="producer">The producer's registered id.</param>
    /// <param name="settings">The settings object the producer opens the image with, or <see langword="null"/> for its
    /// defaults.</param>
    /// <returns>The instance.</returns>
    /// <exception cref="ArgumentException"><paramref name="producer"/> is empty.</exception>
    public static RenderGraphInstance Source(string name, string producer, IReadOnlyDictionary<string, JsonElement>? settings = null) => new(
        ExternalPackage: SourcePackage(producer: producer),
        Name: name,
        Passes: 1,
        Reads: [],
        Refresh: RenderGraphRefresh.EveryFrame,
        Settings: settings
    );
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
    /// <summary>An instance carries settings but is no source, or a source names no producer id, refreshes other than on
    /// every frame its cadence allows, or declares an output that is not an image.</summary>
    SourceDeclaration = 9,
    /// <summary>An authored output extent is non-positive, or belongs to a buffer or negotiated source.</summary>
    ExtentInvalid = 10,
    /// <summary>The set's declared nesting depth is negative or past
    /// <see cref="RenderGraphInstanceSet.MaxNestingDepth"/>.</summary>
    NestingDepthInvalid = 11,
}
/// <summary>A refused set of render-graph instances.</summary>
/// <param name="Code">Why it was refused.</param>
/// <param name="Message">The refusal, naming what it concerns.</param>
/// <param name="Instances">The instances concerned; for <see cref="RenderGraphInstanceRefusalCode.SameFrameCycle"/>,
/// every instance in the cycle in read order; for <see cref="RenderGraphInstanceRefusalCode.KindMismatch"/>, the
/// consumer, then the producer.</param>
public sealed record RenderGraphInstanceRefusal(RenderGraphInstanceRefusalCode Code, string Message, IReadOnlyList<string> Instances);
