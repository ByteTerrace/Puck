namespace Puck.Shaders;

public sealed partial class ShaderPipelinePlan {
    private Facts? m_facts;

    // The questions a reconfiguration asks of a plan again and again (which storages a pass writes or transfer-reads,
    // which are external, which package writes each, whether a package pass may stand for its input, which external
    // versions the default output may stand for), answered once for the plan: a plan never changes after planning, so
    // installing it, binding a set and budgeting a replacement read these instead of scanning its passes each time.
    internal Facts Derived => (Volatile.Read(location: ref m_facts) ?? Interlocked.CompareExchange(location1: ref m_facts, value: new Facts(plan: this), comparand: null) ?? m_facts!);

    internal sealed class Facts {
        private readonly ShaderPipelinePlan m_plan;
        private readonly bool[] m_written;
        private readonly bool[] m_transferRead;
        private readonly (string Package, string? Part)[][] m_writers;
        private string?[]? m_aliasRefusals;
        private IReadOnlySet<string>? m_standable;

        public Facts(ShaderPipelinePlan plan) {
            m_plan = plan;
            var storages = plan.Storages.Count;

            m_written = new bool[storages];
            m_transferRead = new bool[storages];
            var writers = new List<(string, string?)>?[storages];

            foreach (var pass in plan.Passes) {
                foreach (var access in pass.Accesses) {
                    if (access.Use.Writes) { m_written[access.Storage] = true; }
                    if ((access.Use.Access & Puck.Abstractions.Gpu.GpuAccess.TransferRead) != 0) { m_transferRead[access.Storage] = true; }
                }
                if (pass.Package is not { } package) { continue; }
                foreach (var output in pass.Outputs) {
                    if (plan.FindResource(name: output.Name) is not { } resource) { continue; }
                    (writers[resource.Storage] ??= []).Add((package.Package, package.Part));
                }
            }
            m_writers = new (string, string?)[storages][];
            for (var index = 0; (index < storages); index++) { m_writers[index] = writers[index]?.ToArray() ?? []; }

            var external = new Dictionary<string, ShaderPipelinePlannedStorage>(comparer: StringComparer.Ordinal);

            foreach (var storage in plan.Storages) {
                if (storage.IsExternal && !storage.Declaration.IsHostBuffer) { external.TryAdd(key: storage.Name, value: storage); }
                if (storage.Declaration.IsHostBuffer) { HostBuffer ??= storage.Name; }
            }
            External = external;
        }

        // Whether any pass writes this frame's or a previous frame's instance of a storage.
        public bool Writes(int storage) => m_written[storage];
        // Whether any pass reads a storage as a transfer source.
        public bool TransferReads(int storage) => m_transferRead[storage];
        // The package passes (package and fragment part) whose outputs are versions of a storage, in execution order.
        public ReadOnlySpan<(string Package, string? Part)> Writers(int storage) => m_writers[storage];
        // The external storages a consumer binds, by name; host buffer ports are the host's to bind and are absent.
        public Dictionary<string, ShaderPipelinePlannedStorage> External { get; }
        // The first host buffer port's name, or null when the plan has none.
        public string? HostBuffer { get; }

        // Why a package pass that draws nothing cannot stand for its input (ShaderPipelineRenderNode.AliasRefusalOf), or
        // null when it can; every package pass's answer is composed at the first question.
        public string? AliasRefusal(ShaderPipelinePlannedPass planned) {
            var refusals = Volatile.Read(location: ref m_aliasRefusals);

            if (refusals is null) {
                var composed = new string?[m_plan.Passes.Count];

                foreach (var pass in m_plan.Passes) {
                    if (pass.Package is not null) { composed[pass.Index] = ShaderPipelineRenderNode.AliasRefusalOf(plan: m_plan, planned: pass); }
                }
                refusals = (Interlocked.CompareExchange(location1: ref m_aliasRefusals, value: composed, comparand: null) ?? composed);
            }
            return refusals[planned.Index];
        }
        // The external versions the default output may stand for (RenderGraphRuntime.StandableVersions).
        public IReadOnlySet<string> Standable(Func<ShaderPipelinePlan, IReadOnlySet<string>> compose) {
            var standable = Volatile.Read(location: ref m_standable);

            if (standable is null) {
                var composed = compose(arg: m_plan);

                standable = (Interlocked.CompareExchange(location1: ref m_standable, value: composed, comparand: null) ?? composed);
            }
            return standable;
        }
    }
}
