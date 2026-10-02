using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly GpuRegion?[] m_skyKindRegions = new GpuRegion?[SdfSkyKindBindings.All.Count];
    private readonly int[] m_skyKindIndices = new int[SdfSkyKindBindings.All.Count];
    private GpuRegion? m_skyLayersRegion;
    private GpuRegion? m_skyAdmissionRegion;
    private SdfSkySnapshot? m_skyLayout;
    private uint[] m_skyAdmissionWords = [];
    private SkyViewAdmission[] m_skyViewAdmissions = [];
    private int m_skyLayerCount;

    private struct SkyViewAdmission {
        internal bool Prepared;
        internal ulong Revision;
        internal QualityTier Quality;
        internal SdfSkyInspection? Inspection;
    }

    /// <summary>Gets the cumulative number of per-view admission rows rewritten after a layout, quality or
    /// inspection change. Native region writes independently count the bytes that actually change.</summary>
    public ulong SkyAdmissionRewrites { get; private set; }

    private void PackSkyTables(SdfFrame frame) {
        var sky = frame.Environment.Sky;
        var layoutChanged = !(sky?.SharesLayout(m_skyLayout) ?? m_skyLayout is null);
        if (layoutChanged) {
            Array.Fill(m_skyKindIndices, -1);
            if (sky is not null) {
                for (var tableIndex = 0; tableIndex < sky.TableCount; tableIndex++) {
                    var table = sky.Table(tableIndex);
                    var found = false;
                    for (var kind = 0; kind < SdfSkyKindBindings.All.Count; kind++) {
                        var binding = SdfSkyKindBindings.All[kind];
                        if (binding.Name != table.Kind) { continue; }
                        if (binding.Member.Structure!.SizeBytes != table.Stride || m_skyKindIndices[kind] >= 0) {
                            throw new ArgumentException($"Sky table '{table.Kind}' has a duplicate or incompatible native record.", nameof(frame));
                        }
                        m_skyKindIndices[kind] = tableIndex;
                        found = true;
                        break;
                    }
                    if (!found) { throw new ArgumentException($"Sky table '{table.Kind}' is not registered.", nameof(frame)); }
                }
            }
            m_skyLayout = sky;
            Array.Clear(m_skyViewAdmissions);
            m_bindingRevision++;
        }
        m_skyLayerCount = sky?.LayerCount ?? 0;
        if (sky is null || sky.LayerCount == 0) { return; }
        if (sky.Common is not { } common) {
            throw new ArgumentException("A rendered sky snapshot requires native common rows.", nameof(frame));
        }
        if (common.Count != sky.LayerCount || common.Stride != Marshal.SizeOf<SdfSkyLayerData>()) {
            throw new ArgumentException("Sky common rows must match the prepared native layer layout.", nameof(frame));
        }
        var admissionCount = checked(frame.Views.Count * sky.LayerCount);
        EnsureSkyCapacity(sky, admissionCount);
        _ = m_skyLayersRegion!.Write(bytes: common.Bytes, offset: 0);
        if (sky.Stops is { } stops) { _ = m_skyStopsRegion.Write(bytes: stops.Bytes, offset: 0); }
        for (var kind = 0; kind < m_skyKindIndices.Length; kind++) {
            if (m_skyKindIndices[kind] is var tableIndex && tableIndex >= 0) {
                _ = m_skyKindRegions[kind]!.Write(bytes: sky.Table(tableIndex).Bytes, offset: 0);
            }
        }
        if (m_skyAdmissionWords.Length < admissionCount) { Array.Resize(ref m_skyAdmissionWords, admissionCount); }
        if (m_skyViewAdmissions.Length < frame.Views.Count) { Array.Resize(ref m_skyViewAdmissions, frame.Views.Count); }
        for (var view = 0; view < frame.Views.Count; view++) {
            var snapshot = frame.Views[view];
            var quality = snapshot.SkyQuality ?? sky.Quality;
            ref var prior = ref m_skyViewAdmissions[view];
            if (prior.Prepared && prior.Revision == sky.AdmissionRevision && prior.Quality == quality
                && ReferenceEquals(prior.Inspection, snapshot.SkyInspection)) { continue; }
            var first = checked(view * sky.LayerCount);
            for (var layerIndex = 0; layerIndex < sky.LayerCount; layerIndex++) {
                var layer = sky.Layer(layerIndex);
                var allowed = (layer.Visibility & SdfSkyVisibility.Camera) != 0 && quality >= layer.MinimumTier
                    && (snapshot.SkyInspection?.Allows(layer.Name) ?? true);
                m_skyAdmissionWords[first + layerIndex] = allowed ? (uint)quality + 1u : 0u;
                SkyAdmissionRewrites++;
            }
            prior = new() { Prepared = true, Revision = sky.AdmissionRevision, Quality = quality, Inspection = snapshot.SkyInspection };
        }
        _ = m_skyAdmissionRegion!.Write(bytes: MemoryMarshal.AsBytes(m_skyAdmissionWords.AsSpan(0, admissionCount)), offset: 0);
    }

    // Structural growth follows the ordinary table transaction: all new regions are created before any old one
    // retires, and each takes its already reserved copy sets. Gates and animation never change these capacities.
    private void EnsureSkyCapacity(SdfSkySnapshot sky, int admissionCount) {
        Span<int> required = stackalloc int[SdfSkyKindBindings.All.Count + 3];
        required[0] = sky.Common!.Bytes.Length;
        required[1] = checked(admissionCount * sizeof(uint));
        required[2] = Math.Max(m_lighting.StopBytes.Length, sky.Stops?.Bytes.Length ?? 0);
        var grows = (m_skyLayersRegion?.ByteCount ?? 0) < required[0]
            || (m_skyAdmissionRegion?.ByteCount ?? 0) < required[1] || m_skyStopsRegion.ByteCount < required[2];
        for (var kind = 0; kind < m_skyKindIndices.Length; kind++) {
            required[kind + 3] = m_skyKindIndices[kind] >= 0 ? sky.Table(m_skyKindIndices[kind]).Bytes.Length : 0;
            grows |= (m_skyKindRegions[kind]?.ByteCount ?? 0) < required[kind + 3];
        }
        if (!grows) { return; }
        m_deviceContext.TryWaitIdle();
        using var scope = new GpuCreationScope();
        var replacements = new GpuRegion?[required.Length];
        for (var index = 0; index < required.Length; index++) {
            var old = SkyRegion(index);
            if ((old?.ByteCount ?? 0) >= required[index]) { continue; }
            var stride = index switch { 0 => Marshal.SizeOf<SdfSkyLayerData>(), 1 => sizeof(uint), 2 => Marshal.SizeOf<SdfSkyStopData>(),
                _ => checked((int)SdfSkyKindBindings.All[index - 3].Member.Structure!.SizeBytes) };
            var count = GrowCapacity((old?.ByteCount ?? 0) / stride, required[index] / stride, int.MaxValue / stride);
            replacements[index] = scope.Own(CreateRegion(SkyRegionIndex(index), checked(count * stride)));
        }
        scope.Complete();
        for (var index = 0; index < replacements.Length; index++) {
            if (replacements[index] is not { } replacement) { continue; }
            SkyRegion(index)?.Dispose();
            switch (index) {
                case 0: m_skyLayersRegion = replacement; break;
                case 1: m_skyAdmissionRegion = replacement; break;
                case 2: m_skyStopsRegion = replacement; break;
                default: m_skyKindRegions[index - 3] = replacement; break;
            }
        }
        m_bindingRevision++;
    }

    private GpuRegion? SkyRegion(int index) => index switch {
        0 => m_skyLayersRegion, 1 => m_skyAdmissionRegion, 2 => m_skyStopsRegion,
        _ => m_skyKindRegions[index - 3],
    };
    private static int SkyRegionIndex(int index) => index switch {
        0 => SkyLayersRegionIndex, 1 => SkyAdmissionRegionIndex, 2 => SkyStopsRegionIndex,
        _ => SkyKindsRegionIndex + index - 3,
    };
}
