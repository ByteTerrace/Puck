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
    /// <summary>Gets the program instances the tables are provisioned for, which a view's per-tile masks and part bounds
    /// are counted by; it grows with a larger program's upload.</summary>
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
            pools: DescriptorPools(brickPool: (options.BrickPoolVoxelCapacity > 0)),
            refusal: out var refusal
        )) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }
    }
    /// <summary>Returns every descriptor pool the tables create, which a device's heap admits them by: their own
    /// (<see cref="DescriptorPoolSizes"/>), then the one pool holding the copy sets they reserve for all their regions
    /// (<see cref="GpuRegionCopyPool"/>, sized by <see cref="GpuRegionCopyPool.SizesOf"/>), whatever policy the device
    /// selects: the eight per-frame tables, the mesh region, and with a brick pool the brick staging. Construction creates
    /// both, so no later frame, the first to draw a mesh or a growing program included, takes a descriptor range.</summary>
    /// <param name="brickPool">Whether the tables keep a brick pool.</param>
    /// <returns>The pools' sizes, the tables' own first.</returns>
    public static GpuDescriptorPoolSizes[] DescriptorPools(bool brickPool) => [
        DescriptorPoolSizes(brickPool: brickPool),
        RegionCopyPoolSizes(brickPool: brickPool),
    ];
    /// <summary>Returns the one descriptor pool the tables create themselves, the statement their construction creates the
    /// pool from: the World set per ring slot, holding the World group of <see cref="SdfWorldInterfaces.World"/>, and with
    /// a brick pool the baker's frame set, holding the frame group of <see cref="SdfWorldInterfaces.BrickBake"/>, and one
    /// bake set per brick slot holding its pass group.</summary>
    /// <param name="brickPool">Whether the tables keep a brick pool.</param>
    /// <returns>The pool's sizes.</returns>
    public static GpuDescriptorPoolSizes DescriptorPoolSizes(bool brickPool) {
        var sizes = default(GpuDescriptorPoolSizes);
        var world = GpuDescriptorPoolSizes.ForGroups(groups: PipelineLayouts.World.Groups.Where(predicate: static group => (group.Ordinal == WorldGroup)).ToArray());

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            sizes += world;
        }

        if (!brickPool) {
            return sizes;
        }

        var groups = PipelineLayouts.BrickBake.Groups;

        sizes += GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == FrameGroup)).ToArray());

        var bake = GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == PassGroup)).ToArray());

        for (var brick = 0; (brick < SdfBrickPoolLayout.MaxBricks); brick++) {
            sizes += bake;
        }

        return sizes;
    }

    // Called only by UploadProgram. Grows the program region, or the instance-grid region, after the device is idle, since
    // every view's submission reads them; a new region starts owing every word, so the program write and grid stage that
    // follow send it whole. A new region writes the copy sets reserved for its table, so growth takes no descriptor range.
    // No per-frame allocations.
    private void EnsureProgramCapacity(SdfProgram program) {
        if ((program.Words.Length <= m_programWordCapacity) && (program.Instances.Count <= m_instanceCapacity)) {
            return;
        }

        var words = GrowCapacity(m_programWordCapacity, program.Words.Length, (int.MaxValue / sizeof(uint)));
        var instances = GrowCapacity(m_instanceCapacity, program.Instances.Count, SdfProgramBuilder.MaxInstances);
        var growProgram = (words != m_programWordCapacity);
        var growInstances = (instances != m_instanceCapacity);
        var gridWords = SdfInstanceGrid.WordCapacity(maxInstances: instances);

        m_deviceContext.TryWaitIdle();

        using var scope = new GpuCreationScope();
        // Create the entire replacement before releasing an old region.
        var programRegion = (growProgram ? scope.Own(created: CreateRegion(byteCount: checked((words * sizeof(uint))), region: ProgramRegionIndex)) : m_programRegion);
        var gridRegion = (growInstances ? scope.Own(created: CreateRegion(byteCount: checked((gridWords * sizeof(uint))), region: InstanceGridRegionIndex)) : m_instanceGridRegion);
        var inputScratch = (growInstances ? new SdfInstanceGridInput[instances] : m_instanceGridInputScratch);
        var workspace = (growInstances ? new SdfInstanceGrid.Workspace(maxInstances: instances) : m_instanceGridWorkspace);

        scope.Complete();

        if (growProgram) {
            m_programRegion.Dispose();
            m_programRegion = programRegion;
        }
        if (growInstances) {
            m_instanceGridRegion.Dispose();
            m_instanceGridRegion = gridRegion;
        }

        m_instanceGridInputScratch = inputScratch;
        m_instanceGridWorkspace = workspace;
        m_programWordCapacity = words;
        m_instanceCapacity = instances;
        m_instanceGridWordCapacity = gridWords;
        m_bindingRevision++;
    }
    private static int GrowCapacity(int current, int required, int ceiling) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, ceiling);
        return ((required <= current) ? current : (int)Math.Max(val1: required, val2: Math.Min(val1: ceiling, val2: (((long)current) + Math.Max(val1: 1, val2: (current / 2))))));
    }
}
