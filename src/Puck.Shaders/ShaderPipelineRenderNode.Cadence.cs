using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// Content identities are separate from barrier state. SkipAccesses remains the only standing-pass barrier path.
// Retained intermediates and package-borrowed buffers share one queue-ordered allocation, so their identities describe the latest queued write,
// independently of the rotating submission slot. Every array below is allocated when the graph installs.
public sealed partial class ShaderPipelineRenderNode {
    private long m_contentGeneration;

    private readonly record struct ExternalBuffer(IGpuBuffer Buffer, GpuImagePublication Publication = default, long Generation = 0);
    private sealed class CadenceVersion(string name) {
        public readonly string Name = name;

        public long Generation;
        public bool Valid;
    }
    private readonly record struct CadenceWrite(RuntimeResource Resource, int Version, bool PreservesPredecessor);
    private sealed class PassCadence(CadenceVersion[] inputs, CadenceWrite[] writes, bool canStand) {
        public readonly CadenceVersion[] Inputs = inputs;
        public readonly CadenceWrite[] Writes = writes;
        public readonly long[] InputGenerations = new long[inputs.Length];
        public readonly bool CanStand = canStand;

        public ulong? Signature;
        public uint Width;
        public uint Height;
    }

    private void ConfigureCadence() {
        foreach (var pass in m_passes) {
            var inputs = new List<CadenceVersion>();
            var writes = new List<CadenceWrite>();
            var canStand = ((pass.Package is not null) && (pass.Outputs.Length > 0));

            foreach (var access in pass.Accesses) {
                if (!access.Use.Writes || m_resources[access.Storage].Spec.IsExternal) {
                    canStand &= (!access.Use.Writes && AddCadenceInput(access.Version, access.PreviousFrame, inputs));
                }
                foreach (var version in access.OtherVersions) {
                    canStand &= AddCadenceInput(version, access.PreviousFrame, inputs);
                }
            }
            foreach (var output in pass.Outputs) {
                var resource = m_resourceLookup[output.Name];
                // A new history instance contains no predecessor; every forwarding writer must execute.
                canStand &= (!resource.History || (resource.Storage.Versions.Count == 1));
                var version = Array.FindIndex(array: resource.Cadence, match: item => (item.Name == output.Name));

                if (version < 0) { canStand = false; continue; }
                var declaration = m_pipeline!.Plan.FindResource(name: output.Name)!.Declaration;

                if (declaration.From is { } predecessor) { canStand &= AddCadenceInput(inputs: inputs, name: predecessor, previous: false); }
                writes.Add(item: new CadenceWrite(resource, version, declaration.PreservesPredecessor));
            }
            pass.Cadence = ((writes.Count == 0) ? null : new PassCadence(inputs.ToArray(), writes.ToArray(), canStand));
        }
        ConfigureCadenceRecovery();
    }
    private bool AddCadenceInput(string name, bool previous, List<CadenceVersion> inputs) {
        var resource = m_resourceLookup[name];
        // Previous history feeds a requested write but creates no demand for another one. The package signature
        // states whether a sample is owed. A current read follows the last successful write's content identity.
        if (previous && resource.History) { return true; }
        if (resource.Spec.IsExternal) {
            if (resource.Spec.IsHostBuffer || (resource.Spec.Kind != ShaderPipelineResourceKind.Buffer)) { return false; }
            // A mutable import's producer publication cannot describe writes made by this graph.
            if (m_passes.Any(predicate: pass => pass.Accesses.Any(predicate: access =>
                ((access.Storage == resource.Storage.Index) && access.Use.Writes)))) { return false; }
        } else if (!resource.Spec.Retained && !resource.History && !resource.Borrowed) { return false; }
        var version = Array.Find(array: resource.Cadence, match: item => (item.Name == name))!;

        if (!inputs.Contains(item: version)) { inputs.Add(item: version); }
        return true;
    }
    private void UpdateExternalBufferCadence() {
        foreach (var resource in m_resources) {
            if (!resource.Spec.IsExternal) { continue; }
            foreach (var version in resource.Cadence) {
                var known = (m_externalBuffers.TryGetValue(key: version.Name, value: out var binding) && binding.Publication.IsKnown);

                version.Valid = known;
                version.Generation = (known ? binding.Generation : 0);
            }
        }
    }
    private static bool Stands(RuntimePass pass, ulong? signature) {
        if ((signature is null) || (pass.Cadence is not { CanStand: true } cadence) || (cadence.Signature != signature) ||
            (cadence.Width != pass.Width) || (cadence.Height != pass.Height)) { return false; }
        for (var index = 0; (index < cadence.Inputs.Length); index++) {
            var input = cadence.Inputs[index];

            if (!input.Valid || (input.Generation != cadence.InputGenerations[index])) { return false; }
        }
        foreach (var output in cadence.Writes) {
            if (!output.Resource.Cadence[output.Version].Valid || (output.Resource.Alias.Target is not null)) { return false; }
        }
        return true;
    }
    private void RecordedCadence(RuntimePass pass, ulong? signature) {
        if (pass.Cadence is not { } cadence) { return; }
        cadence.Signature = signature;
        cadence.Width = pass.Width;
        cadence.Height = pass.Height;
        for (var index = 0; (index < cadence.Inputs.Length); index++) {
            cadence.InputGenerations[index] = cadence.Inputs[index].Generation;
        }
        foreach (var output in cadence.Writes) {
            var versions = output.Resource.Cadence;

            for (var index = 0; (index < versions.Length); index++) {
                // An ordinary successor may overwrite any predecessor word. A declared preserving successor changes
                // only its own fields; either kind of write invalidates all later derived versions.
                if ((index > output.Version) || (!output.PreservesPredecessor && (index < output.Version))) { versions[index].Valid = false; }
            }
            var written = versions[output.Version];

            written.Generation = ++m_contentGeneration;
            written.Valid = (output.Resource.Alias.Target is null);
        }
    }
    private void SkippedCadence(RuntimePass pass) {
        if (pass.Cadence is not { } cadence) { return; }
        cadence.Signature = null;
        foreach (var output in cadence.Writes) {
            var version = output.Resource.Cadence[output.Version];

            if (output.PreservesPredecessor && (output.Version > 0)) {
                var predecessor = output.Resource.Cadence[(output.Version - 1)];

                version.Generation = predecessor.Generation;
                version.Valid = predecessor.Valid;
            } else if (!version.Valid) {
                version.Generation = ++m_contentGeneration;
                version.Valid = true;
            }
        }
    }
    private void InvalidateCadence() {
        foreach (var resource in m_resources) {
            foreach (var version in resource.Cadence) { version.Valid = false; }
        }
        foreach (var pass in m_passes) {
            if (pass.Cadence is { } cadence) { cadence.Signature = null; }
        }
    }
}
