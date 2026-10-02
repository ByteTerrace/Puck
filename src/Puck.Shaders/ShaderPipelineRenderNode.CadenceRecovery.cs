using System.Runtime.CompilerServices;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // These checkpoint the existing tracker for retained and history storage; they do not plan a second set of states.
    private readonly record struct CadenceResourceCheckpoint(RuntimeResource Resource, int Instance,
        bool Initialized, bool HasOverride, bool OverridePlanned, ShaderPipelineAccessState Override, PackageAlias Alias);
    private readonly record struct CadenceVersionCheckpoint(CadenceVersion Version, long Generation, bool Valid);

    private CadenceResourceCheckpoint[] m_cadenceResources = [];
    private CadenceVersionCheckpoint[] m_cadenceVersions = [];
    private bool m_cadenceRecording;
    private bool m_cadenceInitialization;
    private int m_cadenceSlot;

    /// <summary>Gets installed cadence state and array payload bytes, including failure-recovery checkpoints.
    /// Struct arrays use their runtime element size. Excludes managed headers and borrowed resource/name objects;
    /// includes the per-history write cursor and is zero for a graph without retained or history storage.</summary>
    public ulong CadenceCpuBytes {
        get {
            var bytes = checked(((((ulong)m_cadenceResources.Length) * ((ulong)Unsafe.SizeOf<CadenceResourceCheckpoint>())) +
                (((ulong)m_cadenceVersions.Length) * ((ulong)Unsafe.SizeOf<CadenceVersionCheckpoint>()))));

            foreach (var resource in m_resources) {
                if (resource.History) { bytes += sizeof(int) + sizeof(bool); }
                bytes += checked((((ulong)resource.Cadence.Length) * ((ulong)(((IntPtr.Size * 2) + sizeof(long)) + sizeof(bool)))));
            }
            foreach (var pass in m_passes) {
                if (pass.Cadence is not { } cadence) { continue; }
                bytes += checked(((((ulong)((((IntPtr.Size * 3) + sizeof(bool)) + Unsafe.SizeOf<ulong?>()) + (sizeof(uint) * 2))) +
                    (((ulong)cadence.Inputs.Length) * ((ulong)(IntPtr.Size + sizeof(long))))) +
                    (((ulong)cadence.Writes.Length) * ((ulong)Unsafe.SizeOf<CadenceWrite>()))));
            }
            return bytes;
        }
    }

    private void ConfigureCadenceRecovery() {
        m_cadenceRecording = false;
        if (!m_resources.Any(predicate: static resource => (resource.Cadence.Length > 0))) {
            m_cadenceResources = []; m_cadenceVersions = []; return;
        }
        m_cadenceResources = [.. m_resources.SelectMany(selector: static resource => Enumerable.Range(count: resource.Count, start: 0)
            .Select(selector: instance => new CadenceResourceCheckpoint(Alias: default, HasOverride: false, Initialized: false, Instance: instance, Override: default, OverridePlanned: false, Resource: resource)))];
        m_cadenceVersions = [.. m_resources.SelectMany(selector: static resource => resource.Cadence)
            .Select(selector: static version => new CadenceVersionCheckpoint(Generation: 0, Valid: false, Version: version))];
    }
    private void BeginCadenceFrame(int slot) {
        if (m_cadenceResources.Length == 0) { return; }
        m_cadenceRecording = true;
        m_cadenceSlot = slot;
        m_cadenceInitialization = m_initializationPending;
        for (var index = 0; (index < m_cadenceResources.Length); index++) {
            var checkpoint = m_cadenceResources[index];
            var resource = checkpoint.Resource;
            var instance = checkpoint.Instance;

            m_cadenceResources[index] = checkpoint with {
                Initialized = resource.Initialized[instance],
                HasOverride = resource.HasOverride[instance],
                OverridePlanned = resource.OverridePlanned[instance],
                Override = resource.Override[instance],
                Alias = resource.Alias,
            };
        }
        for (var index = 0; (index < m_cadenceVersions.Length); index++) {
            var checkpoint = m_cadenceVersions[index];

            m_cadenceVersions[index] = checkpoint with { Generation = checkpoint.Version.Generation, Valid = checkpoint.Version.Valid };
        }
    }
    private void CommitCadenceFrame() {
        foreach (var resource in m_resources) {
            if (!resource.HistoryWriting) { continue; }
            resource.HistoryLatest = ((resource.HistoryLatest + 1) % resource.Count);
            resource.HistoryWriting = false;
        }
        m_cadenceRecording = false;
    }
    private void AbortCadenceFrame() {
        if (!m_cadenceRecording) { return; }
        m_cadenceRecording = false;
        foreach (var checkpoint in m_cadenceResources) {
            var resource = checkpoint.Resource;
            var instance = checkpoint.Instance;

            resource.Initialized[instance] = checkpoint.Initialized;
            resource.HasOverride[instance] = checkpoint.HasOverride;
            resource.OverridePlanned[instance] = checkpoint.OverridePlanned;
            resource.Override[instance] = checkpoint.Override;
            resource.Alias = checkpoint.Alias;
            resource.HistoryWriting = false;
        }
        foreach (var checkpoint in m_cadenceVersions) {
            checkpoint.Version.Generation = checkpoint.Generation;
            checkpoint.Version.Valid = checkpoint.Valid;
        }
        m_initializationPending = m_cadenceInitialization;
        foreach (var pass in m_passes) {
            if (pass.Cadence is { } cadence) { cadence.Signature = null; }
            Rearm(region: pass.FrameRegion); Rearm(region: pass.PassRegion);
            foreach (var region in (pass.Regions ?? [])) { Rearm(region: region); }
            foreach (var row in (pass.RowRegions ?? [])) { Rearm(region: row.Region); }
        }
        foreach (var region in m_externalRegions.Values) { Rearm(region: region); }
        // A failed copy recording may still hold an unfinished command and barrier list. The next attempt starts fresh.
        m_copyRecording = null;
        m_work.Invalidate();

        void Rearm(GpuRegion? region) {
            if (region?.Policy == GpuResidencyPolicy.Staged) { region.OweAll(slot: m_cadenceSlot); }
        }
    }
    private void ReleaseCadenceRecovery() {
        m_cadenceRecording = false;
        m_cadenceResources = [];
        m_cadenceVersions = [];
    }
}
