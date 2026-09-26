using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// Every region the host writes and the node's passes read: the regions a package pass's package states
// (IRenderGraphPackageFactory.Regions), created with the graph, and the region a host binds to a host buffer port
// (BindRegion). Each is created under the policy GpuResidency.Select picks with a reader in flight, so a ring or a staged
// copy. The node owns them all through one mechanism: after the frame's passes have recorded (a package writes its
// regions while it records), it flushes every region's share of the slot and records every owed staged copy in one
// command buffer it submits ahead of the frame's passes, between a barrier ordering the earlier submissions' reads before
// the copies' writes and one buffer barrier per copied buffer making the writes visible to the shader stages that read
// it. A recorder records no barrier and no copy of its own.
public sealed partial class ShaderPipelineRenderNode {
    // The stages that read a region: a package draws with one (the overlay reads its region in the fragment stage) and a
    // compute pass dispatches with another (a source conversion reads its host buffer port), so a copy is made visible
    // to both.
    private const GpuStage RegionReaders = GpuStage.ComputeShader | GpuStage.FragmentShader;

    private readonly Dictionary<string, GpuRegion> m_externalRegions = new(comparer: StringComparer.Ordinal);
    private readonly List<IGpuBuffer> m_copiedRegions = [];

    // The device's region-copy pipeline, leased by the build of the first graph whose package stages a region, or by the
    // first host buffer port that stages, and each slot's command pool the frame's copies are recorded in; all held until a
    // device loss or disposal.
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_regionCopy;
    private IGpuComputePipeline? m_copyPipeline;
    private IGpuCommandPool[]? m_copyPools;

    /// <summary>Creates the region the host writes for a named external buffer, a host buffer port, and binds it. The
    /// region holds the buffer's declared size and takes the policy <see cref="GpuResidency.Select"/> picks for it with a
    /// reader in flight; a staged one creates its own copy pool, admitted into the device's heap here. The node owns the
    /// region from here: each frame it records, it binds the slot's buffer, flushes what the slot owes once the frame's
    /// passes have recorded and records the staged copy ahead of them, and it disposes the region on device loss and at
    /// disposal, the only way a name takes another region. The host writes the region's contents at any time.</summary>
    /// <param name="name">The external buffer's version name.</param>
    /// <returns>The region, or <see langword="null"/> while it would stage and the device's region-copy pipeline, which the
    /// first staged binding leases from the host's <see cref="RenderGraphPackageRecorders.RegionCopy"/>, is still being
    /// built; the host asks again on a later frame.</returns>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or not a declared external buffer of positive
    /// size in the candidate graph, or a region is already bound to it.</exception>
    /// <exception cref="InvalidOperationException">The region would stage and the host offers no region-copy
    /// pipeline.</exception>
    /// <exception cref="GpuDescriptorHeapRefusalException">The device's heap cannot admit a staged region's copy
    /// pool.</exception>
    public GpuRegion? BindRegion(string name) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var declaration = ValidateExternalBinding(
            kind: ShaderPipelineResourceKind.Buffer,
            name: name
        );

        if ((declaration?.SizeBytes is not { } sizeBytes) || (sizeBytes == 0UL) || (sizeBytes > int.MaxValue)) {
            throw new ArgumentException(
                message: $"External buffer '{name}' declares no size a region can hold.",
                paramName: nameof(name)
            );
        }
        if (m_externalRegions.ContainsKey(key: name)) {
            throw new ArgumentException(
                message: $"Region '{name}' is already bound; a name takes another region only after a device loss.",
                paramName: nameof(name)
            );
        }

        var staged = Staged(
            byteCount: sizeBytes,
            device: m_device
        );

        if (staged) {
            if (m_copyPipeline is null) {
                // A lease acquired on the frame thread polls its build rather than waiting for it.
                m_regionCopy ??= RegionCopyOf(
                    device: m_device,
                    instance: m_descriptor.Name,
                    packages: m_packages
                ).Acquire(device: m_device);
                m_copyPipeline = m_regionCopy.Poll()?.Compute;

                if (m_copyPipeline is null) {
                    return null;
                }
            }
            if (!m_gpu.Bindings.CanAdmit(
                owner: $"shader pipeline {m_descriptor.Name} region '{name}'",
                pools: [GpuRegionCopyPool.SizesOf(regionCount: 1, slotCount: ((int)m_inFlight))],
                refusal: out var refusal
            )) {
                throw new GpuDescriptorHeapRefusalException(message: refusal);
            }

            EnsureCopyPools();
        }

        var region = CreateRegion(
            byteCount: ((int)sizeBytes),
            copyPipeline: m_copyPipeline,
            copySets: null,
            name: new GpuObjectName(
                owner: m_descriptor.Name,
                part: name
            ),
            staged: staged
        );

        m_externalRegions.Add(
            key: name,
            value: region
        );
        m_externalBuffers[name] = region.Buffer(slot: 0);

        return region;
    }

    // Whether a region of byteCount bytes that a submission in flight reads stages on the device.
    private static bool Staged(IGpuDeviceContext device, ulong byteCount) =>
        (GpuResidency.Select(
            byteCount: byteCount,
            profile: device.MemoryProfile,
            readersInFlight: true
        ) == GpuResidencyPolicy.Staged);
    // The host's region-copy pipelines, which a staged region needs.
    private static GpuRegionCopyPass RegionCopyOf(IGpuDeviceContext device, string instance, RenderGraphPackageRecorders packages) =>
        (packages.RegionCopy ?? throw new InvalidOperationException(message: $"Instance '{instance}' stages a region on this device ({device.MemoryProfile}), but its host offers no region-copy pipeline."));
    // Takes the finished build's lease on the region-copy pipeline once its graph has installed: the node's own when it
    // holds none, and otherwise released, since both share the one pipeline the device's cache keeps.
    private void AdoptRegionCopy(GraphBuild built) {
        if (built.TakeRegionCopy() is not { } lease) {
            return;
        }

        if (m_regionCopy is null) {
            m_regionCopy = lease;
            m_copyPipeline = built.CopyPipeline;
        } else {
            lease.Release();
            m_copyPipeline ??= built.CopyPipeline;
        }
    }
    // Creates each slot's command pool the frame's region copies are recorded in, once; a failure partway releases the
    // pools it created.
    private void EnsureCopyPools() {
        if (m_copyPools is not null) {
            return;
        }

        var pools = new IGpuCommandPool[m_inFlight];

        try {
            for (var slot = 0; (slot < m_inFlight); slot++) {
                pools[slot] = m_gpu.CommandPoolFactory.Create(name: new GpuObjectName(
                    index: slot,
                    owner: m_descriptor.Name,
                    part: "region copies"
                ));
            }
        } catch {
            foreach (var pool in pools) {
                pool?.Dispose();
            }

            throw;
        }

        m_copyPools = pools;
    }
    // Binds each host buffer port's buffer for the slot, before the frame's passes write their descriptors.
    private void BindRegionBuffers(int slot) {
        foreach (var (name, region) in m_externalRegions) {
            m_externalBuffers[name] = region.Buffer(slot: slot);
        }
    }
    // Creates the regions a package pass's package states, in its order, and one copy pool reserving the staged ones'
    // sets, which DescriptorPools states for the pass. Each is stored as soon as it exists, so a failure partway leaves
    // it where RuntimePass.Dispose releases it.
    private void CreatePackageRegions(RuntimePass runtime, RenderGraphPackageRegion[] declared, IGpuComputePipeline? copyPipeline) {
        if (declared.Length == 0) {
            return;
        }

        var staged = new bool[declared.Length];
        var stagedNames = new List<GpuObjectName>();

        for (var index = 0; (index < declared.Length); index++) {
            staged[index] = Staged(
                byteCount: ((ulong)declared[index].ByteCount),
                device: m_device
            );

            if (staged[index]) {
                stagedNames.Add(item: NameOf(pass: runtime, region: declared[index]));
            }
        }

        if (stagedNames.Count > 0) {
            runtime.RegionCopySets = new GpuRegionCopyPool(
                bindings: m_gpu.Bindings,
                copyPipeline: (copyPipeline ?? throw new InvalidOperationException(message: $"Pass '{runtime.Name}' stages a region, but its build holds no region-copy pipeline.")),
                name: new GpuObjectName(
                    detail: "region copies",
                    owner: m_descriptor.Name,
                    part: runtime.Name
                ),
                regions: [.. stagedNames],
                slotCount: ((int)m_inFlight)
            );
        }

        runtime.Regions = new GpuRegion[declared.Length];

        var share = 0;

        for (var index = 0; (index < declared.Length); index++) {
            runtime.Regions[index] = CreateRegion(
                byteCount: declared[index].ByteCount,
                copyPipeline: copyPipeline,
                copySets: (staged[index]
                    ? runtime.RegionCopySets!.Region(index: share++)
                    : null),
                name: NameOf(pass: runtime, region: declared[index]),
                staged: staged[index]
            );
        }
    }
    private GpuRegion CreateRegion(int byteCount, bool staged, IGpuComputePipeline? copyPipeline, GpuRegionCopySets? copySets, in GpuObjectName name) => new(
        bindings: m_gpu.Bindings,
        buffers: m_gpu.BufferFactory,
        byteCount: byteCount,
        copyPipeline: (staged
            ? copyPipeline
            : null),
        copySets: copySets,
        memory: GpuResidency.RingMemory(profile: m_device.MemoryProfile),
        name: name,
        policy: (staged
            ? GpuResidencyPolicy.Staged
            : GpuResidencyPolicy.Ring),
        recorder: m_gpu.Recorder,
        slotCount: ((int)m_inFlight)
    );
    private GpuObjectName NameOf(RuntimePass pass, RenderGraphPackageRegion region) => new(
        detail: region.Name,
        owner: m_descriptor.Name,
        part: pass.Name
    );
    // Flushes every region's share of the slot, now that the frame's passes have written theirs, and records each owed
    // staged copy in the graph's copy command buffer for the slot, which goes ahead of the frame's passes.
    private void RecordRegionCopies(List<nint> commands, int slot) {
        var command = ((nint)0);

        foreach (var pass in m_passes) {
            if (pass.Regions is not { } regions) {
                continue;
            }

            foreach (var region in regions) {
                RecordRegionCopy(
                    command: ref command,
                    region: region,
                    slot: slot
                );
            }
        }
        foreach (var region in m_externalRegions.Values) {
            RecordRegionCopy(
                command: ref command,
                region: region,
                slot: slot
            );
        }

        if (command == 0) {
            return;
        }

        var recorder = m_gpu.Recorder;

        foreach (var buffer in m_copiedRegions) {
            recorder.TransitionBuffer(
                bufferHandle: buffer.BufferHandle,
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: RegionReaders,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
        }

        m_copiedRegions.Clear();
        recorder.EndDebugGroup(commandBufferHandle: command);
        recorder.EndCommandBuffer(commandBufferHandle: command);
        commands.Insert(
            index: 0,
            item: command
        );
    }
    // Flushes one region's share of the slot and records its copy when it owes one, beginning the frame's copy command
    // buffer, behind the barrier that orders the earlier submissions' reads of every staged destination before the
    // copies write them, at the first.
    private void RecordRegionCopy(GpuRegion region, int slot, ref nint command) {
        region.Flush(slot: slot);

        if (!region.OwesCopy) {
            return;
        }

        var recorder = m_gpu.Recorder;

        if (command == 0) {
            command = (m_copyPools ?? throw new InvalidOperationException(message: $"Instance '{m_descriptor.Name}' holds a staged region but no copy command pool."))[slot].CommandBufferHandle;
            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.BeginDebugGroup(
                commandBufferHandle: command,
                label: "region copies"
            );
            recorder.MemoryBarrier(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: GpuAccess.ShaderRead,
                sourceStageMask: RegionReaders
            );
        }

        region.RecordCopy(
            commandBuffer: command,
            slot: slot
        );
        m_copiedRegions.Add(item: region.Buffer(slot: slot));
    }
    // Releases the copy command pools and gives up the node's lease on the region-copy pipeline, once every region that
    // copies with it is disposed and no submission that recorded a copy is in flight.
    private void ReleaseRegionCopy() {
        foreach (var pool in (m_copyPools ?? [])) {
            pool.Dispose();
        }

        m_copyPools = null;
        m_regionCopy?.Release();
        m_regionCopy = null;
        m_copyPipeline = null;
    }
    // Disposes every bound region, once no submission can read it: after the node waited its submissions, after a device
    // loss, or when a retired node's host has seen its readers complete.
    private void ReleaseRegions() {
        foreach (var (name, region) in m_externalRegions) {
            _ = m_externalBuffers.Remove(key: name);
            region.Dispose();
        }

        m_externalRegions.Clear();
    }
}
