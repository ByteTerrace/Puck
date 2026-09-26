using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Re-uploads the scene program (the host's <c>ProgramChanged</c> path — e.g. a rebuilt overworld scene).
    /// Program and instance buffers grow on demand after draining the frame ring. Persistent images, baked bricks,
    /// screen bindings and pipelines remain in place.</summary>
    /// <param name="program">The scene program to upload.</param>
    /// <exception cref="ArgumentException">The program contains an opcode not declared by <see cref="SdfOp"/>, or its
    /// dynamic-transform requirements exceed the host's reserved transform slots.</exception>
    public void UploadProgram(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);
        program.ValidateIsa();

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

        // The program words go through their region like every other table, so a frame in flight keeps reading the words
        // its slot was sent; only growing a capacity drains the frame ring.
        EnsureProgramCapacity(program: program);

        // A program whose grid contains no active maskable dynamic instance has one invariant table. Build it against
        // the engine's actual capacity envelope and stage the words that differ from the uploaded grid; the next frame
        // copies them. Programs with moving binnable instances rebuild on the next frame and on every frame whose
        // transforms move.
        var rebuildInstanceGridPerFrame = program.RequiresFrameInstanceGridRebuild;

        if (!rebuildInstanceGridPerFrame) {
            var invariantInstanceGrid = program.BuildInvariantFrameInstanceGrid(
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );

            ValidateInstanceGridCapacity(words: invariantInstanceGrid);
            StageInstanceGrid(words: invariantInstanceGrid);
        }

        WriteProgramWords(program: program);
        // Seed the screen-surface table from the program's declared surfaces (the "program uploaded once" baseline);
        // any SetScreenSurface call made before the next produced frame patches this same table before it goes out — a
        // re-upload never resurrects the program's original frame over a live SetScreenSurface write made in between.
        // Only the words that differ are owed.
        _ = m_screenSurfaceRegion.Write(
            bytes: MemoryMarshal.Cast<uint, byte>(span: program.ScreenSurfaceWords),
            offset: 0
        );

        m_instanceGridRebuildOwed = rebuildInstanceGridPerFrame;
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
        ReconfigureWork();

        // Stage 1 kernel-variant selection — a pure function of the uploaded program's instruction stream (see
        // SdfViewsKernelVariant): a program touching any exotic op/shape runs the full-ISA reference kernel; a
        // core-only program runs the exotic-stripped variant, bit-identical by construction (the stripped cases are
        // unreachable) but with far less live register state in the interpreter.
        var (viewsVariant, _) = SdfViewsKernelVariants.Select(program: program);

        m_viewsVariant = viewsVariant;
    }
}
