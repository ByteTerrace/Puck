using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Names one of the cadence gate's hashed change-signature spans (see <see cref="SdfWorldEngine.DecideCadenceSkip"/>) —
/// a flag set in <see cref="SdfCadenceDiagnostics.ChangedSpans"/> means that span's bytes differed from the previous
/// decided frame's, i.e. it is a candidate driver of a never-skipping gate.</summary>
[Flags]
public enum SdfCadenceSpan {
    /// <summary>No span changed.</summary>
    None = 0,
    /// <summary>The program/decal revisions + live viewport count.</summary>
    Revisions = (1 << 0),
    /// <summary>The Stage 0/1 push constant (extent, tile grid, viewport/child/screen masks, instance-mask width).</summary>
    Push = (1 << 1),
    /// <summary>The per-view camera/region/render-scale table, excluding each row's presentation-time lane.</summary>
    Viewports = (1 << 2),
    /// <summary>The per-entity dynamic-transform table.</summary>
    Dynamics = (1 << 3),
    /// <summary>The screen-surface sampling-frame table.</summary>
    ScreenSurfaces = (1 << 4),
    /// <summary>The screen-light/environment/grid/bench-lever table.</summary>
    ScreenLights = (1 << 5),
}
/// <summary>The cadence gate's diagnostics for the most recently decided frame — read-only, never fed back into the
/// skip decision. Exposed via <see cref="SdfWorldEngine.CadenceDiagnostics"/> and surfaced by the <c>sdf.info</c>
/// verb's cadence section, so a live session names exactly which span keeps a static scene from skipping instead of
/// guessing.</summary>
/// <param name="GateEnabled">Whether the gate was armed (<see cref="SdfFrame.EnableCadenceGate"/>) for this frame.</param>
/// <param name="Skipped">Whether this frame skipped the sky/mask/beam/cull-args/views passes.</param>
/// <param name="SkippedFrameCount">The cumulative skipped-frame count since the gate last armed (reset whenever the gate turns off).</param>
/// <param name="RenderedFrameCount">The cumulative fully-rendered-frame count since the gate last armed (reset alongside <paramref name="SkippedFrameCount"/>).</param>
/// <param name="RevisionsHash">This frame's independent FNV-1a hash of the revisions span (see <see cref="SdfCadenceSpan.Revisions"/>).</param>
/// <param name="PushHash">This frame's independent hash of the push-constant span.</param>
/// <param name="ViewportsHash">This frame's independent hash of the viewport span (time lane excluded).</param>
/// <param name="DynamicsHash">This frame's independent hash of the dynamic-transform span.</param>
/// <param name="ScreenSurfacesHash">This frame's independent hash of the screen-surface span.</param>
/// <param name="ScreenLightsHash">This frame's independent hash of the screen-light span.</param>
/// <param name="ChangedSpans">Which spans' hashes differ from the previous decided frame's — the payload a human reads
/// to find the never-skipping driver.</param>
/// <param name="ScreenSourceBound">Whether any screen source slot is bound this frame (<c>m_screenSourceMask != 0</c>)
/// — informational only: it does not gate the skip decision, because a live console booted anywhere in the engine
/// binds this per-engine mask independent of which program is uploaded, and sampleScreenSurface is unreachable
/// without a ScreenSlab material — see <see cref="SdfWorldEngine.DecideCadenceSkip"/>'s coverage rationale.</param>
/// <param name="ProgramDeclaresScreenSlab">Whether the live uploaded program declares any ScreenSlab shape — the
/// first of the two conditions not covered by any hashed span (see <see cref="SdfWorldEngine.DecideCadenceSkip"/>).</param>
/// <param name="BrickBaking">Whether a carve-bake is in progress (the second uncovered condition).</param>
public readonly record struct SdfCadenceDiagnostics(
    bool GateEnabled,
    bool Skipped,
    ulong SkippedFrameCount,
    ulong RenderedFrameCount,
    ulong RevisionsHash,
    ulong PushHash,
    ulong ViewportsHash,
    ulong DynamicsHash,
    ulong ScreenSurfacesHash,
    ulong ScreenLightsHash,
    SdfCadenceSpan ChangedSpans,
    bool ScreenSourceBound,
    bool ProgramDeclaresScreenSlab,
    bool BrickBaking
);
public sealed partial class SdfWorldEngine {
    // Feeds the same byte sequence into two independent FNV-1a accumulators in one pass — the combined signature and
    // this span's own diagnostics hash — so the per-span hash costs no second traversal of the buffer.
    private static void AddToBoth(ref Fnv1aHash combined, ref Fnv1aHash span, ReadOnlySpan<byte> values) {
        foreach (var value in values) {
            combined.Add(value: value);
            span.Add(value: value);
        }
    }
    private static void AddViewportsExcludingTime(ref Fnv1aHash combined, ref Fnv1aHash span, ReadOnlySpan<byte> viewportScratch) {
        const int TimeLaneOffset = (sizeof(float) * 3); // position.xyz precede time in each row (PackViewports)
        const int TimeLaneLength = sizeof(float);

        for (var rowStart = 0; (rowStart < viewportScratch.Length); rowStart += ViewportByteLength) {
            var row = viewportScratch.Slice(
                length: ViewportByteLength,
                start: rowStart
            );

            AddToBoth(
                combined: ref combined,
                span: ref span,
                values: row[..TimeLaneOffset]
            );
            AddToBoth(
                combined: ref combined,
                span: ref span,
                values: row[(TimeLaneOffset + TimeLaneLength)..]
            );
        }
    }
    // Whether any brick slot is mid-bake: a Baking slot has RecordBrickBakeSlices writing new voxels every frame, so the
    // sampled field the beam/views marches changes with no packed-span change — the cadence gate must render through it.
    private bool AnyBrickBaking() {
        if (m_brickBakePipeline is null) {
            return false;
        }

        for (var slot = 0; (slot < SdfBrickPoolLayout.MaxBricks); slot++) {
            if (m_brickStates[slot] == BrickBakeState.Baking) {
                return true;
            }
        }

        return false;
    }
    // The 64-bit FNV-1a change signature over every packed span + revision the skipped passes consume (see
    // DecideCadenceSkip for the coverage rationale), computed in the SAME pass as each span's own independent
    // diagnostics hash (CadenceSpanHashes) — one traversal of the packed buffers feeds both the combined accumulator
    // and that span's accumulator per byte (AddToBoth), rather than hashing every buffer twice. Hashing the WHOLE
    // scratch buffers (including any rows past the live count) is deliberately conservative: extra stale bytes can
    // only make two frames look DIFFERENT (a redundant render), never make a changed frame look the SAME (a stale
    // skip). A collision would require a 64-bit hash clash across two genuinely different input sets — negligible,
    // and still only presentation, never simulation.
    private (ulong Signature, CadenceSpanHashes SpanHashes) ComputeFrameSignature(uint viewportCount) {
        var combined = Fnv1aHash.Create();
        var revisionsSpan = Fnv1aHash.Create();
        var pushSpan = Fnv1aHash.Create();
        var viewportsSpan = Fnv1aHash.Create();
        var dynamicsSpan = Fnv1aHash.Create();
        var screenSurfacesSpan = Fnv1aHash.Create();
        var screenLightsSpan = Fnv1aHash.Create();

        Span<byte> revisions = stackalloc byte[(sizeof(ulong) * 3)];

        MemoryMarshal.Write(
            destination: revisions[..sizeof(ulong)],
            value: in m_programRevision
        );
        MemoryMarshal.Write(
            destination: revisions.Slice(
                length: sizeof(ulong),
                start: sizeof(ulong)
            ),
            value: in m_decalRevision
        );
        var viewportCountWide = ((ulong)viewportCount);

        MemoryMarshal.Write(
            destination: revisions.Slice(
                length: sizeof(ulong),
                start: (sizeof(ulong) * 2)
            ),
            value: in viewportCountWide
        );

        AddToBoth(
            combined: ref combined,
            span: ref revisionsSpan,
            values: revisions
        );
        AddToBoth(
            combined: ref combined,
            span: ref pushSpan,
            values: m_pushConstant
        );
        AddViewportsExcludingTime(
            combined: ref combined,
            span: ref viewportsSpan,
            viewportScratch: m_viewportScratch
        );
        AddToBoth(
            combined: ref combined,
            span: ref dynamicsSpan,
            values: m_dynamicTransformScratch
        );
        AddToBoth(
            combined: ref combined,
            span: ref screenSurfacesSpan,
            values: m_screenSurfaceScratch
        );
        AddToBoth(
            combined: ref combined,
            span: ref screenLightsSpan,
            values: m_screenLightScratch
        );

        return (
            combined.Value,
            new CadenceSpanHashes(
                Revisions: revisionsSpan.Value,
                Push: pushSpan.Value,
                Viewports: viewportsSpan.Value,
                Dynamics: dynamicsSpan.Value,
                ScreenSurfaces: screenSurfacesSpan.Value,
                ScreenLights: screenLightsSpan.Value
            )
        );
    }
    // Cadence gate: latches whether Record may skip the sky/mask/beam/cull-args/views passes and re-composite from
    // the retained (ring-shared) views output + tile buffer. A skip is permitted only when the gate is enabled, this
    // frame's change signature exactly matches the last rendered frame's, the live program declares no ScreenSlab,
    // and no carve bake is in progress.
    //
    // Signature coverage — the signature (ComputeFrameSignature) folds in everything the five skipped passes consume
    // (the sky pass reads only m_viewportScratch + m_screenLightScratch, both already covered below):
    //   - m_programRevision  : the uploaded program (words, live instance-mask width, kernel variant, reseeded
    //                          screen-surface table, invariant instance grid) — bumped by UploadProgram.
    //   - m_pushConstant     : Stage 0/1 push — width/height/tileGrid (constant), viewportCount, childMask,
    //                          screenSourceMask (bound-slot bitmask), liveInstanceMaskWordCount.
    //   - m_viewportScratch  : per-view camera basis + fov/aspect, region, debug view mode, the quantized
    //                          render-scale numerator, and the frame's far distance — excluding each row's presentation-time lane (PackViewports'
    //                          position.w; byte offset 12 of each 96-byte ViewportData row). Time free-runs every
    //                          frame (it feeds the animated test-card in screenContent, sdf-world.hlsli), so hashing
    //                          it would make the signature never repeat and the gate permanently inert. Any camera
    //                          ease still counts (it changes the surrounding lanes in the same row).
    //   - m_dynamicTransformScratch : every moving entity's position/orientation + soft-shadow participation. Also
    //                          covers the frame instance grid (a pure function of these transforms + the program).
    //   - m_screenSurfaceScratch : the screen-surface sampling table (a slab riding a dynamic rig re-poses here).
    //   - m_screenLightScratch : per-screen glow colors + the environment row (ambient/sun/slice) + the grid-overlay
    //                          rows + the engine-bench lever rows (soft-shadow/AO/shadow-distance/screen-lights) + the
    //                          shadow-proxy rows + the analytic-normal and shadow-cull toggles — every shading lever.
    //   - m_decalRevision    : the glyph-decal buffer — revision-tracked (it is 820 KB, not re-hashed each frame).
    // Deliberately excluded (composite-only inputs — composite runs every frame, so a change to them is applied by
    // this frame's composite and can never produce a stale pixel): the composite push's UpscaleSharpness lane and
    // WarpAmount. The region layout is covered (it rides m_viewportScratch, because Stage 1 renders into the region
    // extent).
    // Not covered by any packed span — handled conservatively by forcing a render:
    //   - m_programDeclaresScreenSlab (computed once at UploadProgram — see there): covers both the declared-but-
    //     unbound case (the excluded time lane is the sole per-frame driver of screenContent's test-card —
    //     sdf-world.hlsli's renderView reads it into `time` at exactly one call site, gated on
    //     `material >= SDF_SCREEN_MATERIAL`) and the bound case (a live CRT's image content updates in place each
    //     frame with the same view handle, unseen by any packed span) — force-renders on any declared ScreenSlab
    //     regardless of binding.
    //   - AnyBrickBaking() : an in-progress carve bake writing brick voxels each frame.
    // Refinement path: a per-source content revision the provider supplies (then a static bound source could skip),
    // and a "settled" flag once every bake completes.
    private void DecideCadenceSkip(SdfFrame frame, uint viewportCount) {
        if (!frame.EnableCadenceGate) {
            // OFF: never skip (byte-identical to a build without the gate), and forget any prior signature so the first
            // frame after the gate is re-enabled always renders before it can skip. Diagnostics reset alongside it (the
            // "since gate-arm" counters restart the instant the gate re-arms).
            m_skipThisFrame = false;
            m_hasPreviousFrameSignature = false;
            m_hasPreviousCadenceSpanHashes = false;
            m_cadenceSkippedFrameCount = 0;
            m_cadenceRenderedFrameCount = 0;
            m_cadenceDiagnostics = default;

            return;
        }

        var (signature, spanHashes) = ComputeFrameSignature(viewportCount: viewportCount);
        var brickBaking = AnyBrickBaking();

        m_skipThisFrame =
            (m_hasPreviousFrameSignature &&
            (signature == m_previousFrameSignature) &&
            !m_programDeclaresScreenSlab &&
            !brickBaking);
        m_previousFrameSignature = signature;
        m_hasPreviousFrameSignature = true;

        if (m_skipThisFrame) {
            m_cadenceSkippedFrameCount++;
        } else {
            m_cadenceRenderedFrameCount++;
        }

        var changedSpans = SdfCadenceSpan.None;

        if (m_hasPreviousCadenceSpanHashes) {
            if (spanHashes.Revisions != m_previousCadenceSpanHashes.Revisions) { changedSpans |= SdfCadenceSpan.Revisions; }
            if (spanHashes.Push != m_previousCadenceSpanHashes.Push) { changedSpans |= SdfCadenceSpan.Push; }
            if (spanHashes.Viewports != m_previousCadenceSpanHashes.Viewports) { changedSpans |= SdfCadenceSpan.Viewports; }
            if (spanHashes.Dynamics != m_previousCadenceSpanHashes.Dynamics) { changedSpans |= SdfCadenceSpan.Dynamics; }
            if (spanHashes.ScreenSurfaces != m_previousCadenceSpanHashes.ScreenSurfaces) { changedSpans |= SdfCadenceSpan.ScreenSurfaces; }
            if (spanHashes.ScreenLights != m_previousCadenceSpanHashes.ScreenLights) { changedSpans |= SdfCadenceSpan.ScreenLights; }
        }

        m_cadenceDiagnostics = new SdfCadenceDiagnostics(
            GateEnabled: true,
            Skipped: m_skipThisFrame,
            SkippedFrameCount: m_cadenceSkippedFrameCount,
            RenderedFrameCount: m_cadenceRenderedFrameCount,
            RevisionsHash: spanHashes.Revisions,
            PushHash: spanHashes.Push,
            ViewportsHash: spanHashes.Viewports,
            DynamicsHash: spanHashes.Dynamics,
            ScreenSurfacesHash: spanHashes.ScreenSurfaces,
            ScreenLightsHash: spanHashes.ScreenLights,
            ChangedSpans: changedSpans,
            ScreenSourceBound: (0u != m_screenSourceMask),
            ProgramDeclaresScreenSlab: m_programDeclaresScreenSlab,
            BrickBaking: brickBaking
        );
        m_previousCadenceSpanHashes = spanHashes;
        m_hasPreviousCadenceSpanHashes = true;
    }
    // Whether the program's instruction stream declares any shape of the given type — a one-time UploadProgram walk
    // backing per-program facts (the SampledRegion frozen-envelope guard; the cadence gate's ScreenSlab force-render).
    private static bool ProgramDeclaresShape(SdfProgram program, SdfShapeType shapeType) {
        foreach (var instruction in program.Instructions) {
            if (
                (instruction.Op == SdfOp.ShapeBlend) &&
                (((SdfShapeType)instruction.Shape) == shapeType)
            ) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The cadence gate's per-span diagnostics for the most recently decided frame (see <see cref="SdfCadenceDiagnostics"/>).
    /// Default (all-zero, <see cref="SdfCadenceDiagnostics.GateEnabled"/> false) until the gate first arms.</summary>
    public SdfCadenceDiagnostics CadenceDiagnostics => m_cadenceDiagnostics;

    // The same six spans ComputeFrameSignature chains, but each hashed INDEPENDENTLY (fresh from the FNV basis) so
    // DecideCadenceSkip can name exactly which span changed frame-to-frame. Never used for the skip decision — a
    // diagnostics-only read of state ComputeFrameSignature already touched.
    private readonly record struct CadenceSpanHashes(ulong Revisions, ulong Push, ulong Viewports, ulong Dynamics, ulong ScreenSurfaces, ulong ScreenLights);
}
