using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.Audio.Mixing;

namespace Puck.World.Server;

public sealed partial class WorldMachineHost {
    private Dictionary<string, MachineInstance> m_instances = new(comparer: StringComparer.Ordinal);

    private ulong m_instanceRevision;

    private ulong m_nextInstanceGeneration = 1;

    /// <inheritdoc/>
    public IEnumerable<string> InstanceNames => m_instances.Keys;

    /// <inheritdoc/>
    public WorldMachineInstanceState? InstanceState(string name) {
        if (!m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            return null;
        }
        var lease = instance.Lease;
        var queued = (lease.Runtime as IQueuedMachineRuntime);

        return new(
            name,
            lease.Generation,
            instance.Declaration.Engine,
            instance.Declaration.Running,
            lease.Runtime.Status,
            (queued?.CompletedSteps ?? lease.CompletedSteps),
            (queued?.PendingSteps ?? 0),
            queued?.QueueFault
        );
    }
    /// <inheritdoc/>
    public IMachineVideoOutput? VideoOutput(string instance, string output) =>
        ((m_instances.TryGetValue(
            key: instance,
            value: out var entry
        ) && (entry.Lease.Runtime is IMachineVideoOutputs outputs) &&
        outputs.VideoOutputs.TryGetValue(
            key: output,
            value: out var selected
        ))
            ? selected
            : null
        );
    /// <inheritdoc/>
    public IAudioMachine? AudioOutput(string instance, string output) =>
        ((m_instances.TryGetValue(
            key: instance,
            value: out var entry
        ) && (entry.Lease.Runtime is IMachineAudioOutputs outputs) &&
        outputs.AudioOutputs.TryGetValue(
            key: output,
            value: out var selected
        ))
            ? selected
            : null
        );
    /// <inheritdoc/>
    public IReadOnlyList<WorldMachine> CaptureInstances() => [.. m_instances.Values.Select(selector: static instance => instance.Declaration)];
    /// <summary>Resolves a prepared content symbol for a named machine without coupling the lookup to a display.</summary>
    public bool TryResolveSymbol(string instance, string symbol, out int address) {
        if (m_instances.TryGetValue(
            key: instance,
            value: out var entry
        )) {
            foreach (var asset in entry.Lease.Assets.Values) {
                if (
                    (asset.PreparedContent?.Symbols.TryGetValue(
                    key: symbol,
                    value: out var exported
                ) == true) &&
                    (exported.Space == "bus") &&
                    (exported.Address <= int.MaxValue)
                ) {
                    address = ((int)exported.Address);
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

        if (!m_instances.TryGetValue(
            key: instance,
            value: out var live
        )) {
            refusal = new(
                MachineOperationStatus.Refused,
                reason: $"Machine '{instance}' is not declared."
            );
            return false;
        }
        if (live.Lease.Generation != expectedGeneration) {
            refusal = new(
                MachineOperationStatus.Refused,
                reason: $"Machine '{instance}' generation {expectedGeneration} was replaced."
            );
            return false;
        }
        if (
            !m_engines.TryGet(
            key: live.Declaration.Engine,
            extension: out var engine
        ) ||
            (engine is not IMachineOperationProvider provider)
        ) {
            refusal = new(
                MachineOperationStatus.Unsupported,
                reason: $"Machine '{instance}' provider does not support operations."
            );
            return false;
        }
        if (TryGetLiveLinkForInstance(
            instance: instance,
            linkName: out var linkName
        )) {
            refusal = new(
                MachineOperationStatus.Refused,
                reason: $"Machine '{instance}' participates in live link '{linkName}'; unlink first."
            );
            return false;
        }

        try {
            var current = live.Declaration;
            var requestForProvider = new MachineCreationRequest(
                Configuration: current.Configuration.Clone(),
                Assets: live.Lease.Assets,
                AudioSampleRate: MachineAudioRate.SampleRate
            );
            var prepared = provider.PrepareOperation(
                current: requestForProvider,
                request: request
            );

            switch (prepared) {
                case MachineOperationPreparation.Refusal refused:
                    refusal = refused.Result;
                    return false;
                case MachineOperationPreparation.Replacement replacement: {
                        var requestForReplacement = PrepareCreation(
                            engine: engine,
                            configuration: replacement.Configuration
                        );
                        var runtime = engine.CreateMachine(request: requestForReplacement);

                        try {
                            ValidateRuntimeOutputs(
                                descriptor: engine.Descriptor,
                                runtime: runtime
                            );
                            ValidateInputRoute(
                                current.Name,
                                runtime
                            );
                            if (runtime.Status == MachineRuntimeStatus.Faulted) {
                                refusal = new(
                                    MachineOperationStatus.Faulted,
                                    reason: "The provider returned a faulted runtime."
                                );
                                return false;
                            }

                            var lease = new MachineLease(
                                runtime,
                                m_nextInstanceGeneration,
                                requestForReplacement.Assets
                            );
                            var candidate = current with { Configuration = requestForReplacement.Configuration };
                            var bindings = ResolveBindings(
                                declaration: candidate,
                                lease: lease
                            );

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
                        MachineConfigurationValidation.Validate(
                            descriptor: engine.Descriptor.Configuration,
                            value: configuration
                        );
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
                    refusal = new(
                        MachineOperationStatus.Faulted,
                        reason: "The provider returned an unknown operation preparation."
                    );
                    return false;
            }
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or MachineContentException)) {
            refusal = new(
                MachineOperationStatus.Faulted,
                reason: $"Machine '{instance}' operation preparation failed: {exception.Message}"
            );
            return false;
        }
    }
    /// <inheritdoc/>
    public MachineOperationResult TryCommitOperation(IWorldMachineOperationPreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(plan);
        if (
            (plan is not PreparedMachineOperation prepared) ||
            !ReferenceEquals(
            objA: prepared.Owner,
            objB: this
        ) ||
            prepared.Disposed ||
            prepared.Committed
        ) {
            return new(
                MachineOperationStatus.Refused,
                reason: "The machine operation plan is foreign, stale, disposed, or already committed."
            );
        }
        if (
            (prepared.Revision != m_instanceRevision) ||
            !m_instances.TryGetValue(
            key: prepared.Current.Name,
            value: out var live
        ) ||
            !ReferenceEquals(
            objA: live.Lease,
            objB: prepared.OldLease
        ) ||
            (live.Lease.Generation != prepared.Generation)
        ) {
            return new(
                MachineOperationStatus.Refused,
                reason: $"Machine '{prepared.Current.Name}' changed before its operation committed."
            );
        }

        if (TryGetLiveLinkForInstance(
            instance: prepared.Current.Name,
            linkName: out var linkName
        )) {
            return new(
                MachineOperationStatus.Refused,
                reason: $"Machine '{prepared.Current.Name}' participates in live link '{linkName}'; unlink first."
            );
        }
        var result = new MachineOperationResult(MachineOperationStatus.Applied);

        if (prepared.Operation is { } operation) {
            try {
                result = operation.Apply(runtime: live.Lease.Runtime);
            } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
                return new(
                    MachineOperationStatus.Faulted,
                    reason: exception.Message
                );
            }
            if (result.Status != MachineOperationStatus.Applied) {
                return result;
            }
        }

        if (prepared.ReplacementLease is { } replacement) {
            m_instances[prepared.Current.Name] = new MachineInstance(
                prepared.Candidate,
                replacement,
                prepared.ReplacementBindings
            );
            m_nextInstanceGeneration = Math.Max(
                val1: m_nextInstanceGeneration,
                val2: (replacement.Generation + 1)
            );
        } else {
            m_instances[prepared.Current.Name] = live with { Declaration = prepared.Candidate };
        }
        m_instanceRevision++;
        prepared.Committed = true;
        return result;
    }
    /// <inheritdoc/>
    public MachineAccessResult Inspect(string instance, MachineMemoryAddress address) {
        if (!m_instances.TryGetValue(
            key: instance,
            value: out var entry
        )) {
            return new(
                MachineAccessStatus.Unavailable,
                Reason: $"Machine '{instance}' is not declared."
            );
        }
        return ((entry.Lease.Runtime is IMachineHardwareAccess hardware)
            ? hardware.Read(
                address: address,
                mode: MachineAccessMode.Inspect
            )
            : new(
                MachineAccessStatus.Unsupported,
                Reason: $"Machine '{instance}' has no hardware inspection capability."
            )
        );
    }
    /// <inheritdoc/>
    public bool TryBindingAddress(string instance, string binding, out MachineMemoryAddress address) {
        address = default;
        return (
            m_instances.TryGetValue(
            key: instance,
            value: out var entry
        ) &&
            entry.Bindings.TryGetValue(
            key: binding,
            value: out address
        )
        );
    }
    /// <inheritdoc/>
    public MachineAccessResult WriteHardware(string instance, ulong generation, MachineMemoryAddress address,
        ulong value, MachineAccessMode mode) {
        if (!m_instances.TryGetValue(
            key: instance,
            value: out var entry
        )) {
            return new(
                MachineAccessStatus.Unavailable,
                Reason: $"Machine '{instance}' is not declared."
            );
        }
        if (entry.Lease.Generation != generation) {
            return new(
                MachineAccessStatus.Refused,
                Reason: $"Machine '{instance}' generation {generation} was replaced."
            );
        }
        return ((entry.Lease.Runtime is IMachineHardwareAccess hardware)
            ? hardware.Write(
                address: address,
                mode: mode,
                value: value
            )
            : new(
                MachineAccessStatus.Unsupported,
                Reason: $"Machine '{instance}' has no hardware write capability."
            )
        );
    }

    private void AdvanceInstances(ulong stepTicks, ReadOnlySpan<ScreenPadSnapshot> pads, IReadOnlySet<string> linkedInstances) {
        foreach (var instance in m_instances.Values) {
            if (
                !instance.Declaration.Running ||
                linkedInstances.Contains(item: instance.Declaration.Name)
            ) {
                continue;
            }
            var lease = instance.Lease;
            var runtime = lease.Runtime;

            if (runtime is IMachineInputPorts ports) {
                var input = MachineInput(
                    instance: instance.Declaration.Name,
                    pads: pads,
                    routed: out var routed
                );

                if (
                    (ports.InputPorts.Count > 1) &&
                    routed
                ) {
                    throw new InvalidOperationException(message: $"Machine '{instance.Declaration.Name}' has an ambiguous engaged input route.");
                }
                foreach (var port in ports.InputPorts.Values) {
                    port.SetState(state: in input);
                }
            }
            if (runtime is IQueuedMachineRuntime queued) {
                var submitted = queued.Submit(deltaTicks: stepTicks);

                if (
                    (submitted == QueuedMachineSubmission.Rejected) &&
                    (runtime.Status is MachineRuntimeStatus.Running or MachineRuntimeStatus.Faulted)
                ) {
                    throw new InvalidOperationException(message: $"Machine '{instance.Declaration.Name}' rejected an authoritative segment: {queued.QueueFault}");
                }
                if (submitted != QueuedMachineSubmission.Rejected) {
                    AnyEverPumped = true;
                }
            } else if (runtime.Advance(deltaTicks: stepTicks)) {
                lease.CompletedSteps++;
                AnyEverPumped = true;
            }
        }
    }
    private MachinePadState MachineInput(string instance, ReadOnlySpan<ScreenPadSnapshot> pads, out bool routed) {
        var input = MachinePadState.Neutral;

        routed = false;
        foreach (var slot in m_slots.Values) {
            if (
                (slot.DeclaredSource is WorldScreenSource.Machine source) &&
                string.Equals(
                a: source.Instance,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                foreach (ref readonly var snapshot in pads) {
                    if (snapshot.ScreenIndex != slot.Index) {
                        continue;
                    }
                    var snapshotPad = snapshot.Pad;

                    input = (routed
                        ? MachinePadState.Merge(
                            first: in input,
                            second: in snapshotPad
                        )
                        : snapshotPad
                    );
                    routed = true;
                    break;
                }
            }
        }
        return input;
    }
    private void ValidateInputRoute(string instance, IMachineRuntime runtime, IReadOnlyList<WorldScreen>? candidateScreens = null) {
        if (
            (runtime is not IMachineInputPorts ports) ||
            (ports.InputPorts.Count <= 1)
        ) {
            return;
        }
        var displayed = ((candidateScreens is not null)
            ? candidateScreens.Any(predicate: screen => ((screen.Source is WorldScreenSource.Machine source) &&
                string.Equals(
                a: source.Instance,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )))
            : m_slots.Values.Any(predicate: slot => ((slot.DeclaredSource is WorldScreenSource.Machine source) &&
                string.Equals(
                a: source.Instance,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )))
        );

        if (displayed) {
            throw new ArgumentException(message: $"Machine '{instance}' has multiple input ports and cannot infer an engaged display route.");
        }
    }
    private bool TryPrepareInstances(WorldDefinition? current, WorldDefinition candidate,
        out PreparedInstances? prepared, out string? reason) {
        var plan = new PreparedInstances(
            nextGeneration: m_nextInstanceGeneration,
            owner: this,
            revision: m_instanceRevision
        );
        var preparing = string.Empty;

        try {
            foreach (var declaration in candidate.Machines) {
                preparing = declaration.Name;
                if (
                    m_instances.TryGetValue(
                    key: declaration.Name,
                    value: out var linkedLive
                ) &&
                    TryGetLiveLinkForInstance(
                    instance: declaration.Name,
                    linkName: out var linkName
                ) &&
                    !MachineDeclarationsMatch(
                    first: linkedLive.Declaration,
                    second: declaration
                )
                ) {
                    throw new InvalidOperationException(message: $"Machine '{declaration.Name}' participates in live link '{linkName}'; unlink first.");
                }
                if (
                    (current is not null) &&
                    !m_documentDirectoryChanged &&
                    m_instances.TryGetValue(
                    key: declaration.Name,
                    value: out var live
                ) &&
                    (live.Declaration.Engine == declaration.Engine) &&
                    JsonElement.DeepEquals(
                    element1: live.Declaration.Configuration,
                    element2: declaration.Configuration
                )
                ) {
                    ValidateInputRoute(
                        declaration.Name,
                        live.Lease.Runtime,
                        candidate.Screens
                    );
                    plan.Candidate.Add(
                        key: declaration.Name,
                        value: new(
                            declaration,
                            live.Lease,
                            ResolveBindings(
                                declaration: declaration,
                                lease: live.Lease
                            )
                        )
                    );
                    continue;
                }
                var engine = Catalog.Engines[declaration.Engine];
                var request = PrepareCreation(
                    engine,
                    declaration.Configuration
                );
                var runtime = engine.CreateMachine(request: request);
                var lease = new MachineLease(
                    runtime,
                    plan.NextGeneration++,
                    request.Assets
                );

                plan.Created.Add(item: lease);
                ValidateRuntimeOutputs(
                    descriptor: engine.Descriptor,
                    runtime: runtime
                );
                ValidateInputRoute(
                    declaration.Name,
                    runtime,
                    candidate.Screens
                );
                if (runtime.Status == MachineRuntimeStatus.Faulted) {
                    throw new InvalidOperationException(message: "The provider returned a faulted runtime.");
                }
                plan.Candidate.Add(
                    key: declaration.Name,
                    value: new(
                        declaration,
                        lease,
                        ResolveBindings(
                            declaration: declaration,
                            lease: lease
                        )
                    )
                );
            }
            foreach (var live in m_instances.Values) {
                if (
                    !plan.Candidate.TryGetValue(
                    key: live.Declaration.Name,
                    value: out var next
                ) ||
                    !ReferenceEquals(
                    objA: live.Lease,
                    objB: next.Lease
                )
                ) {
                    if (TryGetLiveLinkForInstance(
                        instance: live.Declaration.Name,
                        linkName: out var linkName
                    )) {
                        throw new InvalidOperationException(message: $"Machine '{live.Declaration.Name}' participates in live link '{linkName}'; unlink first.");
                    }
                    plan.Retired.Add(item: live.Lease);
                }
            }
            prepared = plan;
            reason = null;
            return true;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or MachineContentException)) {
            plan.Dispose();
            prepared = null;
            reason = $"Machine '{preparing}' preparation refused: {exception.Message}";
            return false;
        }
    }
    private static bool MachineDeclarationsMatch(WorldMachine first, WorldMachine second) =>
        (string.Equals(
            a: first.Engine,
            b: second.Engine,
            comparisonType: StringComparison.Ordinal
        ) &&
        (first.Running == second.Running) &&
        JsonElement.DeepEquals(
            element1: first.Configuration,
            element2: second.Configuration
        ));
    private MachineCreationRequest PrepareCreation(IMachineEngine engine, JsonElement configuration) {
        MachineConfigurationValidation.Validate(
            descriptor: engine.Descriptor.Configuration,
            value: configuration
        );
        var tree = JsonNode.Parse(configuration.GetRawText())!.AsObject();
        var assets = new Dictionary<string, PreparedMachineAsset>(comparer: StringComparer.Ordinal);

        MachineConfigurationFields.Visit(
            configuration: tree,
            descriptor: engine.Descriptor.Configuration,
            visitor: site => {
            if (site.Field.Role is not (MachineFieldRole.ContentPath or MachineFieldRole.AssetPath)) {
                return;
            }
            var path = site.Value!.GetValue<string>();

            if (!TryReadContent(
                path,
                documentRelative: true,
                out var source,
                out var fault
            )) {
                throw new ArgumentException(message: $"configuration.{site.Path}: {fault}");
            }
            var image = source;
            PreparedMachineContent? content = null;

            if (
                (site.Field.Role == MachineFieldRole.ContentPath) &&
                !TryResolveContent(
                bytes: out image,
                cartridge: out _,
                compilation: out content,
                content: source,
                contentPath: path,
                engine: engine,
                fault: out fault
            )
            ) {
                throw new ArgumentException(message: $"configuration.{site.Path}: {fault}");
            }
            var admission = m_contentAdmissionPolicy.Evaluate(request: new MachineContentAdmissionRequest(
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
            assets.Add(
                key: site.Path,
                value: new(
                    path,
                    image,
                    WorldDefinitionFileSource.ComputeContentHash(content: source),
                    source,
                    content
                )
            );
        }
        );
        return new(
            configuration.Clone(),
            assets.ToFrozenDictionary(comparer: StringComparer.Ordinal),
            MachineAudioRate.SampleRate
        );
    }
    private static void ValidateRuntimeOutputs(MachineEngineDescriptor descriptor, IMachineRuntime runtime) {
        ValidatePorts(
            descriptor.VideoOutputs,
            (runtime as IMachineVideoOutputs)?.VideoOutputs.Keys,
            "video"
        );
        ValidatePorts(
            descriptor.AudioOutputs,
            (runtime as IMachineAudioOutputs)?.AudioOutputs.Keys,
            "audio"
        );
        ValidatePorts(
            descriptor.InputPorts,
            (runtime as IMachineInputPorts)?.InputPorts.Keys,
            "input"
        );

        static void ValidatePorts(IReadOnlyList<MachinePortDescriptor> declared, IEnumerable<string>? actual, string kind) {
            var names = new HashSet<string>(
                collection: (actual ?? []),
                comparer: StringComparer.Ordinal
            );

            if (
                (names.Count != declared.Count) ||
                declared.Any(predicate: port => !names.Contains(item: port.Name))
            ) {
                throw new ArgumentException(message: $"Runtime {kind} ports do not match the provider descriptor.");
            }
        }
    }
    private void CommitInstances(PreparedInstances plan) {
        if (
            !ReferenceEquals(
            objA: plan.Owner,
            objB: this
        ) ||
            (plan.Revision != m_instanceRevision) ||
            plan.Disposed ||
            plan.Committed
        ) {
            throw new InvalidOperationException(message: "The machine plan is foreign, stale, disposed, or already committed.");
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
        public ulong Generation { get; set; } = generation;
        public IReadOnlyDictionary<string, PreparedMachineAsset> Assets { get; } = assets;

        public long CompletedSteps { get; set; }
    }
    internal sealed class PreparedInstances(WorldMachineHost owner, ulong revision, ulong nextGeneration) : IDisposable {
        public WorldMachineHost Owner { get; } = owner;
        public ulong Revision { get; } = revision;
        public ulong NextGeneration { get; set; } = nextGeneration;
        public Dictionary<string, MachineInstance> Candidate { get; } = new(comparer: StringComparer.Ordinal);
        public List<MachineLease> Created { get; } = [];
        public List<MachineLease> Retired { get; } = [];

        public bool Committed { get; set; }
        public bool Disposed { get; private set; }

        public void Dispose() {
            if (Disposed) {
                return;
            }
            Disposed = true;
            foreach (var lease in (Committed
                ? Retired
                : Created)) {
                lease.Runtime.Dispose();
            }
        }
    }
    internal sealed class PreparedMachineOperation : IWorldMachineOperationPreparedPlan {
        internal bool Committed { get; set; }
        internal bool Disposed { get; private set; }
        internal MachineLease OldLease { get; }
        internal IMachinePreparedOperation? Operation { get; }
        internal WorldMachineHost Owner { get; }
        internal IReadOnlyDictionary<string, MachineMemoryAddress> ReplacementBindings { get; }
        internal MachineLease? ReplacementLease { get; }
        internal ulong Revision { get; }

        public WorldMachine Candidate { get; }
        public WorldMachine Current { get; }
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
            if (
                Committed &&
                (ReplacementLease is not null)
            ) {
                OldLease.Runtime.Dispose();
            } else {
                ReplacementLease?.Runtime.Dispose();
            }
        }
    }
}
