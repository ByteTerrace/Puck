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
    public IReadOnlyList<WorldMachine> CaptureInstances() => [.. m_instances.Values.Select(static instance => instance.Declaration)];

    /// <summary>Resolves a prepared content symbol for a named machine without coupling the lookup to a display.</summary>
    public bool TryResolveSymbol(string instance, string symbol, out int address) {
        if (m_instances.TryGetValue(instance, out var entry)) {
            foreach (var asset in entry.Lease.Assets.Values) {
                if (asset.PreparedContent?.Symbols.TryGetValue(symbol, out var exported) == true &&
                    exported.Space == "bus" && exported.Address <= int.MaxValue) {
                    address = (int)exported.Address;
                    return true;
                }
            }
        }
        address = 0;
        return false;
    }

    /// <inheritdoc/>
    public bool TryPrepareOperation(string instance, ulong expectedGeneration, MachineOperationRequest request,
        out IWorldMachineOperationPreparedPlan? plan, out MachineOperationResult refusal) {
        plan = null;

        if (!m_instances.TryGetValue(instance, out var live)) {
            refusal = new(MachineOperationStatus.Refused, reason: $"Machine '{instance}' is not declared.");
            return false;
        }
        if (live.Lease.Generation != expectedGeneration) {
            refusal = new(MachineOperationStatus.Refused, reason: $"Machine '{instance}' generation {expectedGeneration} was replaced.");
            return false;
        }
        if (!m_engines.TryGet(key: live.Declaration.Engine, extension: out var engine) || engine is not IMachineOperationProvider provider) {
            refusal = new(MachineOperationStatus.Unsupported, reason: $"Machine '{instance}' provider does not support operations.");
            return false;
        }
        if (TryGetLiveLinkForInstance(instance, out var linkName)) {
            refusal = new(MachineOperationStatus.Refused,
                reason: $"Machine '{instance}' participates in live link '{linkName}'; unlink first.");
            return false;
        }

        try {
            var current = live.Declaration;
            var requestForProvider = new MachineCreationRequest(
                Configuration: current.Configuration.Clone(),
                Assets: live.Lease.Assets,
                AudioSampleRate: MachineAudioRate.SampleRate
            );
            var prepared = provider.PrepareOperation(current: requestForProvider, request: request);

            switch (prepared) {
                case MachineOperationPreparation.Refusal refused:
                    refusal = refused.Result;
                    return false;
                case MachineOperationPreparation.Replacement replacement: {
                        var requestForReplacement = PrepareCreation(engine: engine, configuration: replacement.Configuration);
                        var runtime = engine.CreateMachine(request: requestForReplacement);
                        try {
                            ValidateRuntimeOutputs(engine.Descriptor, runtime);
                            ValidateInputRoute(current.Name, runtime);
                            if (runtime.Status == MachineRuntimeStatus.Faulted) {
                                refusal = new(MachineOperationStatus.Faulted, reason: "The provider returned a faulted runtime.");
                                return false;
                            }

                            var lease = new MachineLease(runtime, m_nextInstanceGeneration, requestForReplacement.Assets);
                            var candidate = current with { Configuration = requestForReplacement.Configuration };
                            var bindings = ResolveBindings(candidate, lease);
                            plan = new PreparedMachineOperation(
                                owner: this,
                                revision: m_instanceRevision,
                                current: current,
                                candidate: candidate,
                                generation: expectedGeneration,
                                oldLease: live.Lease,
                                replacementLease: lease,
                                replacementBindings: bindings,
                                operation: null
                            );
                            refusal = default;
                            return true;
                        } finally {
                            if (plan is null) {
                                runtime.Dispose();
                            }
                        }
                    }
                case MachineOperationPreparation.Runtime runtime:
                    if (runtime.Configuration is { } configuration) {
                        MachineConfigurationValidation.Validate(engine.Descriptor.Configuration, configuration);
                    }
                    plan = new PreparedMachineOperation(
                        owner: this,
                        revision: m_instanceRevision,
                        current: current,
                        candidate: current with { Configuration = (runtime.Configuration ?? current.Configuration).Clone() },
                        generation: expectedGeneration,
                        oldLease: live.Lease,
                        replacementLease: null,
                        replacementBindings: live.Bindings,
                        operation: runtime.Operation
                    );
                    refusal = default;
                    return true;
                default:
                    refusal = new(MachineOperationStatus.Faulted, reason: "The provider returned an unknown operation preparation.");
                    return false;
            }
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or MachineContentException) {
            refusal = new(MachineOperationStatus.Faulted, reason: $"Machine '{instance}' operation preparation failed: {exception.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public MachineOperationResult TryCommitOperation(IWorldMachineOperationPreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan is not PreparedMachineOperation prepared || !ReferenceEquals(prepared.Owner, this) || prepared.Disposed || prepared.Committed) {
            return new(MachineOperationStatus.Refused, reason: "The machine operation plan is foreign, stale, disposed, or already committed.");
        }
        if (
            prepared.Revision != m_instanceRevision ||
            !m_instances.TryGetValue(prepared.Current.Name, out var live) ||
            !ReferenceEquals(live.Lease, prepared.OldLease) ||
            live.Lease.Generation != prepared.Generation
        ) {
            return new(MachineOperationStatus.Refused, reason: $"Machine '{prepared.Current.Name}' changed before its operation committed.");
        }

        if (TryGetLiveLinkForInstance(prepared.Current.Name, out var linkName)) {
            return new(MachineOperationStatus.Refused,
                reason: $"Machine '{prepared.Current.Name}' participates in live link '{linkName}'; unlink first.");
        }
        var result = new MachineOperationResult(MachineOperationStatus.Applied);
        if (prepared.Operation is { } operation) {
            try {
                result = operation.Apply(live.Lease.Runtime);
            } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
                return new(MachineOperationStatus.Faulted, reason: exception.Message);
            }
            if (result.Status != MachineOperationStatus.Applied) {
                return result;
            }
        }

        if (prepared.ReplacementLease is { } replacement) {
            m_instances[prepared.Current.Name] = new MachineInstance(prepared.Candidate, replacement, prepared.ReplacementBindings);
            m_nextInstanceGeneration = Math.Max(m_nextInstanceGeneration, replacement.Generation + 1);
        } else {
            m_instances[prepared.Current.Name] = live with { Declaration = prepared.Candidate };
        }
        m_instanceRevision++;
        prepared.Committed = true;
        return result;
    }

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

    private void AdvanceInstances(ulong stepTicks, ReadOnlySpan<ScreenPadSnapshot> pads, IReadOnlySet<string> linkedInstances) {
        foreach (var instance in m_instances.Values) {
            if (!instance.Declaration.Running || linkedInstances.Contains(instance.Declaration.Name)) {
                continue;
            }
            var lease = instance.Lease;
            var runtime = lease.Runtime;
            if (runtime is IMachineInputPorts ports) {
                var input = MachineInput(instance.Declaration.Name, pads, out var routed);
                if (ports.InputPorts.Count > 1 && routed) {
                    throw new InvalidOperationException($"Machine '{instance.Declaration.Name}' has an ambiguous engaged input route.");
                }
                foreach (var port in ports.InputPorts.Values) {
                    port.SetState(in input);
                }
            }
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

    private MachinePadState MachineInput(string instance, ReadOnlySpan<ScreenPadSnapshot> pads, out bool routed) {
        var input = MachinePadState.Neutral;
        routed = false;
        foreach (var slot in m_slots.Values) {
            if (slot.DeclaredSource is WorldScreenSource.Machine source &&
                string.Equals(source.Instance, instance, StringComparison.Ordinal)) {
                foreach (ref readonly var snapshot in pads) {
                    if (snapshot.ScreenIndex != slot.Index) {
                        continue;
                    }
                    var snapshotPad = snapshot.Pad;
                    input = routed ? MachinePadState.Merge(in input, in snapshotPad) : snapshotPad;
                    routed = true;
                    break;
                }
            }
        }
        return input;
    }

    private void ValidateInputRoute(string instance, IMachineRuntime runtime, IReadOnlyList<WorldScreen>? candidateScreens = null) {
        if (runtime is not IMachineInputPorts ports || ports.InputPorts.Count <= 1) {
            return;
        }
        var displayed = candidateScreens is not null
            ? candidateScreens.Any(screen => screen.Source is WorldScreenSource.Machine source &&
                string.Equals(source.Instance, instance, StringComparison.Ordinal))
            : m_slots.Values.Any(slot => slot.DeclaredSource is WorldScreenSource.Machine source &&
                string.Equals(source.Instance, instance, StringComparison.Ordinal));
        if (displayed) {
            throw new ArgumentException($"Machine '{instance}' has multiple input ports and cannot infer an engaged display route.");
        }
    }

    private bool TryPrepareInstances(WorldDefinition? current, WorldDefinition candidate,
        out PreparedInstances? prepared, out string? reason) {
        var plan = new PreparedInstances(this, m_instanceRevision, m_nextInstanceGeneration);
        var preparing = string.Empty;
        try {
            foreach (var declaration in candidate.Machines) {
                preparing = declaration.Name;
                if (m_instances.TryGetValue(declaration.Name, out var linkedLive) &&
                    TryGetLiveLinkForInstance(declaration.Name, out var linkName) &&
                    !MachineDeclarationsMatch(linkedLive.Declaration, declaration)) {
                    throw new InvalidOperationException($"Machine '{declaration.Name}' participates in live link '{linkName}'; unlink first.");
                }
                if (current is not null && !m_documentDirectoryChanged &&
                    m_instances.TryGetValue(declaration.Name, out var live) &&
                    live.Declaration.Engine == declaration.Engine &&
                    JsonElement.DeepEquals(live.Declaration.Configuration, declaration.Configuration)) {
                    ValidateInputRoute(declaration.Name, live.Lease.Runtime, candidate.Screens);
                    plan.Candidate.Add(declaration.Name, new(declaration, live.Lease, ResolveBindings(declaration, live.Lease)));
                    continue;
                }
                var engine = Catalog.Engines[declaration.Engine];
                var request = PrepareCreation(engine, declaration.Configuration);
                var runtime = engine.CreateMachine(request);
                var lease = new MachineLease(runtime, plan.NextGeneration++, request.Assets);
                plan.Created.Add(lease);
                ValidateRuntimeOutputs(engine.Descriptor, runtime);
                ValidateInputRoute(declaration.Name, runtime, candidate.Screens);
                if (runtime.Status == MachineRuntimeStatus.Faulted) {
                    throw new InvalidOperationException("The provider returned a faulted runtime.");
                }
                plan.Candidate.Add(declaration.Name, new(declaration, lease, ResolveBindings(declaration, lease)));
            }
            foreach (var live in m_instances.Values) {
                if (!plan.Candidate.TryGetValue(live.Declaration.Name, out var next) ||
                    !ReferenceEquals(live.Lease, next.Lease)) {
                    if (TryGetLiveLinkForInstance(live.Declaration.Name, out var linkName)) {
                        throw new InvalidOperationException($"Machine '{live.Declaration.Name}' participates in live link '{linkName}'; unlink first.");
                    }
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

    private static bool MachineDeclarationsMatch(WorldMachine first, WorldMachine second) =>
        string.Equals(first.Engine, second.Engine, StringComparison.Ordinal) &&
        first.Running == second.Running &&
        JsonElement.DeepEquals(first.Configuration, second.Configuration);
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
            var admission = m_contentAdmissionPolicy.Evaluate(new MachineContentAdmissionRequest(
                EngineId: engine.Id,
                FieldPath: site.Path,
                Role: site.Field.Role,
                VerifiedSourceFormat: content?.VerifiedSourceFormat,
                SourceBytes: source,
                ExecutableBytes: image
            ));
            if (!admission.Allowed) {
                throw new MachineContentException($"configuration.{site.Path}: content admission refused ({admission.Code}): {admission.Detail}");
            }
            assets.Add(site.Path, new(path, image, WorldDefinitionFileSource.ComputeContentHash(source), source, content));
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

    internal sealed class PreparedMachineOperation : IWorldMachineOperationPreparedPlan {
        internal WorldMachineHost Owner { get; }
        internal ulong Revision { get; }
        internal MachineLease OldLease { get; }
        internal MachineLease? ReplacementLease { get; }
        internal IReadOnlyDictionary<string, MachineMemoryAddress> ReplacementBindings { get; }
        internal IMachinePreparedOperation? Operation { get; }
        internal bool Committed { get; set; }
        internal bool Disposed { get; private set; }
        public WorldMachine Current { get; }
        public WorldMachine Candidate { get; }
        public ulong Generation { get; }

        internal PreparedMachineOperation(WorldMachineHost owner, ulong revision, WorldMachine current, WorldMachine candidate,
            ulong generation, MachineLease oldLease, MachineLease? replacementLease,
            IReadOnlyDictionary<string, MachineMemoryAddress> replacementBindings, IMachinePreparedOperation? operation) {
            Owner = owner;
            Revision = revision;
            Current = current;
            Candidate = candidate;
            Generation = generation;
            OldLease = oldLease;
            ReplacementLease = replacementLease;
            ReplacementBindings = replacementBindings;
            Operation = operation;
        }

        public void Dispose() {
            if (Disposed) {
                return;
            }
            Disposed = true;
            if (Committed && ReplacementLease is not null) {
                OldLease.Runtime.Dispose();
            } else {
                ReplacementLease?.Runtime.Dispose();
            }
        }
    }
}
