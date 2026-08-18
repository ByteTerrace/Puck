using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Construction options for <see cref="SdfWorldEngine"/>.</summary>
/// <param name="Program">The scene program; the GPU buffer is sized to it and it is uploaded once at construction
/// (the "program uploaded once" seam the dynamic-transform channel rides). A host whose scene later changes calls
/// <see cref="SdfWorldEngine.UploadProgram"/> — the new program must fit the constructed buffer.</param>
/// <param name="ViewportCapacity">The number of viewport slots to provision (source textures + packed viewport rows).
/// Frames may carry fewer views than the capacity, never more; the kernels' source array caps it at 5.</param>
/// <param name="ChildMask">Bit <c>v</c> set means viewport <c>v</c> is backed by a hosted child surface, not an SDF
/// camera: no source texture is allocated for it (the host binds the child's storage image each frame via
/// <see cref="SdfWorldEngine.SetChildSource"/>), and the beam prepass + Stage 1 skip the slot.</param>
/// <param name="DynamicTransformCapacity">The number of dynamic entity-transform slots to allocate (at least one slot
/// is always bound so the binding stays valid for a static scene). The engine automatically raises this floor to the
/// program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/>. A plain per-engine choice with no fixed ceiling —
/// hundreds of slots cost 32 bytes each and an O(capacity) per-frame upload; excess transforms in a frame beyond the
/// capacity are dropped.</param>
/// <param name="CreateOutputImage">An optional factory for the output image. When it returns an
/// <see cref="IGpuExportableStorageImage"/>, the engine runs in <em>export</em> mode: each submitted frame ends in the
/// cross-backend handoff layout and <see cref="SdfWorldEngine.SubmitFrame"/> drains the producer queue so the shared
/// handle may be consumed on another device. When <see langword="null"/>, a plain same-device storage image is
/// created from the resolved <see cref="IGpuStorageImageFactory"/>.</param>
/// <param name="TimingFactory">An optional GPU timing pool factory; with <paramref name="TimingRecorder"/>, enables
/// the per-pass timestamp marks (gated on the device reporting usable timestamps).</param>
/// <param name="TimingRecorder">An optional GPU timing recorder (see <paramref name="TimingFactory"/>).</param>
/// <param name="LiveArmedTiming">When <see langword="true"/> (the live node path), the timing pools are created lazily
/// on the first armed frame and each frame consults <see cref="GpuTimingControl.Shared"/> — a disarmed frame skips the
/// timestamp writes/reads at near-zero cost, so timing arms and disarms mid-session with no rebuild. When
/// <see langword="false"/> (the default, the waited harness/measure path), timing runs eagerly the moment a supported
/// factory + recorder are supplied — the pools are created at construction and every frame is timed, never consulting
/// the shared arming control.</param>
/// <param name="ProgramWordCapacity">An optional floor on the program buffer's packed-word capacity (the engine
/// always provisions at least <paramref name="Program"/>'s length). A host that hot-swaps programs via
/// <see cref="SdfWorldEngine.UploadProgram"/> declares its envelope here instead of relying on every future program
/// staying within the first one's size.</param>
/// <param name="InstanceCapacity">An optional floor on the instance count the per-tile mask buffer is sized for (the
/// engine always provisions at least <paramref name="Program"/>'s <see cref="SdfProgram.InstanceMaskWordCount"/>).
/// The hot-swap counterpart of <paramref name="ProgramWordCapacity"/> for instanced programs.</param>
/// <param name="BrickPoolVoxelCapacity">The carve-bake brick pool's voxel (f32 word) capacity, fixed at construction.
/// Defaults to <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (16.7M voxels = 64 MB —
/// <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full <see cref="SdfBrickPoolLayout.BrickDim"/><sup>3</sup>
/// resolution). <c>0</c> provisions no pool (a 4-byte filler keeps the always-present shader binding valid). A pool-less
/// engine still accepts a program declaring a <see cref="SdfShapeType.SampledRegion"/> — baking and rendering are split:
/// the shader detects the filler (by its element count) and renders the region via the conservative uncarved-hull
/// fallback (the Subtraction never bites), so a filming view (<c>SdfCameraView</c>/<c>NestedWorldView</c>) shows a
/// SampledRegion world uncarved rather than a box-shaped hole. Only <see cref="SdfWorldEngine.RequestBrickBake"/> stays a
/// loud rejection on a pool-less engine (nothing to bake into). The pool is a persistent device-local buffer the sliced
/// background bake (<see cref="SdfWorldEngine.RequestBrickBake"/>) writes and the beam + views kernels sample.</param>
public sealed record SdfWorldEngineOptions(
    SdfProgram Program,
    uint ViewportCapacity = SdfWorldEngine.MaxViewports,
    uint ChildMask = 0,
    int DynamicTransformCapacity = 1,
    Func<IGpuDeviceContext, IGpuStorageImage>? CreateOutputImage = null,
    IGpuTimingPoolFactory? TimingFactory = null,
    IGpuTimingRecorder? TimingRecorder = null,
    int ProgramWordCapacity = 0,
    int InstanceCapacity = 0,
    bool LiveArmedTiming = false,
    int BrickPoolVoxelCapacity = SdfWorldEngine.DefaultBrickPoolVoxelCapacity
);
