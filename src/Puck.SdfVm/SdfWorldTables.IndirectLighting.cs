using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private void PlanIndirectLighting(SdfIndirectCache cache, SdfFrame frame) {
        if ((ScreenClosure is not { HoldsLighting: true }) && cache.CanBeginLighting &&
            (!cache.HasLightingCycle || (cache.Lighting is null) || !cache.Lighting.Matches(frame: frame))) {
            var lighting = EnsureIndirectLighting(cache: cache);

            lighting.Capture(frame: frame);
            // CPU source regions are staged before the upload. An image-backed environment is produced afterward;
            // only the declared pair of successful copies admits the finite solve that consumes those sources.
            if (!lighting.AwaitingEnvironment) { cache.BeginLighting(); }
        }
        cache.PlanLighting();
    }

    internal PinnedIndirectLighting EnsureIndirectLighting(SdfIndirectCache cache) =>
        cache.Lighting ??= new PinnedIndirectLighting(tables: this);

    // The same World layout and table decoders serve live views and a finite indirect solve. The solve takes its
    // own immutable region contents; later frame packing cannot change a partly submitted sweep's sources.
    internal sealed partial class PinnedIndirectLighting : IDisposable {
        private static readonly string[] Members = [SdfWorldPackage.ProgramWords, SdfWorldPackage.DynamicTransforms,
            SdfWorldPackage.FrameInstanceGrid, SdfWorldPackage.ScreenSurfaces, SdfWorldPackage.ScreenMappings,
            SdfKernelInterfaces.Lights, SdfKernelInterfaces.ShadowHandoffs];

        private readonly SdfWorldTables m_tables;
        private readonly GpuRegionCopyPool m_copies;
        private readonly nint m_pool;

        private readonly nint[] m_sets = new nint[FrameRingSize];
        private readonly GpuRegion?[] m_regions = new GpuRegion?[Members.Length];
        private long m_bindingRevision = -1;

        private SdfLightGeometry m_geometry;
        private SdfPassValues m_values;
        private SdfFrame? m_frame;
        private ulong m_sequence;
        private ulong m_programRevision;

        public PinnedIndirectLighting(SdfWorldTables tables) {
            m_tables = tables;
            var world = GpuDescriptorPoolSizes.ForGroups(PipelineLayouts.World.Groups.Where(predicate: group => (group.Ordinal == WorldGroup)).ToArray());
            var sizes = default(GpuDescriptorPoolSizes);

            for (var slot = 0; (slot < FrameRingSize); slot++) { sizes += world; }
            if (!tables.m_bindings.CanAdmit(owner: "SDF indirect source", pools: [sizes,
                GpuRegionCopyPool.SizesOf(regionCount: Members.Length, slotCount: FrameRingSize)], refusal: out var refusal)) {
                throw new GpuDescriptorHeapRefusalException(message: refusal);
            }
            using var scope = new GpuCreationScope();

            m_pool = tables.m_bindings.CreatePool(name: NameOf(part: "indirect-source"), sizes: sizes);
            _ = scope.Own(handle: m_pool, release: tables.m_bindings.DestroyPool);
            var groups = tables.Pipeline(kernel: SdfKernel.Beam).GroupLayoutHandles;

            for (var slot = 0; (slot < FrameRingSize); slot++) {
                m_sets[slot] = tables.m_bindings.AllocateSet(name: NameOf(part: "indirect-source", index: slot),
                    descriptorSetLayoutHandle: groups[((int)WorldGroup)], poolHandle: m_pool);
            }
            m_copies = scope.Own(created: new GpuRegionCopyPool(bindings: tables.m_bindings, copyPipeline: tables.m_regionCopyPipeline,
                name: NameOf(part: "indirect-source-copies"), regions: Members.Select(selector: member => NameOf(part: "indirect-source", detail: member)).ToArray(),
                slotCount: FrameRingSize));
            scope.Complete();
        }

        public SdfFrame Frame => (m_frame ?? throw new InvalidOperationException(message: "The indirect source has not been captured."));
        public SdfPassValues Values => m_values;
        public SdfIndirectLightingSnapshot? Snapshot { get; private set; }
        public IEnumerable<GpuRegion> Regions => m_regions.OfType<GpuRegion>();
        public GpuMemoryBytes Bytes => Regions.Aggregate(new GpuMemoryBytes(DeviceLocal:
            (((EnvironmentMap?.SizeBytes ?? 0) + (EnvironmentCoefficients?.SizeBytes ?? 0)) + (ScreenEmission?.SizeBytes ?? 0)), HostVisible: 0), (bytes, region) => (bytes + region.OwnedBytes));

        public bool Matches(SdfFrame frame) {
            if (AwaitingEnvironment || !MatchesEnvironment(frame: frame)) { return false; }
            return MatchesScene(frame: frame);
        }

        internal bool MatchesScene(SdfFrame frame) {
            if ((m_frame is not { } held) || (m_geometry != m_tables.LightGeometry) || (m_programRevision != m_tables.m_programRevision) ||
                (m_values.ScreenCount != m_tables.PassValues.ScreenCount) || (held.FarDistance != frame.FarDistance) ||
                (held.DisableScreenLights != frame.DisableScreenLights) || (held.EnableShadowProxy != frame.EnableShadowProxy) ||
                (held.IndirectSources != frame.IndirectSources) || (held.IndirectGains != frame.IndirectGains) || (held.IndirectBounces != frame.IndirectBounces) ||
                (held.Lights.Count != frame.Lights.Count) || (held.Lights.ShadowSlots.SlotCount != frame.Lights.ShadowSlots.SlotCount) ||
                (held.Lights.ShadowSlots.FadeCapacity != frame.Lights.ShadowSlots.FadeCapacity) || (held.Lights.ShadowSlots.FadeCount != frame.Lights.ShadowSlots.FadeCount)) { return false; }
            for (var slot = 0; (slot < SdfShadowSlots.MaxSlots); slot++) {
                if ((held.Lights.ShadowSlots[slot] != frame.Lights.ShadowSlots[slot]) ||
                    (held.Lights.ShadowSlots.Owner(slot: slot) != frame.Lights.ShadowSlots.Owner(slot: slot))) { return false; }
            }
            for (var channel = 0; (channel < SdfShadowSlots.MaxFadeSlots); channel++) {
                if (held.Lights.ShadowSlots.IncomingOwner(channel: channel) != frame.Lights.ShadowSlots.IncomingOwner(channel: channel)) { return false; }
            }
            // Program, transforms and the grid have exact revisions; the bounded source tables compare their actual
            // packed bytes. Camera and presentation clock changes do not reauthor these lighting sources.
            for (var index = 3; (index < Members.Length); index++) {
                var source = Source(index: index);

                if ((m_regions[index] is not { } region) || (region.Contents.Length < source.Length) ||
                    !source.SequenceEqual(other: region.Contents[..source.Length])) { return false; }
            }
            return true;
        }

        public void Capture(SdfFrame frame) {
            if (AwaitingEnvironment && MatchesScene(frame: frame)) { return; }
            var grow = false;

            for (var index = 0; (index < Members.Length); index++) {
                grow |= ((m_regions[index] is not { } region) || (region.Contents.Length < Source(index: index).Length));
            }
            if (grow) { m_tables.m_deviceContext.TryWaitIdle(); }
            for (var index = 0; (index < Members.Length); index++) {
                var source = Source(index: index);

                if ((m_regions[index] is not { } region) || (region.Contents.Length < source.Length)) {
                    var profile = m_tables.m_deviceContext.MemoryProfile;
                    var replacement = new GpuRegion(bindings: m_tables.m_bindings, buffers: m_tables.m_gpu.BufferFactory,
                        byteCount: source.Length, copyPipeline: m_tables.m_regionCopyPipeline, copySets: m_copies.Region(index: index),
                        memory: GpuResidency.RingMemory(profile: profile), name: NameOf(part: "indirect-source", detail: Members[index]),
                        policy: GpuResidency.Select(byteCount: ((ulong)source.Length), profile: profile, readersInFlight: true),
                        recorder: m_tables.m_gpu.Recorder, slotCount: FrameRingSize);

                    m_regions[index]?.Dispose();
                    m_regions[index] = replacement;
                }
                m_regions[index]!.Write(bytes: source, offset: 0);
            }
            var lights = new SdfLights();

            lights.CopyFrom(source: frame.Lights);
            var sky = new SdfSky();

            sky.CopyFrom(source: frame.Sky);
            m_frame = frame with {
                Lights = lights,
                Sky = sky,
                DynamicTransforms = Array.AsReadOnly(array: frame.DynamicTransforms.ToArray()),
                Views = Array.AsReadOnly(array: frame.Views.ToArray()),
                MovedTransforms = null,
            };
            m_values = m_tables.PassValues;
            m_geometry = m_tables.LightGeometry;
            m_programRevision = m_tables.m_programRevision;
            AwaitingEnvironment = ((frame.IndirectSources & (SdfIndirectSources.Sky | SdfIndirectSources.Screens)) != 0);
            m_environment = default;
            m_screens = default;
            Snapshot = new SdfIndirectLightingSnapshot(m_frame, m_geometry, ++m_sequence, environment: default, screens: default, tainted: false);
            if (grow) { m_bindingRevision = -1; }
        }
        public nint WorldSet(int slot) {
            if (m_bindingRevision != m_tables.m_bindingRevision) {
                if (m_bindingRevision >= 0) { m_tables.m_deviceContext.TryWaitIdle(); }
                for (var ring = 0; (ring < FrameRingSize); ring++) {
                    m_tables.WriteWorldSet(set: m_sets[ring], slot: ring);
                    for (var index = 0; (index < Members.Length); index++) {
                        m_tables.WriteWorldBuffer(set: m_sets[ring], member: Members[index], buffer: m_regions[index]!.Buffer(slot: ring));
                    }
                    if ((EnvironmentMap is { } map) && (EnvironmentCoefficients is { } coefficients)) {
                        m_tables.WriteWorldBuffer(set: m_sets[ring], member: SdfKernelInterfaces.SkyEnvironment, buffer: map);
                        m_tables.WriteWorldBuffer(set: m_sets[ring], member: SdfKernelInterfaces.SkyCoefficients, buffer: coefficients);
                    }
                    if (ScreenEmission is { } screens) {
                        m_tables.WriteWorldBuffer(set: m_sets[ring], member: SdfWorldPackage.ScreenLights, buffer: screens);
                    }
                }
                m_bindingRevision = m_tables.m_bindingRevision;
            }
            return m_sets[slot];
        }
        public void Dispose() {
            foreach (var region in Regions) { region.Dispose(); }
            EnvironmentMap?.Dispose();
            EnvironmentCoefficients?.Dispose();
            ScreenEmission?.Dispose();
            m_copies.Dispose();
            m_tables.m_bindings.DestroyPool(poolHandle: m_pool);
        }

        private ReadOnlySpan<byte> Source(int index) => index switch {
            0 => m_tables.m_programRegion.Contents,
            1 => m_tables.m_dynamicTransformRegion.Contents,
            2 => m_tables.m_instanceGridRegion.Contents,
            3 => m_tables.m_screenSurfaceRegion.Contents,
            4 => m_tables.m_screenMappingRegion.Contents,
            5 => MemoryMarshal.AsBytes(span: m_tables.m_lightRecords.AsSpan()),
            _ => MemoryMarshal.AsBytes(span: m_tables.m_shadowHandoffs.AsSpan()),
        };
    }
}
