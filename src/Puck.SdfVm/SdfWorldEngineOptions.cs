using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Construction options for <see cref="SdfWorldEngine"/>.</summary>
/// <param name="Program">The scene program; the GPU buffer is sized to it and it is uploaded once at construction
/// (the "program uploaded once" seam the dynamic-transform channel rides). A host whose scene later changes calls
/// <see cref="SdfWorldEngine.UploadProgram"/> — program and instance buffers grow when necessary.</param>
/// <param name="ViewportCapacity">The number of viewport slots to provision (source textures + packed viewport rows).
/// Frames may carry fewer views than the capacity, never more; the kernels' source array caps it at 5.</param>
/// <param name="DynamicTransformCapacity">The number of dynamic entity-transform slots to allocate (at least one slot
/// is always bound so the binding stays valid for a static scene). The engine automatically raises this floor to the
/// program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/>. Each slot costs 48 bytes of the dynamic-transform
/// region, and a frame uploads only the words that changed. Excess transforms in a frame beyond the capacity are dropped.</param>
/// <param name="CreateOutputImage">An optional factory for the output image. When it returns an
/// <see cref="IGpuExportableImage"/>, the engine runs in <em>export</em> mode: each submitted frame ends in the
/// cross-backend handoff layout and <see cref="SdfWorldEngine.SubmitFrame"/> drains the producer queue so the shared
/// handle may be consumed on another device. When <see langword="null"/>, a plain same-device storage image is
/// created from the resolved <see cref="IGpuImageFactory"/>.</param>
/// <param name="ProgramWordCapacity">The packed words the engine is provisioned for, which
/// <see cref="SdfWorldEngine.ProgramWordCapacity"/> reports while no program exceeds it. It allocates nothing: the
/// program region holds the live program and <see cref="SdfWorldEngine.UploadProgram"/> grows it by half again when a
/// larger one arrives.</param>
/// <param name="InstanceCapacity">An optional initial instance reserve for the per-tile masks and instance grids.
/// The engine provisions at least the initial program's instance count and grows with later uploads.</param>
/// <param name="BrickPoolVoxelCapacity">The carve-bake brick pool's voxel (f32 word) capacity, fixed at construction.
/// Defaults to <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (16.7M voxels = 64 MB —
/// <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full <see cref="SdfBrickPoolLayout.BrickDim"/><sup>3</sup>
/// resolution). <c>0</c> provisions no pool (a 4-byte filler keeps the always-present shader binding valid). A pool-less
/// engine still accepts a program declaring a <see cref="SdfShapeType.SampledRegion"/> — baking and rendering are split:
/// the shader detects the filler (by its element count) and renders the region via the conservative uncarved-hull
/// fallback (the Subtraction never bites), so a filming view (<c>SdfCameraView</c>/<c>WorldSessionView</c>) shows a
/// SampledRegion world uncarved rather than a box-shaped hole. Only <see cref="SdfWorldEngine.RequestBrickBake"/> stays a
/// loud rejection on a pool-less engine (nothing to bake into). The pool is a persistent device-local buffer the sliced
/// background bake (<see cref="SdfWorldEngine.RequestBrickBake"/>) writes and the beam + views kernels sample.</param>
/// <param name="WorkLedger">The ledger the engine counts its GPU work into, owned by the caller so that submission
/// identities keep increasing when the caller rebuilds the engine (after a device loss, say); created with
/// <see cref="SdfWorldEngine.FrameRingSize"/> frames in flight. The caller invalidates it when it drops an engine.
/// When <see langword="null"/>, the engine creates its own.</param>
public sealed record SdfWorldEngineOptions(
    SdfProgram Program,
    uint ViewportCapacity = SdfWorldEngine.MaxViewports,
    int DynamicTransformCapacity = 1,
    Func<IGpuDeviceContext, IGpuImage>? CreateOutputImage = null,
    int ProgramWordCapacity = 0,
    int InstanceCapacity = 0,
    int BrickPoolVoxelCapacity = SdfWorldEngine.DefaultBrickPoolVoxelCapacity,
    GpuWorkLedger? WorkLedger = null
);
