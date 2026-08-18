using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Re-uploads the scene program (the host's <c>ProgramChanged</c> path — e.g. a rebuilt overworld scene).
    /// The program must fit the buffers sized at construction (including its screen-surface table and its per-tile
    /// instance-mask width).</summary>
    /// <param name="program">The scene program to upload.</param>
    /// <exception cref="ArgumentException">The program contains an opcode not declared by <see cref="SdfOp"/>, or its
    /// instance count derives a wider per-tile mask than the construction program's (the mask buffer cannot grow after
    /// construction).</exception>
    public void UploadProgram(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);
        program.ValidateIsa();

        if (program.Words.Length > m_programWordCapacity) {
            throw new ArgumentException(
                message: $"The uploaded program has {program.Words.Length} packed words; the engine was constructed for {m_programWordCapacity} (construct the engine with the larger program).",
                paramName: nameof(program)
            );
        }

        if (program.InstanceMaskWordCount > m_instanceMaskWordCount) {
            throw new ArgumentException(
                message: $"The uploaded program's instance count derives {program.InstanceMaskWordCount} mask words per tile; the engine was constructed for {m_instanceMaskWordCount} (construct the engine with the wider program).",
                paramName: nameof(program)
            );
        }

        if (program.Instances.Count > m_instanceCapacity) {
            throw new ArgumentException(
                message: $"The uploaded program has {program.Instances.Count} instances; the engine was constructed for {m_instanceCapacity} frame-grid entries (increase InstanceCapacity or construct the engine with the larger program).",
                paramName: nameof(program)
            );
        }

        if (program.RequiredDynamicTransformCapacity > m_dynamicTransformCapacity) {
            throw new ArgumentException(
                message: $"The uploaded program requires {program.RequiredDynamicTransformCapacity} dynamic-transform slots; the engine was constructed for {m_dynamicTransformCapacity} (increase DynamicTransformCapacity or construct the engine with the larger program).",
                paramName: nameof(program)
            );
        }

        // Baking and rendering are SPLIT: a pool-less engine (BrickPoolVoxelCapacity 0) still accepts a SampledRegion
        // program. It cannot BAKE (RequestBrickBake stays a loud rejection — nothing to write into), but it RENDERS the
        // region via the shader's conservative uncarved-hull fallback (sdfSampledRegion detects the single-float filler
        // by element count and returns SDF_FAR_DISTANCE, so the Subtraction never bites). Only the pool's own capacity
        // (checked in RequestBrickBake) is the frozen envelope now — not the program's shape declaration.

        // The program buffer is SHARED across the frame ring (a program swap is a rare host event, not per-frame
        // state), so rewriting it must first drain every in-flight frame still reading the current words. A no-op when
        // nothing is outstanding (construction, or a waited harness).
        WaitForFrameRing();

        // A program whose grid contains no active maskable dynamic instance has one invariant ring-local table. Build
        // it against the engine's actual capacity envelope and seed every now-idle slot once. Programs with moving
        // binnable instances retain the per-frame build after the matching transform upload.
        var rebuildInstanceGridPerFrame = program.RequiresFrameInstanceGridRebuild;
        ReadOnlySpan<uint> invariantInstanceGrid = default;

        if (!rebuildInstanceGridPerFrame) {
            invariantInstanceGrid = program.BuildInvariantFrameInstanceGrid(
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );
            ValidateInstanceGridCapacity(words: invariantInstanceGrid);
        }

        m_programBuffer.Write<uint>(data: program.Words);
        // Seed the host-side mirror from the program's declared surfaces (the "program uploaded once" baseline); any
        // SetScreenSurface call made before the next produced frame patches this same mirror before it goes out — a
        // re-upload never resurrects the program's original frame over a live SetScreenSurface write made in between.
        MemoryMarshal.Cast<uint, byte>(span: program.ScreenSurfaceWords).CopyTo(destination: m_screenSurfaceScratch);
        // Every ring slot's copy is now stale relative to the freshly seeded mirror (all idle after the drain above);
        // PrepareFrame's dirty gate catches each one up on its next turn — mirrors m_decalDirty's pattern.
        Array.Fill(
            array: m_screenSurfaceDirty,
            value: true
        );

        if (!rebuildInstanceGridPerFrame) {
            foreach (var instanceGridBuffer in m_instanceGridBuffers) {
                instanceGridBuffer.Write<uint>(data: invariantInstanceGrid);
            }
        }

        m_liveInstanceMaskWordCount = program.InstanceMaskWordCount;
        m_liveProgram = program;
        m_rebuildInstanceGridPerFrame = rebuildInstanceGridPerFrame;
        m_requiredDynamicTransformCapacity = program.RequiredDynamicTransformCapacity;
        // CADENCE GATE: whether ANY declared screen forces every frame to render — see m_programDeclaresScreenSlab.
        m_programDeclaresScreenSlab = ProgramDeclaresShape(
            program: program,
            shapeType: SdfShapeType.ScreenSlab
        );
        // CADENCE GATE: a new program (words, live mask width, kernel variant, reseeded screen-surface table, invariant
        // instance grid) invalidates any prior frame's signature — bump the revision the signature folds in.
        m_programRevision++;

        // Stage 1 kernel-variant selection — a pure function of the uploaded program's instruction stream (see
        // SdfViewsKernelVariant): a program touching any exotic op/shape runs the full-ISA reference kernel; a
        // core-only program runs the exotic-stripped variant, bit-identical by construction (the stripped cases are
        // unreachable) but with far less live register state in the interpreter. Logged (when GPU timing is armed) only
        // when the selection CHANGES, so a per-interaction overworld rebuild doesn't spam the digest stream.
        var exoticTouch = SdfViewsKernelVariants.FirstExoticTouch(program: program);
        var viewsVariant = ((exoticTouch is null)
            ? SdfViewsKernelVariant.CoreOps
            : SdfViewsKernelVariant.Full
        );

        m_useCoreViews = (SdfViewsKernelVariant.CoreOps == viewsVariant);

        if (
            Views.ViewTiming.Enabled &&
            (m_loggedViewsVariant != viewsVariant)
        ) {
            m_loggedViewsVariant = viewsVariant;
            Console.Error.WriteLine(value: ((exoticTouch is null)
                ? $"[world-timing] {DebugLabel} views variant: core-ops (no exotic op in the program)"
                : $"[world-timing] {DebugLabel} views variant: full (program touches {exoticTouch})"));
        }
    }
}
