using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // One more whenever what the World sets bind changes: a region replaced by growth, the glyph atlas uploaded or cleared,
    // the mesh atlases repacked.
    private long m_bindingRevision;

    /// <summary>Gets the program words the tables are provisioned for: the options' reserve, or the program region's
    /// words once a program has grown it past that. The region itself holds the live program and grows by half again
    /// when a larger one is uploaded.</summary>
    public int ProgramWordCapacity => Math.Max(
        val1: m_programWordCapacity,
        val2: m_programWordReserve
    );
    /// <summary>Gets the program instances the tables are provisioned for, which the bake's instance masks are counted
    /// by; it grows with a larger program's upload.</summary>
    public int InstanceCapacity => m_instanceCapacity;

    /// <summary>Refuses, by name and before anything is allocated, tables whose descriptor pools
    /// (<see cref="DescriptorPools"/>) the device's heaps cannot admit (<see cref="IGpuBindings.CanAdmit"/>), so nothing
    /// grows. The constructor calls it first, with the arguments it was given, so every creation site is admitted and one
    /// building through a pipeline source's refusing build records the refusal like any other.</summary>
    /// <param name="device">The device the tables would be created on.</param>
    /// <param name="options">The options they would be created with.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The device's heaps cannot admit the tables' pools; the message
    /// carries <see cref="GpuDescriptorHeapBudget.RefusalCode"/>.</exception>
    public static void CheckAdmission(IGpuDeviceContext device, SdfWorldTablesOptions options) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: options);

        if (!device.Services.Bindings.CanAdmit(
            owner: "SDF world tables",
            pools: DescriptorPools,
            refusal: out var refusal
        )) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }
    }
    /// <summary>Gets every descriptor pool the tables create, which a device's heap admits them by: their own
    /// (<see cref="DescriptorPoolSizes"/>), then the one pool holding the copy sets they reserve for all their regions
    /// (<see cref="GpuRegionCopyPool"/>, sized by <see cref="GpuRegionCopyPool.SizesOf"/>), whatever policy the device
    /// selects: the shared frame tables and the shadow handoff controls, then the mesh region's own pool for its upload
    /// and background slots. Construction creates these and
    /// the strided history-copy pool, and the residency the mesh bake's background work pool, so no later frame, the
    /// first to draw a mesh or a growing program included, takes a descriptor range.</summary>
    public static GpuDescriptorPoolSizes[] DescriptorPools => [
        DescriptorPoolSizes,
        RegionCopyPoolSizes,
        MeshRegionCopyPoolSizes,
        GpuBufferStridedCopy.PoolSizes(frameSlotCount: FrameRingSize, recordingCount: 2),
        SdfMeshBakeGpuWork.PoolSizes,
    ];
    /// <summary>Gets the one descriptor pool the tables create themselves, the statement their construction creates the
    /// pool from: the World set per ring slot, holding the World group of <see cref="SdfWorldInterfaces.World"/>.</summary>
    public static GpuDescriptorPoolSizes DescriptorPoolSizes {
        get {
            var sizes = default(GpuDescriptorPoolSizes);
            var world = GpuDescriptorPoolSizes.ForGroups(groups: PipelineLayouts.World.Groups.Where(predicate: static group => (group.Ordinal == WorldGroup)).ToArray());

            for (var slot = 0; (slot < FrameRingSize); slot++) {
                sizes += world;
            }

            return sizes;
        }
    }

    // The words the program region holds: the program, then the default shade program's code and parameters after its
    // last vector (PackShadeDefault). The constructor, UploadProgram and PackShadeDefault all size the region by it.
    private static int ProgramRegionWords(SdfProgram program, SdfShadeProgram? shade) =>
        checked(program.Words.Length + ((shade is null) ? 0 : (shade.Words.Length + shade.ParameterWords.Length)));

    // The one growth path of the program region and the instance capacity the bake's masks are sized by. The region grows
    // without waiting for the device: what it replaces retires after the following upload, which signals only once every
    // earlier view and background job that bound it has finished. A grown region keeps every word it held (GpuRegion.Grow),
    // so no caller writes the program again after growth, and a size past the ring's share of the aperture restages it
    // there. No per-frame allocations.
    private void EnsureProgramCapacity(SdfProgram program, SdfShadeProgram? shade) {
        var requiredWords = ProgramRegionWords(program: program, shade: shade);
        if ((requiredWords <= m_programWordCapacity) && (program.Instances.Count <= m_instanceCapacity)) {
            return;
        }

        var words = GrowCapacity(m_programWordCapacity, requiredWords, (int.MaxValue / sizeof(uint)));
        var instances = GrowCapacity(m_instanceCapacity, program.Instances.Count, SdfProgramBuilder.MaxInstances);

        if (words != m_programWordCapacity) {
            var bytes = checked(((long)words) * sizeof(uint));

            // Retirement allocates nothing once the region has changed.
            m_retired.EnsureCapacity(capacity: checked((m_retired.Count + 1)));
            Retire(replaced: m_programRegion.Grow(byteCount: bytes));
        }
        m_programWordCapacity = words;
        m_instanceCapacity = instances;
        m_bindingRevision++;
    }
    private static int GrowCapacity(int current, int required, int ceiling) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, ceiling);
        return ((required <= current) ? current : (int)Math.Max(val1: required, val2: Math.Min(val1: ceiling, val2: (((long)current) + Math.Max(val1: 1, val2: (current / 2))))));
    }
}
