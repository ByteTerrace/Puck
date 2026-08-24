using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The host seam <see cref="SdfCompositionFrameSource"/> calls once per <see cref="ISdfFrameSource.CaptureFrame"/>
/// to turn the composed <see cref="SdfProgram"/> + dynamic transforms into a full <see cref="SdfFrame"/> — the "dress"
/// half of frame production (views, camera/lighting mood, the grid-lock overlay, debug view flags) that has nothing to
/// do with what geometry exists and everything to do with how this frame presents it. Composition owns the emitter
/// list (content); a host implements this to own presentation, without needing to know how the program was built.</summary>
public interface ISdfFrameDresser {
    /// <summary>Builds this frame's <see cref="SdfFrame"/> from the composed program/transforms.</summary>
    /// <param name="program">This frame's program (freshly rebuilt this call, or the same instance as last call when
    /// the composed content revision hasn't changed — compare by reference against a previous frame's
    /// <see cref="SdfFrame.Program"/> to detect a real change, exactly like <see cref="SdfFrame.ProgramChanged"/>).</param>
    /// <param name="transforms">This frame's packed dynamic-transform buffer (every registered emitter's slots) — the
    /// host's OWN buffer, handed over as the concrete array rather than a read-only view because a dresser typically
    /// retains it past this call (an offscreen view pass rendering the same world after capture) and passes it to
    /// span-taking consumers. Never mutated by the host until the next <see cref="ISdfFrameSource.CaptureFrame"/>.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The presentation frame delta in seconds.</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> toward the current fixed simulation tick.</param>
    /// <returns>The frame to render.</returns>
    SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha);
}
/// <summary>Composes a fixed list of <see cref="ISdfSceneEmitter"/>s into one <see cref="ISdfFrameSource"/> — the
/// generalization of the hand-written <c>BuildProgram</c> method every prior frame source wrote for itself: rather
/// than one method inlining every content block, a host picks a list of emitters (a room, a sculpted scene, an
/// authoring pool, a debug takeover, …) and this type owns the shared mechanics every one of them needs — contiguous
/// dynamic-transform slot assignment, the one-time worst-case capacity probe, and the material-scope wrap for any
/// <see cref="ISdfSceneEmitter.OwnsMaterialScope"/> emitter.
/// <para>
/// Slot assignment: at construction, each configured emitter is assigned
/// a contiguous <see cref="SdfEmitContext.SlotBase"/> equal to the running sum of every earlier emitter's
/// <see cref="ISdfSceneEmitter.DynamicSlotCount"/> — emitter 0 starts at slot 0, emitter 1 starts where emitter 0's
/// range ends, and so on. This never changes for the lifetime of this instance (an emitter's <see cref="ISdfSceneEmitter.DynamicSlotCount"/>
/// must therefore stay constant — see its remarks).
/// </para>
/// <para>
/// The capacity probe: also at construction, one combined worst-case program is built by calling every emitter's
/// <see cref="ISdfSceneEmitter.Emit"/> with <see cref="SdfEmitContext.Probe"/> set — each in its own material scope
/// when <see cref="ISdfSceneEmitter.OwnsMaterialScope"/> is set — and measuring the result once
/// (<see cref="WorstCaseProgramWordCapacity"/>/<see cref="WorstCaseInstanceCapacity"/>/<see cref="WorstCaseDynamicTransformCapacity"/>).
/// This generalizes the per-host <c>MeasureWorstCaseEnvelope</c> pattern: any live rebuild (every real
/// <see cref="CaptureFrame"/> call) is a program built from the same emitters' non-probe branches, which by the probe
/// contract (see <see cref="ISdfSceneEmitter"/>) can never exceed what the probe measured.
/// </para>
/// <para>
/// Rebuild trigger: the composed program rebuilds only when some emitter's revision components
/// (<see cref="ISdfSceneEmitter.WriteRevision"/>) differ elementwise from what that emitter reported when the held
/// program was built (or on the first call) — an emitter that never changes (the default
/// <c>RevisionComponentCount =&gt; 0</c>) never forces a rebuild on its own. The comparison is against the whole
/// flattened vector of every emitter's every counter rather than any combined number, because a revision can move down
/// (one is assigned from a server-supplied snapshot value): two counters moving in opposite directions by the same
/// amount would cancel in any sum and hold a stale program, and any digest would only make that collision improbable
/// rather than impossible. This is why the contract hands over components and not a single number — a per-emitter
/// aggregate would just relocate the same cancellation one level down, where the host cannot see it.
/// </para></summary>
public sealed class SdfCompositionFrameSource : ISdfFrameSource {
    // The components as they stood when the program currently held in m_program was built. No sentinel is needed:
    // m_program is null until the first build, and that null is what forces it.
    private readonly int[] m_builtRevisions;
    private readonly ISdfFrameDresser m_dresser;
    private readonly IReadOnlyList<ISdfSceneEmitter> m_emitters;
    // This frame's freshly-read components, and the STAGING half of the built record: captured before a build, promoted
    // into m_builtRevisions only once that build has returned. See CaptureRevisions/CaptureFrame for why both halves
    // of that ordering are load-bearing.
    private readonly int[] m_pendingRevisions;
    // Where each emitter's revision components start inside the two flattened vectors below — Count + 1 entries, so
    // emitter i owns [m_revisionOffsets[i], m_revisionOffsets[i + 1]) and the last entry is the total length. Sized
    // once at construction from each emitter's RevisionComponentCount, which its contract pins for the emitter's life.
    private readonly int[] m_revisionOffsets;
    private readonly int[] m_slotBases;
    private readonly DynamicTransform[] m_transforms;

    private float m_interpolationAlpha;
    private SdfProgram? m_program;
    private float m_time;

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
        m_revisionOffsets = new int[(m_emitters.Count + 1)];

        var slotCursor = 0;
        var revisionCursor = 0;

        for (var index = 0; (index < m_emitters.Count); index++) {
            if (m_emitters[index] is not { } emitter) {
                throw new ArgumentNullException(
                    paramName: nameof(emitters),
                    message: $"emitters[{index}] is null."
                );
            }

            m_slotBases[index] = slotCursor;
            slotCursor += Math.Max(
                val1: 0,
                val2: emitter.DynamicSlotCount
            );
            // Both counts are read exactly ONCE, here, and both are contract-pinned for the emitter's lifetime — an
            // emitter that grew a component later would silently overrun the slice this layout handed it.
            m_revisionOffsets[index] = revisionCursor;
            revisionCursor += Math.Max(
                val1: 0,
                val2: emitter.RevisionComponentCount
            );
        }

        m_revisionOffsets[m_emitters.Count] = revisionCursor;
        m_builtRevisions = new int[revisionCursor];
        m_pendingRevisions = new int[revisionCursor];
        m_transforms = new DynamicTransform[slotCursor];

        var probe = BuildProgram(context: new SdfEmitContext(
            Probe: true,
            Time: 0f,
            RenderOrigin: Vector3.Zero,
            ParkPosition: ParkPosition,
            SlotBase: 0
        ));

        WorstCaseProgramWordCapacity = probe.Words.Length;
        WorstCaseInstanceCapacity = probe.Instances.Count;
        WorstCaseDynamicTransformCapacity = m_transforms.Length;
    }

    /// <summary>Gets the generic "far below anything" fallback park position — a host that needs a matching literal
    /// outside a live <see cref="SdfEmitContext"/> (a construction-time capacity probe, say) reads this rather than
    /// carrying its own copy.</summary>
    public static readonly Vector3 DefaultParkPosition = new(
        x: 0f,
        y: -1000f,
        z: 0f
    );
    /// <summary>Gets or sets where a hidden/unused dynamic-transform slot parks this frame (<see cref="SdfEmitContext.ParkPosition"/>)
    /// — settable so a host can move it to sit well outside its own world's camera/tile-cull reach. Changing this does
    /// not rebuild the program (it only affects <see cref="ISdfSceneEmitter.PackDynamicTransforms"/>, called every
    /// frame regardless).</summary>
    public Vector3 ParkPosition { get; set; } = DefaultParkPosition;
    /// <summary>Gets the dynamic-transform slot floor the render assembly must reserve — the sum of every registered
    /// emitter's <see cref="ISdfSceneEmitter.DynamicSlotCount"/>.</summary>
    public int WorstCaseDynamicTransformCapacity { get; }
    /// <summary>Gets the instance-count floor the engine's mask buffer must reserve (see <see cref="WorstCaseProgramWordCapacity"/>).</summary>
    public int WorstCaseInstanceCapacity { get; }
    /// <summary>Gets the packed-word floor the engine's program buffer must reserve — every registered emitter's probe
    /// form, combined into one program, measured once at construction.</summary>
    public int WorstCaseProgramWordCapacity { get; }

    // Shared by construction's probe build and every live rebuild: one builder, every emitter's Emit in list order,
    // wrapped in a material scope for any OwnsMaterialScope emitter (the ONLY behavior difference from calling Emit
    // directly — see SdfMaterialScope). context.SlotBase is overwritten per-emitter from m_slotBases; the caller's
    // context otherwise carries Probe/Time/RenderOrigin/ParkPosition through unchanged.
    private SdfProgram BuildProgram(SdfEmitContext context) {
        var builder = new SdfProgramBuilder();

        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];
            var emitContext = new SdfEmitContext(
                Probe: context.Probe,
                Time: context.Time,
                RenderOrigin: context.RenderOrigin,
                ParkPosition: context.ParkPosition,
                SlotBase: m_slotBases[index],
                InterpolationAlpha: context.InterpolationAlpha
            );

            if (emitter.OwnsMaterialScope) {
                using var scope = builder.BeginMaterialScope();

                emitter.Emit(
                    builder: builder,
                    context: in emitContext
                );
            } else {
                emitter.Emit(
                    builder: builder,
                    context: in emitContext
                );
            }
        }

        return builder.Build();
    }
    // THE REBUILD TRIGGER, and it must not be able to CANCEL — reading this frame's components into m_pendingRevisions
    // and reporting whether any of them moved, in one pass.
    //
    // CANCELLATION IS WHAT THIS SHAPE EXISTS TO KILL, and it has to be killed at the SOURCE, not layered over. An
    // elementwise compare of one number PER EMITTER is not enough while that number is itself a sum: not every counter
    // feeding it is monotonic (at least one is assigned from a server-supplied snapshot value and can move DOWN), so a
    // server revision falling by k while a sibling rises by k leaves that emitter's element unchanged, no rebuild
    // happens, and the frame renders silently stale geometry — the same defect, one level in. So emitters hand over
    // their COMPONENTS (ISdfSceneEmitter.WriteRevision) and this compares the flattened vector of all of them: with no
    // addition anywhere on the path, there is nothing left that can cancel. Impossible BY COMPARISON, not improbable
    // by hashing. Never re-fold any part of this into a sum, and never trade it for a digest — a digest reintroduces a
    // collision this shape does not have, and a per-emitter aggregate reintroduces the one just removed.
    //
    // Both arrays are sized once at construction against the defensively-copied emitter list, so the layout is fixed
    // for the instance's life and this allocates nothing; the indexed loop avoids an interface enumerator, and the
    // span slice hands each emitter exactly its own range. It deliberately does NOT stop at the first difference: the
    // whole pending vector must be filled, because a promotion after the build copies all of it.
    private bool CaptureRevisions() {
        var moved = false;

        for (var index = 0; (index < m_emitters.Count); index++) {
            var start = m_revisionOffsets[index];
            var length = (m_revisionOffsets[(index + 1)] - start);

            if (length == 0) {
                continue;
            }

            var components = m_pendingRevisions.AsSpan(
                length: length,
                start: start
            );

            m_emitters[index].WriteRevision(destination: components);

            for (var component = 0; (component < length); component++) {
                if (components[component] != m_builtRevisions[(start + component)]) {
                    moved = true;
                }
            }
        }

        return moved;
    }
    private void PackTransforms() {
        Array.Fill(
            array: m_transforms,
            value: new DynamicTransform(
                Position: ParkPosition,
                Orientation: Quaternion.Identity
            )
        );

        for (var index = 0; (index < m_emitters.Count); index++) {
            var emitter = m_emitters[index];

            if (emitter.DynamicSlotCount == 0) {
                continue;
            }

            var context = new SdfEmitContext(
                Probe: false,
                Time: m_time,
                RenderOrigin: Vector3.Zero,
                ParkPosition: ParkPosition,
                SlotBase: m_slotBases[index],
                InterpolationAlpha: m_interpolationAlpha
            );

            emitter.PackDynamicTransforms(
                context: in context,
                slots: m_transforms
            );
        }
    }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_time += MathF.Max(
            x: deltaSeconds,
            y: 0f
        );
        m_interpolationAlpha = interpolationAlpha;

        // Captured BEFORE the build, deliberately, and into PENDING rather than straight into the built record. Both
        // halves of that are load-bearing, against two different failures:
        //
        //   BEFORE the build — because these are the revisions the program is built FROM. Were an emitter's Emit ever
        //   to bump its own revision inputs mid-build, capturing afterwards would record the post-bump values and
        //   silently absorb a change the built program does not reflect. Capturing first leaves it visible next frame.
        //
        //   Into PENDING — because a build can THROW. Writing the built record up front would leave it claiming the
        //   held program was built from revisions no program was ever built from, and since the compare then sees no
        //   movement, the stale program is held indefinitely: one failed build turns into permanently frozen geometry.
        //   Promotion below happens only once BuildProgram has returned, so a throw leaves the record describing the
        //   program actually held and the next frame retries the rebuild.
        var moved = CaptureRevisions();

        if (
            (m_program is null) ||
            moved
        ) {
            m_program = BuildProgram(context: new SdfEmitContext(
                Probe: false,
                Time: m_time,
                RenderOrigin: Vector3.Zero,
                ParkPosition: ParkPosition,
                SlotBase: 0,
                InterpolationAlpha: interpolationAlpha
            ));

            Array.Copy(
                sourceArray: m_pendingRevisions,
                destinationArray: m_builtRevisions,
                length: m_builtRevisions.Length
            );
        }

        PackTransforms();

        return m_dresser.Dress(
            deltaSeconds: deltaSeconds,
            height: height,
            interpolationAlpha: interpolationAlpha,
            program: m_program,
            transforms: m_transforms,
            width: width
        );
    }
}
