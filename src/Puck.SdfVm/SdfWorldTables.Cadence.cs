using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The cadence: whether a view's latest render stands for the frame the tables hold now. A view stands only when its
// signature equals the one it last rendered at and nothing unhashed forces a render (ForcesRender).
//
// Signature coverage — the tables' signature (TablesSignature) folds in everything every pass of a view reads besides its
// pass block's view and frame values, and the view's signature (ViewSignature) folds that with the pass block:
//   - m_programRevision  : the uploaded program (words, live instance-mask width, kernel variant, reseeded screen-surface
//                          table, invariant instance grid) and every kernel reload — bumped by UploadProgram and
//                          InstallReload.
//   - the world values    : the bound screen count and the mesh draws' count.
//   - the pass block      : the view's camera basis, fov/aspect, off-axis offset, far distance and debug view mode, every
//                          lever and the environment (SdfFrameBlock) with its presented-tick bakes (the twinkle phase,
//                          the cloud offsets), less the render extent, which the scheduler renders a view again for when
//                          it moves.
//   - m_volumeRegion     : the bounded media, whose advection and pulse are baked from the presented tick, so a view
//                          showing one renders again exactly when the presented tick moves it.
//   - m_dynamicTransformRevision : bumped whenever a frame packs an owed dynamic-transform row, so the table is never
//                          re-hashed. Also covers the frame instance grid (a pure function of these transforms + the
//                          program).
//   - m_meshRevision     : the mesh draws the mesh pass rasterizes and primary bounds its march by.
//   - m_screenSurfaceRegion, m_screenMappingRegion : the screen-surface sampling table and the mapping and bound flag
//                          each screen is drawn from.
//   - m_screenLightScratch : the per-screen glow colors.
//   - m_decalRevision    : the glyph-decal buffer — revision-tracked (it is 820 KB, not re-hashed each frame).
// Not covered by any packed span — handled conservatively by forcing a render:
//   - m_programDeclaresScreenSlab : a bound screen's image content updates in place each frame with the same view handle,
//                          unseen by any packed span, so any declared ScreenSlab force-renders.
//   - AnyBrickBaking()  : an in-progress carve bake writing brick voxels each upload.
// The pass block's temporal values (jitter, previous view) and the previous transform tables belong to each instance's
// history, not the tables: SdfWorldPasses.IsUnchanged holds them through SdfTemporalHistory.Stands.
public sealed partial class SdfWorldTables {
    private readonly byte[] m_signatureBlock = new byte[SdfFrameBlock.SizeBytes];

    private ulong m_tablesSignature;

    /// <summary>Returns whether the frame the tables hold forces every view to render whatever its signature: a
    /// declared screen slab, whose bound image changes in place, or a carve bake in progress.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <returns><see langword="true"/> when no view's latest render may stand for the frame.</returns>
    public bool ForcesRender(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        return (
            !frame.EnableCadenceGate ||
            m_programDeclaresScreenSlab ||
            AnyBrickBaking()
        );
    }
    /// <summary>Returns the 64-bit FNV-1a signature of what one view renders from in the frame the tables hold: the
    /// tables' signature (<see cref="UpdateTablesSignature"/>) folded with the view's pass block
    /// (<see cref="SdfFrameBlock"/>), written with no render extent. A collision would need a
    /// 64-bit hash clash across two genuinely different input sets — negligible, and still only presentation, never
    /// simulation.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The signature.</returns>
    public ulong ViewSignature(SdfFrame frame, int view) {
        var hash = Fnv1aHash.Create();
        var block = m_signatureBlock.AsSpan();
        Span<byte> tables = stackalloc byte[sizeof(ulong)];

        SdfFrameBlock.Write(
            block: block,
            frame: frame,
            height: 0u,
            tables: PassValues,
            view: view,
            width: 0u
        );
        MemoryMarshal.Write(
            destination: tables,
            value: in m_tablesSignature
        );
        hash.Add(values: tables);
        hash.Add(values: block);

        return hash.Value;
    }
    /// <summary>Folds everything every view reads of the tables besides its pass block into the tables' signature, once
    /// the frame is packed and its screens are bound.</summary>
    public void UpdateTablesSignature() {
        var hash = Fnv1aHash.Create();

        Span<ulong> values = [
            m_programRevision,
            m_decalRevision,
            m_dynamicTransformRevision,
            unchecked((ulong)m_meshRevision),
            m_meshDrawCount,
            BoundScreenCount(),
        ];

        hash.Add(values: MemoryMarshal.AsBytes(span: values));
        hash.Add(values: m_volumeRegion.Contents);
        hash.Add(values: m_screenSurfaceRegion.Contents);
        hash.Add(values: m_screenMappingRegion.Contents);
        hash.Add(values: m_screenLightScratch);
        m_tablesSignature = hash.Value;
    }

    // Whether any brick slot is mid-bake: a Baking slot has RecordBrickBakeSlices writing new voxels every upload, so the
    // sampled field the beam/views marches changes with no packed-span change — the cadence must render through it.
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
    // Whether the program's instruction stream declares any shape of the given type — a one-time UploadProgram walk
    // backing per-program facts (the cadence's ScreenSlab force-render).
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
