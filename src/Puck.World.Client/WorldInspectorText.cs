using System.Globalization;
using Puck.Abstractions.Cameras;
using Puck.Overlays;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>The inspector's fixed reusable formatter. The panel and command read precisely this text.</summary>
public sealed class WorldInspectorText {
    private readonly char[] m_chars = new char[(InspectorWriter.MaxLines * (InspectorWriter.MaxLineChars + 1))];

    private int m_length;

    /// <summary>Gets whether this snapshot exceeded the editor writer's declared text reservation.</summary>
    public bool Refused { get; private set; }
    /// <summary>Gets the formatted panel and command text without allocating.</summary>
    public ReadOnlySpan<char> Text => m_chars.AsSpan(length: m_length, start: 0);

    /// <summary>Replaces the text with one coherent presentation snapshot.</summary>
    public void Format(in WorldInspectorSnapshot snapshot) {
        var hit = snapshot.Pick;
        var target = (hit?.Target as WorldPickTarget);
        var camera = snapshot.Camera.GetValueOrDefault();
        var point = hit?.Point;
        var normal = (hit?.Normal ?? default);
        var settings = snapshot.Settings;
        var quality = snapshot.View?.Quality;
        var authoredScale = (snapshot.View?.RenderScale ?? (settings?.RenderScale ?? 1f));
        var ceiling = RenderGraphExtent.Quantize(fraction: ((authoredScale > 0f) ? authoredScale : 1f));
        var scale = ((snapshot.View is { ResolvedRenderScale: > 0f } view) ? Math.Min(val1: view.ResolvedRenderScale, val2: ceiling) : ceiling);
        var shadows = ((quality is { } resolved) ? (resolved.DisableSoftShadows ? 0 : ((resolved.ShadowDistanceScale > 0) ? resolved.ShadowDistanceScale : 1)) : (settings?.ShadowReach ?? 0));
        var ambient = ((quality is { } shading) ? !shading.DisableAmbientOcclusion : (settings?.AmbientOcclusion ?? false));

        Refused = false;
        Span<char> surface = stackalloc char[InspectorWriter.MaxLineChars];
        int surfaceLength;
        var distance = ((hit?.Hit ?? false) ? hit.GetValueOrDefault().Distance : 0);
        var surfaceWritten = (point is { } position)
            ? surface.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out surfaceLength,
                handler: $"point={position.X:0.###},{position.Y:0.###},{position.Z:0.###} normal={normal.X:0.###},{normal.Y:0.###},{normal.Z:0.###} distance={distance:0.###}")
            : surface.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out surfaceLength,
                handler: $"point=unavailable normal=unavailable distance={distance:0.###}");

        if (!surfaceWritten) { Refuse(); return; }
        if (!m_chars.AsSpan().TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out m_length,
            handler: $"[world.inspect: seat={(snapshot.Slot + 1)} hit={((hit?.Hit ?? false) ? "surface" : "none")}\nplacement={(target?.Placement ?? "none")} body={(target?.BodyIndex ?? -1)} prototype={(target?.Prototype ?? "none")}\nmaterial={(hit?.MaterialName ?? "none")} index={((hit?.Hit ?? false) ? hit.GetValueOrDefault().Material : -1)}\n{surface[..surfaceLength]}\nsteps={(hit?.Steps ?? 0)} queries={(hit?.Queries ?? 0)} selection={(snapshot.Selection ?? "none")}\ncamera={camera.Position.X:0.###},{camera.Position.Y:0.###},{camera.Position.Z:0.###} forward={camera.Forward.X:0.###},{camera.Forward.Y:0.###},{camera.Forward.Z:0.###}\nup={camera.Up.X:0.###},{camera.Up.Y:0.###},{camera.Up.Z:0.###} fov={((2 * MathF.Atan(x: camera.TanHalfFieldOfView)) * (180 / MathF.PI)):0.###}\nsimulation-tick={snapshot.SimulationTick} presentation-tick={snapshot.PresentationTick}\nrender-scale={scale:0.####} debug={snapshot.DebugMode} shadows={shadows:0.###} ao={ambient}\nfast-shadow={(quality?.UseFastSoftShadowMarch ?? false)} tile-mask={(quality?.UseCameraTileShadowMask ?? false)} fast-ao={(quality?.UseFastAmbientOcclusion ?? false)}\nfar-bound={!(quality?.DisableFarBound ?? false)}\ndispatches={snapshot.Dispatches} upload-bytes={snapshot.Uploads} created-total={snapshot.Created}\nwords={snapshot.Words}/{snapshot.WordCapacity} headroom={(snapshot.WordCapacity - snapshot.Words)}\ninstances={snapshot.Instances}/{SdfProgramBuilder.MaxInstances} headroom={(SdfProgramBuilder.MaxInstances - snapshot.Instances)}\nreload={snapshot.ReloadError}")) { Refuse(); }
        Validate();
    }
    /// <summary>Appends the observational frame-rate readout while timing is enabled.</summary>
    public void FrameRate(float mean, float slowest) {
        if (Refused) { return; }
        if (m_chars.AsSpan(start: m_length).TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out var count,
            handler: $"\nfps={mean:0.0} slowest-fps={slowest:0.0}")) { m_length += count; } else { Refuse(); }
        Validate();
    }
    /// <summary>Appends an observational completed pass mean.</summary>
    public void Timing(string node, in Puck.Abstractions.Gpu.GpuPassTiming timing) {
        if (Refused) { return; }
        if (m_chars.AsSpan(start: m_length).TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out var count,
            handler: $"\n{node}/{timing.Pass}: {timing.Milliseconds:0.000} ms samples={timing.Samples}")) { m_length += count; } else { Refuse(); }
        Validate();
    }
    /// <summary>Closes the shared console and panel record.</summary>
    public void Finish() {
        if (Refused) { return; }
        if (m_length < m_chars.Length) { m_chars[m_length++] = ']'; } else { Refuse(); }
        Validate();
    }

    private void Validate() {
        var lines = 0;

        foreach (var range in Text.Split(separator: '\n')) {
            if ((++lines > InspectorWriter.MaxLines) || (Text[range].Length > InspectorWriter.MaxLineChars)) {
                Refuse();
                return;
            }
        }
    }
    private void Refuse() {
        const string Message = "[world.inspect: editor refused text beyond its declared 32-line/96-column reservation]";

        Message.AsSpan().CopyTo(destination: m_chars);
        m_length = Message.Length;
        Refused = true;
    }
}
/// <summary>The captured presentation facts a formatter displays; no field changes simulation state.</summary>
public readonly record struct WorldInspectorSnapshot {
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
}
