using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// Every region the host writes and the node's passes read: the regions a package pass's package states
// (IRenderGraphPackageFactory.Regions), created with the graph, and the region a host binds to a host buffer port
// (BindRegion). Each is created under the policy GpuResidency.Select picks with a reader in flight, so a ring or a staged
// copy. The installed graph holds one copy pool reserving every staged region's sets, its package regions' in pass
// order and then its ports' in declaration order (DescriptorPools), admitted with the graph and owned by its first pass,
// so it retires with the graph; a bound port's region moves to the next installed graph's share. The node owns them all
// through one mechanism: after the frame's passes have recorded (a package writes its
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

    // The device's region-copy pipeline, leased by the build of the first graph with a staged region, and each slot's
    // command pool the frame's copies are recorded in; all held until a device loss or disposal.
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_regionCopy;
    private IGpuComputePipeline? m_copyPipeline;
    private IGpuCommandPool[]? m_copyPools;
    // The installed graph's copy pool, owned by its first pass, and the share each of its staged host buffer ports
    // takes. While a graph allocates, the pool it created belongs to m_regionCopiesOwner until its first pass takes it,
    // and m_nextRegionShare is the share its next staged package region takes.
    private GpuRegionCopyPool? m_regionCopies;
    private GpuRegionCopyPool? m_regionCopiesOwner;
    private int m_nextRegionShare;

    private Dictionary<string, int> m_portShares = new(comparer: StringComparer.Ordinal);

    /// <summary>Creates the region the host writes for a host buffer port (<see cref="ShaderPipelineInitialization.Host"/>)
    /// and binds it. The region holds the port's declared size and takes the policy <see cref="GpuResidency.Select"/> picks
    /// for it with a reader in flight; a staged one takes the copy sets the installed graph's copy pool reserved for the
    /// port, so binding takes no descriptor range. The node owns the region from here: each frame it records, it binds
    /// the slot's buffer, flushes what the slot owes once the frame's passes have recorded and records the staged copy
    /// ahead of them, moves it to each later graph's share, and disposes it on device loss and at disposal, the only way a
    /// name takes another region. The host writes the region's contents at any time.</summary>
    /// <param name="name">The host buffer port's name.</param>
    /// <returns>The region, or <see langword="null"/> while it would stage and no installed graph reserves its copy sets
    /// yet, which binding advances as a produced frame does (the candidate's build starts, and installs once finished);
    /// the host asks again on a later frame.</returns>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or not a declared host buffer port of the
    /// candidate graph, or a region is already bound to it.</exception>
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

        if (
            (declaration is not { IsHostBuffer: true, SizeBytes: { } sizeBytes }) ||
            (sizeBytes == 0UL) ||
            (sizeBytes > int.MaxValue)
        ) {
            throw new ArgumentException(
                message: $"Resource '{name}' is not a host buffer port of a size a region can hold.",
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
        var share = -1;

        if (staged) {
            // A port's copy sets are the installed graph's, so until a graph reserving them installs, binding advances the
            // candidate's build and install as a produced frame does: a host that produces only once its region is bound
            // would otherwise never install one.
            if (!m_portShares.ContainsKey(key: name)) {
                EnsureBuild();
                InstallPending();
            }
            if (!m_portShares.TryGetValue(
                key: name,
                value: out share
            )) {
                return null;
            }
        }

        var region = CreateRegion(
            byteCount: ((int)sizeBytes),
            copyPipeline: m_copyPipeline,
            copySets: (staged
                ? m_regionCopies!.Region(index: share)
                : null),
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
    // The host buffer ports a graph declares, in declaration order.
    private static List<ShaderPipelineResource> HostBufferPorts(ShaderPipelinePlan plan) => [
        .. plan.Resources
            .Select(selector: static resource => resource.Declaration)
            .Where(predicate: static declaration => declaration.IsHostBuffer),
    ];
    // Creates the installing graph's copy pool when any of its regions stages: one copy set per slot for each staged
    // package region, in pass order, then for each staged host buffer port, in declaration order. The pool belongs to
    // m_regionCopiesOwner until the graph's first pass takes it.
    private void CreateRegionCopies(GraphBuild built, ShaderPipelinePlan plan) {
        var names = new List<GpuObjectName>();
        var ports = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var planned in plan.Passes) {
            foreach (var region in (built.Passes[planned.Index]?.Regions ?? [])) {
                if (Staged(byteCount: ((ulong)region.ByteCount), device: m_device)) {
                    names.Add(item: NameOf(pass: planned.Name, region: region));
                }
            }
        }
        foreach (var port in HostBufferPorts(plan: plan)) {
            if (Staged(byteCount: port.SizeBytes!.Value, device: m_device)) {
                ports.Add(
                    key: port.Name,
                    value: names.Count
                );
                names.Add(item: new GpuObjectName(
                    owner: m_descriptor.Name,
                    part: port.Name
                ));
            }
        }

        m_nextRegionShare = 0;
        m_portShares = ports;
        m_regionCopies = ((names.Count == 0)
            ? null
            : new GpuRegionCopyPool(
                bindings: m_gpu.Bindings,
                copyPipeline: (built.CopyPipeline ?? throw new InvalidOperationException(message: $"Instance '{m_descriptor.Name}' stages a region, but its build holds no region-copy pipeline.")),
                name: new GpuObjectName(
                    owner: m_descriptor.Name,
                    part: "region copies"
                ),
                regions: [.. names],
                slotCount: ((int)m_inFlight)
            ));
        m_regionCopiesOwner = m_regionCopies;
    }
    // Moves each bound port's staged region to the share the graph just installed reserved for it; RequireSwappable keeps
    // every bound port declared, at its size, by each graph a node installs.
    private void MovePortRegions() {
        foreach (var (name, region) in m_externalRegions) {
            if (region.Policy == GpuResidencyPolicy.Staged) {
                region.MoveCopySets(copySets: m_regionCopies!.Region(index: m_portShares[name]));
            }
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
    // Creates the regions a package pass's package states, in its order, each staged one taking the next share of the
    // graph's copy pool, which CreateRegionCopies reserved in the same order. Each is stored as soon as it exists, so a
    // failure partway leaves it where RuntimePass.Dispose releases it.
    private void CreatePackageRegions(RuntimePass runtime, RenderGraphPackageRegion[] declared, IGpuComputePipeline? copyPipeline) {
        if (declared.Length == 0) {
            return;
        }

        runtime.Regions = new GpuRegion[declared.Length];

        for (var index = 0; (index < declared.Length); index++) {
            var staged = Staged(
                byteCount: ((ulong)declared[index].ByteCount),
                device: m_device
            );

            runtime.Regions[index] = CreateRegion(
                byteCount: declared[index].ByteCount,
                copyPipeline: copyPipeline,
                copySets: (staged
                    ? m_regionCopies!.Region(index: m_nextRegionShare++)
                    : null),
                name: NameOf(pass: runtime.Name, region: declared[index]),
                staged: staged
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
    private GpuObjectName NameOf(string pass, RenderGraphPackageRegion region) => new(
        detail: region.Name,
        owner: m_descriptor.Name,
        part: pass
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
        m_regionCopies = null;
        m_portShares = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
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
