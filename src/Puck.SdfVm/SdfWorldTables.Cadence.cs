using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The cadence: whether a view's latest render stands for the frame the tables hold now. A view stands only when its
// signature equals the one it last rendered at and nothing unhashed forces a render (ForcesRender).
//
// Signature coverage — the tables' signature (TablesSignature) folds in everything every pass of a view reads besides the
// view's own row, and the view's signature (ViewSignature) folds that with the row:
//   - m_programRevision  : the uploaded program (words, live instance-mask width, kernel variant, reseeded screen-surface
//                          table, invariant instance grid) and every kernel reload — bumped by UploadProgram and
//                          InstallReload.
//   - the pass values     : the bound screen count, the twinkle tick and the mesh draws' count.
//   - the view's row      : its camera basis + fov/aspect, debug view mode, off-axis offset and the frame's far distance,
//                          excluding the presentation-time lane (position.w) and the render extent, which the scheduler
//                          renders a view again for when it moves. Time free-runs every frame, so hashing it would make
//                          the signature never repeat; what reads it (bounded volumes) forces a render below.
//   - m_dynamicTransformRevision : bumped whenever a frame packs an owed dynamic-transform row, so the table is never
//                          re-hashed. Also covers the frame instance grid (a pure function of these transforms + the
//                          program).
//   - m_meshRevision     : the mesh draws the mesh pass rasterizes and primary bounds its march by.
//   - m_screenSurfaceRegion, m_screenMappingRegion : the screen-surface sampling table and the mapping and bound flag
//                          each screen is drawn from.
//   - m_screenLightScratch : per-screen glow colors + the environment row (ambient/sun/slice) + the grid-overlay rows +
//                          the engine-bench lever rows + the shadow-proxy rows + the analytic-normal and shadow-cull
//                          toggles — every shading lever.
//   - m_decalRevision    : the glyph-decal buffer — revision-tracked (it is 820 KB, not re-hashed each frame).
// Not covered by any packed span — handled conservatively by forcing a render:
//   - m_programDeclaresScreenSlab : a bound screen's image content updates in place each frame with the same view handle,
//                          unseen by any packed span, so any declared ScreenSlab force-renders.
//   - AnyBrickBaking()  : an in-progress carve bake writing brick voxels each upload.
//   - frame.Volumes     : bounded volumes animate on the excluded time lane, and their table is not hashed.
public sealed partial class SdfWorldTables {
    // The row lanes the view signature leaves out: the presentation time (position.w) and the render extent.
    private const int TimeLane = 3;
    private const int ExtentLane = 16;

    private ulong m_tablesSignature;

    /// <summary>Returns whether the frame the tables hold forces every view to render whatever its signature: a
    /// declared screen slab, whose bound image changes in place; a carve bake in progress; or a bounded volume, which
    /// animates on the presentation clock.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <returns><see langword="true"/> when no view's latest render may stand for the frame.</returns>
    public bool ForcesRender(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        return (
            !frame.EnableCadenceGate ||
            m_programDeclaresScreenSlab ||
            AnyBrickBaking() ||
            (frame.Volumes.Count > 0)
        );
    }
    /// <summary>Returns the 64-bit FNV-1a signature of what one view renders from in the frame the tables hold: the
    /// tables' signature (<see cref="UpdateTablesSignature"/>) folded with the view's row, less its presentation time and
    /// its render extent. A collision would need a 64-bit hash clash across two genuinely different input sets —
    /// negligible, and still only presentation, never simulation.</summary>
    /// <param name="frame">The frame the tables packed.</param>
    /// <param name="view">The view's index in <see cref="SdfFrame.Views"/>.</param>
    /// <returns>The signature.</returns>
    public ulong ViewSignature(SdfFrame frame, int view) {
        var hash = Fnv1aHash.Create();
        Span<byte> row = stackalloc byte[ViewportByteLength];
        Span<byte> tables = stackalloc byte[sizeof(ulong)];

        WriteViewportRow(
            frame: frame,
            height: 0u,
            row: row,
            view: view,
            width: 0u
        );
        MemoryMarshal.Write(
            destination: tables,
            value: in m_tablesSignature
        );
        hash.Add(values: tables);
        hash.Add(values: row[..(TimeLane * sizeof(float))]);
        hash.Add(values: row[((TimeLane + 1) * sizeof(float))..(ExtentLane * sizeof(float))]);
        hash.Add(values: row[((ExtentLane + 2) * sizeof(float))..]);

        return hash.Value;
    }
    /// <summary>Folds everything every view reads of the tables besides its own row into the tables' signature, once the
    /// frame is packed and its screens are bound.</summary>
    public void UpdateTablesSignature() {
        var hash = Fnv1aHash.Create();

        Span<ulong> values = [
            m_programRevision,
            m_decalRevision,
            m_dynamicTransformRevision,
            unchecked((ulong)m_meshRevision),
            m_meshDrawCount,
            m_sampleIndex,
            BoundScreenCount(),
        ];

        hash.Add(values: MemoryMarshal.AsBytes(span: values));
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
