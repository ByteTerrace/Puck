using System.CommandLine;
using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using Puck.SdfVm;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo.SdfDebug;

/// <summary>
/// The <c>sdf.*</c> console verbs — the control plane for the fullscreen SDF-debug mode (the first deliverable of the
/// SDF-VM accuracy/robustness arc). <c>sdf</c> toggles the mode; <c>sdf.shape</c>/<c>sdf.lift</c> pick the debuggable
/// primitive; <c>sdf.op</c>(<c>.pop</c>/<c>.clear</c>/<c>.list</c>) manage a stackable modifier list; <c>sdf.floor</c>
/// toggles a ground plane; <c>sdf.info</c> prints the program facts (word/instance count, the baked Lipschitz
/// <see cref="SdfProgram.StepScale"/>, and per-pass GPU ms). View shading is the existing global <c>debug.view.*</c>
/// verbs (now including <c>termination</c> and <c>slice</c>). The debug scene is reached through
/// <see cref="ICreatorModeHost.CreatorFrameSource"/>, every authoring surface's composition point; the mode flip + the
/// timings read go through the host directly. Usage-string-on-bad-input, never throws.
/// </summary>
internal sealed class SdfDebugCommandModule(IRenderNode rootNode) : ICommandModule {
    private readonly ICreatorModeHost? m_host = (rootNode as ICreatorModeHost);

    private SdfDebugMode? Mode => m_host?.CreatorFrameSource?.SdfDebug;
    private SdfDebugScene? Scene => m_host?.CreatorFrameSource?.SdfDebug.Scene;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Plain(
            description: "Toggles the fullscreen SDF-debug mode (one shape at the origin; mutually exclusive with creator/world/tracker).",
            handler: _ => new CommandResult(m_host?.ToggleSdfDebugMode() ?? "[sdf: unavailable — the overworld is not the active root]"),
            name: "sdf"
        );
        yield return WithArgs(
            description: "Selects the debug primitive with optional params: sdf.shape <name> [params...]. Names: sphere, box, torus, capsule, cylinder, ellipsoid, vesica, round-cone, rounded-rect, polygon, star, trapezoid, ellipse (the last five take sdf.lift).",
            handler: WithSceneArgs(handler: HandleShape),
            name: "sdf.shape"
        );
        yield return WithArgs(
            description: "Sets the 2D-family lift used by rounded-rect/polygon/star/trapezoid/ellipse: sdf.lift revolve|extrude [amount].",
            handler: WithSceneArgs(handler: HandleLift),
            name: "sdf.lift"
        );
        yield return WithArgs(
            description: "Adds/replaces a SECOND shape (the blend-debugging partner; same catalog as sdf.shape): sdf.shape2 <name> [params...]. It composes against shape 1 through sdf.blend; the op stack stays shape 1's.",
            handler: WithSceneArgs(handler: HandleShape2),
            name: "sdf.shape2"
        );
        yield return Plain(
            description: "Removes the second shape (back to the single subject).",
            handler: WithScene(handler: static scene => (scene.ClearShape2() ? $"[sdf.shape2.off] {Summary(scene: scene)}" : "[sdf.shape2.off: no second shape]")),
            name: "sdf.shape2.off"
        );
        yield return WithArgs(
            description: "Sets the second shape's translation: sdf.offset2 <x y z> (default 1.2 0 0).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (!TryParseFloats(args: args, count: 3, start: 0, values: out var xyz)) {
                    return "[sdf.offset2: usage — sdf.offset2 <x y z>]";
                }

                scene.SetOffset2(offset: new Vector3(xyz[0], xyz[1], xyz[2]));

                return $"[sdf.offset2] {Summary(scene: scene)}";
            }),
            name: "sdf.offset2"
        );
        yield return WithArgs(
            description: "Sets how shape 2 composes against shape 1: sdf.blend <union|smooth|subtract|smooth-subtract|intersect|smooth-intersect|chamfer|chamfer-intersect|chamfer-subtract|xor> [smooth-k].",
            handler: WithSceneArgs(handler: HandleBlend),
            name: "sdf.blend"
        );
        yield return WithArgs(
            description: "Positions the slice view's plane: sdf.slice x|y|z [offset] pins a world-axis plane; sdf.slice camera returns to the camera-locked plane through the origin.",
            handler: WithSceneArgs(handler: HandleSlice),
            name: "sdf.slice"
        );
        yield return WithArgs(
            description: "Pushes a modifier onto the stack: sdf.op <name> [args...]. Names: twist, bend-x/y/z, scale, elongate, repeat, repeat-limited, polar, symmetry, logsphere, celljitter, domainwarp (point ops); onion, dilate, displace (field ops).",
            handler: WithSceneArgs(handler: HandleOp),
            name: "sdf.op"
        );
        yield return Plain(
            description: "Pops the last-pushed modifier.",
            handler: WithScene(handler: static scene => (scene.PopOp() ? $"[sdf.op.pop] {Summary(scene: scene)}" : "[sdf.op.pop: the stack is empty]")),
            name: "sdf.op.pop"
        );
        yield return Plain(
            description: "Clears the whole modifier stack.",
            handler: WithScene(handler: static scene => { var n = scene.ClearOps(); return $"[sdf.op.clear] removed {n} op(s) — {Summary(scene: scene)}"; }),
            name: "sdf.op.clear"
        );
        yield return Plain(
            description: "Lists the modifier stack in emission order (point ops before the shape, field ops after).",
            handler: WithScene(handler: static scene => ListOps(scene: scene)),
            name: "sdf.op.list"
        );
        yield return WithArgs(
            description: "Appends a CARVE — a sphere SUBTRACTED from the assembled subject+floor field (emitted last, so it bites both): sdf.carve <x> <y> <z> [r] [smooth [k]]. Default radius ~0.35; 'smooth' makes it a SmoothSubtraction (default k ~0.15). The rebuild-on-append IS the replay model — a scripted carve sequence reproduces deterministically.",
            handler: WithSceneArgs(handler: HandleCarve),
            name: "sdf.carve"
        );
        yield return Plain(
            description: "Removes the last-appended carve.",
            handler: WithScene(handler: static scene => (scene.PopCarve() ? $"[sdf.carve.pop] carves={scene.Carves.Count} — {Summary(scene: scene)}" : "[sdf.carve.pop: the pool is empty]")),
            name: "sdf.carve.pop"
        );
        yield return Plain(
            description: "Clears the whole carve pool.",
            handler: WithScene(handler: static scene => { var n = scene.ClearCarves(); return $"[sdf.carve.clear] removed {n} carve(s) — {Summary(scene: scene)}"; }),
            name: "sdf.carve.clear"
        );
        yield return WithArgs(
            description: "Rains a METEOR SHOWER on the subject: sdf.meteors [n|off]. One impact lands per produced frame (default 128) — each an ordinary pool carve at a deterministic low-discrepancy point (floor craters around the subject, every 3rd biting the subject, every 7th a smooth molten hit), so a scripted shower replays bit-for-bit and the craters persist as carve-pool data (sdf.carve.clear resets the field).",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if ((args.Length > 0) && string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase)) {
                    var cancelled = scene.StopMeteors();

                    return $"[sdf.meteors off — {cancelled} unfallen impact(s) cancelled; {scene.Carves.Count} crater(s) stay]";
                }

                var requested = (((args.Length > 0) && int.TryParse(s: args[0], style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var n)) ? n : 128);
                var scheduled = scene.StartMeteors(count: requested);

                return ((scheduled > 0)
                    ? $"[sdf.meteors {scheduled} — the sky darkens (one impact per frame; sdf.meteors off to stop; carves={scene.Carves.Count}/{SdfDebugScene.MaxCarves})]"
                    : $"[sdf.meteors: no room — the pool holds {scene.Carves.Count}/{SdfDebugScene.MaxCarves} (sdf.carve.clear first)]");
            }),
            name: "sdf.meteors"
        );
        yield return WithArgs(
            description: "Toggles the ground plane under the subject (default OFF — it contaminates the slice/iteration views): sdf.floor on|off.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                var on = ((args.Length > 0) && ParseOnOff(token: args[0]));

                scene.SetFloor(on: on);

                return $"[sdf.floor {(on ? "on" : "off")}] {Summary(scene: scene)}";
            }),
            name: "sdf.floor"
        );
        yield return WithArgs(
            description: "Toggles the scoped field accumulator: sdf.scope on|off. ON (default) shells the SUBJECT alone with a field op (onion/dilate/displace) and leaves the floor solid; OFF shares one flat field so a field op shells the WHOLE scene, floor included — the contrast is the tool.",
            handler: WithSceneArgs(handler: static (scene, args) => {
                if (args.Length == 0) {
                    return $"[sdf.scope {(scene.Scope ? "on" : "off")}] {Summary(scene: scene)} — sdf.scope on|off";
                }

                var on = ParseOnOff(token: args[0]);

                scene.SetScope(on: on);

                return $"[sdf.scope {(on ? "on" : "off")}] {Summary(scene: scene)}";
            }),
            name: "sdf.scope"
        );
        yield return WithArgs(
            description: "Toggles the beam prepass's uniform-grid instance cull for the mode's programs: sdf.grid on|off. ON (default) packs the world-space grid the beam walks; OFF packs a disabled grid so the beam flat-loops every instance — the live A/B lever for grid-vs-flat beam measurement (pair with sdf.bench). No args echoes the current state.",
            handler: (context, args) => new CommandResult(HandleGrid(args: args)),
            name: "sdf.grid"
        );
        yield return WithArgs(
            description: "Selects the lit surface-normal probe: sdf.normals taps|analytic. ANALYTIC (default) is the forward-mode gradient dual — one dual field eval at the hit, exact through the transform chain and free of finite-difference banding; TAPS is the legacy 4-tap probe, kept for the A/B lever (pair with debug.view.normals). No args echoes the current mode.",
            handler: (context, args) => new CommandResult(HandleNormals(args: args)),
            name: "sdf.normals"
        );
        yield return WithArgs(
            description: "Toggles the soft-shadow grid cull: sdf.shadowcull on|off. ON (default) gathers each lit pixel's shadow-ray grid neighborhood and marches only those instances — bit-identical to the flat all-instances shadow but far cheaper on spread scenes (and correct for occluders outside the camera cone); OFF marches every instance (the flat reference, the A/B lever for shadow-cull measurement — pair with sdf.bench). No args echoes the current state.",
            handler: (context, args) => new CommandResult(HandleShadowCull(args: args)),
            name: "sdf.shadowcull"
        );
        yield return WithArgs(
            description: "Poses the orbit camera deterministically (the scriptable form of the pad orbit): sdf.cam [pitch <p>] [yaw <y>] [dist <d>] [target <x y z>]. Each clause is optional and keeps the current value; pitch clamps to [0.05, 1.35] (low = grazing), dist to [0.5, 14]. Use a low pitch + far target to graze the ground behind a tall occluder.",
            handler: (context, args) => new CommandResult(HandleCam(args: args)),
            name: "sdf.cam"
        );
        yield return Plain(
            description: "Prints the debug subject's facts: shape, op stack, scope state, program word/instance count, the baked Lipschitz step scale, and per-pass GPU ms (PUCK_TIMING=1).",
            handler: _ => new CommandResult(DescribeInfo()),
            name: "sdf.info"
        );
        yield return WithArgs(
            description: "The SDF perf-bench (needs the sdf mode active + PUCK_TIMING=1): sdf.bench shapes | ops | carves | storm | instances <shape> <n> | sweep [shape] (ladder 64/256/1024/4096) | warm <n> | frames <n> | abort. storm = the motion/churn ladder (moving instances + per-frame rebuild + a camera sweep). Runs async across frames — progress + a fixed-width table print to stdout (step past it).",
            handler: (context, args) => new CommandResult(HandleBench(args: args)),
            name: "sdf.bench"
        );
        yield return WithArgs(
            description: "The SDF torture museum — a curated tour of known-nasty scenes INSIDE the sdf debug mode, each printing a stdout PLAQUE (what to look for, what's settled, which gate/doc owns it). sdf.gallery enters then advances; sdf.gallery <name|index> jumps; sdf.gallery off exits to the plain subject; sdf.gallery list names them. Exhibits: liar-spiral, droste, celljitter, notch, smooth-chain, wallpaper-p4g, carve-ceiling, logsphere-rundoc.",
            handler: (context, args) => new CommandResult(HandleGallery(args: args)),
            name: "sdf.gallery"
        );
    }

    // The torture museum's control plane (SdfDebugMode.Gallery). Like sdf.shape, the gallery state configures freely and
    // renders only while the sdf mode is up — a nudge is appended when the tour is armed but the mode is down.
    private string HandleGallery(string[] args) {
        if (Mode is not { } mode) {
            return "[sdf.gallery: unavailable — the overworld is not the active root]";
        }

        var gallery = mode.Gallery;
        string status;

        if (args.Length == 0) {
            status = gallery.EnterOrAdvance();
        }
        else {
            switch (args[0].ToLowerInvariant()) {
                case "off":
                    return gallery.Off();
                case "list":
                    return gallery.List();
                default:
                    status = (TryParseInt(text: args[0], value: out var index) ? gallery.Jump(index: index) : gallery.JumpByName(name: args[0]));

                    break;
            }
        }

        return ((!mode.Active && gallery.Active) ? $"{status} (run `sdf` to view)" : status);
    }

    // The uniform-grid instance cull's live A/B toggle (SdfDebugMode.SetGridCull → the frame source's Build(
    // buildInstanceGrid:) + a revision bump, so the next frame's takeover program packs the new state).
    private string HandleGrid(string[] args) {
        if (Mode is not { } mode) {
            return "[sdf.grid: unavailable — the overworld is not the active root]";
        }

        if (args.Length == 0) {
            return $"[sdf.grid {(mode.GridCull ? "on" : "off")}] — sdf.grid on|off (the beam's uniform-grid instance cull; off = the flat per-instance loop)";
        }

        var on = ParseOnOff(token: args[0]);

        mode.SetGridCull(on: on);

        return $"[sdf.grid {(on ? "on" : "off")}]{(on ? string.Empty : " — the beam flat-loops every instance (the pre-grid reference path)")}";
    }

    // The analytic-vs-taps surface-normal A/B lever (SdfDebugMode.SetFiniteDifferenceNormals → the frame's
    // UseFiniteDifferenceNormals flag; a pure frame channel, no program rebuild).
    private string HandleNormals(string[] args) {
        if (Mode is not { } mode) {
            return "[sdf.normals: unavailable — the overworld is not the active root]";
        }

        if (args.Length == 0) {
            return $"[sdf.normals {(mode.UseFiniteDifferenceNormals ? "taps" : "analytic")}] — sdf.normals taps|analytic (analytic = the forward-mode gradient dual)";
        }

        switch (args[0].ToLowerInvariant()) {
            case "taps":
            case "fd":
                mode.SetFiniteDifferenceNormals(useTaps: true);
                return "[sdf.normals taps] — the legacy 4-tap finite-difference probe (the A/B reference)";
            case "analytic":
            case "dual":
                mode.SetFiniteDifferenceNormals(useTaps: false);
                return "[sdf.normals analytic] — the forward-mode gradient dual (one dual eval at the hit)";
            default:
                return $"[sdf.normals: '{args[0]}' — taps|analytic]";
        }
    }

    // The soft-shadow grid-cull A/B lever (SdfDebugMode.SetShadowCull → the frame's DisableShadowCull flag; a pure frame
    // channel, no program rebuild — the grid the shadow march walks is the SAME one the beam already packs).
    private string HandleShadowCull(string[] args) {
        if (Mode is not { } mode) {
            return "[sdf.shadowcull: unavailable — the overworld is not the active root]";
        }

        if (args.Length == 0) {
            return $"[sdf.shadowcull {(mode.ShadowCull ? "on" : "off")}] — sdf.shadowcull on|off (the soft-shadow march's grid cull; off = the flat all-instances reference)";
        }

        var on = ParseOnOff(token: args[0]);

        mode.SetShadowCull(on: on);

        return $"[sdf.shadowcull {(on ? "on" : "off")}]{(on ? string.Empty : " — the shadow march evaluates every instance (the flat reference path)")}";
    }

    private string HandleCam(string[] args) {
        if (Mode is not { } mode) {
            return "[sdf.cam: unavailable — the overworld is not the active root]";
        }

        if (args.Length == 0) {
            return "[sdf.cam: sdf.cam [pitch <p>] [yaw <y>] [dist <d>] [target <x y z>] — low pitch (~0.08) grazes; keeps unspecified values]";
        }

        float? pitch = null;
        float? yaw = null;
        float? distance = null;
        Vector3? target = null;

        for (var index = 0; (index < args.Length); index++) {
            switch (args[index].ToLowerInvariant()) {
                case "pitch":
                    if (((index + 1) >= args.Length) || !TryParseFloat(text: args[++index], value: out var p)) {
                        return "[sdf.cam: pitch wants one number]";
                    }
                    pitch = p;
                    break;
                case "yaw":
                    if (((index + 1) >= args.Length) || !TryParseFloat(text: args[++index], value: out var y)) {
                        return "[sdf.cam: yaw wants one number]";
                    }
                    yaw = y;
                    break;
                case "dist":
                case "distance":
                    if (((index + 1) >= args.Length) || !TryParseFloat(text: args[++index], value: out var d)) {
                        return "[sdf.cam: dist wants one number]";
                    }
                    distance = d;
                    break;
                case "target":
                    if (!TryParseFloats(args: args, count: 3, start: (index + 1), values: out var xyz)) {
                        return "[sdf.cam: target wants three numbers — target <x y z>]";
                    }
                    target = new Vector3(xyz[0], xyz[1], xyz[2]);
                    index += 3;
                    break;
                default:
                    return $"[sdf.cam: '{args[index]}' — expected pitch|yaw|dist|target]";
            }
        }

        mode.PoseCamera(pitch: pitch, yaw: yaw, distance: distance, target: target);

        return $"[sdf.cam pitch={(pitch is { } pv ? pv.ToString("0.###", CultureInfo.InvariantCulture) : "·")} yaw={(yaw is { } yv ? yv.ToString("0.###", CultureInfo.InvariantCulture) : "·")} dist={(distance is { } dv ? dv.ToString("0.###", CultureInfo.InvariantCulture) : "·")} target={(target is { } tv ? $"{tv.X:0.##},{tv.Y:0.##},{tv.Z:0.##}" : "·")}]";
    }

    private string HandleBench(string[] args) {
        if (Mode is not { } mode) {
            return "[sdf.bench: unavailable — the overworld is not the active root]";
        }

        var bench = mode.Bench;

        if (args.Length == 0) {
            return $"[sdf.bench: shapes | ops | carves | storm | instances <shape> <n> | sweep [shape] | warm <n> | frames <n> | abort — warm={bench.WarmFrames} samples={bench.SampleFrames}]";
        }

        switch (args[0].ToLowerInvariant()) {
            case "abort":
                return bench.Abort();
            case "warm":
                return ((args.Length > 1) && TryParseInt(text: args[1], value: out var warm)) ? bench.SetWarmFrames(frames: warm) : "[sdf.bench.warm: usage — sdf.bench warm <n>]";
            case "frames":
                return ((args.Length > 1) && TryParseInt(text: args[1], value: out var frames)) ? bench.SetSampleFrames(frames: frames) : "[sdf.bench.frames: usage — sdf.bench frames <n>]";
            case "shapes":
                return (RequireBenchMode(mode: mode) ?? bench.StartShapes());
            case "ops":
                return (RequireBenchMode(mode: mode) ?? bench.StartOps());
            case "carves":
                return (RequireBenchMode(mode: mode) ?? bench.StartCarves());
            case "storm":
                return (RequireBenchMode(mode: mode) ?? bench.StartStorm());
            case "instances":
                return HandleBenchInstances(mode: mode, bench: bench, args: args);
            case "sweep": {
                var shape = ((args.Length > 1) ? ParseShape(name: args[1]) : SdfDebugShapeKind.Torus) ?? SdfDebugShapeKind.Torus;

                return (RequireBenchMode(mode: mode) ?? bench.StartSweep(shape: shape));
            }
            default:
                return $"[sdf.bench: '{args[0]}' — shapes | ops | carves | storm | instances <shape> <n> | sweep [shape] | warm <n> | frames <n> | abort]";
        }
    }

    private static string HandleBenchInstances(SdfDebugMode mode, SdfBenchScene bench, string[] args) {
        if ((args.Length < 3) || (ParseShape(name: args[1]) is not { } shape) || !TryParseInt(text: args[2], value: out var count) || (count < 1)) {
            return "[sdf.bench instances: usage — sdf.bench instances <shape> <n> (n >= 1, up to 4096)]";
        }

        return (RequireBenchMode(mode: mode) ?? bench.StartInstances(shape: shape, count: count));
    }

    // The bench workload REPLACES the room, so the mode must be active for the timings to measure the workload (not the
    // room). Returns a guidance string when it is not, or null to proceed.
    private static string? RequireBenchMode(SdfDebugMode mode) =>
        (mode.Active ? null : "[sdf.bench: enter the SDF-debug mode first (run `sdf`), then start the bench]");

    private static string HandleShape(SdfDebugScene scene, string[] args) {
        if ((args.Length == 0) || (ParseShape(name: args[0]) is not { } kind)) {
            return "[sdf.shape: give a name — sphere, box, torus, capsule, cylinder, ellipsoid, vesica, round-cone, rounded-rect, polygon, star, trapezoid, ellipse]";
        }

        var overrides = new List<float>();

        for (var index = 1; (index < args.Length); index++) {
            if (!TryParseFloat(text: args[index], value: out var value)) {
                return $"[sdf.shape: '{args[index]}' is not a number]";
            }

            overrides.Add(item: value);
        }

        scene.SetShape(kind: kind, overrides: overrides);

        return $"[sdf.shape {kind}] {Summary(scene: scene)}";
    }

    private static string HandleShape2(SdfDebugScene scene, string[] args) {
        if ((args.Length == 0) || (ParseShape(name: args[0]) is not { } kind)) {
            return "[sdf.shape2: give a name — same catalog as sdf.shape (sdf.shape2.off removes it)]";
        }

        var overrides = new List<float>();

        for (var index = 1; (index < args.Length); index++) {
            if (!TryParseFloat(text: args[index], value: out var value)) {
                return $"[sdf.shape2: '{args[index]}' is not a number]";
            }

            overrides.Add(item: value);
        }

        scene.SetShape2(kind: kind, overrides: overrides);

        return $"[sdf.shape2 {kind}] {Summary(scene: scene)}";
    }

    private static string HandleBlend(SdfDebugScene scene, string[] args) {
        if ((args.Length == 0) || (ParseBlend(name: args[0]) is not { } blend)) {
            return "[sdf.blend: one of union, smooth, subtract, smooth-subtract, intersect, smooth-intersect, chamfer, chamfer-intersect, chamfer-subtract, xor — optional [smooth-k]]";
        }

        var smooth = (((args.Length > 1) && TryParseFloat(text: args[1], value: out var parsed)) ? parsed : scene.BlendSmooth);

        scene.SetBlend(blend: blend, smooth: smooth);

        return $"[sdf.blend {blend} k={scene.BlendSmooth:0.###}] {Summary(scene: scene)}";
    }

    private static string HandleSlice(SdfDebugScene scene, string[] args) {
        if (args.Length == 0) {
            return $"[sdf.slice: {DescribeSlice(scene: scene)} — sdf.slice x|y|z [offset] pins an axis plane, sdf.slice camera returns to camera-locked]";
        }

        var axis = args[0].ToLowerInvariant() switch {
            "camera" or "cam" or "off" => 0,
            "x" => 1,
            "y" => 2,
            "z" => 3,
            _ => -1,
        };

        if (axis < 0) {
            return "[sdf.slice: usage — sdf.slice x|y|z [offset] | sdf.slice camera]";
        }

        var offset = (((args.Length > 1) && TryParseFloat(text: args[1], value: out var parsed)) ? parsed : 0f);

        scene.SetSlicePlane(axis: axis, offset: offset);

        return $"[sdf.slice {DescribeSlice(scene: scene)}]";
    }

    private static string DescribeSlice(SdfDebugScene scene) {
        return scene.SliceAxis switch {
            1 => $"x @ {scene.SliceOffset:0.###}",
            2 => $"y @ {scene.SliceOffset:0.###}",
            3 => $"z @ {scene.SliceOffset:0.###}",
            _ => "camera-locked (through the origin)",
        };
    }

    private static SdfBlendOp? ParseBlend(string name) {
        return name.ToLowerInvariant() switch {
            "union" => SdfBlendOp.Union,
            "smooth" or "smooth-union" or "smoothunion" => SdfBlendOp.SmoothUnion,
            "subtract" or "subtraction" => SdfBlendOp.Subtraction,
            "smooth-subtract" or "smoothsubtract" => SdfBlendOp.SmoothSubtraction,
            "intersect" or "intersection" => SdfBlendOp.Intersection,
            "smooth-intersect" or "smoothintersect" => SdfBlendOp.SmoothIntersection,
            "chamfer" or "chamfer-union" => SdfBlendOp.ChamferUnion,
            "chamfer-intersect" => SdfBlendOp.ChamferIntersection,
            "chamfer-subtract" => SdfBlendOp.ChamferSubtraction,
            "xor" => SdfBlendOp.Xor,
            _ => null,
        };
    }

    private static string HandleLift(SdfDebugScene scene, string[] args) {
        if (args.Length == 0) {
            return $"[sdf.lift: {scene.Lift} {scene.LiftAmount:F2} — give revolve|extrude [amount]]";
        }

        var lift = args[0].ToLowerInvariant() switch {
            "revolve" or "rev" => (SdfLift?)SdfLift.Revolve,
            "extrude" or "ext" => SdfLift.Extrude,
            _ => null,
        };

        if (lift is not { } mode) {
            return "[sdf.lift: revolve or extrude]";
        }

        var amount = (((args.Length > 1) && TryParseFloat(text: args[1], value: out var parsed)) ? parsed : scene.LiftAmount);

        scene.SetLift(lift: mode, amount: amount);

        return $"[sdf.lift {mode} {scene.LiftAmount:F2}] {Summary(scene: scene)}";
    }

    private static string HandleOp(SdfDebugScene scene, string[] args) {
        if (args.Length == 0) {
            return "[sdf.op: give an op — twist, bend-x/y/z, scale, elongate, repeat, repeat-limited, polar, symmetry, logsphere, celljitter, domainwarp, onion, dilate, displace]";
        }

        if (BuildOp(args: args) is not { } built) {
            return $"[sdf.op: '{args[0]}' — bad op or arguments. {OpUsage(name: args[0])}]";
        }

        if (!scene.PushOp(op: built)) {
            return $"[sdf.op: the stack is full ({SdfDebugScene.MaxOps} ops)]";
        }

        return $"[sdf.op {args[0]}] {Summary(scene: scene)}";
    }

    // sdf.carve <x> <y> <z> [r] [smooth [k]] — the coords are required; an optional radius (a bare number after the
    // coords) precedes an optional 'smooth' token that flips it to a SmoothSubtraction, itself taking an optional k. A
    // pad-chord carve appends the SAME record (a hard, default-radius carve) through SdfDebugMode.AdvanceInput.
    private static string HandleCarve(SdfDebugScene scene, string[] args) {
        if (!TryParseFloats(args: args, count: 3, start: 0, values: out var xyz)) {
            return "[sdf.carve: usage — sdf.carve <x> <y> <z> [r] [smooth [k]]]";
        }

        var index = 3;
        var radius = SdfDebugScene.DefaultCarveRadius;

        if ((index < args.Length) && TryParseFloat(text: args[index], value: out var parsedRadius)) {
            radius = parsedRadius;
            index++;
        }

        var smooth = false;
        var smoothK = SdfDebugScene.DefaultCarveSmoothK;

        if ((index < args.Length) && string.Equals(a: args[index], b: "smooth", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            smooth = true;
            index++;

            if ((index < args.Length) && TryParseFloat(text: args[index], value: out var parsedK)) {
                smoothK = parsedK;
            }
        }

        var carve = new SdfCarve(Center: new Vector3(xyz[0], xyz[1], xyz[2]), Radius: radius, Smooth: smooth, SmoothK: smoothK);

        if (!scene.AddCarve(carve: carve)) {
            return $"[sdf.carve: pool full — MaxCarves={SdfDebugScene.MaxCarves} reached (sdf.carve.clear to reset)]";
        }

        // Echo the CLAMPED record the scene actually stored (radius floored positive, k floored non-negative).
        var stored = scene.Carves[scene.Carves.Count - 1];

        return $"[sdf.carve {SdfDebugScene.FormatCarve(carve: stored)}] carves={scene.Carves.Count} — {Summary(scene: scene)}";
    }

    // Parses one op name + its args into an SdfDebugOp, or null on a bad name / bad args (the caller returns usage).
    // Guards CellJitter's in-cell constraint here so the builder (evaluated every frame) never throws mid-render.
    private static SdfDebugOp? BuildOp(string[] args) {
        var rest = args[1..];

        float F(int index, float fallback) => (((index < rest.Length) && TryParseFloat(text: rest[index], value: out var value)) ? value : fallback);
        Vector3 V3(int start, Vector3 fallback) => new(F(start, fallback.X), F(start + 1, fallback.Y), F(start + 2, fallback.Z));

        switch (args[0].ToLowerInvariant()) {
            case "twist": return Op(kind: SdfDebugOpKind.Twist, a: F(0, 1f));
            case "bend-x" or "bendx": return Op(kind: SdfDebugOpKind.BendX, a: F(0, 1f));
            case "bend-y" or "bendy": return Op(kind: SdfDebugOpKind.BendY, a: F(0, 1f));
            case "bend-z" or "bendz": return Op(kind: SdfDebugOpKind.BendZ, a: F(0, 1f));
            case "scale": return Op(kind: SdfDebugOpKind.Scale, v0: ((rest.Length == 1) ? new Vector3(F(0, 1f)) : V3(0, Vector3.One)));
            case "elongate": return Op(kind: SdfDebugOpKind.Elongate, v0: V3(0, new Vector3(0.3f, 0f, 0f)));
            case "repeat": return Op(kind: SdfDebugOpKind.Repeat, v0: V3(0, new Vector3(2.5f, 2.5f, 2.5f)));
            case "repeat-limited" or "repeatlimited": return Op(kind: SdfDebugOpKind.RepeatLimited, v0: V3(0, new Vector3(2.5f, 2.5f, 2.5f)), v1: V3(3, new Vector3(1f, 1f, 1f)));
            case "polar": return BuildPolar(rest: rest);
            case "symmetry": return BuildSymmetry(rest: rest);
            case "logsphere": return Op(kind: SdfDebugOpKind.LogSphere, a: F(0, 2f), b: F(1, 0f));
            case "celljitter": return BuildCellJitter(rest: rest);
            case "domainwarp": return Op(kind: SdfDebugOpKind.DomainWarp, v0: V3(0, new Vector3(2f, 2f, 2f)), a: F(3, 0.2f));
            case "onion": return Op(kind: SdfDebugOpKind.Onion, a: F(0, 0.05f));
            case "dilate": return Op(kind: SdfDebugOpKind.Dilate, a: F(0, 0.1f));
            case "displace": return Op(kind: SdfDebugOpKind.Displace, v0: V3(0, new Vector3(6f, 6f, 6f)), a: F(3, 0.08f));
            default: return null;
        }
    }

    private static SdfDebugOp? BuildPolar(string[] rest) {
        var count = (((rest.Length > 0) && TryParseInt(text: rest[0], value: out var parsed)) ? Math.Max(1, parsed) : 6);
        var axis = ((rest.Length > 1) ? ParseAxis(token: rest[1]) : 1);
        var mirror = ((rest.Length > 2) && (string.Equals(a: rest[2], b: "mirror", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: rest[2], b: "m", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: rest[2], b: "true", comparisonType: StringComparison.OrdinalIgnoreCase)));

        return new SdfDebugOp(Kind: SdfDebugOpKind.Polar, V0: Vector3.Zero, V1: Vector3.Zero, A: 0f, B: 0f, I0: count, I1: axis, Flag: mirror);
    }

    private static SdfDebugOp? BuildSymmetry(string[] rest) {
        // sdf.op symmetry x|y|z [offset]  OR  sdf.op symmetry nx ny nz [offset]
        if ((rest.Length >= 1) && (ParseAxisNormal(token: rest[0]) is { } axisNormal)) {
            var axisOffset = (((rest.Length > 1) && TryParseFloat(text: rest[1], value: out var o)) ? o : 0f);

            return Op(kind: SdfDebugOpKind.Symmetry, v0: axisNormal, a: axisOffset);
        }

        if ((rest.Length >= 3) && TryParseFloat(text: rest[0], value: out var nx) && TryParseFloat(text: rest[1], value: out var ny) && TryParseFloat(text: rest[2], value: out var nz)) {
            var normal = new Vector3(nx, ny, nz);

            if (normal.LengthSquared() < 1e-8f) {
                return null; // a zero normal would produce NaN in the builder's normalize
            }

            var symOffset = (((rest.Length > 3) && TryParseFloat(text: rest[3], value: out var o2)) ? o2 : 0f);

            return Op(kind: SdfDebugOpKind.Symmetry, v0: normal, a: symOffset);
        }

        return null;
    }

    private static SdfDebugOp? BuildCellJitter(string[] rest) {
        var spacing = (((rest.Length > 0) && TryParseFloat(text: rest[0], value: out var s)) ? MathF.Max(0.001f, MathF.Abs(s)) : 2f);
        var jitter = (((rest.Length > 1) && TryParseFloat(text: rest[1], value: out var j)) ? j : 0.5f);
        var seed = (((rest.Length > 2) && TryParseInt(text: rest[2], value: out var sd)) ? sd : 0);
        var tumble = (((rest.Length > 3) && TryParseFloat(text: rest[3], value: out var t)) ? t : 0f);

        // The in-cell constraint the SdfProgramBuilder enforces (evaluated every frame): jitter/2 < spacing/2. Guard it
        // HERE so a bad push returns usage instead of throwing inside BuildProgram during a render. The full authoring
        // rule is stronger — jitter/2 + prototype radius <= spacing/2, and even a contained prototype seams/overestimates
        // at cell walls (containment != nearest-copy; see SdfProgramBuilder.CellJitter) — but the prototype is the
        // subject shape, unknown to this parse.
        if ((MathF.Abs(jitter) * 0.5f) >= (0.5f * spacing)) {
            return null;
        }

        return new SdfDebugOp(Kind: SdfDebugOpKind.CellJitter, V0: new Vector3(spacing), V1: Vector3.Zero, A: jitter, B: tumble, I0: seed, I1: 0, Flag: false);
    }

    private static SdfDebugOp Op(SdfDebugOpKind kind, Vector3 v0 = default, Vector3 v1 = default, float a = 0f, float b = 0f) =>
        new(Kind: kind, V0: v0, V1: v1, A: a, B: b, I0: 0, I1: 0, Flag: false);

    // Shared on/off token parse ("on"/"true"/"1", case-insensitive) — used by sdf.floor and sdf.scope. NOT used by
    // BuildPolar's mirror parse ("mirror"/"m"/"true"): a distinct token set, not this same on/off vocabulary.
    private static bool ParseOnOff(string token) =>
        (string.Equals(a: token, b: "on", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: token, b: "true", comparisonType: StringComparison.OrdinalIgnoreCase) || string.Equals(a: token, b: "1", comparisonType: StringComparison.OrdinalIgnoreCase));

    private static SdfDebugShapeKind? ParseShape(string name) {
        return name.ToLowerInvariant() switch {
            "sphere" => SdfDebugShapeKind.Sphere,
            "box" => SdfDebugShapeKind.Box,
            "torus" => SdfDebugShapeKind.Torus,
            "capsule" => SdfDebugShapeKind.Capsule,
            "cylinder" => SdfDebugShapeKind.Cylinder,
            "ellipsoid" => SdfDebugShapeKind.Ellipsoid,
            "vesica" => SdfDebugShapeKind.Vesica,
            "round-cone" or "roundcone" or "cone" => SdfDebugShapeKind.RoundCone,
            "rounded-rect" or "roundedrect" or "roundrect" or "rect" => SdfDebugShapeKind.RoundedRect,
            "polygon" or "poly" => SdfDebugShapeKind.Polygon,
            "star" => SdfDebugShapeKind.Star,
            "trapezoid" or "trap" => SdfDebugShapeKind.Trapezoid,
            "ellipse" => SdfDebugShapeKind.Ellipse,
            _ => null,
        };
    }

    private static int ParseAxis(string token) {
        return token.ToLowerInvariant() switch {
            "x" => 0,
            "z" => 2,
            _ => 1, // y (default)
        };
    }

    private static Vector3? ParseAxisNormal(string token) {
        return token.ToLowerInvariant() switch {
            "x" => Vector3.UnitX,
            "y" => Vector3.UnitY,
            "z" => Vector3.UnitZ,
            _ => null,
        };
    }

    private static string OpUsage(string name) {
        return name.ToLowerInvariant() switch {
            "scale" => "usage: sdf.op scale <s> | <x y z>",
            "elongate" => "usage: sdf.op elongate <x y z>",
            "repeat" => "usage: sdf.op repeat <x y z>",
            "repeat-limited" or "repeatlimited" => "usage: sdf.op repeat-limited <x y z lx ly lz>",
            "polar" => "usage: sdf.op polar <count> [x|y|z] [mirror]",
            "symmetry" => "usage: sdf.op symmetry <x|y|z|nx ny nz> [offset]",
            "logsphere" => "usage: sdf.op logsphere <shellRatio> [twist]",
            "celljitter" => "usage: sdf.op celljitter <spacing> <jitter(<spacing)> [seed] [tumble] — keep jitter/2 + shape radius <= spacing/2, and expect cell-wall seams even then (round fold picks the own cell, not the nearest copy)",
            "domainwarp" or "displace" => $"usage: sdf.op {name.ToLowerInvariant()} <fx fy fz amplitude>",
            _ => "usage: sdf.op <name> <value>",
        };
    }

    // A one-line summary of the whole subject — auto-echoed after every state change.
    private static string Summary(SdfDebugScene scene) {
        var lift = ((SdfDebugScene.IsLiftedShape(kind: scene.Shape) || ((scene.Shape2 is { } s2) && SdfDebugScene.IsLiftedShape(kind: s2))) ? $" lift={scene.Lift}/{scene.LiftAmount:F2}" : "");
        var pair = ((scene.Shape2 is { } second)
            ? $" {scene.Blend}(k={scene.BlendSmooth:0.###}) shape2={second}({FormatParams(values: scene.Params2)})@({scene.Offset2.X:0.##},{scene.Offset2.Y:0.##},{scene.Offset2.Z:0.##})"
            : "");

        return $"shape={scene.Shape}({FormatParams(values: scene.Params)}){pair}{lift} ops={scene.Ops.Count} carves={scene.Carves.Count} floor={(scene.Floor ? "on" : "off")} scope={(scene.Scope ? "on" : "off")}";
    }

    private static string FormatParams(IReadOnlyList<float> values) =>
        string.Join(separator: ",", values: values.Select(selector: static p => p.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)));

    private static string ListOps(SdfDebugScene scene) {
        if (scene.Ops.Count == 0) {
            return "[sdf.op.list: (empty)]";
        }

        var point = scene.Ops.Where(predicate: static o => !SdfDebugScene.IsFieldOp(kind: o.Kind)).Select(selector: static o => o.Kind.ToString());
        var field = scene.Ops.Where(predicate: static o => SdfDebugScene.IsFieldOp(kind: o.Kind)).Select(selector: static o => o.Kind.ToString());

        return $"[sdf.op.list: point[{string.Join(separator: " → ", values: point)}] shape field[{string.Join(separator: " → ", values: field)}]]";
    }

    private string DescribeInfo() {
        if (Mode is not { } mode) {
            return "[sdf.info: unavailable — the overworld is not the active root]";
        }

        var scene = mode.Scene;
        var (words, instances, stepScale) = mode.Measure();
        var lipschitz = ((stepScale == 1.0f) ? "1.0 (isometric — byte-exact)" : $"{stepScale:F4} (warp/relief clamp)");
        // The subject is WORLD-level (always evaluated, no per-object cull bound) — there is no instance sphere to report.
        var bound = ((instances == 0) ? "world-set (no cull bound — always evaluated)" : $"{instances} instance(s)");
        var timing = DescribeTiming();

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"[sdf.info active={mode.Active} {Summary(scene: scene)} slice={DescribeSlice(scene: scene)} | words={words} instances={instances} bound={bound} stepScale={lipschitz} | {ListOps(scene: scene)} | timing: {timing}]");
    }

    // The previous frame's per-pass GPU ms, one term per SdfWorldEngine.PassTimingLabels entry (so a future pass shows
    // here with no edit), plus the whole-frame total. "off" when timing is not live (PUCK_TIMING=1 arms it).
    private string DescribeTiming() {
        Span<double> passMilliseconds = stackalloc double[SdfWorldEngine.PassTimingCount];

        if ((m_host is null) || !m_host.TryReadSdfPassTimings(passMilliseconds: passMilliseconds, passCount: out var passCount, frame: out var frame)) {
            return "off (set PUCK_TIMING=1 to time)";
        }

        var builder = new System.Text.StringBuilder();
        var labels = SdfWorldEngine.PassTimingLabels;

        for (var index = 0; (index < passCount); index++) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{labels[index]}={passMilliseconds[index]:F3} ");
        }

        return builder.Append(provider: CultureInfo.InvariantCulture, handler: $"frame={frame:F3} ms").ToString();
    }

    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    private CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }

    // The debug scene has no separate on/off "mode" to gate on (it configures freely, then `sdf` enters) — only the
    // host gate applies (mirrors WorldScene's single-tier guard).
    private Func<CommandContext, CommandResult> WithScene(Func<SdfDebugScene, string> handler) =>
        CommandAvailability.WithTarget(
            getTarget: () => Scene,
            handler: handler,
            unavailableMessage: "[sdf: unavailable — the overworld is not the active root]"
        );

    private Func<CommandContext, string[], CommandResult> WithSceneArgs(Func<SdfDebugScene, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(
            getTarget: () => Scene,
            handler: handler,
            unavailableMessage: "[sdf: unavailable — the overworld is not the active root]"
        );
}
