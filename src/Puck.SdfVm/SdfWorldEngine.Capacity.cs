using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Gets the program words the engine is provisioned for: the options' reserve, or the program region's
    /// words once a program has grown it past that. The region itself holds the live program and grows by half again
    /// when a larger one is uploaded.</summary>
    public int ProgramWordCapacity => Math.Max(
        val1: m_programWordCapacity,
        val2: m_programWordReserve
    );
    /// <summary>Gets the bytes allocated for the visibility records: one record of <see cref="VisibilityRecordByteLength"/>
    /// bytes for every pixel of the full extent in every viewport the engine reserves.</summary>
    public ulong VisibilityRecordBytes => FrameBufferBytes(
        buffer: SdfFrameBuffer.PrimaryHits,
        capacity: FrameCapacity
    );
    /// <summary>Gets the bytes of one visibility record, the words <c>sdf-visibility.hlsli</c> lays out.</summary>
    public static int VisibilityRecordByteLength => PrimaryHitByteLength;

    private SdfFrameCapacity FrameCapacity => new(
        BrickPoolVoxels: m_brickPoolVoxelCapacity,
        Height: m_height,
        Instances: m_instanceCapacity,
        Viewports: m_viewportCapacity,
        Width: m_width
    );

    /// <summary>Refuses, by name and before anything is allocated, an engine whose descriptor pools
    /// (<see cref="DescriptorPools"/>) the device's heaps cannot admit (<see cref="IGpuBindings.CanAdmit"/>), so nothing
    /// grows. The constructor calls it first, with the arguments it was given, so every creation site is admitted and
    /// one building through a pipeline source's refusing build records the refusal like any other.</summary>
    /// <param name="device">The device the engine would be created on.</param>
    /// <param name="options">The options it would be created with.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The device's heaps cannot admit the engine's pools; the message
    /// carries <see cref="GpuDescriptorHeapBudget.RefusalCode"/>.</exception>
    public static void CheckAdmission(IGpuDeviceContext device, SdfWorldEngineOptions options) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: options);

        if (!device.Services.Bindings.CanAdmit(
            owner: "SDF world engine",
            pools: DescriptorPools(brickPool: (options.BrickPoolVoxelCapacity > 0)),
            refusal: out var refusal
        )) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }
    }
    /// <summary>Returns every descriptor pool an engine may create, which a device's heap admits it by: its own
    /// (<see cref="DescriptorPoolSizes"/>), then the copy pool each of its regions creates under the staged policy
    /// (<see cref="GpuRegion.CopyPoolSizes"/>), whatever policy the device selects: the eight per-frame tables, the mesh
    /// region, and with a brick pool the brick staging.</summary>
    /// <param name="brickPool">Whether the engine keeps a brick pool.</param>
    /// <returns>The pools' sizes, the engine's own first.</returns>
    public static GpuDescriptorPoolSizes[] DescriptorPools(bool brickPool) => [
        DescriptorPoolSizes(brickPool: brickPool),
        .. RegionPoolSizes(brickPool: brickPool),
    ];
    /// <summary>Returns the one descriptor pool an engine creates itself, the statement its construction creates the
    /// pool from: one cull-args set (bound once to shared device-local buffers), then per frame ring slot the beam,
    /// instance-cull, views and composite sets, which bind that slot's buffers; with a brick pool, one bake set per
    /// brick slot. Array bindings count every element.</summary>
    /// <param name="brickPool">Whether the engine keeps a brick pool.</param>
    /// <returns>The pool's sizes.</returns>
    public static GpuDescriptorPoolSizes DescriptorPoolSizes(bool brickPool) {
        var sets = new List<IReadOnlyList<GpuComputeBinding>> { PipelineLayouts.CullArgs };

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            sets.Add(item: PipelineLayouts.Beam);
            sets.Add(item: PipelineLayouts.InstanceCull);
            sets.Add(item: PipelineLayouts.Views);
            sets.Add(item: PipelineLayouts.Composite);
        }
        if (brickPool) {
            for (var brick = 0; (brick < SdfBrickPoolLayout.MaxBricks); brick++) {
                sets.Add(item: PipelineLayouts.BrickBake);
            }
        }

        return GpuDescriptorPoolSizes.ForSets([.. sets]);
    }
    /// <summary>Returns the bytes the engine allocates for one of its device-local frame buffers at a capacity, the one
    /// statement of each buffer's size that construction and program growth allocate by. The host-written tables are
    /// regions, sized where the engine creates them.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="capacity">The capacities the buffer is sized for.</param>
    /// <returns>The buffer's size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="buffer"/> is not a declared buffer.</exception>
    /// <exception cref="OverflowException">The size does not fit in 64 bits.</exception>
    public static ulong FrameBufferBytes(SdfFrameBuffer buffer, SdfFrameCapacity capacity) => buffer switch {
        SdfFrameBuffer.BrickPool => checked((((ulong)Math.Max(
            val1: 1,
            val2: capacity.BrickPoolVoxels
        )) * sizeof(float))),
        SdfFrameBuffer.InstanceMasks => checked(((((ulong)capacity.Viewports) * capacity.Tiles) * (((ulong)SdfProgram.InstanceMaskStorageWordCountFor(instanceCount: capacity.Instances)) * sizeof(uint)))),
        // Four tile planes per tile, then the primary and AO part-bound bands per instance, per viewport.
        SdfFrameBuffer.Tiles => checked(((((ulong)capacity.Viewports) * ((TilePlaneCount * capacity.Tiles) + (((ulong)PartBoundFloatCount) * ((uint)capacity.Instances)))) * sizeof(float))),
        SdfFrameBuffer.ViewsArgs => ViewsArgsByteLength,
        SdfFrameBuffer.CullBounds => CullBoundsByteLength,
        SdfFrameBuffer.PrimaryHits => checked(((((ulong)capacity.Width) * capacity.Height) * (((ulong)capacity.Viewports) * PrimaryHitByteLength))),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: buffer,
            message: "Unknown SDF frame buffer.",
            paramName: nameof(buffer)
        ),
    };

    // Called only by UploadProgram. Grows the program region, or the instance-grid region with the instance masks and
    // tiles, after draining the frame ring; a new region starts owing every word, so the program write and grid stage
    // that follow send it whole. No per-frame allocations.
    private void EnsureProgramCapacity(SdfProgram program) {
        if ((program.Words.Length <= m_programWordCapacity) && (program.Instances.Count <= m_instanceCapacity)) {
            return;
        }

        var words = GrowCapacity(m_programWordCapacity, program.Words.Length, (int.MaxValue / sizeof(uint)));
        var instances = GrowCapacity(m_instanceCapacity, program.Instances.Count, SdfProgramBuilder.MaxInstances);
        var growProgram = (words != m_programWordCapacity);
        var growInstances = (instances != m_instanceCapacity);
        var grown = (FrameCapacity with { Instances = instances });
        var gridWords = SdfInstanceGrid.WordCapacity(maxInstances: instances);
        var maskWords = SdfProgram.InstanceMaskWordCountFor(instanceCount: instances);
        var inputScratch = (growInstances ? new SdfInstanceGridInput[instances] : m_instanceGridInputScratch);
        var workspace = (growInstances ? new SdfInstanceGrid.Workspace(maxInstances: instances) : m_instanceGridWorkspace);
        var replacements = new List<IDisposable>(capacity: 4);
        var committed = false;

        T Replacement<T>(T created) where T : IDisposable {
            replacements.Add(item: created);
            return created;
        }
        IGpuBuffer DeviceBuffer(SdfFrameBuffer buffer, in GpuObjectName name) => Replacement(created: m_gpu.BufferFactory.CreateDeviceLocal(
            name: name,
            sizeBytes: FrameBufferBytes(buffer: buffer, capacity: grown),
            usage: GpuBufferUsage.Storage
        ));

        WaitForFrameRing();

        try {
            // Create the entire replacement before changing any binding or releasing an old region or buffer.
            var programRegion = (growProgram ? Replacement(created: CreateRegion(byteCount: checked((words * sizeof(uint))), name: NameOf(part: "program"))) : m_programRegion);
            var gridRegion = (growInstances ? Replacement(created: CreateRegion(byteCount: checked((gridWords * sizeof(uint))), name: NameOf(part: "instance-grid"))) : m_instanceGridRegion);
            var masks = (growInstances ? DeviceBuffer(buffer: SdfFrameBuffer.InstanceMasks, name: NameOf(part: "instance-masks")) : m_instanceMaskBuffer);
            var tiles = (growInstances ? DeviceBuffer(buffer: SdfFrameBuffer.Tiles, name: NameOf(part: "tiles")) : m_tileBuffer);

            var (oldProgram, oldGrid, oldMasks, oldTiles) = (m_programRegion, m_instanceGridRegion, m_instanceMaskBuffer, m_tileBuffer);

            (m_programRegion, m_instanceGridRegion, m_instanceMaskBuffer, m_tileBuffer) = (programRegion, gridRegion, masks, tiles);

            try {
                BindProgramCapacity();
            } catch {
                (m_programRegion, m_instanceGridRegion, m_instanceMaskBuffer, m_tileBuffer) = (oldProgram, oldGrid, oldMasks, oldTiles);
                BindProgramCapacity();
                throw;
            }

            m_instanceGridInputScratch = inputScratch;
            m_instanceGridWorkspace = workspace;
            m_programWordCapacity = words;
            m_instanceCapacity = instances;
            m_instanceGridWordCapacity = gridWords;
            m_instanceMaskWordCount = maskWords;
            committed = true;

            if (growProgram) {
                oldProgram.Dispose();
            }
            if (growInstances) {
                oldGrid.Dispose();
                oldMasks.Dispose();
                oldTiles.Dispose();
            }
        } finally {
            if (!committed) {
                foreach (var replacement in replacements) { replacement.Dispose(); }
            }
        }
    }
    private static int GrowCapacity(int current, int required, int ceiling) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(required, ceiling);
        return ((required <= current) ? current : (int)Math.Max(val1: required, val2: Math.Min(val1: ceiling, val2: (((long)current) + Math.Max(val1: 1, val2: (current / 2))))));
    }
    // The same descriptor contracts as construction: views/primary/surface/ambient share viewsSets.
    private void BindProgramCapacity() {
        WriteStorageBufferReadOnly(binding: TileBindingIndex, buffer: m_tileBuffer, set: m_cullArgsSet);
        for (var slot = 0; (slot < FrameRingSize); slot++) {
            var beam = m_beamSets[slot];
            var cull = m_instanceCullSets[slot];
            var views = m_viewsSets[slot];

            BindRegions(slot: slot);
            WriteStorageBufferReadWrite(binding: TileBindingIndex, buffer: m_tileBuffer, set: beam);
            WriteStorageBufferReadOnly(binding: TileBindingIndex, buffer: m_tileBuffer, set: views);
            WriteStorageBufferReadOnly(binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer, set: beam);
            WriteStorageBufferReadWrite(binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer, set: cull);
            WriteStorageBufferReadOnly(binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer, set: views);
        }
    }
}
