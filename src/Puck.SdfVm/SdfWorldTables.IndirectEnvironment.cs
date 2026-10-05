using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    internal sealed partial class PinnedIndirectLighting {
        private GpuImagePublication m_environment;
        private GpuImagePublication m_screens;

        public IGpuBuffer? EnvironmentMap { get; private set; }
        public IGpuBuffer? EnvironmentCoefficients { get; private set; }
        public IGpuBuffer? ScreenEmission { get; private set; }
        public bool AwaitingEnvironment { get; private set; }

        public void InvalidateSource() {
            AwaitingEnvironment = false;
            m_environment = default;
            m_screens = default;
            Snapshot = null;
        }
        // These are the existing source snapshot's GPU-written regions. They need no CPU shadow or readback, and
        // their lifetime and bytes follow the same retained cache as the host-written source regions.
        public void EnsureEnvironmentBuffers() {
            if (EnvironmentMap is not null) { return; }
            if (m_bindingRevision >= 0) { m_tables.m_deviceContext.TryWaitIdle(); }
            using var scope = new GpuCreationScope();
            var map = scope.Own(created: m_tables.m_gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "indirect-source", detail: SdfKernelInterfaces.SkyEnvironment),
                sizeBytes: SdfSkyEnvironment.MapBytes, usage: GpuBufferUsage.Storage));
            var coefficients = scope.Own(created: m_tables.m_gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "indirect-source", detail: SdfKernelInterfaces.SkyCoefficients),
                sizeBytes: SdfSkyEnvironment.CoefficientBytes, usage: GpuBufferUsage.Storage));
            var screens = scope.Own(created: m_tables.m_gpu.BufferFactory.CreateDeviceLocal(
                name: NameOf(part: "indirect-source", detail: SdfWorldPackage.ScreenLights),
                sizeBytes: SdfScreenEmission.Bytes, usage: GpuBufferUsage.Storage));

            EnvironmentMap = map;
            EnvironmentCoefficients = coefficients;
            ScreenEmission = screens;
            m_bindingRevision = -1;
            scope.Complete();
        }

        private bool MatchesEnvironment(SdfFrame frame) => ((((frame.IndirectSources & SdfIndirectSources.Sky) == 0) ||
            (!m_tables.SkyEnvironmentOwes && (m_tables.SubmittedSkyEnvironment is { } submitted) &&
                (submitted.Publication == m_environment) && m_environment.IsKnown)) &&
            (((frame.IndirectSources & SdfIndirectSources.Screens) == 0) ||
                ((m_tables.ScreenClosure is { HoldsLighting: true }) && m_screens.IsKnown) ||
                (!m_tables.ScreenEmissionOwes && (m_tables.SubmittedScreenEmission is { } screens) &&
                    (screens.Publication == m_screens) && m_screens.IsKnown)));

        public bool CanRecordEnvironment => (AwaitingEnvironment &&
            (((Frame.IndirectSources & SdfIndirectSources.Sky) == 0) || (!m_tables.SkyEnvironmentOwes && (m_tables.SubmittedSkyEnvironment is not null))) &&
            (((Frame.IndirectSources & SdfIndirectSources.Screens) == 0) || (!m_tables.ScreenEmissionOwes && (m_tables.SubmittedScreenEmission is not null))));

        public void RecordEnvironment(in RenderGraphPackageRecording recording, SdfFrame frame, string prefix) {
            if (!CanRecordEnvironment || !MatchesScene(frame: frame)) {
                throw new InvalidOperationException(message: "The indirect environment copy requires the current prepared scene and its submitted projection.");
            }
            var coefficients = Input(recording.Inputs, SdfSkyEnvironmentGraph.Input, prefix);
            var map = Input(recording.Inputs, SdfSkyEnvironmentGraph.MapInput, prefix);

            if (!ReferenceEquals(objA: coefficients, objB: m_tables.SkyEnvironmentCoefficients) || !ReferenceEquals(objA: map, objB: m_tables.SkyEnvironmentMap)) {
                throw new InvalidOperationException(message: "Both indirect environment inputs must belong to the projection's actual tables.");
            }
            if ((Frame.IndirectSources & SdfIndirectSources.Sky) != 0) {
                Copy(recording, coefficients, Input(recording.Outputs, SdfSkyEnvironmentGraph.PinnedCoefficients, prefix), SdfSkyEnvironment.CoefficientBytes);
                Copy(recording, map, Input(recording.Outputs, SdfSkyEnvironmentGraph.PinnedMap, prefix), SdfSkyEnvironment.MapBytes);
                m_environment = m_tables.SubmittedSkyEnvironment!.Value.Publication;
            }
            if ((Frame.IndirectSources & SdfIndirectSources.Screens) != 0) {
                var screens = Input(recording.Inputs, SdfSkyEnvironmentGraph.ScreensInput, prefix);

                if (!ReferenceEquals(objA: screens, objB: m_tables.ScreenEmission)) {
                    throw new InvalidOperationException(message: "The indirect screen input must be the submitted reduction's actual buffer.");
                }
                Copy(recording, screens, Input(recording.Outputs, SdfSkyEnvironmentGraph.PinnedScreens, prefix), SdfScreenEmission.Bytes);
                m_screens = m_tables.SubmittedScreenEmission!.Value.Publication;
            }
            // A pending graph may take several frames to install. Its host source regions were restaged before this
            // frame's upload, and the projection used this frame's sky. Freeze that sky alongside the actual pair.
            var sky = new SdfSky();

            sky.CopyFrom(source: frame.Sky);
            m_frame = Frame with { Sky = sky };
            var tainted = ((((Frame.IndirectSources & SdfIndirectSources.Sky) != 0) && m_tables.SubmittedSkyEnvironment!.Value.Tainted) ||
                (((Frame.IndirectSources & SdfIndirectSources.Screens) != 0) && m_tables.SubmittedScreenEmission!.Value.Tainted));

            Snapshot = new SdfIndirectLightingSnapshot(environment: m_environment, frame: m_frame, geometry: m_geometry, screens: m_screens, sequence: m_sequence, tainted: tainted);
        }
        public void EnvironmentSubmitted(SdfIndirectCache cache) {
            if (!AwaitingEnvironment ||
                (((Frame.IndirectSources & SdfIndirectSources.Sky) != 0) && !m_environment.IsKnown) ||
                (((Frame.IndirectSources & SdfIndirectSources.Screens) != 0) && !m_screens.IsKnown)) { return; }
            AwaitingEnvironment = false;
            cache.BeginLighting();
            cache.PlanLighting();
        }

        private static IGpuBuffer Input(ReadOnlySpan<RenderGraphPackageResource> resources, string version, string prefix) {
            foreach (var resource in resources) {
                if (((resource.Version == version) || (resource.Version.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix) &&
                    resource.Version.AsSpan(start: prefix.Length).SequenceEqual(other: version))) && (resource.Buffer is { } buffer)) { return buffer; }
            }
            throw new InvalidOperationException(message: $"The indirect environment pass has no buffer '{version}'.");
        }
        private static void Copy(in RenderGraphPackageRecording recording, IGpuBuffer source, IGpuBuffer destination, ulong bytes) =>
            recording.Recorder.CopyBuffer(commandBufferHandle: recording.CommandBuffer, sourceBufferHandle: source.BufferHandle,
                destinationBufferHandle: destination.BufferHandle, sizeBytes: bytes);
    }
}
