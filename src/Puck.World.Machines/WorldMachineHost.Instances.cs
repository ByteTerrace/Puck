using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.Audio.Mixing;

namespace Puck.World.Server;

public sealed partial class WorldMachineHost {
    private Dictionary<string, MachineInstance> m_instances = new(StringComparer.Ordinal);
    private ulong m_instanceRevision;
    private ulong m_nextInstanceGeneration = 1;

    /// <inheritdoc/>
    public IEnumerable<string> InstanceNames => m_instances.Keys;

    /// <inheritdoc/>
    public WorldMachineInstanceState? InstanceState(string name) {
        if (!m_instances.TryGetValue(name, out var instance)) {
            return null;
        }
        var lease = instance.Lease;
        var queued = lease.Runtime as IQueuedMachineRuntime;
        return new(name, lease.Generation, instance.Declaration.Engine, instance.Declaration.Running,
            lease.Runtime.Status, queued?.CompletedSteps ?? lease.CompletedSteps, queued?.PendingSteps ?? 0,
            queued?.QueueFault);
    }

    /// <inheritdoc/>
    public IMachineVideoOutput? VideoOutput(string instance, string output) =>
        m_instances.TryGetValue(instance, out var entry) && entry.Lease.Runtime is IMachineVideoOutputs outputs &&
        outputs.VideoOutputs.TryGetValue(output, out var selected) ? selected : null;

    /// <inheritdoc/>
    public IAudioMachine? AudioOutput(string instance, string output) =>
        m_instances.TryGetValue(instance, out var entry) && entry.Lease.Runtime is IMachineAudioOutputs outputs &&
        outputs.AudioOutputs.TryGetValue(output, out var selected) ? selected : null;

    /// <inheritdoc/>
    public MachineAccessResult Inspect(string instance, MachineMemoryAddress address) {
        if (!m_instances.TryGetValue(instance, out var entry)) {
            return new(MachineAccessStatus.Unavailable, Reason: $"Machine '{instance}' is not declared.");
        }
        return entry.Lease.Runtime is IMachineHardwareAccess hardware
            ? hardware.Read(address, MachineAccessMode.Inspect)
            : new(MachineAccessStatus.Unsupported, Reason: $"Machine '{instance}' has no hardware inspection capability.");
    }

    /// <inheritdoc/>
    public bool TryBindingAddress(string instance, string binding, out MachineMemoryAddress address) {
        address = default;
        return m_instances.TryGetValue(instance, out var entry) && entry.Bindings.TryGetValue(binding, out address);
    }

    /// <inheritdoc/>
    public MachineAccessResult WriteHardware(string instance, ulong generation, MachineMemoryAddress address,
        ulong value, MachineAccessMode mode) {
        if (!m_instances.TryGetValue(instance, out var entry)) {
            return new(MachineAccessStatus.Unavailable, Reason: $"Machine '{instance}' is not declared.");
        }
        if (entry.Lease.Generation != generation) {
            return new(MachineAccessStatus.Refused, Reason: $"Machine '{instance}' generation {generation} was replaced.");
        }
        return entry.Lease.Runtime is IMachineHardwareAccess hardware
            ? hardware.Write(address, value, mode)
            : new(MachineAccessStatus.Unsupported, Reason: $"Machine '{instance}' has no hardware write capability.");
    }

    private void AdvanceInstances(ulong stepTicks) {
        foreach (var instance in m_instances.Values) {
            if (!instance.Declaration.Running) {
                continue;
            }
            var lease = instance.Lease;
            var runtime = lease.Runtime;
            if (runtime is IQueuedMachineRuntime queued) {
                var submitted = queued.Submit(stepTicks);
                if (submitted == QueuedMachineSubmission.Rejected &&
                    runtime.Status is MachineRuntimeStatus.Running or MachineRuntimeStatus.Faulted) {
                    throw new InvalidOperationException($"Machine '{instance.Declaration.Name}' rejected an authoritative segment: {queued.QueueFault}");
                }
                if (submitted != QueuedMachineSubmission.Rejected) {
                    AnyEverPumped = true;
                }
            } else if (runtime.Advance(stepTicks)) {
                lease.CompletedSteps++;
                AnyEverPumped = true;
            }
        }
    }

    private bool TryPrepareInstances(WorldDefinition? current, WorldDefinition candidate,
        out PreparedInstances? prepared, out string? reason) {
        var plan = new PreparedInstances(this, m_instanceRevision, m_nextInstanceGeneration);
        var preparing = string.Empty;
        try {
            foreach (var declaration in candidate.Machines) {
                preparing = declaration.Name;
                if (current is not null && !m_documentDirectoryChanged &&
                    m_instances.TryGetValue(declaration.Name, out var live) &&
                    live.Declaration.Engine == declaration.Engine &&
                    JsonElement.DeepEquals(live.Declaration.Configuration, declaration.Configuration)) {
                    plan.Candidate.Add(declaration.Name, new(declaration, live.Lease, ResolveBindings(declaration, live.Lease)));
                    continue;
                }
                var engine = Catalog.Engines[declaration.Engine];
                var request = PrepareCreation(engine, declaration.Configuration);
                var runtime = engine.CreateMachine(request);
                var lease = new MachineLease(runtime, plan.NextGeneration++, request.Assets);
                plan.Created.Add(lease);
                ValidateRuntimeOutputs(engine.Descriptor, runtime);
                if (runtime.Status == MachineRuntimeStatus.Faulted) {
                    throw new InvalidOperationException("The provider returned a faulted runtime.");
                }
                plan.Candidate.Add(declaration.Name, new(declaration, lease, ResolveBindings(declaration, lease)));
            }
            foreach (var live in m_instances.Values) {
                if (!plan.Candidate.TryGetValue(live.Declaration.Name, out var next) ||
                    !ReferenceEquals(live.Lease, next.Lease)) {
                    plan.Retired.Add(live.Lease);
                }
            }
            prepared = plan;
            reason = null;
            return true;
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or MachineContentException) {
            plan.Dispose();
            prepared = null;
            reason = $"Machine '{preparing}' preparation refused: {exception.Message}";
            return false;
        }
    }

    private MachineCreationRequest PrepareCreation(IMachineEngine engine, JsonElement configuration) {
        MachineConfigurationValidation.Validate(engine.Descriptor.Configuration, configuration);
        var tree = JsonNode.Parse(configuration.GetRawText())!.AsObject();
        var assets = new Dictionary<string, PreparedMachineAsset>(StringComparer.Ordinal);
        MachineConfigurationFields.Visit(tree, engine.Descriptor.Configuration, site => {
            if (site.Field.Role is not (MachineFieldRole.ContentPath or MachineFieldRole.AssetPath)) {
                return;
            }
            var path = site.Value!.GetValue<string>();
            if (!TryReadContent(path, documentRelative: true, out var source, out var fault)) {
                throw new ArgumentException($"configuration.{site.Path}: {fault}");
            }
            var image = source;
            PreparedMachineContent? content = null;
            if (site.Field.Role == MachineFieldRole.ContentPath &&
                !TryResolveContent(engine, path, source, out image, out _, out content, out fault)) {
                throw new ArgumentException($"configuration.{site.Path}: {fault}");
            }
            assets.Add(site.Path, new(path, image, WorldDefinitionFileSource.ComputeContentHash(source), content));
        });
        return new(configuration.Clone(), assets.ToFrozenDictionary(StringComparer.Ordinal), MachineAudioRate.SampleRate);
    }

    private static void ValidateRuntimeOutputs(MachineEngineDescriptor descriptor, IMachineRuntime runtime) {
        ValidatePorts(descriptor.VideoOutputs, (runtime as IMachineVideoOutputs)?.VideoOutputs.Keys, "video");
        ValidatePorts(descriptor.AudioOutputs, (runtime as IMachineAudioOutputs)?.AudioOutputs.Keys, "audio");
        ValidatePorts(descriptor.InputPorts, (runtime as IMachineInputPorts)?.InputPorts.Keys, "input");

        static void ValidatePorts(IReadOnlyList<MachinePortDescriptor> declared, IEnumerable<string>? actual, string kind) {
            var names = new HashSet<string>(actual ?? [], StringComparer.Ordinal);
            if (names.Count != declared.Count || declared.Any(port => !names.Contains(port.Name))) {
                throw new ArgumentException($"Runtime {kind} ports do not match the provider descriptor.");
            }
        }
    }

    private void CommitInstances(PreparedInstances plan) {
        if (!ReferenceEquals(plan.Owner, this) || plan.Revision != m_instanceRevision || plan.Disposed || plan.Committed) {
            throw new InvalidOperationException("The machine plan is foreign, stale, disposed, or already committed.");
        }
        m_instances = plan.Candidate;
        m_nextInstanceGeneration = plan.NextGeneration;
        m_instanceRevision++;
        plan.Committed = true;
    }

    private void DisposeInstances() {
        foreach (var instance in m_instances.Values) {
            instance.Lease.Runtime.Dispose();
        }
        m_instances.Clear();
    }

    internal sealed record MachineInstance(WorldMachine Declaration, MachineLease Lease,
        IReadOnlyDictionary<string, MachineMemoryAddress> Bindings);

    internal sealed class MachineLease(IMachineRuntime runtime, ulong generation,
        IReadOnlyDictionary<string, PreparedMachineAsset> assets) {
        public IMachineRuntime Runtime { get; } = runtime;
        public ulong Generation { get; } = generation;
        public IReadOnlyDictionary<string, PreparedMachineAsset> Assets { get; } = assets;
        public long CompletedSteps { get; set; }
    }

    internal sealed class PreparedInstances(WorldMachineHost owner, ulong revision, ulong nextGeneration) : IDisposable {
        public WorldMachineHost Owner { get; } = owner;
        public ulong Revision { get; } = revision;
        public ulong NextGeneration { get; set; } = nextGeneration;
        public Dictionary<string, MachineInstance> Candidate { get; } = new(StringComparer.Ordinal);
        public List<MachineLease> Created { get; } = [];
        public List<MachineLease> Retired { get; } = [];
        public bool Committed { get; set; }
        public bool Disposed { get; private set; }
        public void Dispose() {
            if (Disposed) {
                return;
            }
            Disposed = true;
            foreach (var lease in Committed ? Retired : Created) {
                lease.Runtime.Dispose();
            }
        }
    }
}
