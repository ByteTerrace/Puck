using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    internal sealed partial class PinnedIndirectLighting {
        private GpuImagePublication m_environment;

        public IGpuBuffer? EnvironmentMap { get; private set; }
        public IGpuBuffer? EnvironmentCoefficients { get; private set; }
        public bool AwaitingEnvironment { get; private set; }

        // These are the existing source snapshot's GPU-written regions. They need no CPU shadow or readback, and
        // their lifetime and bytes follow the same retained cache as the host-written source regions.
        public void EnsureEnvironmentBuffers() {
            if (EnvironmentMap is not null) { return; }
            if (m_bindingRevision >= 0) { m_tables.m_deviceContext.TryWaitIdle(); }
            using var scope = new GpuCreationScope();
            var map = scope.Own(m_tables.m_gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "indirect-source", detail: SdfKernelInterfaces.SkyEnvironment),
                sizeBytes: SdfSkyEnvironment.MapBytes, usage: GpuBufferUsage.Storage));
            var coefficients = scope.Own(m_tables.m_gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "indirect-source", detail: SdfKernelInterfaces.SkyCoefficients),
                sizeBytes: SdfSkyEnvironment.CoefficientBytes, usage: GpuBufferUsage.Storage));
            EnvironmentMap = map;
            EnvironmentCoefficients = coefficients;
            m_bindingRevision = -1;
            scope.Complete();
        }

        private bool MatchesEnvironment(SdfFrame frame) => (frame.IndirectSources & SdfIndirectSources.Sky) == 0 ||
            (!m_tables.SkyEnvironmentOwes && m_tables.SubmittedSkyEnvironment is { } submitted &&
                submitted.Publication == m_environment && m_environment.IsKnown);

        public bool CanRecordEnvironment => AwaitingEnvironment && !m_tables.SkyEnvironmentOwes &&
            m_tables.SubmittedSkyEnvironment is not null;

        public void RecordEnvironment(in RenderGraphPackageRecording recording, SdfFrame frame, string prefix) {
            if (!CanRecordEnvironment || m_tables.SubmittedSkyEnvironment is not { } submitted || !MatchesScene(frame)) {
                throw new InvalidOperationException("The indirect environment copy requires the current prepared scene and its submitted projection.");
            }
            var coefficients = Input(recording.Inputs, SdfSkyEnvironmentGraph.Input, prefix);
            var map = Input(recording.Inputs, SdfSkyEnvironmentGraph.MapInput, prefix);
            if (!ReferenceEquals(coefficients, m_tables.SkyEnvironmentCoefficients) || !ReferenceEquals(map, m_tables.SkyEnvironmentMap)) {
                throw new InvalidOperationException("Both indirect environment inputs must belong to the projection's actual tables.");
            }
            Copy(recording, coefficients, Input(recording.Outputs, SdfSkyEnvironmentGraph.PinnedCoefficients, prefix), SdfSkyEnvironment.CoefficientBytes);
            Copy(recording, map, Input(recording.Outputs, SdfSkyEnvironmentGraph.PinnedMap, prefix), SdfSkyEnvironment.MapBytes);
            // A pending graph may take several frames to install. Its host source regions were restaged before this
            // frame's upload, and the projection used this frame's sky. Freeze that sky alongside the actual pair.
            var sky = new SdfSky();
            sky.CopyFrom(frame.Sky);
            m_frame = Frame with { Sky = sky };
            m_environment = submitted.Publication;
            Snapshot = new SdfIndirectLightingSnapshot(m_frame, m_geometry, m_sequence, m_environment);
        }

        public void EnvironmentSubmitted(SdfIndirectCache cache) {
            if (!AwaitingEnvironment || !m_environment.IsKnown) { return; }
            AwaitingEnvironment = false;
            cache.BeginLighting();
            cache.PlanLighting();
        }

        private static IGpuBuffer Input(ReadOnlySpan<RenderGraphPackageResource> resources, string version, string prefix) {
            foreach (var resource in resources) {
                if ((resource.Version == version || (resource.Version.StartsWith(prefix, StringComparison.Ordinal) &&
                    resource.Version.AsSpan(prefix.Length).SequenceEqual(version))) && resource.Buffer is { } buffer) { return buffer; }
            }
            throw new InvalidOperationException($"The indirect environment pass has no buffer '{version}'.");
        }

        private static void Copy(in RenderGraphPackageRecording recording, IGpuBuffer source, IGpuBuffer destination, ulong bytes) =>
            recording.Recorder.CopyBuffer(commandBufferHandle: recording.CommandBuffer, sourceBufferHandle: source.BufferHandle,
                destinationBufferHandle: destination.BufferHandle, sizeBytes: bytes);
    }
}
