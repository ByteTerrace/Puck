using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Assets;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Why a scheduled capture wrote no frame.</summary>
public enum WorldCaptureRefusal : byte {
    /// <summary>The camera sat inside geometry (<c>map(cameraPos) &lt;= 0</c>) at the armed tick.</summary>
    CameraInside,
    /// <summary>Another capture still held the render chain at the armed tick.</summary>
    Busy,
    /// <summary>The frame that served the capture showed a tick other than the armed one.</summary>
    Stale,
    /// <summary>The readback, the PNG write, or the PNG's decode failed.</summary>
    Failed,
    /// <summary>No frame served the capture: the run ended first, or a host holding its clock for it spent a hold
    /// budget first, and the detail names why the render chain could not serve it, naming the engine's pipeline build
    /// while the engine was not ready.</summary>
    Unserved,
    /// <summary>The graphics device was lost while the capture was armed or being read back; the host rebuilt the
    /// device and ran on, and the detail carries the loss's reason.</summary>
    DeviceLost,
}
/// <summary>One scheduled capture's outcome, wire-shaped to the <c>puck.parity.manifest.v1</c> contract: either a
/// frame and its census, or a refusal and its detail, never both and never neither.</summary>
/// <param name="Station">The capture row's station name.</param>
/// <param name="Tick">The simulation tick the capture was armed for.</param>
/// <param name="RegionTick">The simulation tick the frame that served the capture refreshed its bound regions at, which
/// <c>puck parity</c> holds to <paramref name="Tick"/>, or <see langword="null"/> when refused.</param>
/// <param name="Frame">The PNG file name inside the capture directory, or <see langword="null"/> when refused.</param>
/// <param name="StateHash">The capture-scope state hash at <paramref name="Tick"/>, as 16 lower-case hex digits.</param>
/// <param name="Census">The per-material pixel census of the frame, or <see langword="null"/> when refused.</param>
/// <param name="Refusal">Why no frame was written, or <see langword="null"/> when the frame landed.</param>
/// <param name="Detail">The refusal's prose, naming the ticks involved, or <see langword="null"/> when the frame
/// landed.</param>
/// <param name="SourceVerdict">The exact verdict of a landed capture of a source instance whose source states the image
/// it shows, or <see langword="null"/> for any other capture.</param>
public sealed record WorldCaptureManifestEntry(
    string Station,
    ulong Tick,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? RegionTick,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Frame,
    [property: JsonPropertyName("stateHash")] string StateHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, long>? Census,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCaptureRefusal? Refusal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCaptureSourceVerdict? SourceVerdict = null
);
/// <summary>The exact verdict a landed capture of a source instance gets against the image its source states it shows
/// (<see cref="ImageSourceVerdict"/>): whether every pixel matched, and the verdict or the reason none could be
/// reached.</summary>
/// <param name="Holds">Whether the capture shows exactly the source's reference, of the tick the capture was rendered
/// at.</param>
/// <param name="Detail">The verdict's prose: the producer, the extent and <c>exact</c>, the count and first of the
/// differing pixels, or why the source's reference could not be compared.</param>
public sealed record WorldCaptureSourceVerdict(bool Holds, string Detail);
/// <summary>The <c>manifest.json</c> document a capture run writes into its output directory.</summary>
/// <param name="Schema">The manifest schema identifier, <see cref="SchemaId"/>.</param>
/// <param name="Backend">The graphics backend that rendered the frames: <c>vulkan</c> or <c>directx</c>.</param>
/// <param name="World">The booted world document's file name.</param>
/// <param name="Captures">Every armed capture's outcome, in the order each was decided.</param>
public sealed record WorldCaptureManifest(string Schema, string Backend, string World, IReadOnlyList<WorldCaptureManifestEntry> Captures) {
    /// <summary>The manifest schema identifier.</summary>
    public const string SchemaId = "puck.parity.manifest.v1";
}
/// <summary>
/// Arms captures at the document's scheduled authority ticks and accounts for every one of them: each armed capture
/// becomes exactly one manifest entry, either the frame that showed its tick or a named
/// <see cref="WorldCaptureRefusal"/>. A capture is armed when its tick is published and served by the next composed
/// frame. While it waits, <see cref="AwaitsFrame"/> asks the fixed-step host to compose that frame before the next
/// step, so a catch-up burst cannot carry the simulation past the armed tick first; the frame then shows exactly the
/// state the entry's state hash describes. A frame that served the capture after a later tick had completed is
/// refused as <see cref="WorldCaptureRefusal.Stale"/>, naming the armed tick and the tick shown.
/// <para>
/// A host that holds its clock (the offscreen host) goes further: <see cref="HoldsClock"/> withholds every step while
/// a capture armed at the last published tick is neither served nor refused, whatever keeps the render chain from
/// serving it, so no tick past the armed one runs before the capture is decided. The capture hold counts from
/// readiness: host time held while the engine is not ready (<see cref="IWorldEngineReadiness"/>: its pipeline set not
/// yet installed, or no frame produced from it) is spent from <see cref="BuildHoldBudgetSeconds"/>, and only time held
/// while it is ready from <see cref="HoldBudgetSeconds"/>. Past either budget the capture is refused by name, naming
/// the build when the build spent it, and the run steps on. <see cref="Drain"/> decides whatever is still
/// owed a frame as the run ends, before the render chain is disposed. The scheduler counts what it sees
/// under <see cref="WorkSourceName"/>: <see cref="TicksWhileArmed"/>, the ticks published while a capture armed at an
/// earlier tick was still unserved, which a holding host keeps at zero, and <see cref="HeldTicks"/>, the host time
/// withheld.
/// </para>
/// <para>
/// A capture of a source instance (a row naming a screen, or an instance that is an uploaded source) whose source states
/// the image it shows (<see cref="IImageSourceReference"/>) gets the exact verdict too (<see cref="ImageSourceVerdict"/>):
/// the landed frame against the source's reference, which must state the tick the frame was rendered at. The capture
/// holds the reference its source had when it was armed, which states its last write whatever the source declares or
/// whichever source replaces it afterwards, and is judged against the one source now running only when the held one
/// states another tick (a source rebuilt between arming and serving). The entry
/// records it (<see cref="WorldCaptureManifestEntry.SourceVerdict"/>), and stderr narrates it as
/// <c>[captures] &lt;station&gt; tick &lt;tick&gt;: verdict &lt;detail&gt;</c>.
/// </para>
/// </summary>
/// <remarks>
/// The camera-inside check reads <see cref="WorldServer.SolidField"/> — the same field <c>world.collision.probe</c>
/// reads — never the full decorative render composition, which no CPU-side evaluator walks. A world authoring no
/// field-selecting <c>collision.requirements</c> (so <see cref="WorldServer.SolidField"/> is <see langword="null"/>)
/// gets an honestly UNCHECKED inside-check (narrated, never refused) rather than a crash or a silent wrong answer.
/// Every member runs on the host pump.
/// </remarks>
public sealed class WorldCaptureScheduler {
    private readonly record struct Pending(CellName Station, ulong Tick, string Path, string FrameName, FrameCaptureRequest Request, ulong StateHash, IReadOnlyList<WorldCapturePaletteEntry> Palette, string? Instance, IImageSourceReference? Reference);

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new() {
        Converters = { new JsonStringEnumConverter(namingPolicy: JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string m_backend;
    private readonly Func<string?, ICaptureRequestTarget?>? m_captureTarget;
    private readonly string m_directory;

    private readonly List<WorldCaptureManifestEntry> m_landed = [];
    private readonly WorldTickSchedule<WorldCaptureRow> m_schedule = new();

    // The state mirror a camera program's bound select key reads through, over the server's own document, installed at
    // each armed tick: the value the offscreen presentation's mirror presents at that tick with the fraction pinned.
    private readonly WorldStateMirror m_state;
    private readonly WorldServer m_server;
    private readonly IWorldEngineReadiness? m_readiness;
    private readonly Func<ulong?>? m_regionTick;
    private readonly IWorldCaptureSources? m_sources;
    private readonly string m_worldFile;

    private readonly WorkCounterSet m_work = new(
        kinds: [TicksWhileArmed, HeldTicks],
        name: WorkSourceName
    );

    // The host time a holding host has withheld steps for over the whole run: while the engine was not ready, against
    // BuildHoldBudgetTicks, and while it was ready, against HoldBudgetTicks.
    private ulong m_buildHeldTicks;
    private ulong m_heldTicks;
    private ulong? m_lastPublishedTick;
    private Pending? m_pending;

    /// <summary>Initializes a new instance of the <see cref="WorldCaptureScheduler"/> class over the server's
    /// authored <c>captures</c> rows.</summary>
    /// <param name="server">The authoritative server whose ticks are published here.</param>
    /// <param name="directory">The resolved capture output directory, or the empty string when none resolved.</param>
    /// <param name="backend">The manifest's backend name: <c>vulkan</c> or <c>directx</c>.</param>
    /// <param name="worldFile">The booted world document's file name.</param>
    /// <param name="captureTarget">Returns the target a row's capture is armed on, given the render-graph instance the
    /// row names (<see cref="WorldCaptureRow.Instance"/>, <see langword="null"/> for the root), or
    /// <see langword="null"/> while no renderer is composed; it throws <see cref="ArgumentException"/> for an instance the
    /// render graph does not have. <see langword="null"/> itself for a boot that composes no renderer.</param>
    /// <param name="readiness">The engine readiness a hold reads: time held while it is not ready is spent from the
    /// pipeline-build budget, and a capture refused then names its reason. <see langword="null"/> for a boot that
    /// composes no renderer, whose holds all count as ready.</param>
    /// <param name="regionTick">Reads the simulation tick the frame being composed refreshed its bound regions at, which
    /// each capture records from the frame that serves it (<see cref="WorldCaptureManifestEntry.RegionTick"/>);
    /// <see langword="null"/> for a boot that composes no renderer.</param>
    /// <param name="sources">The source instance each screen reads, which a row naming a screen captures, and the images
    /// deterministic sources state they show, which a capture of one is held to; <see langword="null"/> for a boot that
    /// composes no renderer, where a row naming a screen captures nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/>, <paramref name="directory"/>,
    /// <paramref name="backend"/>, or <paramref name="worldFile"/> is <see langword="null"/>.</exception>
    public WorldCaptureScheduler(WorldServer server, string directory, string backend, string worldFile, Func<string?, ICaptureRequestTarget?>? captureTarget, IWorldEngineReadiness? readiness = null, Func<ulong?>? regionTick = null, IWorldCaptureSources? sources = null) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: backend);
        ArgumentNullException.ThrowIfNull(argument: worldFile);

        m_server = server;
        m_directory = directory;
        m_backend = backend;
        m_worldFile = worldFile;
        m_captureTarget = captureTarget;
        m_readiness = readiness;
        m_regionTick = regionTick;
        m_sources = sources;
        m_state = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => server.Definition));

        if (server.Definition.Captures is not { Rows: { } rows }) {
            return;
        }

        foreach (var row in rows) {
            if (row is null) {
                continue;
            }

            foreach (var tick in row.Ticks) {
                m_schedule.Add(
                    row: row,
                    tick: tick
                );
            }
        }
    }

    /// <summary>Gets whether a capture armed at the last published tick still waits for the frame that shows it.
    /// The fixed-step host composes a frame before its next step while this holds.</summary>
    public bool AwaitsFrame => (
        (m_pending is { } pending) &&
        (m_lastPublishedTick == pending.Tick) &&
        !pending.Request.Completion.IsCompleted
    );
    /// <summary>Gets every capture outcome recorded so far, in the order each was decided.</summary>
    public IReadOnlyList<WorldCaptureManifestEntry> Entries => m_landed;

    /// <summary>Gets the kind counting the host time a holding host withheld steps for while a capture was owed, in
    /// engine ticks: <c>world.captures.held</c>. Paced by the wall clock, so two runs may differ.</summary>
    public static WorkKind HeldTicks { get; } = new(name: "world.captures.held", unit: "engine-ticks", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting ticks published while a capture armed at an earlier tick was still neither
    /// served nor refused: <c>world.captures.ticks-while-armed</c>. A holding host keeps it at zero; a host paced to a
    /// display steps on and counts whatever its frames were late for.</summary>
    public static WorkKind TicksWhileArmed { get; } = new(name: "world.captures.ticks-while-armed", unit: "count", workClass: WorkClass.Pacing);

    /// <summary>Gets the scheduler's counters, under <see cref="WorkSourceName"/>.</summary>
    public IWorkCounterSource Work => m_work;

    /// <summary>The name a counters report heads the scheduler's section with.</summary>
    public const string WorkSourceName = "world.captures";
    /// <summary>The host time, in seconds, a run may hold its clock for unserved captures while the engine is ready,
    /// summed over the whole run.</summary>
    public const int HoldBudgetSeconds = 60;
    /// <summary><see cref="HoldBudgetSeconds"/> in engine ticks, the unit a holding host withholds time in.</summary>
    public const ulong HoldBudgetTicks = (HoldBudgetSeconds * EngineTicks.PerSecond);
    /// <summary>The host time, in seconds, a run may hold its clock for unserved captures while the engine is not ready,
    /// summed over the whole run: the engine's pipeline set building on a cold driver cache, or rebuilding after a device
    /// loss. Both budgets together still fit inside a parity leg's exit backstop with the leg's own run after them.</summary>
    public const int BuildHoldBudgetSeconds = 180;
    /// <summary><see cref="BuildHoldBudgetSeconds"/> in engine ticks.</summary>
    public const ulong BuildHoldBudgetTicks = (BuildHoldBudgetSeconds * EngineTicks.PerSecond);

    /// <summary>Answers a host that holds its clock: whether to withhold its next step because a capture armed at the
    /// last published tick is still neither served nor refused. The render chain may not be able to serve it yet for
    /// any reason (the engine's pipelines not yet installed, a device being rebuilt); the answer is the same. The hold is
    /// bounded, and counts from readiness: time withheld while the engine is not ready is spent from
    /// <see cref="BuildHoldBudgetSeconds"/>, and time withheld while it is ready from <see cref="HoldBudgetSeconds"/>, each
    /// summed over the run. Once the budget the current hold draws on is spent, the capture is refused as
    /// <see cref="WorldCaptureRefusal.Unserved"/>, naming the engine's pipeline build when it was the build that held
    /// it, and withdrawn from the chain, so the host steps on and a later capture can arm. A capture that cannot be
    /// served after its budget is spent is refused the same way at once.</summary>
    /// <param name="withheldTicks">The host time withheld when the answer is <see langword="true"/>, counted under
    /// <see cref="HeldTicks"/>.</param>
    /// <returns><see langword="true"/> to withhold the step.</returns>
    public bool HoldsClock(ulong withheldTicks) {
        if (
            !AwaitsFrame ||
            (m_pending is not { } pending)
        ) {
            return false;
        }

        if (m_readiness is { IsReady: false } readiness) {
            if (m_buildHeldTicks >= BuildHoldBudgetTicks) {
                Withdraw(
                    detail: $"{(readiness.NotReadyReason ?? "the engine was not ready")} (the host held its clock at tick {pending.Tick} while the engine's pipeline set built, until its {BuildHoldBudgetSeconds}-second pipeline-build hold budget was spent)",
                    pending: pending
                );

                return false;
            }

            m_buildHeldTicks = Spend(
                budget: BuildHoldBudgetTicks,
                held: m_buildHeldTicks,
                withheld: withheldTicks
            );
        } else {
            if (m_heldTicks >= HoldBudgetTicks) {
                Withdraw(
                    detail: $"no frame served it (the host held its clock at tick {pending.Tick} until its {HoldBudgetSeconds}-second capture hold budget was spent)",
                    pending: pending
                );

                return false;
            }

            m_heldTicks = Spend(
                budget: HoldBudgetTicks,
                held: m_heldTicks,
                withheld: withheldTicks
            );
        }

        m_work.Add(
            amount: ((long)Math.Min(
                val1: withheldTicks,
                val2: long.MaxValue
            )),
            kind: HeldTicks
        );

        return true;
    }

    // Adds withheld host time to a hold budget's spent time, saturating at the budget.
    private static ulong Spend(ulong budget, ulong held, ulong withheld) =>
        (((budget - held) > withheld)
            ? (held + withheld)
            : budget
        );
    private void Arm(WorldCaptureRow row, ulong tick) {
        if (string.IsNullOrEmpty(value: m_directory)) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: captures.directory did not resolve — skipping.");

            return;
        }

        var stateHash = ComputeStateHash(
            server: m_server,
            tick: tick
        );

        var (cameraInside, narration) = ProbeCameraInside();

        if (narration is { Length: > 0 }) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: {narration}");
        }

        if (cameraInside) {
            Refuse(
                detail: "the camera is inside geometry (map(cameraPos) <= 0); no frame written",
                refusal: WorldCaptureRefusal.CameraInside,
                stateHash: stateHash,
                station: row.Station,
                tick: tick
            );

            return;
        }

        ICaptureRequestTarget? target;
        var instance = row.Instance;

        try {
            if (
                (m_captureTarget is not null) &&
                (row.Screen is { } screen)
            ) {
                instance = (m_sources?.InstanceOf(screen: screen) ?? throw new ArgumentException(message: $"screen {screen} reads no source instance"));
            }

            target = m_captureTarget?.Invoke(arg: instance);
        } catch (ArgumentException exception) {
            Refuse(
                detail: $"the render graph cannot capture {((row.Screen is { } named) ? $"screen {named}'s source" : $"instance '{row.Instance}'")} ({exception.Message})",
                refusal: WorldCaptureRefusal.Failed,
                stateHash: stateHash,
                station: row.Station,
                tick: tick
            );

            return;
        }

        if (target is null) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: no renderer is composed — captures need host.presentation offscreen or windowed.");

            return;
        }

        try {
            Directory.CreateDirectory(path: m_directory);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
            Console.Error.WriteLine(value: $"[captures] {row.Station} tick {tick}: could not create {m_directory} ({exception.Message}).");

            return;
        }

        if (m_pending is { } outstanding) {
            Refuse(
                detail: $"{outstanding.Station} tick {outstanding.Tick} still holds the render chain",
                refusal: WorldCaptureRefusal.Busy,
                stateHash: stateHash,
                station: row.Station,
                tick: tick
            );

            return;
        }

        if (target.PendingCapturePath is { } busyPath) {
            Refuse(
                detail: $"a capture of {busyPath} still holds the render chain",
                refusal: WorldCaptureRefusal.Busy,
                stateHash: stateHash,
                station: row.Station,
                tick: tick
            );

            return;
        }

        var frameName = string.Concat(
            str0: WorldCaptureRow.CaptureName(
                station: row.Station.Value,
                tick: tick
            ),
            str1: ".png"
        );
        var path = Path.Combine(
            path1: m_directory,
            path2: frameName
        );
        var request = new FrameCaptureRequest(
            path: path,
            tick: m_regionTick
        );

        try {
            target.RequestCapture(request: request);
        } catch (Exception exception) when ((exception is InvalidOperationException or ObjectDisposedException)) {
            Refuse(
                detail: $"the render chain refused the request ({exception.Message})",
                refusal: WorldCaptureRefusal.Failed,
                stateHash: stateHash,
                station: row.Station,
                tick: tick
            );

            return;
        }

        m_pending = new Pending(
            FrameName: frameName,
            Instance: instance,
            Palette: row.Palette,
            Reference: ((instance is null)
                ? null
                : m_sources?.ReferenceOf(instance: instance)),
            Path: path,
            Request: request,
            StateHash: stateHash,
            Station: row.Station,
            Tick: tick
        );
    }
    private static ulong ComputeStateHash(WorldServer server, ulong tick) => WorldStateHashComposition.Hash(
        scope: WorldStateHashScope.Capture,
        server: server,
        tick: tick
    );
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
            var g = pixels[(offset + 1)];
            var b = pixels[(offset + 2)];
            var bestIndex = 0;
            var bestDistance = long.MaxValue;

            for (var swatch = 0; (swatch < swatchMaterial.Length); swatch++) {
                var dr = (r - swatchR[swatch]);
                var dg = (g - swatchG[swatch]);
                var db = (b - swatchB[swatch]);
                var distance = (((((long)dr) * dr) + (((long)dg) * dg)) + (((long)db) * db));

                if (distance < bestDistance) {
                    bestDistance = distance;
                    bestIndex = swatch;
                }
            }

            var key = swatchMaterial[bestIndex].ToString(provider: CultureInfo.InvariantCulture);

            counts[key] = (counts.TryGetValue(
                key: key,
                value: out var existing
            )
                ? (existing + 1)
                : 1
            );
        }

        return counts;
    }
    // The camera position feeding the inside-check is read straight off the document — the same worldPoint a
    // 'select' op's winning case's program anchors at — rather than the compiled render rig, so this needs nothing
    // client-side. Every authored station camera therefore needs a leading 'anchor' op naming a worldPoint subject
    // (directly, or transitively through 'select'); anything else is an honest "cannot resolve" narration.
    private (bool Inside, string? Narration) ProbeCameraInside() {
        if (!TryResolveCameraPosition(
            position: out var position,
            reason: out var reason
        )) {
            return (false, $"could not resolve the active camera position ({reason}) — inside-check unchecked.");
        }

        if (m_server.SolidField is not { } field) {
            return (false, "no field-selecting collision provider is live — inside-check unchecked.");
        }

        var fixedPosition = FixedVector3.FromVector3(value: position);

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
    private static string ToHex(ulong hash) => hash.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );
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

    /// <summary>Resolves the position of the camera the first authored layout slot names, as the inside-check of a
    /// capture armed now reads it: the program's <c>anchor.worldPoint</c>, followed through <c>select</c> ops whose
    /// key reads the server's rows through a state mirror at the tick a capture arms at — the server's last completed
    /// tick, and the engine tick it completed at.</summary>
    /// <param name="position">The resolved world position, or zero when it does not resolve.</param>
    /// <param name="reason">Why the position does not resolve, or the empty string when it does.</param>
    /// <returns><see langword="true"/> when the position resolves.</returns>
    public bool TryResolveCameraPosition(out Vector3 position, out string reason) {
        var definition = m_server.Definition;

        position = default;
        m_state.Install(
            engineTick: m_server.CompletedEngineTicks,
            tick: ((m_server.NextInputTick > 0UL)
                ? (m_server.NextInputTick - 1UL)
                : 0UL)
        );

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
            reason: out reason
        );
    }

    private bool TryResolveProgramPosition(WorldDefinition definition, string programName, int depth, out Vector3 position, out string reason) {
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
            // Read as the presentation's camera reads it: the bound slot's value, eased unless the token asks for
            // .$target, at the armed tick.
            var key = ((long)MathF.Round(x: m_state.Scalar(
                fallback: 0f,
                scalar: select.Key
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
                reason: out reason
            );
        }

        reason = $"camera program '{programName}' authors neither an anchor.worldPoint nor a select op";

        return false;
    }
    // A served request lands only when the frame that served it showed the armed tick. Frames are composed between
    // steps, so the frame that completed the request showed the last tick published before completion was observed.
    private void Finalize(Pending pending, ulong? shownTick) {
        var result = pending.Request.Completion.GetAwaiter().GetResult();

        if (result.Error is DeviceLostException deviceLost) {
            Refuse(
                detail: deviceLost.Message,
                refusal: WorldCaptureRefusal.DeviceLost,
                stateHash: pending.StateHash,
                station: pending.Station,
                tick: pending.Tick
            );

            return;
        }

        if (result.Error is { } error) {
            Refuse(
                detail: $"the capture failed ({error.Message})",
                refusal: WorldCaptureRefusal.Failed,
                stateHash: pending.StateHash,
                station: pending.Station,
                tick: pending.Tick
            );

            return;
        }

        if (shownTick != pending.Tick) {
            // The file on disk shows another tick under this capture's name; remove it so nothing reads it as this one.
            TryDelete(path: pending.Path);
            Refuse(
                detail: $"armed at tick {pending.Tick}, but the frame that served it showed tick {(shownTick?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}",
                refusal: WorldCaptureRefusal.Stale,
                stateHash: pending.StateHash,
                station: pending.Station,
                tick: pending.Tick
            );

            return;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: pending.Path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Refuse(
                detail: $"the capture completed but {pending.Path} could not be read ({exception.Message})",
                refusal: WorldCaptureRefusal.Failed,
                stateHash: pending.StateHash,
                station: pending.Station,
                tick: pending.Tick
            );

            return;
        }

        PngImage image;

        try {
            image = PngDecoder.Decode(pngBytes: bytes);
        } catch (Exception exception) when ((exception is InvalidDataException or EndOfStreamException)) {
            Refuse(
                detail: $"{pending.Path} did not decode as PNG ({exception.Message})",
                refusal: WorldCaptureRefusal.Failed,
                stateHash: pending.StateHash,
                station: pending.Station,
                tick: pending.Tick
            );

            return;
        }

        var verdict = Judge(
            image: image,
            pending: pending,
            regionTick: result.Tick
        );

        Record(entry: new WorldCaptureManifestEntry(
            Census: ComputeCensus(
                image: image,
                palette: pending.Palette
            ),
            Detail: null,
            Frame: pending.FrameName,
            RegionTick: result.Tick,
            Refusal: null,
            SourceVerdict: verdict,
            StateHash: ToHex(hash: pending.StateHash),
            Station: pending.Station.Value,
            Tick: pending.Tick
        ));
    }
    // Holds a landed capture of a source instance to the image its source states it shows, when the source states one:
    // the reference must state the tick the frame was rendered at, and every pixel must match it exactly.
    private WorldCaptureSourceVerdict? Judge(Pending pending, PngImage image, ulong? regionTick) {
        if (pending.Instance is not { } instance) {
            return null;
        }

        var current = m_sources?.ReferenceOf(instance: instance);
        var reference = (pending.Reference ?? current);

        if (reference is null) {
            return null;
        }

        var descriptor = reference.Descriptor;
        var expected = new byte[checked((int)((((ulong)descriptor.Width) * descriptor.Height) * 4UL))];
        var stated = reference.TryWriteReference(
            rgba: expected,
            stamp: out var stamp
        );

        // The source running now, when the one held at arming states another tick: a source rebuilt before it served.
        if (
            (stamp.Tick != regionTick) &&
            (current is not null) &&
            !ReferenceEquals(
                objA: current,
                objB: reference
            )
        ) {
            reference = current;
            descriptor = reference.Descriptor;
            expected = new byte[checked((int)((((ulong)descriptor.Width) * descriptor.Height) * 4UL))];
            stated = reference.TryWriteReference(
                rgba: expected,
                stamp: out stamp
            );
        }

        WorldCaptureSourceVerdict verdict;

        if (!stated) {
            verdict = new(
                Detail: $"source '{instance}' states no image",
                Holds: false
            );
        } else if (stamp.Tick != regionTick) {
            verdict = new(
                Detail: $"source '{instance}' states the image of tick {stamp.Tick}, and the capture shows tick {(regionTick?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}",
                Holds: false
            );
        } else if (
            (image.Width != descriptor.Width) ||
            (image.Height != descriptor.Height)
        ) {
            verdict = new(
                Detail: $"the capture is {image.Width}x{image.Height}, and source '{instance}' is {descriptor.Width}x{descriptor.Height}",
                Holds: false
            );
        } else {
            var result = ImageSourceVerdict.Compare(
                actual: image.RgbaPixels,
                descriptor: descriptor,
                expected: expected
            );

            verdict = new(
                Detail: result.ToString(),
                Holds: result.Holds
            );
        }

        Console.Error.WriteLine(value: $"[captures] {pending.Station} tick {pending.Tick}: verdict {verdict.Detail}.");

        return verdict;
    }
    private void Record(WorldCaptureManifestEntry entry) {
        m_landed.Add(item: entry);
        WriteManifest();
    }
    private void Refuse(CellName station, ulong tick, ulong stateHash, WorldCaptureRefusal refusal, string detail) {
        var kind = JsonNamingPolicy.CamelCase.ConvertName(name: refusal.ToString());

        Console.Error.WriteLine(value: $"[captures] {station} tick {tick}: REFUSED {kind} — {detail}.");
        Record(entry: new WorldCaptureManifestEntry(
            Census: null,
            Detail: detail,
            Frame: null,
            RegionTick: null,
            Refusal: refusal,
            StateHash: ToHex(hash: stateHash),
            Station: station.Value,
            Tick: tick
        ));
    }
    // Observes the outstanding capture at a step boundary. A completed request is settled against the tick its frame
    // showed; an uncompleted one stays armed on the render chain, since only serving or disposal can release it.
    private void Settle() {
        if (
            (m_pending is not { } pending) ||
            !pending.Request.Completion.IsCompleted
        ) {
            return;
        }

        m_pending = null;
        Finalize(
            pending: pending,
            shownTick: m_lastPublishedTick
        );
    }
    private static void TryDelete(string path) {
        try {
            File.Delete(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[captures] could not remove {path} ({exception.Message}).");
        }
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
            Directory.CreateDirectory(path: m_directory);
            File.WriteAllText(
                contents: JsonSerializer.Serialize(
                    options: ManifestSerializerOptions,
                    value: manifest
                ),
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[captures] could not write {path} ({exception.Message}).");
        }
    }
    // Refuses an unserved capture as Unserved and withdraws its request from the render chain, which then drops it, so
    // no later frame writes it and a later capture can arm. A request a frame completed first is settled instead.
    private void Withdraw(Pending pending, string detail) {
        m_pending = null;

        if (!pending.Request.TryFail(error: new OperationCanceledException(message: detail))) {
            Finalize(
                pending: pending,
                shownTick: m_lastPublishedTick
            );

            return;
        }

        Refuse(
            detail: detail,
            refusal: WorldCaptureRefusal.Unserved,
            stateHash: pending.StateHash,
            station: pending.Station,
            tick: pending.Tick
        );
    }

    /// <summary>Settles the outstanding capture as the run ends: after the host's last produced frame, and before it
    /// disposes the render chain, so the capture is decided while the chain that would have served it is alive. A
    /// served request is recorded exactly as a later step boundary would record it; an unserved one is refused as
    /// <see cref="WorldCaptureRefusal.Unserved"/> and withdrawn, since no frame will ever serve it.</summary>
    public void Drain() {
        if (m_pending is not { } pending) {
            return;
        }

        if (pending.Request.Completion.IsCompleted) {
            Settle();

            return;
        }

        Withdraw(
            detail: $"the run ended before any frame served it (last completed tick {(m_lastPublishedTick?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")}){((m_readiness is { IsReady: false } readiness) ? $"; {readiness.NotReadyReason}" : "")}",
            pending: pending
        );
    }
    /// <summary>The authority tick-complete hook: settles the outstanding capture against the frames composed since
    /// the previous tick, then arms every row scheduled at <paramref name="tick"/>. A tick published twice (a rewound
    /// authority timeline) arms nothing the second time.</summary>
    /// <param name="tick">The just-completed simulation tick.</param>
    public void PublishTick(ulong tick) {
        Settle();

        // Still armed after settling: this tick was stepped while an earlier tick's capture was owed its frame.
        if (m_pending is not null) {
            m_work.Count(kind: TicksWhileArmed);
        }

        m_lastPublishedTick = tick;

        _ = m_schedule.Publish(
            fire: static (armed, row) => armed.Scheduler.Arm(
                row: row,
                tick: armed.Tick
            ),
            state: (Scheduler: this, Tick: tick),
            tick: tick
        );
    }
}
