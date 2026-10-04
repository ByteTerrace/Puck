using System.Globalization;
using System.Text.RegularExpressions;
using Puck.Abstractions;
using Puck.Abstractions.Cameras;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>The inspector's fixed reusable formatter. The panel and command read precisely this text.</summary>
/// <remarks>Every line fits the editor channel's reservation (<see cref="InspectorWriter.MaxLines"/> lines of
/// <see cref="InspectorWriter.MaxLineChars"/> characters): a long line wraps onto indented continuation lines up to its
/// own line budget and elides its end past it, and optional lines (the frame rate, pass times) past the panel's last
/// line are counted into one closing <c>... n more lines</c> line. A reload diagnostic shows its file and location first,
/// shortened to the part relative to the world's document directory. The fixed snapshot lines fit the reservation by
/// construction; later lines share its remaining room and are counted when omitted.</remarks>
public sealed partial class WorldInspectorText {
    // Display lines a wrapped logical line may take: a reload diagnostic, and a line naming authored identifiers.
    private const int ReloadLines = 5;
    private const int NameLines = 2;
    // Every line leaves one column for the closing bracket the last line carries.
    private const int LineRoom = (InspectorWriter.MaxLineChars - 1);
    private const string Continuation = "  ";
    private const string Elision = "...";

    private readonly char[] m_chars = new char[(InspectorWriter.MaxLines * (InspectorWriter.MaxLineChars + 1))];
    private readonly char[] m_scratch = new char[4096];

    private int m_length;
    private int m_lines;
    private int m_omitted;
    private string? m_reloadSource;
    private string? m_reloadRoot;

    private string m_reloadLine = "reload=none";

    private WorldRenderSky? m_sky;
    private WorldRenderAtmosphere? m_atmosphere;
    private string? m_skyText;

    /// <summary>Gets the formatted panel and command text without allocating.</summary>
    public ReadOnlySpan<char> Text => m_chars.AsSpan(length: m_length, start: 0);

    /// <summary>Replaces the text with one coherent presentation snapshot. A steady snapshot allocates nothing; a
    /// changed reload diagnostic is shaped once.</summary>
    /// <param name="snapshot">The captured presentation facts.</param>
    public void Format(in WorldInspectorSnapshot snapshot) {
        var hit = snapshot.Pick;
        var target = (hit?.Target as WorldPickTarget);
        var camera = snapshot.Camera.GetValueOrDefault();
        var point = hit?.Point;
        var normal = (hit?.Normal ?? default);
        var settings = snapshot.Settings;
        var quality = snapshot.View?.Quality;
        var scale = (snapshot.View ?? new SdfViewSnapshot(Camera: camera, Region: default) { RenderScale = (settings?.RenderCeiling ?? 1f) }).RenderGrid;
        var shadows = ((quality is { } resolved) ? (resolved.DisableSoftShadows ? 0 : ((resolved.ShadowDistanceScale > 0) ? resolved.ShadowDistanceScale : 1)) : (settings?.ShadowReach ?? 0));
        var ambient = ((quality is { } shading) ? !shading.DisableAmbientOcclusion : (settings?.AmbientOcclusion ?? false));
        var surfaced = (hit?.Hit ?? false);
        var distance = (surfaced ? hit.GetValueOrDefault().Distance : 0);
        var scratch = m_scratch.AsSpan();
        int written;

        m_length = 0;
        m_lines = 0;
        m_omitted = 0;
        ShapeReload(diagnostic: snapshot.ReloadError, root: snapshot.WorldRoot);
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"[world.inspect: seat={(snapshot.Slot + 1)} hit={(surfaced ? "surface" : "none")}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"placement={(target?.Placement ?? "none")} body={(target?.BodyIndex ?? -1)} prototype={(target?.Prototype ?? "none")}") && Line(text: scratch[..written], lines: NameLines));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"material={(hit?.MaterialName ?? "none")} index={(surfaced ? hit.GetValueOrDefault().Material : -1)}") && Line(text: scratch[..written], lines: NameLines));
        _ = (((point is { } position)
            ? scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
                handler: $"point={position.X:0.###},{position.Y:0.###},{position.Z:0.###} normal={normal.X:0.###},{normal.Y:0.###},{normal.Z:0.###} distance={distance:0.###}")
            : scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
                handler: $"point=unavailable normal=unavailable distance={distance:0.###}")) && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"steps={(hit?.Steps ?? 0)} queries={(hit?.Queries ?? 0)} selection={(snapshot.Selection ?? "none")}") && Line(text: scratch[..written], lines: NameLines));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"camera={camera.Position.X:0.###},{camera.Position.Y:0.###},{camera.Position.Z:0.###} forward={camera.Forward.X:0.###},{camera.Forward.Y:0.###},{camera.Forward.Z:0.###}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"up={camera.Up.X:0.###},{camera.Up.Y:0.###},{camera.Up.Z:0.###} fov={((2 * MathF.Atan(x: camera.TanHalfFieldOfView)) * (180 / MathF.PI)):0.###}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"simulation-tick={snapshot.SimulationTick} presentation-tick={snapshot.PresentationTick}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"render-scale={scale:0.####} debug={snapshot.DebugMode} shadows={shadows:0.###} ao={ambient}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"fast-shadow={(quality?.UseFastSoftShadowMarch ?? false)} tile-mask={(quality?.UseCameraTileShadowMask ?? false)} fast-ao={(quality?.UseFastAmbientOcclusion ?? false)}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"far-bound={!(quality?.DisableFarBound ?? false)}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"dispatches={snapshot.Dispatches} upload-bytes={snapshot.Uploads} created-total={snapshot.Created}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"words={snapshot.Words}/{snapshot.WordCapacity} headroom={(snapshot.WordCapacity - snapshot.Words)}") && Line(text: scratch[..written]));
        _ = (scratch.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out written,
            handler: $"instances={snapshot.Instances}/{SdfProgramBuilder.MaxInstances} headroom={(SdfProgramBuilder.MaxInstances - snapshot.Instances)}") && Line(text: scratch[..written]));
        _ = Line(text: m_reloadLine, lines: ReloadLines);
        Environment(snapshot: snapshot);
    }

    private void Environment(in WorldInspectorSnapshot snapshot) {
        if (snapshot.Definition is not { } definition) { return; }
        if ((m_skyText is null) || !ReferenceEquals(objA: m_sky, objB: definition.Render.Sky) || !ReferenceEquals(objA: m_atmosphere, objB: definition.Render.Atmosphere)) {
            m_sky = definition.Render.Sky;
            m_atmosphere = definition.Render.Atmosphere;
            m_skyText = WorldLightingText.DescribeSky(atmosphere: m_atmosphere, sky: m_sky);
        }
        _ = Line(m_skyText, lines: 4);
        var scratch = m_scratch.AsSpan();

        _ = (scratch.TryWrite(CultureInfo.InvariantCulture, $"timeline clocks={(definition.Timeline.Clocks?.Count ?? 0)}", out var written) && Line(scratch[..written]));
        var clocks = definition.Timeline.Clocks;

        for (var index = 0; (index < (clocks?.Count ?? 0)); index++) {
            var clock = clocks![index];
            var mirror = snapshot.Mirror;
            var rate = 1d;
            var held = (mirror?.ClockHeld(name: clock.Name, rate: out rate) ?? false);
            var tick = (mirror?.ClockTick(name: clock.Name) ?? default);
            var phase = 0d;
            var available = (mirror?.TryReadPhase(clock.Name, out _, out phase) ?? false);

            _ = (scratch.TryWrite(CultureInfo.InvariantCulture,
                $"clock={clock.Name} source={(clock.State ?? (clock.IsTickClock ? "tick" : "anchor"))} held={held} rate={rate:0.######} tick={tick.Whole}+{tick.Fraction:0.######} phase={(available ? phase : double.NaN):0.######}", out written) && Line(scratch[..written], lines: NameLines));
        }
    }

    /// <summary>Appends the observational frame-rate readout while timing is enabled.</summary>
    /// <param name="mean">The mean frames per second over the monitor's window.</param>
    /// <param name="slowest">The slowest frame's rate over the same window.</param>
    public void FrameRate(float mean, float slowest) {
        if (m_scratch.AsSpan().TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out var written,
            handler: $"fps={mean:0.0} slowest-fps={slowest:0.0}")) { _ = Line(text: m_scratch.AsSpan(length: written, start: 0)); }
    }
    /// <summary>Appends an observational completed pass mean.</summary>
    /// <param name="node">The render-graph instance that recorded the pass.</param>
    /// <param name="timing">The pass's completed mean.</param>
    public void Timing(string node, in Puck.Abstractions.Gpu.GpuPassTiming timing) {
        if (m_scratch.AsSpan().TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out var written,
            handler: $"{node}/{timing.Pass}: {timing.Milliseconds:0.000} ms samples={timing.Samples}")) { _ = Line(text: m_scratch.AsSpan(length: written, start: 0)); }
    }
    /// <summary>Appends why a render-graph instance refused the pass timing it was asked for.</summary>
    /// <param name="node">The render-graph instance.</param>
    /// <param name="refusal">The instance's named refusal.</param>
    public void TimingRefused(string node, string refusal) {
        if (m_scratch.AsSpan().TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out var written,
            handler: $"{node}: gpu-timing refused {refusal}")) { _ = Line(text: m_scratch.AsSpan(length: written, start: 0), lines: NameLines); }
    }
    /// <summary>Closes the shared console and panel record, naming the optional lines the panel had no room for.</summary>
    public void Finish() {
        if ((m_omitted > 0) &&
            m_scratch.AsSpan().TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out var written, handler: $"... {m_omitted} more lines")) {
            Write(text: m_scratch.AsSpan(length: written, start: 0));
        }
        m_chars[m_length++] = ']';
    }

    // Appends one logical line, wrapped onto continuation lines up to its budget and elided past it. An optional line
    // past the panel's last content line is counted instead; fixed snapshot content fits within this reservation.
    private bool Line(ReadOnlySpan<char> text, int lines = 1) {
        var remaining = text;

        for (var used = 1; ; used++) {
            if (m_lines >= (InspectorWriter.MaxLines - 1)) {
                m_omitted++;
                return true;
            }
            var room = ((used == 1) ? LineRoom : (LineRoom - Continuation.Length));
            var prefix = ((used == 1) ? ReadOnlySpan<char>.Empty : Continuation.AsSpan());

            if (remaining.Length <= room) {
                Write(text: remaining, prefix: prefix);
                return true;
            }
            if (used == lines) {
                Write(text: remaining[..(room - Elision.Length)], prefix: prefix, elided: true);
                return true;
            }
            var cut = remaining[..room].LastIndexOf(value: ' ');

            if (cut < (room / 2)) { cut = room; }
            Write(text: remaining[..cut], prefix: prefix);
            remaining = remaining[cut..].TrimStart(trimChar: ' ');
            if (remaining.IsEmpty) { return true; }
        }
    }
    private void Write(ReadOnlySpan<char> text, ReadOnlySpan<char> prefix = default, bool elided = false) {
        if (m_lines > 0) { m_chars[m_length++] = '\n'; }
        prefix.CopyTo(destination: m_chars.AsSpan(start: m_length));
        m_length += prefix.Length;
        text.CopyTo(destination: m_chars.AsSpan(start: m_length));
        m_length += text.Length;
        if (elided) {
            Elision.AsSpan().CopyTo(destination: m_chars.AsSpan(start: m_length));
            m_length += Elision.Length;
        }
        m_lines++;
    }
    // Shapes a reload diagnostic once per change: the refusal's wrapper removed, paths under the world's document
    // directory relative to it, line breaks folded, and the first file and location it names leading the line.
    private void ShapeReload(string? diagnostic, string? root) {
        if (ReferenceEquals(objA: diagnostic, objB: m_reloadSource) && string.Equals(a: root, b: m_reloadRoot, comparisonType: StringComparison.Ordinal)) { return; }
        m_reloadSource = diagnostic;
        m_reloadRoot = root;
        m_reloadLine = ("reload=" + ShapeDiagnostic(diagnostic: (diagnostic ?? "none"), root: root));
    }

    /// <summary>Returns a reload diagnostic as the inspector shows it: without its <c>[world.reload: …]</c> wrapper,
    /// every path under <paramref name="root"/> relative to it, line breaks folded to <c> | </c>, and led by the first
    /// <c>.puck</c> or <c>.json</c> file it names with that file's location.</summary>
    /// <param name="diagnostic">The reload refusal, or <c>none</c>.</param>
    /// <param name="root">The world's document directory, or <see langword="null"/> when it has none.</param>
    /// <returns>The shaped diagnostic.</returns>
    public static string ShapeDiagnostic(string diagnostic, string? root) {
        ArgumentNullException.ThrowIfNull(argument: diagnostic);
        const string Wrapper = "[world.reload: ";
        var text = ((diagnostic.StartsWith(comparisonType: StringComparison.Ordinal, value: Wrapper) && diagnostic.EndsWith(value: ']'))
            ? diagnostic[Wrapper.Length..^1]
            : diagnostic);

        if (!string.IsNullOrEmpty(value: root)) {
            var directory = (root.Replace(newChar: '/', oldChar: '\\').TrimEnd(trimChar: '/') + "/");

            text = text.Replace(comparisonType: PuckPaths.Comparison, newValue: string.Empty, oldValue: directory)
                .Replace(comparisonType: PuckPaths.Comparison, newValue: string.Empty, oldValue: directory.Replace(newChar: '\\', oldChar: '/'));
        }
        text = text.Replace(newValue: " | ", oldValue: "\r\n").Replace(newValue: " | ", oldValue: "\n");
        var location = SourceLocation().Match(input: text);

        return ((!location.Success || (location.Index == 0))
            ? text
            : $"{location.Value}: {text}");
    }

    [GeneratedRegex(pattern: @"[^\s'""(),;]+?\.(?:puck|json)(?:\(\d+,\d+\)|:\d+(?::\d+)?)?")]
    private static partial Regex SourceLocation();
}
/// <summary>The captured presentation facts a formatter displays; no field changes simulation state.</summary>
public readonly record struct WorldInspectorSnapshot {
    /// <summary>The inspected world's authored environment and timeline.</summary>
    public WorldDefinition? Definition { get; init; }
    /// <summary>That world's presented clock readings and session previews.</summary>
    public WorldStateMirror? Mirror { get; init; }
    /// <summary>The zero-based local seat.</summary>
    public int Slot { get; init; }
    /// <summary>The completed pixel and its captured lookup.</summary>
    public SdfPickResult? Pick { get; init; }
    /// <summary>The exact displayed camera.</summary>
    public CameraSnapshot? Camera { get; init; }
    /// <summary>The selected placement, or null.</summary>
    public string? Selection { get; init; }
    /// <summary>The simulation state tick.</summary>
    public ulong SimulationTick { get; init; }
    /// <summary>The latest engine presentation tick.</summary>
    public ulong PresentationTick { get; init; }
    /// <summary>The live render levers.</summary>
    public WorldRenderSettings? Settings { get; init; }
    /// <summary>The rendered view's resolved quality and scale, including a passthrough pane's own view.</summary>
    public SdfViewSnapshot? View { get; init; }
    /// <summary>The active renderer debug mode.</summary>
    public int DebugMode { get; init; }
    /// <summary>Dispatches in the latest completed submissions.</summary>
    public long Dispatches { get; init; }
    /// <summary>Host upload bytes in the latest completed submissions.</summary>
    public long Uploads { get; init; }
    /// <summary>GPU objects created over the live nodes' lifetimes.</summary>
    public long Created { get; init; }
    /// <summary>Live packed program words.</summary>
    public int Words { get; init; }
    /// <summary>The allocated program-word ceiling.</summary>
    public int WordCapacity { get; init; }
    /// <summary>Live SDF instances.</summary>
    public int Instances { get; init; }
    /// <summary>The last reload refusal, or the text none.</summary>
    public string ReloadError { get; init; }
    /// <summary>The world's document directory, which a reload diagnostic's paths are shown relative to, or null.</summary>
    public string? WorldRoot { get; init; }
}
