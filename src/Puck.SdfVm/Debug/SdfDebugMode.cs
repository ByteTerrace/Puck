using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm.Debug;

/// <summary>
/// The mode-state owner for the fullscreen SDF-debug takeover — holds the scene state, the orbit controller, and the
/// gallery tour, and dispatches the one <see cref="Emit"/>/<see cref="EmitProbe"/> pair a composition
/// host (see <see cref="Puck.SdfVm.Debug.SdfDebugEmitter"/>, its <see cref="ISdfSceneEmitter"/> adapter) needs to treat
/// the whole surface as one alternate-list takeover. A host's frame source (e.g. the demo's
/// <c>OverworldFrameSource</c>) drives it through these members; the host's console module reaches <see cref="Scene"/>
/// through it. Presentation only — the deterministic simulation never sees the debug subject.
/// </summary>
public sealed class SdfDebugMode {
    private readonly SdfDebugScene m_scene = new();
    private readonly SdfGalleryScene m_gallery = new();
    private readonly SdfDebugRenderer m_renderer = new();
    private readonly SdfDebugController m_controller = new();
    private bool m_gridCull = true;
    private bool m_shadowCull = true;

    /// <summary>How many counters <see cref="WriteRevision"/> reports — the component count
    /// <see cref="Puck.SdfVm.ISdfSceneEmitter.RevisionComponentCount"/> forwards for this mode's emitter adapter.</summary>
    public const int RevisionComponentCount = 4;

    private bool m_active;
    // The carve-bake handoff revision: bumped whenever the debug pool's planner adopts/releases a brick, so the frame source rebuilds the takeover program to emit (or drop) that SampledRegion. One component of
    // WriteRevision's vector.
    private int m_bakeRevision;
    private int m_gridRevision;
    private bool m_useFdNormals;

    /// <summary>Whether the mode is active (the fullscreen debug subject replaces the room).</summary>
    public bool Active => m_active;
    /// <summary>The camera frame while active — the active gallery exhibit's authored pose, otherwise the pad orbit
    /// (object-intent). Null when the mode is down.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame =>
        (m_active
            ? (m_gallery.CameraFrame ?? m_controller.CameraFrame)
            : null
        );
    /// <summary>Whether the camera pose must be applied verbatim (no easing): true while a gallery exhibit is active
    /// (each exhibit's defect wants its authored framing held exactly, not eased into).</summary>
    public bool CameraSnaps => (m_active && m_gallery.Active);
    /// <summary>The carve-bake settle planner for the interactive debug carve pool — the seam the
    /// <c>sdf.bake status</c>/<c>sdf.bake now</c> verbs inspect and nudge. The process-wide <c>sdf.carve-bake</c> switch
    /// (<see cref="SdfCarveBakePlanner.Enabled"/>) gates whether it ever bakes; off, the pool stays fully analytic.</summary>
    public SdfCarveBakePlanner CarveBake => m_scene.CarvePlanner;
    /// <summary>The torture-museum tour the <c>sdf.gallery</c> verb drives (a curated cycle of known-nasty scenes). While
    /// an exhibit is active the mode renders it in place of the debug subject.</summary>
    public SdfGalleryScene Gallery => m_gallery;
    /// <summary>Whether the mode's takeover programs pack the uniform-grid instance cull (default on — the production
    /// path). Off packs a disabled grid so the beam takes the flat per-instance fallback over the same instances — the
    /// <c>sdf.grid</c> verb's live A/B lever for grid-vs-flat beam measurement (no rebuild, no checkout).</summary>
    public bool GridCull => m_gridCull;
    /// <summary>The debug scene the <c>sdf.*</c> verbs mutate (shape, op stack, floor, lift).</summary>
    public SdfDebugScene Scene => m_scene;
    /// <summary>Whether the soft-shadow grid cull is on (default true = the production path). On gathers each lit pixel's
    /// shadow-ray grid neighborhood and marches only those instances (bit-identical to flat, cheaper on spread scenes);
    /// off forces the flat all-instances shadow march — the <c>sdf.shadowcull</c> verb's live A/B lever. A pure
    /// frame-channel flag (<see cref="SdfFrame.DisableShadowCull"/>), so no rebuild — the frame source reads it fresh
    /// each frame.</summary>
    public bool ShadowCull => m_shadowCull;
    /// <summary>The slice view's plane selector as a frame-channel float (0 = camera-locked, 1/2/3 = world X/Y/Z) —
    /// the frame source threads it into <see cref="SdfFrame.DebugSliceAxis"/> every frame.</summary>
    public float SliceAxis => m_scene.SliceAxis;
    /// <summary>The axis slice plane's signed offset (see <see cref="SdfFrame.DebugSliceOffset"/>).</summary>
    public float SliceOffset => m_scene.SliceOffset;
    /// <summary>Whether the lit surface normal uses the four-tap finite-difference probe instead of
    /// the default analytic forward-mode gradient dual. A pure frame-channel flag (<see cref="SdfFrame.UseFiniteDifferenceNormals"/>) —
    /// it changes no geometry, so it needs no revision bump; the frame source reads it fresh each frame. Pair with
    /// <c>debug.view.normals</c> for the visual A/B.</summary>
    public bool UseFiniteDifferenceNormals => m_useFdNormals;

    // Drains a pending pad-chord carve — appending it to the scene and
    // echoing it to stdout — and ticks the meteor shower. Draining HERE (the per-frame call the render node already
    // makes) keeps the carve as pure data without new render-node plumbing: a pad carve appends the exact same
    // SdfCarve a scripted sdf.carve does, and the same revision bump rebuilds the program.
    private void DrainPadCarveAndMeteor() {
        if (m_controller.ConsumeCarveRequest() is { } center) {
            // A pad carve is a hard subtraction at the default radius (the chord carries no size/smooth args — the verb
            // is the way to author those); the SmoothK rides along for a uniform record but is unused while Smooth=false.
            var carve = new SdfCarve(
                Center: center,
                Radius: SdfDebugScene.DefaultCarveRadius,
                Smooth: false,
                SmoothK: SdfDebugScene.DefaultCarveSmoothK
            );

            Console.Out.WriteLine(value: (m_scene.AddCarve(carve: carve)
                ? $"[sdf.carve (pad) {SdfDebugScene.FormatCarve(carve: carve)}] carves={m_scene.Carves.Count}"
                : $"[sdf.carve (pad): pool full — MaxCarves={SdfDebugScene.MaxCarves} reached (sdf.carve.clear to reset)]"));
        }

        // The METEOR SHOWER: one impact lands per produced frame while a shower is in flight — each an ordinary pool
        // carve accumulating through the same rebuild-per-frame path a scripted carve uses. A progress line every 64
        // impacts keeps the console readable; the landing itself is the on-screen show.
        if (m_scene.TickMeteor() is { } impact) {
            var landed = m_scene.Carves.Count;

            if (
                (m_scene.MeteorsRemaining == 0) ||
                ((landed % 64) == 0)
            ) {
                Console.Out.WriteLine(value: ((m_scene.MeteorsRemaining == 0)
                    ? $"[sdf.meteors: the sky clears — {landed} carve(s) in the pool, last {SdfDebugScene.FormatCarve(carve: impact)}]"
                    : $"[sdf.meteors: …{m_scene.MeteorsRemaining} still falling — carves={landed}]"));
            }
        }
    }

    /// <summary>Drives the interactive debug pool's carve-bake settle planner one produced frame against the live
    /// engine. A handoff (a bin adopting or releasing a brick) bumps the carve-bake component of
    /// <see cref="WriteRevision"/> so the frame source
    /// rebuilds the takeover program to emit (or drop) the SampledRegion. No-op while the mode is down — nothing owns
    /// the screen to bake for. Called from the host's <see cref="Puck.SdfVm.ISdfFrameSource.AdvanceBricks"/> forwarder.</summary>
    /// <param name="bakes">The engine's brick-bake service (poll/request).</param>
    public void AdvanceBricks(ISdfBrickBakeService bakes) {
        ArgumentNullException.ThrowIfNull(bakes);

        if (!m_active) {
            return;
        }

        if (m_scene.CarvePlanner.Advance(
            carves: m_scene.Carves,
            carveRevision: m_scene.Revision,
            bakes: bakes
        )) {
            m_bakeRevision++;
        }
    }
    /// <summary>Forwards the host's neutral orbit input to the orbit controller (only while active) and drains a
    /// pending pad-chord carve — appending it to the scene and echoing it to stdout. Draining here (the per-frame call
    /// the render node already makes) keeps the carve as pure data without new render-node plumbing: a pad carve
    /// appends the exact same <see cref="SdfCarve"/> a scripted <c>sdf.carve</c> does, and the same revision bump
    /// rebuilds the program.</summary>
    /// <param name="raw">The neutral orbit input this frame.</param>
    /// <param name="deltaSeconds">The render-clock delta.</param>
    public void AdvanceInput(in SdfOrbitInput raw, float deltaSeconds) {
        if (!m_active) {
            return;
        }

        m_controller.Advance(
            deltaSeconds: deltaSeconds,
            raw: in raw
        );

        DrainPadCarveAndMeteor();
    }
    /// <summary>Whether the orbit controller's exit button fired since the last consume (clears it).</summary>
    public bool ConsumeExitRequest() =>
        m_controller.ConsumeExitRequest();
    /// <summary>Emits the live takeover program: the current gallery exhibit while the tour is active, otherwise the
    /// debug subject (+ optional floor). Both are within the frozen worst-case envelope (the gallery's exhibits are
    /// small — see <see cref="SdfDebugRenderer.EmitProbe"/>).</summary>
    /// <param name="builder">The program builder.</param>
    public void Emit(SdfProgramBuilder builder) {
        if (m_gallery.Active) {
            m_renderer.EmitGallery(
                builder: builder,
                exhibit: m_gallery.Exhibit
            );

            return;
        }

        m_renderer.Emit(
            builder: builder,
            scene: m_scene
        );
    }
    /// <summary>Emits the worst-case debug subject for the frame source's capacity probe (never rendered).</summary>
    /// <param name="builder">The probe builder.</param>
    public void EmitProbe(SdfProgramBuilder builder) =>
        m_renderer.EmitProbe(builder: builder);
    /// <summary>Builds the current debug subject as a standalone program and measures it (word/instance count + the
    /// baked Lipschitz <see cref="SdfProgram.StepScale"/>) — the facts <c>sdf.info</c> reports. Never rendered.</summary>
    /// <returns>The word count, instance count, and step scale.</returns>
    public (int Words, int Instances, float StepScale) Measure() {
        var builder = new SdfProgramBuilder();

        m_renderer.Emit(
            builder: builder,
            scene: m_scene
        );

        var program = builder.Build();

        return (program.Words.Length, program.Instances.Count, program.StepScale);
    }
    /// <summary>Poses the orbit camera directly (forwards to <see cref="SdfDebugController.SetPose"/>) — the scriptable
    /// lever the <c>sdf.cam</c> verb drives so a deterministic repro can pin a grazing pitch/framing.</summary>
    /// <param name="pitch">The orbit pitch (radians), or null to keep it.</param>
    /// <param name="yaw">The orbit yaw (radians), or null to keep it.</param>
    /// <param name="distance">The orbit distance (world units), or null to keep it.</param>
    /// <param name="target">The orbit target (world units), or null to keep it.</param>
    public void PoseCamera(float? pitch, float? yaw, float? distance, Vector3? target) =>
        m_controller.SetPose(
            distance: distance,
            pitch: pitch,
            target: target,
            yaw: yaw
        );
    /// <summary>Enters or leaves the mode; resets the orbit controller's edge tracking on either transition (a held
    /// button never fires a stale edge into the other mode).</summary>
    /// <param name="active">Whether the mode should be active.</param>
    public void SetActive(bool active) {
        if (m_active == active) {
            return;
        }

        m_active = active;
        m_controller.Reset();
    }
    /// <summary>Selects the surface-normal probe (see <see cref="UseFiniteDifferenceNormals"/>): analytic dual (the
    /// default) or the 4-tap finite difference.</summary>
    /// <param name="useTaps">Whether to use the 4-tap finite-difference probe; <see langword="false"/> = analytic.</param>
    public void SetFiniteDifferenceNormals(bool useTaps) {
        m_useFdNormals = useTaps;
    }
    /// <summary>Sets the grid-cull toggle (see <see cref="GridCull"/>) and bumps the revision so the frame source
    /// rebuilds the takeover program with the new packing on the next frame.</summary>
    /// <param name="on">Whether the uniform-grid instance cull is packed.</param>
    public void SetGridCull(bool on) {
        if (m_gridCull == on) {
            return;
        }

        m_gridCull = on;
        m_gridRevision++;
    }
    /// <summary>Sets the soft-shadow grid-cull toggle (see <see cref="ShadowCull"/>). A pure frame channel — no revision
    /// bump.</summary>
    /// <param name="on">Whether the shadow march uses the grid cull; <see langword="false"/> = the flat reference.</param>
    public void SetShadowCull(bool on) {
        m_shadowCull = on;
    }
    /// <summary>Writes the content counters the frame source rebuilds on: the debug scene's, the gallery's (an exhibit
    /// enter/advance/jump/off rebuilds the program to that exhibit), the grid-cull toggle's, and the carve-bake
    /// handoff's — side by side, never added together. Handing the host one sum would let two counters moving in
    /// opposite directions by the same amount cancel and hold a stale takeover program; the host compares componentwise
    /// precisely so no addition on this path can hide a change (see <see cref="Puck.SdfVm.ISdfSceneEmitter.WriteRevision"/>).</summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_scene.Revision;
        destination[1] = m_gallery.Revision;
        destination[2] = m_gridRevision;
        destination[3] = m_bakeRevision;
    }
}
