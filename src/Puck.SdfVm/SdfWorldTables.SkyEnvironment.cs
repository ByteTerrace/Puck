using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The sky's environment: one map and its coefficients a residency keeps for its sky (SdfSkyEnvironment), rendered by the
// residency's upload, the one submission a frame every view of the residency follows, and read by every view's composite
// through the World set. The upload renders them only when the sky it packed draws another gradient than the map holds
// (SdfSkyEnvironment.SameMap) and the fog reads the map (a positive density), so a still sky renders them once and then
// records nothing: the pass reads skipped. One copy serves every frame in flight: the views that read the map are
// submitted on the same queue before the upload that rewrites it, whose first barrier orders their reads before its writes,
// as the brick pool's is. A refresh is the map's dispatch, one invocation a texel, then the reduction's, one group, between
// the barriers that hand each buffer from its readers to its writer and back, with the kernel counters cleared before them
// and copied after, counted under the environment pass (EnvironmentPass).
public sealed partial class SdfWorldTables {
    // The map's dispatch groups along each axis: sdf-sky-environment.comp's [numthreads(8, 8, 1)].
    private const uint SkyEnvironmentGroups = (SdfSkyEnvironment.Size / 8);

    private readonly SkyEnvironmentPass m_skyEnvironment;

    /// <summary>Gets the bytes the sky's environment keeps on the device: the map and its coefficients
    /// (<see cref="SdfSkyEnvironment.PayloadBytes"/>), one pair however many views read it.</summary>
    public static int SkyEnvironmentBytes => SdfSkyEnvironment.PayloadBytes;
    /// <summary>Gets how many times the upload has rendered the sky's environment: once for each change of the gradient
    /// the fog reads, and never on an upload whose sky draws the gradient the map holds.</summary>
    public long SkyEnvironmentRenders => m_skyEnvironment.Renders;

    // The descriptor pool the environment's sets take from the tables' own: its frame set and a pass set per ring slot.
    private static GpuDescriptorPoolSizes SkyEnvironmentPoolSizes() {
        var groups = PipelineLayouts.Environment.Groups;
        var sizes = GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == FrameGroup)).ToArray());
        var pass = GpuDescriptorPoolSizes.ForGroups(groups: groups.Where(predicate: static group => (group.Ordinal == PassGroup)).ToArray());

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            sizes += pass;
        }

        return sizes;
    }
    // Whether this upload renders the environment, which the fog reads and whose gradient moved; records it if so.
    private void RecordSkyEnvironment(nint commandBuffer, int slot) {
        if (!m_skyEnvironment.Owes(block: in m_skyRecord[0], stops: m_skyStopRecords)) {
            m_work.SkipPass(pass: EnvironmentPass);

            return;
        }

        m_work.EnterPass(pass: EnvironmentPass);
        m_skyEnvironment.Record(
            commandBuffer: commandBuffer,
            pipelines: m_pipelines,
            recorder: m_gpu.Recorder,
            slot: slot
        );
        m_work.LeavePass();
        m_work.ReadOnCompletion(readback: m_skyEnvironment.Counters, slot: slot);
        m_skyEnvironment.Rendered(block: in m_skyRecord[0], stops: m_skyStopRecords);
    }

    // The environment's objects and the gradient its map holds.
    private sealed class SkyEnvironmentPass : IDisposable {
        private readonly IGpuBuffer m_map;
        private readonly IGpuBuffer m_coefficients;
        private readonly IGpuStorageBuffer m_frameBlock;
        private readonly IGpuStorageBuffer m_block;
        private readonly nint m_frameSet;

        private readonly nint[] m_sets = new nint[FrameRingSize];

        private readonly GpuKernelCounters m_counters;

        private readonly SdfSkyStop[] m_renderedStops = new SdfSkyStop[SdfSky.MaxStops];

        private readonly SdfWorldTables m_tables;
        private readonly IGpuBindings m_bindings;

        private SdfSkyBlock m_renderedBlock;
        private bool m_holdsSky;
        private bool m_written;

        // Creates the map, the coefficients, the blocks and the sets, each owned by the tables' construction scope. Nothing is
        // written into them until the first refresh (WriteOnce), so the writes count under the environment pass that
        // needs them, never outside every pass of the tables' first submission.
        public SkyEnvironmentPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            var layout = SdfWorldInterfaces.EnvironmentParameters;
            var groups = tables.m_pipelines.Pipeline(kernel: SdfKernel.SkyEnvironment).GroupLayoutHandles;

            m_tables = tables;
            m_bindings = gpu.Bindings;
            m_map = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "sky-environment", detail: "map"),
                sizeBytes: SdfSkyEnvironment.MapBytes,
                usage: GpuBufferUsage.Storage
            ));
            m_coefficients = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "sky-environment", detail: "coefficients"),
                sizeBytes: SdfSkyEnvironment.CoefficientBytes,
                usage: GpuBufferUsage.Storage
            ));
            m_counters = scope.Own(created: new GpuKernelCounters(
                buffers: gpu.BufferFactory,
                owner: ObjectOwner,
                part: "sky-environment-counters",
                rows: PassLabelTable.Length,
                slots: FrameRingSize
            ));
            m_frameBlock = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                name: NameOf(part: "sky-environment", detail: "frame block"),
                sizeBytes: ((ulong)UniformBytes(blockBytes: layout.FrameBlockSizeBytes)),
                usage: GpuBufferUsage.Uniform
            ));
            m_block = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                name: NameOf(part: "sky-environment", detail: "block"),
                sizeBytes: ((ulong)UniformBytes(blockBytes: layout.SizeBytes)),
                usage: GpuBufferUsage.Uniform
            ));
            m_frameSet = gpu.Bindings.AllocateSet(
                name: NameOf(part: "sky-environment", detail: "frame group"),
                descriptorSetLayoutHandle: groups[((int)FrameGroup)],
                poolHandle: tables.m_pool
            );
            for (var slot = 0; (slot < FrameRingSize); slot++) {
                m_sets[slot] = gpu.Bindings.AllocateSet(
                    name: NameOf(part: "sky-environment", index: slot),
                    descriptorSetLayoutHandle: groups[((int)PassGroup)],
                    poolHandle: tables.m_pool
                );
            }
        }

        // The map, which every view's composite binds in the World set.
        public IGpuBuffer Map => m_map;
        // The counter buffers the upload's environment pass counts into.
        public GpuKernelCounters Counters => m_counters;
        // How many times the map has rendered.
        public long Renders { get; private set; }

        // Whether an upload of a sky owes the map: the fog reads it, and it holds no sky or other field runs.
        public bool Owes(in SdfSkyBlock block, ReadOnlySpan<SdfSkyStop> stops) => (
            (block.FogDensity > 0f) &&
            (
                !m_holdsSky ||
                !SdfSkyEnvironment.SameMap(block: in block, otherBlock: in m_renderedBlock, otherStops: m_renderedStops, stops: stops)
            )
        );
        // Records that the map holds a sky's gradient.
        public void Rendered(in SdfSkyBlock block, ReadOnlySpan<SdfSkyStop> stops) {
            m_renderedBlock = block;
            stops.CopyTo(destination: m_renderedStops);
            m_holdsSky = true;
            Renders++;
        }
        // Forgets the sky the map holds, so the next upload whose fog reads it renders it: a kernel reload's.
        public void Forget() =>
            m_holdsSky = false;
        // Records a refresh: on the first, the blocks' and sets' writes (WriteOnce); then the counters' clear, the map's readers before its writes, the map, its writes before the
        // reduction reads it and the coefficients' readers before the reduction writes them, the reduction, both handed to
        // their readers, then the counters' copy.
        public void Record(IGpuRecorder recorder, nint commandBuffer, SdfWorldPipelines pipelines, int slot) {
            WriteOnce();
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, label: "sky-environment");
            m_counters.RecordClear(commandBuffer: commandBuffer, recorder: recorder, slot: slot);
            Transition(access: (GpuAccess.ShaderRead, GpuAccess.ShaderWrite), buffer: m_map, commandBuffer: commandBuffer, recorder: recorder);
            Dispatch(commandBuffer: commandBuffer, groupsX: SkyEnvironmentGroups, groupsY: SkyEnvironmentGroups, pipeline: pipelines.Pipeline(kernel: SdfKernel.SkyEnvironment), recorder: recorder, slot: slot);
            Transition(access: (GpuAccess.ShaderWrite, GpuAccess.ShaderRead | GpuAccess.ShaderWrite), buffer: m_map, commandBuffer: commandBuffer, recorder: recorder);
            Transition(access: (GpuAccess.ShaderRead, GpuAccess.ShaderWrite), buffer: m_coefficients, commandBuffer: commandBuffer, recorder: recorder);
            Dispatch(commandBuffer: commandBuffer, groupsX: 1u, groupsY: 1u, pipeline: pipelines.Pipeline(kernel: SdfKernel.SkyEnvironmentReduce), recorder: recorder, slot: slot);
            Transition(access: (GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.ShaderRead), buffer: m_map, commandBuffer: commandBuffer, recorder: recorder);
            Transition(access: (GpuAccess.ShaderWrite, GpuAccess.ShaderRead), buffer: m_coefficients, commandBuffer: commandBuffer, recorder: recorder);
            m_counters.RecordCopy(commandBuffer: commandBuffer, recorder: recorder, slot: slot);
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
        }

        // Writes the blocks and the sets once, inside the first refresh's pass: the frame block, the block holding the map's
        // extent and the environment pass's counter row, the frame set, and a pass set per ring slot binding the slot's sky
        // block and stops, which the upload has copied before it renders, the map, the coefficients and the counters.
        private void WriteOnce() {
            if (m_written) {
                return;
            }

            var layout = SdfWorldInterfaces.EnvironmentParameters;
            var frameBlockBytes = new byte[layout.FrameBlockSizeBytes];
            var blockBytes = new byte[layout.SizeBytes];

            layout.WriteFrame(block: frameBlockBytes, extent: default, frame: 0UL, values: default);
            m_frameBlock.Write<byte>(data: frameBlockBytes);
            layout.WriteExtent(block: blockBytes, height: SdfSkyEnvironment.Size, width: SdfSkyEnvironment.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: blockBytes.AsSpan(start: ((int)layout.BlockOffsetOf(member: ShaderWorkCounters.Row))), value: EnvironmentPass);
            m_block.Write<byte>(data: blockBytes);
            m_bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: m_frameBlock.BufferHandle, bufferSize: m_frameBlock.SizeBytes, descriptorSetHandle: m_frameSet);
            for (var slot = 0; (slot < FrameRingSize); slot++) {
                var set = m_sets[slot];

                m_bindings.WriteConstantBuffer(arrayElement: 0, binding: 0, bufferHandle: m_block.BufferHandle, bufferSize: m_block.SizeBytes, descriptorSetHandle: set);
                m_tables.WriteInterfaceBuffer(buffer: m_tables.m_skyRegion.Buffer(slot: slot), layout: layout.Layout, member: SdfKernelInterfaces.Sky, set: set);
                m_tables.WriteInterfaceBuffer(buffer: m_tables.m_skyStopRegion.Buffer(slot: slot), layout: layout.Layout, member: SdfKernelInterfaces.SkyStops, set: set);
                m_tables.WriteInterfaceBuffer(buffer: m_map, layout: layout.Layout, member: SdfKernelInterfaces.SkyEnvironmentWritten, set: set);
                m_tables.WriteInterfaceBuffer(buffer: m_coefficients, layout: layout.Layout, member: SdfKernelInterfaces.SkyCoefficientsWritten, set: set);
                m_tables.WriteInterfaceBuffer(buffer: m_counters.RowOf(row: EnvironmentPass, slot: slot).Buffer, layout: layout.Layout, member: ShaderWorkCounters.Buffer, set: set);
            }
            m_written = true;
        }

        public void Dispose() {
            m_counters.Dispose();
            m_block.Dispose();
            m_frameBlock.Dispose();
            m_coefficients.Dispose();
            m_map.Dispose();
        }

        // Binds a kernel and both sets, then dispatches its groups.
        private void Dispatch(IGpuRecorder recorder, nint commandBuffer, IGpuComputePipeline pipeline, uint groupsX, uint groupsY, int slot) {
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, pipelineHandle: pipeline.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, descriptorSetHandle: m_frameSet, group: FrameGroup, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: commandBuffer, descriptorSetHandle: m_sets[slot], group: PassGroup, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(commandBufferHandle: commandBuffer, groupCountX: groupsX, groupCountY: groupsY, groupCountZ: 1);
        }
        // One compute-to-compute buffer barrier.
        private static void Transition(IGpuRecorder recorder, nint commandBuffer, IGpuBuffer buffer, (GpuAccess Source, GpuAccess Destination) access) =>
            recorder.TransitionBuffer(
                bufferHandle: buffer.BufferHandle,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: access.Destination,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: access.Source,
                sourceStageMask: GpuStage.ComputeShader
            );
    }
}
