using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Construction options for <see cref="SdfWorldTables"/>.</summary>
/// <param name="Program">The scene program, uploaded at construction (the "program uploaded once" seam the
/// dynamic-transform channel rides). A frame source whose scene later changes hands another one, which
/// <see cref="SdfWorldTables.UploadProgram"/> uploads, growing the program and instance tables when necessary.</param>
/// <param name="DynamicTransformCapacity">The number of dynamic entity-transform slots to allocate (at least one slot
/// is always bound so the binding stays valid for a static scene). The tables raise this floor to the program's
/// <see cref="SdfProgram.RequiredDynamicTransformCapacity"/>. Each slot costs 48 bytes of the dynamic-transform
/// region, and a frame uploads only the words that changed. Excess transforms in a frame beyond the capacity are
/// dropped.</param>
/// <param name="ProgramWordCapacity">The packed words the tables are provisioned for, which
/// <see cref="SdfWorldTables.ProgramWordCapacity"/> reports while no program exceeds it. It allocates nothing: the
/// program region holds the live program and <see cref="SdfWorldTables.UploadProgram"/> grows it by half again when a
/// larger one arrives.</param>
/// <param name="InstanceCapacity">An optional initial instance reserve for the instance grid and the per-tile masks a
/// view's scratch is counted by. The tables provision at least the initial program's instance count and grow with later
/// uploads.</param>
/// <param name="BrickPoolVoxelCapacity">The carve-bake brick pool's voxel (f32 word) capacity, fixed at construction.
/// Defaults to <see cref="SdfWorldTables.DefaultBrickPoolVoxelCapacity"/> (16.7M voxels = 64 MB —
/// <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full <see cref="SdfBrickPoolLayout.BrickDim"/><sup>3</sup>
/// resolution). <c>0</c> provisions no pool (a 4-byte filler keeps the always-present shader binding valid). Pool-less
/// tables still accept a program declaring a <see cref="SdfShapeType.SampledRegion"/>: the shader detects the filler by
/// its element count and renders the region through the conservative uncarved-hull fallback, so a camera or session
/// view shows a SampledRegion world uncarved rather than a box-shaped hole. Only
/// <see cref="SdfWorldTables.RequestBrickBake"/> refuses without a pool (nothing to bake into).</param>
/// <param name="WorkLedger">The ledger the tables count their uploads into, owned by the caller so that submission
/// identities keep increasing when the caller rebuilds the tables (after a device loss, say); created with
/// <see cref="SdfWorldTables.FrameRingSize"/> frames in flight. The caller invalidates it when it drops the tables.
/// When <see langword="null"/>, the tables create their own.</param>
public sealed record SdfWorldTablesOptions(
    SdfProgram Program,
    int DynamicTransformCapacity = 1,
    int ProgramWordCapacity = 0,
    int InstanceCapacity = 0,
    int BrickPoolVoxelCapacity = SdfWorldTables.DefaultBrickPoolVoxelCapacity,
    GpuWorkLedger? WorkLedger = null
);
