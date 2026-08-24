using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;
using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One landed <c>captures</c> entry, wire-shaped exactly to the <c>puck.parity.manifest.v1</c> contract.</summary>
internal sealed record WorldCaptureManifestEntry(
    string Station,
    ulong Tick,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Frame,
    [property: JsonPropertyName("stateHash")] string StateHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, long>? Census,
    bool CameraInside
);
/// <summary>The <c>manifest.json</c> document a capture run writes into its output directory.</summary>
internal sealed record WorldCaptureManifest(string Schema, string Backend, string World, IReadOnlyList<WorldCaptureManifestEntry> Captures) {
    public const string SchemaId = "puck.parity.manifest.v1";
}
/// <summary>
/// Arms <c>world.screenshot</c>'s own capture path (<see cref="WorldRenderProbe.Render"/>'s
/// <c>RequestCapture</c>) at the exact simulation ticks a document's <c>captures</c> section schedules — the
/// tick-complete hook fires once per completed <see cref="WorldServer"/> step, the SAME clock
/// <c>WorldConsoleWaitGate.PublishTick</c> rides, so two backends stepping the identical document capture the
/// identical moment by construction. Per-capture work (the SDF inside-check, the per-pixel census, the state hash,
/// and the <c>manifest.json</c> stamp) happens once the armed frame has actually landed — checked at the START of
/// the NEXT tick-complete call, since the render chain serves a capture from inside the SAME host-loop iteration's
/// frame production that follows this one's tick step (offscreen: always that same iteration; windowed: at most one
/// frame later through the overlay decorator) — polling <see cref="Puck.SdfVm.SdfWorldRender.PendingCapturePath"/>
/// tells the two cases apart without a timer.
/// </summary>
/// <remarks>
/// The camera-inside check reads <see cref="WorldServer.SolidField"/> — the same field <c>world.collision.probe</c>
/// reads — never the full decorative render composition, which no CPU-side evaluator walks. A world authoring no
/// field-selecting <c>collision.requirements</c> (so <see cref="WorldServer.SolidField"/> is <see langword="null"/>)
/// gets an honestly UNCHECKED inside-check (narrated, <c>cameraInside</c> stays <see langword="false"/>) rather than
/// a crash or a silent wrong answer — the same fallback posture the <c>noiseDisplace</c>-excluded-op case takes at
/// the evaluator's own construction seam.
/// </remarks>
internal sealed class WorldCaptureScheduler {
    private readonly record struct Pending(WorldCellName Station, ulong Tick, string Path, string FrameName, ulong StateHash, IReadOnlyList<WorldCapturePaletteEntry> Palette);

    private readonly string m_backend;
    private readonly string m_directory;
    private readonly List<WorldCaptureManifestEntry> m_landed = [];
    private readonly WorldRenderProbe? m_renderProbe;
    private readonly Dictionary<ulong, List<WorldCaptureRow>> m_schedule = [];
    private readonly WorldServer m_server;
    private readonly string m_worldFile;

    private Pending? m_pending;

    public WorldCaptureScheduler(WorldServer server, WorldDefinitionSource definitionSource, WorldHostSettings hostSettings, WorldRenderProbe? renderProbe = null) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: definitionSource);
        ArgumentNullException.ThrowIfNull(argument: hostSettings);

        m_server = server;
        m_renderProbe = renderProbe;
        m_backend = (hostSettings.HostsOnDirectX
            ? "directx"
            : "vulkan"
        );
        m_worldFile = Path.GetFileName(path: definitionSource.SourcePath);

        var captures = server.Definition.Captures;

        m_directory = ((captures is { } declared)
            ? WorldCaptureRoot.Resolve(authored: declared.Directory)
            : string.Empty
        );

        if (captures is not { Rows: { } rows }) {
            return;
        }

        foreach (var row in rows) {
            if (row is null) {
                continue;
            }

            foreach (var tick in row.Ticks) {
                if (!m_schedule.TryGetValue(
                    key: tick,
                    value: out var atTick
                )) {
                    atTick = [];
                    m_schedule[tick] = atTick;
                }

                atTick.Add(item: row);
            }
        }
    }

    /// <summary>The tick-complete hook — compose beside <c>WorldConsoleWaitGate.PublishTick</c> at the same call
    /// site (<c>publishTick: (waitGate.PublishTick + captureScheduler.PublishTick)</c>).</summary>
    /// <param name="tick">The just-completed simulation tick.</param>
    public void PublishTick(ulong tick) {
        FinalizePending();

        if (
            (m_schedule.Count == 0) ||
            !m_schedule.TryGetValue(
            key: tick,
            value: out var rows
        )
        ) {
            return;
        }

        foreach (var row in rows) {
            Arm(
                row: row,
                tick: tick
            );
        }
    }

    private void Arm(WorldCaptureRow row, ulong tick) {
        if (string.IsNullOrEmpty(value: m_directory)) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: captures.directory did not resolve — skipping.");

            return;
        }

        if (m_pending is { } outstanding) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: {outstanding.Station} tick {outstanding.Tick} is still pending on the same render chain — a station cannot share a composed frame with another station's own capture, so this one is skipped.");

            return;
        }

        var stateHash = ComputeStateHash(
            server: m_server,
            tick: tick
        );

        var (cameraInside, narration) = ProbeCameraInside(
            definition: m_server.Definition,
            server: m_server,
            tick: tick
        );

        if (narration is { Length: > 0 }) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: {narration}");
        }

        if (cameraInside) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: camera is inside geometry (map(cameraPos) <= 0) — capture REFUSED, no frame written.");

            m_landed.Add(item: new WorldCaptureManifestEntry(
                CameraInside: true,
                Census: null,
                Frame: null,
                Station: row.Station.Value,
                StateHash: ToHex(hash: stateHash),
                Tick: tick
            ));
            WriteManifest();

            return;
        }

        if (m_renderProbe?.Render is not { } render) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: no renderer is composed — captures need host.presentation offscreen or windowed.");

            return;
        }

        var frameName = $"{row.Station}-{tick}.png";
        var path = Path.Combine(
            path1: m_directory,
            path2: frameName
        );

        try {
            Directory.CreateDirectory(path: m_directory);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: could not create {m_directory} ({exception.Message}).");

            return;
        }

        render.RequestCapture(path: path);

        m_pending = new Pending(
            FrameName: frameName,
            Palette: row.Palette,
            Path: path,
            StateHash: stateHash,
            Station: row.Station,
            Tick: tick
        );
    }
    private void FinalizePending() {
        if (m_pending is not { } pending) {
            return;
        }

        // Still outstanding on the render chain — this exact path has not been served by a produced frame yet;
        // retry on a later tick's call rather than reading a file that may not exist (windowed) or may be
        // mid-write (offscreen, within the same host-loop iteration this tick's own frame has not reached yet).
        if (string.Equals(
            a: m_renderProbe?.Render?.PendingCapturePath,
            b: pending.Path,
            comparisonType: StringComparison.Ordinal
        )) {
            return;
        }

        m_pending = null;

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: pending.Path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[captures] {pending.Station} tick {pending.Tick}: the render chain cleared its pending path but {pending.Path} could not be read ({exception.Message}) — dropping this capture.");

            return;
        }

        PngImage image;

        try {
            image = PngDecoder.Decode(pngBytes: bytes);
        } catch (Exception exception) when ((exception is InvalidDataException or EndOfStreamException)) {
            Console.Error.WriteLine(value: $"[captures] {pending.Station} tick {pending.Tick}: {pending.Path} did not decode as PNG ({exception.Message}) — dropping this capture.");

            return;
        }

        m_landed.Add(item: new WorldCaptureManifestEntry(
            CameraInside: false,
            Census: ComputeCensus(
                image: image,
                palette: pending.Palette
            ),
            Frame: pending.FrameName,
            Station: pending.Station.Value,
            StateHash: ToHex(hash: pending.StateHash),
            Tick: pending.Tick
        ));
        WriteManifest();
    }
    private void WriteManifest() {
        var manifest = new WorldCaptureManifest(
            Backend: m_backend,
            Captures: m_landed,
            Schema: WorldCaptureManifest.SchemaId,
            World: m_worldFile
        );
        var path = Path.Combine(
            path1: m_directory,
            path2: "manifest.json"
        );

        try {
            File.WriteAllText(
                contents: JsonSerializer.Serialize(
                    value: manifest,
                    options: ManifestSerializerOptions
                ),
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[captures] could not write {path} ({exception.Message}).");
        }
    }

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // The camera position feeding the inside-check is read straight off the document — the same worldPoint a
    // 'select' op's winning case's program anchors at — rather than the compiled render rig, so this needs nothing
    // client-side. Every authored station camera therefore needs a leading 'anchor' op naming a worldPoint subject
    // (directly, or transitively through 'select'); anything else is an honest "cannot resolve" narration.
    private static (bool Inside, string? Narration) ProbeCameraInside(WorldDefinition definition, WorldServer server, ulong tick) {
        if (!TryResolveActiveCameraPosition(
            definition: definition,
            reason: out var reason,
            position: out var position,
            tick: tick
        )) {
            return (false, $"could not resolve the active camera position ({reason}) — inside-check unchecked.");
        }

        if (server.SolidField is not { } field) {
            return (false, "no field-selecting collision provider is live — inside-check unchecked.");
        }

        var fixedPosition = new FixedVector3(
            X: FixedQ4816.FromDouble(value: position.X),
            Y: FixedQ4816.FromDouble(value: position.Y),
            Z: FixedQ4816.FromDouble(value: position.Z)
        );

        if (!field.Probe(
            distance: out var distance,
            gradient: out _,
            material: out _,
            position: in fixedPosition
        )) {
            return (false, "the field has no geometry to answer against — inside-check unchecked.");
        }

        return ((distance <= FixedQ4816.Zero), null);
    }
    private static bool TryResolveActiveCameraPosition(WorldDefinition definition, ulong tick, out Vector3 position, out string reason) {
        position = default;

        if (definition.ViewsRaw is not { } views) {
            reason = "the document authors no views section";

            return false;
        }

        string? cameraName = null;

        foreach (var layout in views.Layouts) {
            foreach (var slot in (layout?.Slots ?? [])) {
                if (slot.Camera is { Length: > 0 } named) {
                    cameraName = named;

                    break;
                }
            }

            if (cameraName is not null) {
                break;
            }
        }

        if (cameraName is null) {
            reason = "no authored views.layouts slot names a camera";

            return false;
        }

        return TryResolveProgramPosition(
            definition: definition,
            depth: 0,
            position: out position,
            programName: cameraName,
            reason: out reason,
            tick: tick
        );
    }
    private static bool TryResolveProgramPosition(WorldDefinition definition, string programName, ulong tick, int depth, out Vector3 position, out string reason) {
        position = default;

        if (depth > 8) {
            reason = $"'{programName}' resolves through more than 8 select/blend hops";

            return false;
        }

        if (!TryFindCameraProgram(
            definition: definition,
            name: programName,
            program: out var program
        )) {
            reason = $"camera program '{programName}' names no declared camera";

            return false;
        }

        if (program.AnchorOp?.Subject is WorldCameraSubject.WorldPoint worldPoint) {
            position = worldPoint.Point.Value;
            reason = string.Empty;

            return true;
        }

        if (program.SelectOp is { } select) {
            var key = ((long)MathF.Round(x: select.Key.Resolve(
                definition: definition,
                fallback: 0f,
                tick: tick
            )));
            var target = select.Default;

            foreach (var candidate in (select.Cases ?? [])) {
                if (candidate.Value == key) {
                    target = candidate.Program;

                    break;
                }
            }

            return TryResolveProgramPosition(
                definition: definition,
                depth: (depth + 1),
                position: out position,
                programName: target,
                reason: out reason,
                tick: tick
            );
        }

        reason = $"camera program '{programName}' authors neither an anchor.worldPoint nor a select op";

        return false;
    }
    private static bool TryFindCameraProgram(WorldDefinition definition, string name, out WorldCameraProgram program) {
        foreach (var camera in definition.Cameras) {
            if (
                (camera?.Rig is { } rig) &&
                string.Equals(
                a: rig.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                program = rig;

                return true;
            }
        }

        var views = definition.ViewsRaw;

        if (
            (views?.SeatRig is { } seatRig) &&
            string.Equals(
            a: seatRig.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            program = seatRig;

            return true;
        }

        if (
            (views?.CameraRig is { } cameraRig) &&
            string.Equals(
            a: cameraRig.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            program = cameraRig;

            return true;
        }

        program = null!;

        return false;
    }
    // Nearest-color match against the station's own authored palette — the mechanically honest census the render
    // path supports: the composed frame is a flat color surface, carrying no per-pixel material-id buffer to read.
    private static Dictionary<string, long> ComputeCensus(PngImage image, IReadOnlyList<WorldCapturePaletteEntry> palette) {
        var swatchMaterial = new int[palette.Count];
        var swatchR = new byte[palette.Count];
        var swatchG = new byte[palette.Count];
        var swatchB = new byte[palette.Count];

        for (var index = 0; (index < palette.Count); index++) {
            _ = HexColor.TryParseRgba(
                rgba: out var rgba,
                value: palette[index].Color
            );

            swatchMaterial[index] = palette[index].Material;
            swatchR[index] = ((byte)MathF.Round(x: (rgba.X * 255f)));
            swatchG[index] = ((byte)MathF.Round(x: (rgba.Y * 255f)));
            swatchB[index] = ((byte)MathF.Round(x: (rgba.Z * 255f)));
        }

        var counts = new Dictionary<string, long>();
        var pixels = image.RgbaPixels;
        var pixelCount = (image.Width * image.Height);

        for (var pixel = 0; (pixel < pixelCount); pixel++) {
            var offset = (pixel * 4);
            var r = pixels[offset];
            var g = pixels[offset + 1];
            var b = pixels[offset + 2];
            var bestIndex = 0;
            var bestDistance = long.MaxValue;

            for (var swatch = 0; (swatch < swatchMaterial.Length); swatch++) {
                var dr = (r - swatchR[swatch]);
                var dg = (g - swatchG[swatch]);
                var db = (b - swatchB[swatch]);
                var distance = ((((long)dr * dr) + ((long)dg * dg)) + ((long)db * db));

                if (distance < bestDistance) {
                    bestDistance = distance;
                    bestIndex = swatch;
                }
            }

            var key = swatchMaterial[bestIndex].ToString(provider: CultureInfo.InvariantCulture);

            counts[key] = (counts.TryGetValue(
                key: key,
                value: out var existing)
                ? (existing + 1)
                : 1
            );
        }

        return counts;
    }
    // Reuses WorldReplaySnapshot.HashState (the pose trajectory replay.verify pins) as the pose half, then chains
    // state.world's every declared row/cell in document order onto the SAME running fold — extending the summary
    // WorldReplayTape already trusts rather than inventing a second one. Body/identity state lanes are ephemeral
    // per-body counters/timers outside the world-scoped decision surface a capture cares about (state.world is what
    // a rule/capture-station row lives in) and are left out, same as the pose hash's own documented scope.
    internal static ulong ComputeStateHash(WorldServer server, ulong tick) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: WorldReplaySnapshot.HashState(population: server.Population));

        var rows = server.Definition.State;

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            var row = rows[rowIndex];
            var keys = (row.IsSlot
                ? ((IReadOnlyList<WorldCellName>)[WorldStateRow.SlotKey])
                : (row.Cells?.Select(selector: static cell => cell.Key).ToArray() ?? [])
            );

            for (var keyIndex = 0; (keyIndex < keys.Count); keyIndex++) {
                if (!WorldStateReader.TryRead(
                    definition: server.Definition,
                    key: keys[keyIndex],
                    rawValue: out var rawValue,
                    row: out _,
                    rowName: row.Name,
                    text: out var text,
                    tick: tick
                )) {
                    continue;
                }

                hash.Add(value: (rawValue ?? 0L));

                if (
                    (row.Kind == CellKind.Text) &&
                    (text is { Length: > 0 })
                ) {
                    hash.Add(values: System.Text.Encoding.UTF8.GetBytes(s: text));
                }
            }
        }

        return hash.Value;
    }
    private static string ToHex(ulong hash) => hash.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );
}
