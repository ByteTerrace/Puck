using System.Diagnostics;
using System.Globalization;
using System.Text;
using Puck.SdfVm;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// Times every edit loop from the edit to the presented frames that show it, in the world the first seat's view shows (the
/// boot world, or the world a portal or painting carried the seat into): the boot (from process start), each
/// <c>world.reload</c> (by hand or from the source watch), each <c>world.shaders.reload</c>, and each loop a script
/// begins by name before its own edit (<c>world.iteration begin</c>), such as a row set. A loop passes six stages,
/// each timed in seconds from the edit to the first produced frame that finds it true. <c>first</c>: a frame is
/// produced at all, so the boot's line separates starting the process and composing the renderer from what follows.
/// <c>frame</c>: the frame renders
/// the edited document (or a reloaded kernel) with the program its content calls for, the moment a look value shows.
/// <c>shaded</c>: every shading program the frame runs draws with its own kernel
/// (<see cref="SdfWorldResidency.ShadesWhole"/>), the moment an edited shading program's look shows once its
/// specialization compiles; the line's <c>worst-frame</c> is the longest wait between two produced frames until then,
/// never counting the wait before the first one, so a boot's start is not read as a held frame.
/// <c>shown</c>: every mesh group of that program draws its own geometry at some tier
/// (<see cref="SdfMeshPathBake.DrawsOwn"/>), the moment a changed form shows whole. <c>meshes</c>: the bakes are settled
/// at the detail their demand asks and resident in the mesh region (<see cref="IWorldBakeReadiness.IsResidentSince"/>),
/// counted from the frame the edit reached. <c>lit</c>: the world residency's irradiance volume has converged
/// (<see cref="SdfMeshIrradiance.Converged"/>: no tracing or relighting left, or under a lighting that changes every
/// frame, its geometry settled and its relight keeping up), the settled frame.
/// A loop settles at <c>lit</c> and prints its line on stderr as <c>[world.iteration] …</c>, so a lane or a batch reads
/// every loop from the World's log; <c>world.iteration</c> reads them all, and <c>world.wait iteration</c> waits for the
/// latest. The stages are presentation facts read on the host pump after each produced frame; nothing here touches
/// simulation state.
/// </summary>
/// <remarks>A loop begun while the latest one has not yet reached its frame and was begun by name adopts the edit rather
/// than starting another loop, so a script's <c>begin</c> line names the reload or kernel reload that follows it. Any
/// other new loop supersedes the latest one, whose stages stop there, since the next edit's frames can no longer tell the
/// two apart. The boot loop runs beside them.</remarks>
/// <param name="feed">The published values whose document version, the one the boot authority installed at the last
/// collect, a loop's edit must move past.</param>
public sealed class WorldIterationClock(WorldPresentationFeed feed) : IWorldIterationReadiness {
    // The loops kept for world.iteration besides the boot's.
    private const int Kept = 16;
    // The source work a loop's line counts, as the world.boot ledger counts it: sources compiled, compositions merged
    // and the root members they merged, keyed rows considered on reload, documents read, and validations.
    private static readonly Puck.Abstractions.Counting.WorkKind[] Work = [
        WorldBootWork.Compiles,
        WorldBootWork.Compositions,
        WorldBootWork.CompositionMembers,
        WorldBootWork.ReloadRowsMerged,
        WorldBootWork.DocumentsRead,
        WorldBootWork.Validations,
        WorldBootWork.CompileStampReads,
    ];

    private readonly List<Loop> m_loops = [];

    private Loop? m_boot;
    private long m_count;
    private WorldRenderProbe? m_probe;
    private IWorldBakeReadiness? m_bakes;
    private Func<SdfWorldResidency?>? m_presented;

    /// <inheritdoc/>
    public long Loops => m_count;

    // One loop: when it began, what its edit must move past, and the timestamp each stage first held.
    private sealed class Loop(long number, string name, long started, WorldDocumentVersion after, long shaderInstalls, bool named) {
        public long Number { get; } = number;
        public string Name { get; } = name;
        public long Started { get; } = started;
        public WorldDocumentVersion After { get; } = after;
        public long ShaderInstalls { get; } = shaderInstalls;
        public bool Named { get; } = named;
        // For a live state edit, the simulation tick the loop began at: a frame carries the edit once the feed has
        // collected a tick after it. Null for a document or kernel edit.
        public ulong? LiveTick { get; init; }
        // For a hot-reload edit, the code revision it applied: a frame carries the edit once the composition emitted under
        // it. Null for any other edit.
        public int? Code { get; init; }
        public long ShaderRequest { get; set; }
        // The first produced frame observed since the loop began.
        public long? First { get; set; }
        public long? Frame { get; set; }
        public long? Shaded { get; set; }
        // The longest wait between two produced frames, or from the edit to the first frame after it, until the shaded
        // stage held, in stopwatch ticks. The wait before the first frame the host ever produced is never a sample.
        public long WorstFrame { get; set; }
        public long? Shown { get; set; }
        public long? Meshes { get; set; }
        public long? Lit { get; set; }
        // The bake readiness's frame count when the frame stage held: the mark the meshes stage measures from.
        public long ContentMark { get; set; }
        // The irradiance volume's lighting matches and captures when the frame stage held: the marks the lit stage
        // measures from.
        public long LightingMark { get; set; }
        public int CaptureMark { get; set; }
        public long PresentedAtStart { get; set; } = -1;
        public long Presented { get; set; }
        public long BuildsAtStart { get; set; } = -1;
        public long Builds { get; set; }
        public long AppearancesAtStart { get; set; } = -1;
        public long Appearances { get; set; }
        // The ledger's Work counts when the loop began (zero for the boot), and when it settled.
        public long[] WorkAtStart { get; init; } = new long[Work.Length];
        public long[]? WorkAtLit { get; set; }
        // Why the loop stopped before it settled, or null while it runs or once it settled.
        public string? Ended { get; set; }
        public bool Open => ((Lit is null) && (Ended is null));
    }

    /// <summary>Attaches the renderer whose frames the loops are timed against, and begins the boot loop from the
    /// process's start if it has not begun. Call on the host pump when the render root is composed.</summary>
    /// <param name="probe">The render probe holding the world residency, the composition and the render graph's
    /// root.</param>
    /// <param name="bakes">The presentation's mesh-path bake readiness.</param>
    /// <param name="presented">Returns the residency the first seat's view renders through now, the world's own or a
    /// routed scene's, or <see langword="null"/> for the world's own; <see langword="null"/> to time against the world's
    /// own residency alone.</param>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> or <paramref name="bakes"/> is
    /// <see langword="null"/>.</exception>
    public void Attach(WorldRenderProbe probe, IWorldBakeReadiness bakes, Func<SdfWorldResidency?>? presented = null) {
        ArgumentNullException.ThrowIfNull(argument: probe);
        ArgumentNullException.ThrowIfNull(argument: bakes);
        m_probe = probe;
        m_bakes = bakes;
        m_presented = presented;
        m_boot ??= new Loop(after: default, name: "boot", named: false, number: ++m_count, shaderInstalls: 0L, started: Puck.Abstractions.ProcessClock.StartTimestamp);
    }

    /// <summary>Begins a loop for an edit about to be made, timed from now. Call on the host pump before the edit is
    /// submitted.</summary>
    /// <param name="name">The loop's name, as <c>world.iteration</c> reports it.</param>
    /// <param name="named">Whether a script named the loop (<c>world.iteration begin</c>), so the reload or kernel reload
    /// that follows joins it; <see langword="false"/> for a loop an edit verb begins itself.</param>
    /// <param name="shaderRequest">The kernel reload request the edit made, or zero for a document edit.</param>
    /// <param name="live">Whether the edit is a live state edit (a state cell a rule or a light reads), which moves no
    /// document: its frame stage holds once a frame presents a tick after the one the loop began at.</param>
    /// <returns>The loop's number.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public long Begin(string name, bool named, long shaderRequest = 0L, bool live = false) => BeginAt(code: null, live: live, name: name, named: named, shaderRequest: shaderRequest, started: Stopwatch.GetTimestamp());

    // Begins a loop timed from a timestamp; a hot-reload edit (code) reaches its frame once the composition emitted under
    // its revision.
    private long BeginAt(string name, bool named, long shaderRequest, bool live, long started, int? code) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        if (!named && (Latest is { Named: true, Frame: null, Ended: null } joined)) {
            if (shaderRequest > 0L) { joined.ShaderRequest = shaderRequest; }
            return joined.Number;
        }
        if (Latest is { Open: true } superseded) {
            superseded.Ended = $"superseded by #{m_count + 1} {name}";
        }

        var presented = Presented();
        var loop = new Loop(
            // The document the presented frame carries: a seat standing in another world shows that world's authority's
            // documents, whose versions the boot authority's never cover.
            after: ((presented?.Frame?.DiagnosticSource is WorldDocumentVersion shown) ? shown : feed.Tick.DocumentVersion),
            name: name,
            named: named,
            number: ++m_count,
            shaderInstalls: (presented?.ShaderBatchesInstalled ?? 0L),
            started: started
        ) { Code = code, LiveTick = (live ? feed.Tick.Tick : null), ShaderRequest = shaderRequest, PresentedAtStart = (m_probe?.Root?.FramesProduced ?? -1L), BuildsAtStart = (m_probe?.Composition?.ProgramBuilds ?? -1L), AppearancesAtStart = (m_probe?.Composition?.AppearanceBuilds ?? -1L), WorkAtStart = ReadWork() };

        m_loops.Add(item: loop);
        if (m_loops.Count > Kept) { m_loops.RemoveAt(index: 0); }
        return loop.Number;
    }

    /// <summary>Ends a loop whose edit was refused, so no frame will ever show it, naming the refusal. A loop that has
    /// already reached its frame or stopped is left as it stands. Call on the host pump.</summary>
    /// <param name="number">The loop's number, as <see cref="Begin"/> returned it.</param>
    /// <param name="refusal">The refusal's text.</param>
    public void Refuse(long number, string refusal) => End(number: number, reason: $"refused: {refusal}");
    /// <summary>Ends a loop whose edit will never reach a frame (a refusal, a source unchanged since the revision the
    /// authority holds), naming why. A loop that has already reached its frame or stopped is left as it stands. Call on
    /// the host pump.</summary>
    /// <param name="number">The loop's number, as <see cref="Begin"/> returned it.</param>
    /// <param name="reason">Why the loop ended.</param>
    public void End(long number, string reason) {
        if ((Latest is { Number: var latest, Frame: null, Ended: null } loop) && (latest == number)) {
            loop.Ended = reason;
        }
    }

    private Loop? Latest => ((m_loops.Count > 0) ? m_loops[^1] : null);
    // The residency the first seat's view renders through, the world's own when no other answers.
    private SdfWorldResidency? Presented() => (m_presented?.Invoke() ?? m_probe?.Residency);
    // The composition a residency films: a routed scene's own, or the world's for the world's residency.
    private static SdfCompositionFrameSource? CompositionOf(SdfWorldResidency residency, WorldRenderProbe probe) =>
        ((residency.FrameSource as SdfCompositionFrameSource) ?? (ReferenceEquals(objA: residency, objB: probe.Residency) ? probe.Composition : null));

    /// <summary>Advances every open loop over the frame just produced. Call on the host pump after the render graph's
    /// root produces a frame; allocates nothing while no stage changes.</summary>
    public void ObserveFrame() {
        if ((m_probe is not { } probe) || (m_bakes is not { } bakes)) { return; }
        var now = Stopwatch.GetTimestamp();
        var previous = m_lastFrame;

        m_lastFrame = now;
        // A hot-reload edit begins its loop at the first frame after the runtime applied it, timed from the application.
        if (Puck.Abstractions.CodeRevision.Current is var code && (code != m_code)) {
            m_code = code;
            _ = BeginAt(code: code, live: false, name: "hot-reload", named: false, shaderRequest: 0L, started: Puck.Abstractions.CodeRevision.AppliedAt);
        }
        if (m_boot is { Open: true } boot) { Advance(bakes: bakes, loop: boot, now: now, previous: previous, probe: probe); }
        if (Latest is { Open: true } latest) { Advance(bakes: bakes, loop: latest, now: now, previous: previous, probe: probe); }
    }

    // When the latest produced frame was observed, zero before the first.
    private long m_lastFrame;
    // The code revision the latest hot-reload loop began at, or the process's own before any.
    private int m_code = Puck.Abstractions.CodeRevision.Current;

    private void Advance(Loop loop, long now, long previous, WorldRenderProbe probe, IWorldBakeReadiness bakes) {
        loop.First ??= now;
        // With no frame before this one, the wait is the first stage's (the boot's, from process start), never a held
        // frame.
        if ((loop.Shaded is null) && (previous != 0L)) { loop.WorstFrame = Math.Max(val1: loop.WorstFrame, val2: (now - Math.Max(val1: previous, val2: loop.Started))); }
        if ((Presented() is not { } residency) || (CompositionOf(probe: probe, residency: residency) is not { } composition)) { return; }
        if (loop.PresentedAtStart < 0) { loop.PresentedAtStart = (probe.Root?.FramesProduced ?? 0L); }
        if (loop.BuildsAtStart < 0) { loop.BuildsAtStart = composition.ProgramBuilds; }
        if (loop.AppearancesAtStart < 0) { loop.AppearancesAtStart = composition.AppearanceBuilds; }
        loop.Presented = ((probe.Root?.FramesProduced ?? 0L) - loop.PresentedAtStart);
        loop.Builds = (composition.ProgramBuilds - loop.BuildsAtStart);
        loop.Appearances = (composition.AppearanceBuilds - loop.AppearancesAtStart);

        if (loop.Frame is null) {
            if (EndedUnchanged(loop: loop, residency: residency)) { return; }
            if (!probe.IsReady || (residency.Frame is not { } frame) || !Reaches(composition: composition, frame: frame, loop: loop, residency: residency)) { return; }
            if (composition.Building || !ReferenceEquals(objA: frame.Program, objB: composition.Program)) { return; }
            loop.Frame = now;
            loop.ContentMark = bakes.Frames;
            loop.LightingMark = residency.MeshIrradiance.LightingMatches;
            loop.CaptureMark = residency.MeshIrradiance.LightingCaptures;
        }
        // The edit's look: every shading program the frame runs draws with its own kernel, not as a material with no
        // program while the specialization that holds it compiles.
        if (loop.Shaded is null) {
            if (!residency.ShadesWhole) { return; }
            loop.Shaded = now;
        }
        if (loop.Shown is null) {
            if (!ReferenceEquals(objA: residency.MeshPathBake.Program, objB: composition.Program) || !residency.MeshPathBake.DrawsOwn) { return; }
            loop.Shown = now;
        }
        if (loop.Meshes is null) {
            if (!bakes.IsResidentSince(mark: loop.ContentMark) || ShaderPending(loop: loop, residency: residency)) { return; }
            loop.Meshes = now;
        }
        if (!Lit(loop: loop, volume: residency.MeshIrradiance)) { return; }
        loop.Lit = now;
        loop.WorkAtLit = ReadWork();
        Console.Error.WriteLine(value: $"[world.iteration] {Line(loop: loop, probe: probe)}");
    }

    // The lighting captures after the frame stage that mark a lighting changing every frame (a day clock): then the volume
    // is lit once its relight keeps within its age bound (SdfMeshIrradiance.Converged); a lighting that came to rest after
    // the edit is lit only once the volume settles.
    private const int MovingCaptures = 8;

    // Whether the volume shows the loop's light: it answered a frame after the one that carried the edit, so it captured
    // the edit's lighting, and it settled, or under a lighting still moving since, its relight keeps within its age bound.
    private static bool Lit(Loop loop, SdfMeshIrradiance volume) =>
        (volume.LightingMatches > loop.LightingMark) && (!volume.Settling || (volume.Converged && ((volume.LightingCaptures - loop.CaptureMark) >= MovingCaptures)));

    // Whether the frame carries the loop's edit: a document past the one installed when it began (any delivered document
    // for the boot), a kernel batch installed since, for a live state edit a tick collected after the one it began at, or for
    // a hot-reload edit a program every emitter emitted under its code revision.
    private bool Reaches(SdfFrame frame, Loop loop, SdfWorldResidency residency, SdfCompositionFrameSource composition) =>
        ((frame.DiagnosticSource is WorldDocumentVersion version) && version.IsDelivered && !loop.After.Covers(other: version)) ||
        (residency.ShaderBatchesInstalled > loop.ShaderInstalls) || ((loop.LiveTick is { } live) && (feed.Tick.Tick > (live + 1UL))) ||
        ((loop.Code is { } code) && (composition.BuiltCodeRevision >= code));

    // Ends a loop whose only edit is a kernel reload that changed no pipeline or failed: no frame will ever show it.
    private static bool EndedUnchanged(Loop loop, SdfWorldResidency residency) {
        if ((loop.ShaderRequest <= 0L) || (residency.ShaderBatchesInstalled > loop.ShaderInstalls)) { return false; }
        var status = residency.ShaderReloadStatus;

        if ((status.RequestId != loop.ShaderRequest) || (status.State == "pending")) { return false; }
        loop.Ended = $"the kernel reload was {status.State}{((status.Error is { } error) ? $": {error}" : string.Empty)}";
        return true;
    }

    private static long[] ReadWork() => [.. Work.Select(selector: static kind => WorldBootWork.Process.Read(kind: kind))];

    private static bool ShaderPending(Loop loop, SdfWorldResidency residency) =>
        ((loop.ShaderRequest > 0L) && (residency.ShaderReloadStatus is { State: "pending" } status) && (status.RequestId == loop.ShaderRequest));

    /// <inheritdoc/>
    public bool IsSettledSince(long mark) => ((Latest ?? m_boot) is { } latest) && (latest.Number >= mark) && (latest.Lit is not null);

    /// <inheritdoc/>
    public string? Describe() => (((Latest ?? m_boot) is { } latest) && (m_probe is { } probe)) ? Line(loop: latest, probe: probe) : null;

    /// <summary>Returns the report <c>world.iteration</c> answers: one line for the boot loop and one for each kept loop,
    /// oldest first.</summary>
    /// <returns>The lines, separated by <c>\n</c>.</returns>
    public string Report() {
        if (m_probe is not { } probe) {
            return "[world.iteration: this host composes no renderer, so no edit loop reaches a frame]";
        }

        var text = new StringBuilder();

        foreach (var loop in ((m_boot is { } boot) ? m_loops.Prepend(element: boot) : m_loops)) {
            _ = text.Append(value: (text.Length == 0) ? string.Empty : "\n").Append(value: "[world.iteration: ").Append(value: Line(loop: loop, probe: probe)).Append(value: ']');
        }
        return ((text.Length == 0) ? "[world.iteration: no loop has begun]" : text.ToString());
    }

    // A loop's line: its number and name, then each stage's seconds from the edit, the first stage still open naming
    // what it waits on, then the frames presented and programs built since the edit, and the source work counted from
    // the edit to the settled frame (to now, while the loop is open), which a loop running beside it also counts.
    private string Line(Loop loop, WorldRenderProbe probe) {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        var waiting = false;

        _ = text.Append(culture, $"#{loop.Number} {loop.Name}");
        Stage(name: "first", at: loop.First, why: static () => "no frame has been produced since the loop began");
        Stage(name: "frame", at: loop.Frame, why: () => FrameWaits(loop: loop, probe: probe));
        Stage(name: "shaded", at: loop.Shaded, why: static () => "a shading program draws as a material with no program while the specialization that holds it compiles");
        Stage(name: "shown", at: loop.Shown, why: static () => "a mesh group still draws the geometry before the edit, or nothing, while it bakes");
        Stage(name: "meshes", at: loop.Meshes, why: () => (m_bakes?.PendingResidencyReasonSince(mark: loop.ContentMark) ?? "the kernel reload is still installing"));
        Stage(name: "lit", at: loop.Lit, why: static () => "the irradiance volume is still tracing, or its relight trails the lighting past its age bound");
        _ = text.Append(culture, $" presented={loop.Presented} builds={loop.Builds} appearances={loop.Appearances} worst-frame={Stopwatch.GetElapsedTime(startingTimestamp: 0L, endingTimestamp: loop.WorstFrame).TotalMilliseconds:0.0}ms");

        var work = (loop.WorkAtLit ?? ReadWork());

        for (var index = 0; (index < Work.Length); index++) {
            _ = text.Append(culture, $" {Work[index].Name["world.boot.".Length..]}={work[index] - loop.WorkAtStart[index]}");
        }
        if (loop.Ended is { } ended) { _ = text.Append(culture, $" ended=({ended})"); }
        return text.ToString();

        void Stage(string name, long? at, Func<string> why) {
            if (at is { } timestamp) {
                _ = text.Append(culture, $" {name}={Stopwatch.GetElapsedTime(startingTimestamp: loop.Started, endingTimestamp: timestamp).TotalSeconds:0.000}s");
            } else if (!waiting) {
                waiting = true;
                _ = text.Append(culture, $" {name}=pending{((loop.Ended is null) ? $" ({why()})" : string.Empty)}");
            }
        }
    }

    // What the frame stage waits on.
    private static string FrameWaits(Loop loop, WorldRenderProbe probe) =>
        (!probe.IsReady ? (probe.NotReadyReason ?? "the engine is not ready")
        : (probe.Composition is { Building: true }) ? "the edited content's program is building"
        : (loop.ShaderRequest > 0L) ? "no reloaded kernel has installed"
        : "no frame has rendered a document past the one installed when the loop began");
}
