using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private static void AddViewportsExcludingTime(ref Fnv1aHash hash, ReadOnlySpan<byte> viewportScratch) {
        const int TimeLaneOffset = (sizeof(float) * 3); // position.xyz precede time in each row (PackViewports)
        const int TimeLaneLength = sizeof(float);

        for (var rowStart = 0; (rowStart < viewportScratch.Length); rowStart += ViewportByteLength) {
            var row = viewportScratch.Slice(
                length: ViewportByteLength,
                start: rowStart
            );

            hash.Add(values: row[..TimeLaneOffset]);
            hash.Add(values: row[(TimeLaneOffset + TimeLaneLength)..]);
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
    // DecideCadenceSkip for the coverage rationale). Hashing the WHOLE scratch buffers (including any rows past the live
    // count) is deliberately conservative: extra stale bytes can only make two frames look DIFFERENT (a redundant
    // render), never make a changed frame look the SAME (a stale skip). A collision would require a 64-bit hash clash
    // across two genuinely different input sets — negligible, and still only presentation, never simulation.
    private ulong ComputeFrameSignature(uint viewportCount) {
        var hash = Fnv1aHash.Create();

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

        hash.Add(values: revisions);
        hash.Add(values: m_worldBlock);
        AddViewportsExcludingTime(
            hash: ref hash,
            viewportScratch: m_viewportScratch
        );
        Span<byte> dynamicsRevision = stackalloc byte[sizeof(ulong)];

        MemoryMarshal.Write(
            destination: dynamicsRevision,
            value: in m_dynamicTransformRevision
        );
        hash.Add(values: dynamicsRevision);
        hash.Add(values: m_screenSurfaceRegion.Contents);
        hash.Add(values: m_screenLightScratch);

        return hash.Value;
    }
    // Cadence gate: latches whether Record may skip every view's sky/mask/beam/cull-args/primary/surface/ambient/views
    // passes, leaving each view's retained output standing. A skip is permitted only when the gate is enabled, this
    // frame's change signature exactly matches the last rendered frame's, the live program declares no ScreenSlab,
    // no carve bake is in progress, and no view's output was replaced this frame (a new image holds nothing).
    //
    // Signature coverage — the signature (ComputeFrameSignature) folds in everything the five skipped passes consume
    // (the sky pass reads only m_viewportScratch + m_screenLightScratch, both already covered below):
    //   - m_programRevision  : the uploaded program (words, live instance-mask width, kernel variant, reseeded
    //                          screen-surface table, invariant instance grid) — bumped by UploadProgram.
    //   - m_worldBlock       : the world values every view's block shares — width/height/tileGrid (constant),
    //                          viewportCount, screenSourceMask (bound-slot bitmask), liveInstanceMaskWordCount and the
    //                          twinkle tick; each view's own extent and index follow from these and the viewports.
    //   - m_viewportScratch  : per-view camera basis + fov/aspect, render extent, debug view mode, the off-axis
    //                          offset, and the frame's far distance — excluding each row's presentation-time lane (PackViewports'
    //                          position.w; byte offset 12 of each 96-byte ViewportData row). Time free-runs every
    //                          frame (it feeds the animated test-card in screenContent, sdf-world.hlsli), so hashing
    //                          it would make the signature never repeat and the gate permanently inert. Any camera
    //                          ease still counts (it changes the surrounding lanes in the same row).
    //   - m_dynamicTransformRevision : bumped whenever a frame packs an owed dynamic-transform row (the producer's moved
    //                          set: every moving entity's position/orientation/lanes + soft-shadow participation), so
    //                          the table is never re-hashed. Also covers the frame instance grid (a pure function of these transforms +
    //                          the program).
    //   - m_screenSurfaceRegion : the screen-surface sampling table (a slab riding a dynamic rig re-poses here).
    //   - m_screenLightScratch : per-screen glow colors + the environment row (ambient/sun/slice) + the grid-overlay
    //                          rows + the engine-bench lever rows (soft-shadow/AO/shadow-distance/screen-lights) + the
    //                          shadow-proxy rows + the analytic-normal and shadow-cull toggles — every shading lever.
    //   - m_decalRevision    : the glyph-decal buffer — revision-tracked (it is 820 KB, not re-hashed each frame).
    // The rect a view is placed in and its reconstruction sharpness are the render graph's (its place pass runs every
    // frame), never this engine's input.
    // Not covered by any packed span — handled conservatively by forcing a render:
    //   - m_programDeclaresScreenSlab (computed once at UploadProgram — see there): covers both the declared-but-
    //     unbound case (the excluded time lane is the sole per-frame driver of screenContent's test-card —
    //     sdf-world.hlsli's renderView reads it into `time` at exactly one call site, gated on
    //     `material >= SDF_SCREEN_MATERIAL`) and the bound case (a live CRT's image content updates in place each
    //     frame with the same view handle, unseen by any packed span) — force-renders on any declared ScreenSlab
    //     regardless of binding.
    //   - AnyBrickBaking() : an in-progress carve bake writing brick voxels each frame.
    //   - frame.Volumes    : bounded volumes animate on the excluded time lane (shadeVolumes), and their table is
    //                        not hashed, so any volume forces a render.
    // Refinement path: a per-source content revision the provider supplies (then a static bound source could skip),
    // and a "settled" flag once every bake completes.
    private void DecideCadenceSkip(SdfFrame frame, uint viewportCount) {
        if (!frame.EnableCadenceGate) {
            // OFF: never skip (byte-identical to a build without the gate), and forget any prior signature so the first
            // frame after the gate is re-enabled always renders before it can skip.
            m_skipThisFrame = false;
            m_hasPreviousFrameSignature = false;

            return;
        }

        var signature = ComputeFrameSignature(viewportCount: viewportCount);

        m_skipThisFrame =
            (m_hasPreviousFrameSignature &&
            (signature == m_previousFrameSignature) &&
            !m_programDeclaresScreenSlab &&
            !AnyBrickBaking() &&
            !m_viewOutputReplaced &&
            (frame.Volumes.Count == 0));
        m_previousFrameSignature = signature;
        m_hasPreviousFrameSignature = true;
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
}
