using System.Numerics;

namespace Puck.SdfVm;

/// <summary>The per-call context an <see cref="ISdfSceneEmitter"/> emits against: whether this is the ONE worst-case
/// capacity probe (never rendered — see <see cref="ISdfSceneEmitter.Emit"/>) or a live frame, the presentation clock,
/// where "no render-relative offset" sits (most emitters ignore it today — every current host anchors render-relative
/// space at its spawn cell, so this is <see cref="Vector3.Zero"/> in practice; it exists so a future emitter that DOES
/// author render-relative geometry has a seam without a contract change), where a hidden/parked dynamic slot should
/// sit (far outside the camera's reach), and the emitter's own assigned dynamic-transform slot range base (see
/// <see cref="ISdfSceneEmitter.DynamicSlotCount"/>).</summary>
/// <param name="Probe">
/// <see langword="true"/> for the ONE construction-time capacity probe (see <see cref="SdfCompositionFrameSource"/>):
/// the emitter must take its LARGEST legal form — every optional shape present, every modifier at its worst-case
/// value — so the probed program/instance/dynamic-transform envelope is a true ceiling no live rebuild can exceed.
/// <see langword="false"/> for an ordinary live frame (the emitter's actual current state).
/// </param>
/// <param name="Time">The presentation clock (seconds), for time-based emission (a hover bob, an eased glow) —
/// never simulation state; a static emitter ignores it.</param>
/// <param name="RenderOrigin">The render-relative origin an emitter authoring render-relative geometry should
/// subtract from world-space positions before emitting. <see cref="Vector3.Zero"/> today (every current host anchors
/// render-relative space at its spawn cell) — reserved for a future emitter that needs it.</param>
/// <param name="ParkPosition">Where a hidden/unused dynamic-transform slot should sit this frame (a "parked" position
/// far outside anything the camera or the tile-cull beam reaches) — the composed value a well-behaved
/// <see cref="ISdfSceneEmitter.PackDynamicTransforms"/> writes for any slot it isn't actively using this frame.</param>
/// <param name="SlotBase">The first dynamic-transform slot index this emitter owns (see
/// <see cref="ISdfSceneEmitter.DynamicSlotCount"/>) — every slot this emitter writes in
/// <see cref="ISdfSceneEmitter.PackDynamicTransforms"/>, and every <see cref="SdfProgramBuilder.TransformDynamic"/>/
/// <see cref="SdfProgramBuilder.BeginInstanceDynamic"/> slot it bakes in <see cref="ISdfSceneEmitter.Emit"/>, must fall
/// in <c>[SlotBase, SlotBase + DynamicSlotCount)</c>. Assigned contiguously by the composition host at construction
/// (accumulating each registered emitter's <see cref="ISdfSceneEmitter.DynamicSlotCount"/> in list order), so it is
/// stable for the emitter's whole lifetime.</param>
/// <param name="InterpolationAlpha">The fraction in <c>[0, 1)</c> toward the current fixed simulation tick — the SAME
/// value <see cref="ISdfFrameSource.CaptureFrame"/> received this call, threaded down so an emitter that needs to
/// pose ANCHOR-driven content (a camera rig following a live body, a smoothed pose read straight from sim state
/// rather than from an already-alpha-baked owner field) has it without reaching into host-private state. 0 for the
/// ONE construction-time probe (<see cref="SdfEmitContext.Probe"/> — there is no simulation tick to interpolate
/// toward yet). Most emitters ignore it (their content is already baked into a render-relative transform upstream,
/// like <c>OverworldFrameSource.PackPlayerRenderTransforms</c>) — it exists for the emitter that needs the raw
/// fraction itself.</param>
public readonly record struct SdfEmitContext(
    bool Probe,
    float Time,
    Vector3 RenderOrigin,
    Vector3 ParkPosition,
    int SlotBase,
    float InterpolationAlpha = 0f
);

/// <summary>One composable content source for an SDF world program — a room's fixed geometry, a sculpted scene, an
/// authoring pool, a debug takeover, or (later) an RTS terrain/unit layer. <see cref="SdfCompositionFrameSource"/>
/// holds a fixed list of these and rebuilds ONE shared <see cref="SdfProgramBuilder"/> from them, so a host composes a
/// world program by picking a list of emitters instead of hand-writing one 2000-line <c>BuildProgram</c> method.
/// <para>
/// THE PROBE CONTRACT (load-bearing — see <see cref="SdfEmitContext.Probe"/>): every emitter's <see cref="Emit"/> must
/// have a branch that, when <c>context.Probe</c> is <see langword="true"/>, takes its LARGEST legal form — every
/// optional shape emitted, every modifier at its worst-case magnitude, every dynamic slot present — so the ONE
/// construction-time probe <see cref="SdfCompositionFrameSource"/> runs (combining every emitter's probe form into a
/// single program) freezes a program-word/instance/dynamic-transform envelope no LIVE rebuild can ever exceed. A new
/// optional emission an emitter grows MUST grow its own probe branch in the same change, or a live rebuild can outgrow
/// the once-sized engine buffers and <see cref="SdfWorldEngine.UploadProgram"/> throws loudly at runtime (the
/// capacity-probe doctrine — see the sdf-world skill). The probe branch of each emitter must DOMINATE its live branch
/// on its own — never reason about it across the whole composed program.
/// </para></summary>
public interface ISdfSceneEmitter {
    /// <summary>Emits this source's content into <paramref name="builder"/> — the shared program under construction.
    /// Every shape/instance this call declares belongs to the composed program; a takeover (a mode that REPLACES the
    /// rest of the scene, like the SDF debugger) is not composed alongside other emitters — the host swaps in an
    /// ALTERNATE emitter list for it instead (never expressed inside one emitter's <see cref="Emit"/>).</summary>
    /// <param name="builder">The shared program builder (earlier emitters in the list may already have added
    /// content — materials/instructions/instances accumulate across the whole composed list).</param>
    /// <param name="context">This call's context (see <see cref="SdfEmitContext"/>) — <see cref="SdfEmitContext.Probe"/>
    /// selects the worst-case branch; <see cref="SdfEmitContext.SlotBase"/> is this emitter's assigned dynamic-transform
    /// range.</param>
    void Emit(SdfProgramBuilder builder, in SdfEmitContext context);

    /// <summary>How many dynamic-transform slots this emitter owns (0 = none, the default — a purely static emitter).
    /// Read ONCE by the composition host to assign <see cref="SdfEmitContext.SlotBase"/> ranges contiguously; must stay
    /// constant for the emitter's lifetime (it is never re-read mid-session to reshuffle already-assigned bases).</summary>
    int DynamicSlotCount => 0;

    /// <summary>Packs this frame's per-slot dynamic transforms into <paramref name="slots"/> — the FULL shared
    /// per-frame buffer every emitter's slots share; write only <c>slots[context.SlotBase]</c> through
    /// <c>slots[context.SlotBase + DynamicSlotCount - 1]</c>. The default no-op is correct for a <see cref="DynamicSlotCount"/>-0
    /// emitter (there is nothing to pack). A slot this call doesn't actively use this frame should get
    /// <see cref="SdfEmitContext.ParkPosition"/>, never a stale/zero transform (a parked slot must read as hidden, not
    /// as identity-posed geometry at the origin).</summary>
    /// <param name="slots">The shared per-frame dynamic-transform buffer.</param>
    /// <param name="context">This call's context — supplies <see cref="SdfEmitContext.SlotBase"/>,
    /// <see cref="SdfEmitContext.ParkPosition"/>, and <see cref="SdfEmitContext.Time"/>.</param>
    void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) { }

    /// <summary>How many INDEPENDENT revision counters this emitter watches (0 = never changes, the default — a purely
    /// static emitter). Read ONCE by the composition host to lay out its revision vector, so — exactly like
    /// <see cref="DynamicSlotCount"/> — it must stay constant for the emitter's lifetime.
    /// <para>
    /// AN EMITTER WATCHING SEVERAL COUNTERS DECLARES SEVERAL COMPONENTS, NEVER THEIR SUM. A sum can CANCEL: not every
    /// counter in this codebase is monotonic (at least one is assigned from a server-supplied snapshot value and can
    /// move DOWN), so one term rising by k while another falls by k leaves the total unchanged and the host holds a
    /// stale program. Keeping the counters apart makes that impossible by construction rather than by hoping every
    /// contributor only ever counts up.
    /// </para></summary>
    int RevisionComponentCount => 0;

    /// <summary>Writes this emitter's current revision counters into <paramref name="destination"/> — the host's own
    /// scratch, sliced to exactly <see cref="RevisionComponentCount"/> entries. The composed program rebuilds whenever
    /// ANY component differs from what this emitter reported when the held program was built, so bumping a counter on a
    /// state edit is how an emitter signals "my next <see cref="Emit"/> would produce different content".
    /// <para>
    /// MUST write every entry of <paramref name="destination"/>: the host reuses one buffer across frames, so an unwritten
    /// entry silently carries last frame's value and can mask the very change this call exists to report. Called every
    /// frame on the render path — read counters, allocate nothing. The default no-op is correct for a
    /// <see cref="RevisionComponentCount"/>-0 emitter (the slice is empty).
    /// </para></summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    void WriteRevision(Span<int> destination) { }

    /// <summary>Whether this emitter OWNS its material palette: <see langword="true"/> tells the composition host to
    /// wrap this emitter's <see cref="Emit"/> call in a <see cref="SdfProgramBuilder.BeginMaterialScope"/> scope, so
    /// any positional material recolor it emits — <see cref="SdfProgramBuilder.WallpaperFold"/>/
    /// <see cref="SdfProgramBuilder.RepeatPolar"/> with a nonzero <c>materialStride</c> — can only ever reach materials
    /// THIS emitter itself registered via <see cref="SdfProgramBuilder.AddMaterial"/>, instead of leaving that to
    /// author discipline.
    /// <para>
    /// An emitter that authors a positional stride today MUST set this. An emitter whose palette is its own but that
    /// emits no stride yet MAY also set it, and should: the scope is INERT without a stride (its clamp never runs, and
    /// the composed words are byte-identical to the unscoped build), so declaring ownership up front costs nothing and
    /// makes a stride grown later safe by construction rather than by remembering. Default <see langword="false"/>.
    /// </para></summary>
    bool OwnsMaterialScope => false;
}
