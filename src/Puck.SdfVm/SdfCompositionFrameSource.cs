using System.Numerics;

namespace Puck.SdfVm;

/// <summary>The host seam <see cref="SdfCompositionFrameSource"/> calls once per <see cref="ISdfFrameSource.CaptureFrame"/>
/// to turn the composed <see cref="SdfProgram"/> + dynamic transforms into a full <see cref="SdfFrame"/> — the "dress"
/// half of frame production (views, camera/lighting mood, the grid-lock overlay, debug view flags) that has nothing to
/// do with WHAT geometry exists and everything to do with HOW this frame presents it. Composition owns the emitter
/// list (content); a host implements this to own presentation, without needing to know how the program was built.</summary>
public interface ISdfFrameDresser {
    /// <summary>Builds this frame's <see cref="SdfFrame"/> from the composed program/transforms.</summary>
    /// <param name="program">This frame's program (freshly rebuilt this call, or the same instance as last call when
    /// the composed content revision hasn't changed — compare by reference against a previous frame's
    /// <see cref="SdfFrame.Program"/> to detect a real change, exactly like <see cref="SdfFrame.ProgramChanged"/>).</param>
    /// <param name="transforms">This frame's packed dynamic-transform buffer (every registered emitter's slots).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The presentation frame delta in seconds.</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> toward the current fixed simulation tick.</param>
    /// <returns>The frame to render.</returns>
    SdfFrame Dress(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha);
}

/// <summary>Composes a fixed list of <see cref="ISdfSceneEmitter"/>s into ONE <see cref="ISdfFrameSource"/> — the
/// generalization of the hand-written <c>BuildProgram</c> method every prior frame source wrote for itself: rather
/// than one method inlining every content block, a host picks a LIST of emitters (a room, a sculpted scene, an
/// authoring pool, a debug takeover, …) and this type owns the shared mechanics every one of them needs — contiguous
/// dynamic-transform slot assignment, the one-time worst-case capacity probe, and the material-scope wrap for any
/// <see cref="ISdfSceneEmitter.OwnsMaterialScope"/> emitter.
/// <para>
/// SLOT ASSIGNMENT: at construction, each configured emitter is assigned
/// a contiguous <see cref="SdfEmitContext.SlotBase"/> equal to the running sum of every earlier emitter's
/// <see cref="ISdfSceneEmitter.DynamicSlotCount"/> — emitter 0 starts at slot 0, emitter 1 starts where emitter 0's
/// range ends, and so on. This never changes for the lifetime of this instance (an emitter's <see cref="ISdfSceneEmitter.DynamicSlotCount"/>
/// must therefore stay constant — see its remarks).
/// </para>
/// <para>
/// THE CAPACITY PROBE: also at construction, one COMBINED worst-case program is built by calling every emitter's
/// <see cref="ISdfSceneEmitter.Emit"/> with <see cref="SdfEmitContext.Probe"/> set — each in its own material scope
/// when <see cref="ISdfSceneEmitter.OwnsMaterialScope"/> is set — and measuring the result once
/// (<see cref="WorstCaseProgramWordCapacity"/>/<see cref="WorstCaseInstanceCapacity"/>/<see cref="WorstCaseDynamicTransformCapacity"/>).
/// This generalizes the per-host <c>MeasureWorstCaseEnvelope</c> pattern: any live rebuild (every real
/// <see cref="CaptureFrame"/> call) is a program built from the SAME emitters' NON-probe branches, which by the probe
/// contract (see <see cref="ISdfSceneEmitter"/>) can never exceed what the probe measured.
/// </para>
/// <para>
/// REBUILD TRIGGER: the composed program rebuilds only when the SUM of every emitter's <see cref="ISdfSceneEmitter.Revision"/>
/// changes since the last build (or on the first call) — an emitter that never changes (the default <c>Revision =&gt; 0</c>)
/// never forces a rebuild on its own.
/// </para></summary>
public sealed class SdfCompositionFrameSource : ISdfFrameSource {
    private readonly IReadOnlyList<ISdfSceneEmitter> m_emitters;
    private readonly int[] m_slotBases;
    private readonly ISdfFrameDresser m_dresser;
    private readonly DynamicTransform[] m_transforms;
    private SdfProgram? m_program;
    private int m_builtRevision = int.MinValue;
    private float m_time;
    private float m_interpolationAlpha;

    /// <summary>Composes <paramref name="emitters"/> into one frame source, dressed each frame by
    /// <paramref name="dresser"/>.</summary>
    /// <param name="emitters">The fixed emitter list, in emission order (also the order their dynamic-transform slot
    /// ranges are assigned — see the type remarks). Copied defensively; mutating the source list afterward has no
    /// effect.</param>
    /// <param name="dresser">Builds each frame's <see cref="SdfFrame"/> from the composed program/transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="emitters"/> or <paramref name="dresser"/> is
    /// <see langword="null"/>, or <paramref name="emitters"/> contains a <see langword="null"/> entry.</exception>
    public SdfCompositionFrameSource(IReadOnlyList<ISdfSceneEmitter> emitters, ISdfFrameDresser dresser) {
        ArgumentNullException.ThrowIfNull(emitters);
        ArgumentNullException.ThrowIfNull(dresser);

        m_emitters = [.. emitters];
        m_dresser = dresser;
        m_slotBases = new int[m_emitters.Count];

        var slotCursor = 0;

        for (var index = 0; (index < m_emitters.Count); index++) {
            if (m_emitters[index] is not { } emitter) {
                throw new ArgumentNullException(paramName: nameof(emitters), message: $"emitters[{index}] is null.");
            }

            m_slotBases[index] = slotCursor;
            slotCursor += Math.Max(val1: 0, val2: emitter.DynamicSlotCount);
        }

        m_transforms = new DynamicTransform[slotCursor];

        var probe = BuildProgram(context: new SdfEmitContext(Probe: true, Time: 0f, RenderOrigin: Vector3.Zero, ParkPosition: ParkPosition, SlotBase: 0));

        WorstCaseProgramWordCapacity = probe.Words.Length;
        WorstCaseInstanceCapacity = probe.Instances.Count;
        WorstCaseDynamicTransformCapacity = m_transforms.Length;
    }

    /// <summary>Where a hidden/unused dynamic-transform slot parks this frame (<see cref="SdfEmitContext.ParkPosition"/>)
    /// — settable so a host can move it to sit well outside its own world's camera/tile-cull reach (the default,
    /// <c>(0, -1000, 0)</c>, is a generic "far below anything" fallback). Changing this does NOT rebuild the program
    /// (it only affects <see cref="ISdfSceneEmitter.PackDynamicTransforms"/>, called every frame regardless).</summary>
    public Vector3 ParkPosition { get; set; } = new(x: 0f, y: -1000f, z: 0f);

    /// <summary>The packed-word floor the engine's program buffer must reserve — every registered emitter's probe
    /// form, combined into one program, measured once at construction.</summary>
    public int WorstCaseProgramWordCapacity { get; }

    /// <summary>The instance-count floor the engine's mask buffer must reserve (see <see cref="WorstCaseProgramWordCapacity"/>).</summary>
    public int WorstCaseInstanceCapacity { get; }

    /// <summary>The dynamic-transform slot floor the render assembly must reserve — the sum of every registered
    /// emitter's <see cref="ISdfSceneEmitter.DynamicSlotCount"/>.</summary>
    public int WorstCaseDynamicTransformCapacity { get; }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_time += MathF.Max(x: deltaSeconds, y: 0f);
        m_interpolationAlpha = interpolationAlpha;

        var revision = AggregateRevision();

        if ((m_program is null) || (revision != m_builtRevision)) {
            m_program = BuildProgram(context: new SdfEmitContext(Probe: false, Time: m_time, RenderOrigin: Vector3.Zero, ParkPosition: ParkPosition, SlotBase: 0, InterpolationAlpha: interpolationAlpha));
            m_builtRevision = revision;
        }

        PackTransforms();

        return m_dresser.Dress(program: m_program, transforms: m_transforms, width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);
    }

    private int AggregateRevision() {
        var sum = 0;

        foreach (var emitter in m_emitters) {
            sum += emitter.Revision;
        }

        return sum;
    }
    private void PackTransforms() {
        Array.Fill(array: m_transforms, value: new DynamicTransform(Position: ParkPosition, Orientation: Quaternion.Identity));

        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];

            if (emitter.DynamicSlotCount == 0) {
                continue;
            }

            var context = new SdfEmitContext(Probe: false, Time: m_time, RenderOrigin: Vector3.Zero, ParkPosition: ParkPosition, SlotBase: m_slotBases[index], InterpolationAlpha: m_interpolationAlpha);

            emitter.PackDynamicTransforms(slots: m_transforms, context: in context);
        }
    }

    // Shared by construction's probe build and every live rebuild: one builder, every emitter's Emit in list order,
    // wrapped in a material scope for any OwnsMaterialScope emitter (the ONLY behavior difference from calling Emit
    // directly — see SdfMaterialScope). context.SlotBase is overwritten per-emitter from m_slotBases; the caller's
    // context otherwise carries Probe/Time/RenderOrigin/ParkPosition through unchanged.
    private SdfProgram BuildProgram(SdfEmitContext context) {
        var builder = new SdfProgramBuilder();

        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];
            var emitContext = new SdfEmitContext(Probe: context.Probe, Time: context.Time, RenderOrigin: context.RenderOrigin, ParkPosition: context.ParkPosition, SlotBase: m_slotBases[index], InterpolationAlpha: context.InterpolationAlpha);

            if (emitter.OwnsMaterialScope) {
                using var scope = builder.BeginMaterialScope();

                emitter.Emit(builder: builder, context: in emitContext);
            } else {
                emitter.Emit(builder: builder, context: in emitContext);
            }
        }

        return builder.Build();
    }
}
