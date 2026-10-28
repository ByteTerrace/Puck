using Puck.Maths;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Authority-owned input sessions. Identity and routed-body generation, never a device, own input state.</summary>
public sealed partial class WorldInputRuntime {
    private sealed class Owner {
        public required Principal Principal;
        public required WorldInputOwner Input;
        public required WorldDefinition Definition;
        public required int Body;
        public required int Generation;
        public string? KitName;
        public required ushort[] Selection;

        public int[] Facing = [];

        public WorldInputPublication Publication;
        public WorldInputControllerOrigin? Controller;
        public WorldInputProducer? Producer;

        public readonly Dictionary<string, string> SeatStates = new(comparer: StringComparer.Ordinal);
    }

    private readonly WorldServer m_host;
    private readonly Dictionary<Principal, Owner> m_owners = [];
    private readonly List<Owner> m_ordered = [];
    private readonly Dictionary<int, List<Owner>> m_bodyOwners = [];
    private readonly Dictionary<(Principal Actor, int Body, string Source), Owner> m_controllers = [];

    private WorldInputPublication[] m_publications = [];
    private WorldInputInterval[] m_intervals;
    private int m_intervalCount;
    private int m_intervalNext;

    /// <summary>Constructs the authority's bounded clock history; owners allocate only when admitted.</summary>
    public WorldInputRuntime(WorldServer host, WorldDefinition definition) {
        m_host = host;
        var horizon = (definition.InputHistory?.MaximumLatenessSeconds ?? 0);
        var intervals = checked((FixedTickConversion.DurationEngineTicks(seconds: horizon) + 2));

        if (intervals > 1_000_000) { throw new ArgumentException(message: "inputHistory lateness requires more than 1,000,000 retained engine intervals", paramName: nameof(definition)); }
        m_intervals = new WorldInputInterval[((int)intervals)];
    }

    /// <summary>The open processing interval, with its original rate's exact engine coordinates.</summary>
    public WorldInputInterval Interval { get; private set; }
    /// <summary>Number of admitted owners, including local script and simulation observation sessions.</summary>
    public int OwnerCount => m_ordered.Count;

    /// <summary>Reads an owner in deterministic principal order at a settled authority boundary.</summary>
    public Principal OwnerPrincipal(int index) => m_ordered[index].Principal;
    /// <summary>Reads a local producer's authorizing actor and source without changing its input episode.</summary>
    public WorldInputControllerOrigin? ControllerOrigin(Principal principal) => (m_owners.TryGetValue(key: principal, value: out var owner) ? owner.Controller : null);

    internal WorldInputCatalog Catalog => m_host.Population.InputCatalog;

    /// <summary>Publishes epoch decisions with immutable source images; unchanged held ticks allocate nothing.</summary>
    public ReadOnlyMemory<WorldInputPublication> Publish() {
        if (m_publications.Length < m_ordered.Count) { m_publications = new WorldInputPublication[m_ordered.Count]; }
        var count = 0;

        for (var index = 0; (index < m_ordered.Count); index++) {
            var owner = m_ordered[index];
            // Local producers reconcile directly with their authority; physical clients need no copy of those profiles.
            if (owner.Controller is not null) { continue; }
            var input = owner.Input;

            if ((owner.Publication.Revision != input.Revision) || (owner.Publication.Epoch != input.Epoch)) {
                owner.Publication = new(owner.Principal, owner.Body, owner.Generation, input.Revision, input.Epoch,
                    input.Mapping.Id, input.Image, input.Sources.ToArray(), input.LastSwitch,
                    owner.SeatStates.OrderBy(static cell => cell.Key, StringComparer.Ordinal)
                        .Select(selector: static cell => new WorldInputSeatState(Family: cell.Key, State: cell.Value)).ToArray());
            }
            m_publications[count++] = owner.Publication;
        }
        return m_publications.AsMemory(length: count, start: 0);
    }
    /// <summary>Starts an interval and expires input history without allocating.</summary>
    public void BeginInterval(WorldInputInterval interval) {
        Interval = interval;
        m_intervals[m_intervalNext] = interval;
        m_intervalNext = ((m_intervalNext + 1) % m_intervals.Length);
        m_intervalCount = Math.Min(val1: (m_intervalCount + 1), val2: m_intervals.Length);
        foreach (var owner in m_ordered) { owner.Input.Expire(now: interval); }
    }
    /// <summary>Admits a generation-scoped profile through its already-authorized semantic source contracts.</summary>
    public WorldInputOwner Admit(Principal principal, int bodyIndex, IReadOnlyList<WorldInputSource> sources) {
        if (m_owners.ContainsKey(key: principal)) { throw new InvalidOperationException(message: "input owner is already admitted"); }
        var owner = PrepareOwner(principal, bodyIndex, sources, 1);

        InstallOwner(owner);
        return owner.Input;
    }
    /// <summary>Replaces a changed profile atomically; repeating the current profile preserves its epoch and holds.</summary>
    public bool TryAdmitProfile(Principal principal, int bodyIndex, IReadOnlyList<WorldInputSource> sources, out string reason, bool freshAttachment = false) {
        m_owners.TryGetValue(key: principal, value: out var previous);
        if (previous?.Controller is not null) { reason = "input.owner: a local controller's profile is authority-owned"; return false; }
        if ((previous is not null) && (previous.Body == bodyIndex) && (previous.Input.Sources.Length == sources.Count) &&
            WorldInputCatalog.SameContract(left: previous.Definition, right: m_host.Document.Definition)) {
            var same = true;

            for (var index = 0; (index < sources.Count); index++) { same &= (previous.Input.Sources[index].Source == sources[index]); }
            if (same) {
                if (freshAttachment) { Reset(principal); }
                reason = string.Empty;
                return true;
            }
        }
        Owner candidate;
        var continued = false;

        try { candidate = PrepareOwner(principal, bodyIndex, sources, checked(((previous?.Input.Revision ?? 0) + 1)), previous?.SeatStates); } catch (ArgumentException error) { reason = error.Message; return false; }
        if (previous is not null) {
            if (!freshAttachment && (previous.Body == bodyIndex) && (previous.Generation == candidate.Generation) &&
                WorldInputCatalog.SameContract(left: previous.Definition, right: candidate.Definition)) {
                candidate.Input.InheritProfile(previous.Input, Interval);
                continued = true;
            } else {
                Reset(principal);
                candidate.Input.RequireNeutralSources(previous: previous.Input);
            }
            RemoveOwner(owner: previous);
        }
        InstallOwner(candidate, previous?.Input.Mapping, reset: !continued);
        reason = string.Empty;
        return true;
    }

    private Owner PrepareOwner(Principal principal, int bodyIndex, IReadOnlyList<WorldInputSource> sources, uint revision, IReadOnlyDictionary<string, string>? seatStates = null,
        WorldInputControllerOrigin? controller = null) {
        if ((bodyIndex < -1) || (bodyIndex >= m_host.Population.Capacity)) { throw new ArgumentOutOfRangeException(paramName: nameof(bodyIndex)); }
        var catalog = m_host.Population.InputCatalog;
        var owner = new Owner {
            Principal = principal,
            Definition = m_host.Document.Definition,
            Input = new WorldInputOwner(catalog, catalog.DefaultMapping, (m_host.Document.Definition.InputHistory ?? new()), sources,
                revision: revision, patterns: m_host.Document.Definition.InputPatterns),
            Body = bodyIndex,
            Generation = ((bodyIndex >= 0) ? m_host.Population.Generation(index: bodyIndex) : 0),
            Selection = new ushort[catalog.Families.Length],
            Facing = new int[(m_host.Document.Definition.InputHistory?.Crossings?.Count ?? 0)],
            Controller = controller,
        };

        foreach (var family in m_host.Document.Definition.SeatModes) {
            owner.SeatStates.Add(key: family.Name, value: (((seatStates is not null) && seatStates.TryGetValue(key: family.Name, value: out var state) &&
                family.States.Any(predicate: row => (row.Name == state))) ? state : family.DefaultState));
        }
        if (!TrySelect(owner, out var mapping, out var reason)) { throw new ArgumentException(message: reason, paramName: nameof(bodyIndex)); }
        if (mapping!.Id != owner.Input.Mapping.Id) { owner.Input.Switch(mapping, Interval, reset: true); }
        return owner;
    }
    private void InstallOwner(Owner owner, WorldInputMapping? previous = null, bool reset = true, bool recompiled = false) {
        owner.KitName = ((owner.Body >= 0) ? m_host.Population.CandidateKitName(bodyIndex: owner.Body, definition: owner.Definition) : null);
        m_owners.Add(key: owner.Principal, value: owner);
        m_ordered.Add(item: owner);
        IndexBodyOwner(owner: owner);
        m_ordered.Sort(comparison: static (a, b) => ComparePrincipal(a: a.Principal, b: b.Principal));
        InstallBodyMapping(owner, (previous ?? m_host.Population.InputCatalog.DefaultMapping), reset, recompiled);
    }
    private void IndexBodyOwner(Owner owner) {
        if (owner.Body < 0) { return; }
        if (!m_bodyOwners.TryGetValue(key: owner.Body, value: out var owners)) { m_bodyOwners.Add(key: owner.Body, value: owners = []); }
        owners.Add(item: owner);
    }
    private void RemoveOwner(Owner owner) {
        m_owners.Remove(key: owner.Principal);
        m_ordered.Remove(item: owner);
        if ((owner.Body >= 0) && m_bodyOwners.TryGetValue(key: owner.Body, value: out var owners)) {
            owners.Remove(item: owner);
            if (owners.Count == 0) { m_bodyOwners.Remove(key: owner.Body); }
        }
    }
    private void InstallBodyMapping(Owner owner, WorldInputMapping previous, bool reset, bool recompiled = false) {
        if (owner.Body < 0) {
            m_host.GrantTable.InstallSessionInputBarrier(owner.Principal, owner.Input.Image);
            return;
        }
        if (!OwnsBodyMapping(owner: owner)) { return; }
        m_host.Population.InstallInputMapping(bodyIndex: owner.Body, owner: owner.Input, previous: previous, recompiled: recompiled, reset: reset);
        m_host.InputHold.InstallBarrier(owner.Body, owner.Input.Image, owner.Input.Mapping.Channels);
    }
    private bool OwnsBodyMapping(Owner owner) => ((owner.Body >= 0) &&
        TryGetBodyOwner(body: owner.Body, principal: out var principal) && (principal == owner.Principal));

    /// <summary>Reconciles rebuilt consumers, translating unchanged actions by name and cancelling changed meanings.</summary>
    public void Reconfigure(WorldDefinition definition) {
        var capacity = checked((((int)FixedTickConversion.DurationEngineTicks(seconds: (definition.InputHistory?.MaximumLatenessSeconds ?? 0))) + 2));

        if (capacity != m_intervals.Length) {
            var replacement = new WorldInputInterval[capacity];
            var retained = Math.Min(val1: m_intervalCount, val2: capacity);
            var start = (((m_intervalNext - retained) + m_intervals.Length) % m_intervals.Length);

            for (var index = 0; (index < retained); index++) { replacement[index] = m_intervals[((start + index) % m_intervals.Length)]; }
            m_intervals = replacement;
            m_intervalCount = retained;
            m_intervalNext = (retained % capacity);
        }
        var catalog = m_host.Population.InputCatalog;

        foreach (var owner in m_ordered.ToArray()) {
            if (WorldInputCatalog.SameContract(left: owner.Definition, right: definition)) {
                owner.Definition = definition;
                if (!TrySynchronize(activation: 0, owner: owner, reason: out var reason)) { throw new ArgumentException(message: reason, paramName: nameof(definition)); }
                if (OwnsBodyMapping(owner: owner)) {
                    m_host.Population.RestoreInputMapping(bodyIndex: owner.Body, owner: owner.Input);
                    m_host.InputHold.InstallBarrier(owner.Body, owner.Input.Image, owner.Input.Mapping.Channels);
                }
                continue;
            }
            var previous = WorldInputCatalog.For(definition: owner.Definition);
            var sources = new List<WorldInputSource>();

            foreach (var state in owner.Input.Sources) {
                var source = state.Source;
                var declaration = previous.Channels[source.Channel];

                if (!catalog.TryGetLogicalId(declaration.Name, out var channel) || (catalog.Channels[channel].Shape != declaration.Shape)) { continue; }
                sources.Add(item: source with { Channel = channel });
            }
            if (owner.Controller is not null) { sources.Clear(); sources.AddRange(collection: SnapshotProfile(catalog: catalog)); }
            var candidate = PrepareOwner(owner.Principal, owner.Body, sources, checked((owner.Input.Revision + 1)), owner.SeatStates, owner.Controller);

            candidate.Input.InheritDefinition(owner.Input, owner.Definition, definition, Interval, ActivationCauses(owner.Principal));
            RemoveOwner(owner: owner);
            InstallOwner(candidate, owner.Input.Mapping, reset: false, recompiled: true);
            if (candidate.Controller is { } controller) { m_controllers[(controller.Actor, candidate.Body, controller.Source)] = candidate; }
        }
    }
    /// <summary>Reads one admitted owner's state without creating a participant.</summary>
    public bool TryGet(Principal principal, out WorldInputOwner? input) {
        if (m_owners.TryGetValue(key: principal, value: out var owner)) { input = owner.Input; return true; }
        input = null;
        return false;
    }
    /// <summary>Resolves an admitted owner's routing subject; a caller cannot substitute another body for authorization.</summary>
    public bool TryGetBody(Principal principal, out int body) {
        if (m_owners.TryGetValue(key: principal, value: out var owner)) { body = owner.Body; return true; }
        body = -1;
        return false;
    }
    /// <summary>Resolves a typed body's admitted input owner without selecting an arbitrary co-driver.</summary>
    public bool TryGetBodyOwner(int body, out Principal principal) {
        principal = default;
        if ((body < 0) || (body >= m_host.Population.Capacity) || (m_host.Body(index: body) is null)) { return false; }
        var own = ((body < m_host.Population.LocalSeatCount) ? Principal.Seat(slot: body) : m_host.Population.PeerPrincipal(index: body));

        if (m_owners.TryGetValue(key: own, value: out var admitted) && (admitted.Body == body)) { principal = own; return true; }
        if (!m_bodyOwners.TryGetValue(key: body, value: out var candidates)) { return false; }
        var found = false;

        foreach (var owner in candidates) {
            if ((owner.Generation != m_host.Population.Generation(index: body)) ||
                ((owner.Controller is not null) && !m_host.GrantTable.IsLiveSession(principal: owner.Principal))) { continue; }
            if (found) { principal = default; return false; }
            principal = owner.Principal;
            found = true;
        }
        return found;
    }
    /// <summary>Accepts a timing-bearing batch only for its admitted identity and current authoritative tuple.</summary>
    public bool TryAdmit(Principal principal, in WorldInputBatch batch, out string reason, int? bodyIndex = null) {
        if (!m_owners.TryGetValue(key: principal, value: out var owner)) { reason = "input.owner: no admitted source profile"; return false; }
        if ((bodyIndex is { } body) && (body != owner.Body)) { reason = "input.owner: the submitted target differs from the admitted route"; return false; }
        if (!TrySynchronize(activation: 0, owner: owner, reason: out reason)) { return false; }
        if (!Permitted(owner: owner, reach: out var reach)) { reason = "input.permission: the owner no longer holds Drive over its route"; return false; }
        if (reach is { } selected) {
            var channels = m_host.Population.InputCatalog.Channels;

            foreach (var source in batch.Contributions.Span) {
                if ((source.RawValue != 0) && (source.Channel < channels.Length) && !selected.Contains(name: channels[source.Channel].Name)) {
                    reason = $"input.permission: Drive reach excludes '{channels[source.Channel].Name}'";
                    return false;
                }
            }
            foreach (var input in batch.Events.Span) {
                if ((input.Channel < channels.Length) && !selected.Contains(name: channels[input.Channel].Name)) {
                    reason = $"input.permission: Drive reach excludes '{channels[input.Channel].Name}'";
                    return false;
                }
            }
        }
        var priorEpoch = owner.Input.Epoch;
        var priorMapping = owner.Input.Mapping;

        if (!batch.Events.IsEmpty) {
            var crossings = owner.Definition.InputHistory?.Crossings;

            for (var index = 0; (index < owner.Facing.Length); index++) {
                owner.Facing[index] = (((crossings![index].FacingState is { } state) && (owner.Body >= 0) &&
                    m_host.Population.TryCandidateInputState(bodyIndex: owner.Body, definition: owner.Definition, name: state, value: out var value)) ? Math.Sign(value: value.Value) : 0);
            }
        }
        var admitted = owner.Input.TryAdmit(batch, Interval, m_intervals.AsSpan(length: m_intervalCount, start: 0), out reason, owner.Facing);

        if (owner.Input.Epoch != priorEpoch) {
            InstallBodyMapping(owner, priorMapping, reset: true);
        }
        return admitted;
    }
    /// <summary>Synchronizes each already-admitted owner after a committed authoritative state change.</summary>
    public bool TrySynchronize(ulong activation, out string reason) {
        if (!TryValidate(reason: out reason)) { return false; }
        foreach (var owner in m_ordered) {
            if (!TrySynchronize(activation: activation, owner: owner, reason: out reason)) { return false; }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Checks all candidate selections while the state's journal can still rewind; changes no input state.</summary>
    public bool TryValidate(out string reason) {
        foreach (var owner in m_ordered) {
            if (!TrySelect(owner, out _, out reason)) { return false; }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Checks a proposed routing set before it changes engagement or releases an admitted hold.</summary>
    public bool TryValidateApplications(Principal principal, IReadOnlyList<ControlApplication> applications, out string reason) {
        foreach (var owner in m_ordered) {
            if ((RoutingPrincipal(owner: owner) == principal) && !TrySelect(applications: applications, mapping: out _, owner: owner, reason: out reason)) { return false; }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Refuses a replacement whose current owners would select a forbidden tuple, before any installation.</summary>
    public bool TryValidateDefinition(WorldDefinition definition, StateArena? arena, out string reason, bool fresh = false) {
        if (m_ordered.Count == 0) { reason = string.Empty; return true; }
        var catalog = WorldInputCatalog.For(definition: definition);
        var definitions = FixedWorldKit.CompileActionStateFile(bodyState: definition.BodyState, identityState: definition.IdentityState);
        var staged = new WorldActionStateLane();

        staged.Bind(arena: (arena ?? m_host.Arena), definitions: definitions);
        foreach (var owner in m_ordered) {
            FixedQ4816? Counter(string name) {
                for (var slot = 0; (slot < definitions.Length); slot++) {
                    var row = definitions[slot];

                    if ((row.Name != name) || (row.Kind != Puck.Physics.Motion.ActionStateKind.Counter)) { continue; }
                    var carried = ((!fresh || ((row.Lifetime == Puck.Physics.Motion.ActionStateLifetime.Durable) && (m_host.Body(index: owner.Body)?.Profile is not null))) &&
                        m_host.Population.ActionStateLane.Definitions.Any(predicate: old => ((old.Name == name) && (old.Kind == row.Kind) && (old.Lifetime == row.Lifetime))));

                    return (carried ? FixedQ4816.FromRawBits(value: staged.Read(ordinal: owner.Body, slot: slot)) : row.InitialValue);
                }
                return null;
            }
            var selections = new ushort[catalog.Families.Length];

            for (var family = 0; (family < selections.Length); family++) {
                var selector = catalog.Selectors[family];
                var selected = selector.Default;
                var known = true;

                switch (selector.Kind) {
                    case WorldInputModeSelectorKind.ActiveKit when (owner.Body >= 0):
                        known = selector.TrySelect(m_host.Population.PreviewKitName(bodyIndex: owner.Body, counter: Counter, definition: definition), out selected);
                        break;
                    case WorldInputModeSelectorKind.BodyState when (owner.Body >= 0):
                        known = ((Counter(name: selector.Name!) is { } value) && selector.TrySelect(state: out selected, value: value));
                        break;
                    case WorldInputModeSelectorKind.Engagement:
                        known = selector.TrySelect((fresh ? "none" : Engagement(RoutingPrincipal(owner: owner), owner.Body)), out selected);
                        break;
                    case WorldInputModeSelectorKind.SeatState:
                        var seat = definition.SeatModes.First(predicate: row => (row.Name == selector.Name));
                        var label = ((!fresh && owner.SeatStates.TryGetValue(key: seat.Name, value: out var held) && seat.States.Any(predicate: row => (row.Name == held)))
                            ? held : seat.DefaultState);
                        known = selector.TrySelect(state: out selected, value: label);
                        break;
                }
                if (!known) { reason = $"input.mode: family '{catalog.Families[family].Name}' refuses the replacement selector value for {owner.Principal.Describe()}"; return false; }
                selections[family] = selected;
            }
            if (!catalog.TryGetMapping(mapping: out _, states: selections)) {
                reason = $"input.mode: replacement selects a forbidden tuple for {owner.Principal.Describe()}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Ends all admitted input episodes for an explicit fresh load/reset. Hosts must attach new profiles.</summary>
    public void Restart() {
        foreach (var owner in m_ordered) { Reset(owner.Principal); }
        m_owners.Clear();
        m_ordered.Clear();
        m_bodyOwners.Clear();
        m_controllers.Clear();
        m_publications = [];
    }
    /// <summary>Commits one finite seat state only when its resulting input tuple is admitted.</summary>
    public bool TrySetSeatState(Principal principal, string family, string state, ulong activation, out string reason, Principal? actor = null) {
        if (!m_owners.TryGetValue(key: principal, value: out var owner) || !owner.SeatStates.TryGetValue(key: family, value: out var previous)) {
            reason = "input.mode: unknown owner or seat family";
            return false;
        }
        var declaration = m_host.Document.Definition.SeatModes.First(predicate: row => (row.Name == family));
        var selected = declaration.States.FirstOrDefault(predicate: row => (row.Name == state));

        if (selected is null) { reason = "input.mode: unknown finite seat state"; return false; }
        if (state == previous) { reason = string.Empty; return true; }
        var previousState = declaration.States.First(predicate: row => (row.Name == previous));

        owner.SeatStates[family] = state;
        if ((previousState.Target == WorldSeatModeState.CameraTarget) != (selected.Target == WorldSeatModeState.CameraTarget)) {
            if (!TrySetCameraRoute(owner, (actor ?? principal), (selected.Target == WorldSeatModeState.CameraTarget), activation, out reason)) {
                owner.SeatStates[family] = previous;
                return false;
            }
        }
        if (!TrySynchronize(activation: activation, owner: owner, reason: out reason)) { owner.SeatStates[family] = previous; return false; }
        foreach (var controller in m_ordered) {
            if ((controller.Controller is not null) && (controller.Body == owner.Body) && !TrySynchronize(activation: 0, owner: controller, reason: out reason)) {
                throw new InvalidOperationException(message: reason);
            }
        }
        owner.Publication = default;
        return true;
    }
    /// <summary>Admits bounded calibration through the same reset and publication path as every unconditional input boundary.</summary>
    public bool TryCalibrate(Principal principal, FixedQ4816 offset, out string reason) {
        if (!m_owners.TryGetValue(key: principal, value: out var owner)) { reason = "input.calibration: unknown owner"; return false; }
        var epoch = owner.Input.Epoch;

        if (!owner.Input.TryCalibrate(offset, Interval)) { reason = "input.calibration: offset exceeds the authored bound"; return false; }
        if (owner.Input.Epoch != epoch) {
            InstallBodyMapping(owner, owner.Input.Mapping, reset: true);
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Cancels input whose Drive authority was revoked before a later re-grant can revive it.</summary>
    public void RevokeDrive(Grantee grantee, GrantSubject subject) {
        var direct = grantee.TryGetPrincipal(principal: out var principal);

        foreach (var owner in m_ordered) {
            if ((owner.Body < 0) || ((subject.Kind != GrantSubjectKind.All) && (subject != GrantSubject.Body(index: owner.Body)))) { continue; }
            var actor = (owner.Controller?.Actor ?? owner.Principal);

            if (direct ? (actor == principal) : !m_host.GrantTable.Allows(actor, WorldCapability.Drive, GrantSubject.Body(index: owner.Body)).IsAllowed) {
                Reset(owner.Principal);
            }
        }
    }
    /// <summary>Records every dispatch boundary, including intermediate routes which return to the same tuple.</summary>
    public void Reset(Principal principal, ulong activation = 0) {
        if (!m_owners.TryGetValue(key: principal, value: out var owner)) { return; }
        Reset(owner, owner.Input.Mapping, activation);
    }

    /// <summary>Cancels every admitted input owner at an authority crossing.</summary>
    internal void ResetBody(int body) {
        if (!m_bodyOwners.TryGetValue(key: body, value: out var owners)) { return; }
        foreach (var owner in owners) { Reset(owner, owner.Input.Mapping, 0); }
    }
    /// <summary>Installs one barrier for an atomic application-set replacement, including its new selection.</summary>
    internal void ResetRoute(Principal principal) {
        if (!m_owners.TryGetValue(key: principal, value: out var owner)) { return; }
        var next = (TrySelect(owner, out var selected, out _) ? selected! : owner.Input.Mapping);

        Reset(activation: 0, next: next, owner: owner);
        foreach (var controller in m_ordered) {
            if ((controller.Controller is null) || (controller.Body != owner.Body) || ReferenceEquals(objA: controller, objB: owner)) { continue; }
            var controllerNext = (TrySelect(controller, out selected, out _) ? selected! : controller.Input.Mapping);

            Reset(activation: 0, next: controllerNext, owner: controller);
        }
    }

    private void Reset(Owner owner, WorldInputMapping next, ulong activation) {
        var previous = owner.Input.Mapping;

        SaveKitInput(owner: owner);
        owner.Input.Switch(next, Interval, reset: true, consumedActivations: ActivationCauses(explicitActivation: activation, principal: owner.Principal));
        InstallBodyMapping(owner, previous, reset: true);
    }
    private bool TrySynchronize(Owner owner, ulong activation, out string reason) {
        RefreshPermissions(owner: owner);
        if (!TrySelect(owner, out var mapping, out reason)) { return false; }
        var generation = ((owner.Body >= 0) ? m_host.Population.Generation(index: owner.Body) : 0);
        var kitName = ((owner.Body >= 0) ? m_host.Population.CandidateKitName(bodyIndex: owner.Body, definition: owner.Definition) : null);
        var reset = ((generation != owner.Generation) || ((mapping!.Id == owner.Input.Mapping.Id) && (kitName != owner.KitName)));

        if ((mapping!.Id == owner.Input.Mapping.Id) && !reset) { return true; }
        var previous = owner.Input.Mapping;

        SaveKitInput(owner: owner);
        owner.Input.Switch(mapping, Interval, reset: reset, consumedActivations: ActivationCauses(explicitActivation: activation, principal: owner.Principal));
        owner.Generation = generation;
        owner.KitName = kitName;
        InstallBodyMapping(owner, previous, reset);
        return true;
    }
    private bool Permitted(Owner owner, out WorldChannelSelection? reach) {
        reach = null;
        if ((owner.Controller is not null) && !m_host.GrantTable.IsLiveSession(principal: owner.Principal)) { return false; }
        if (owner.Body < 0) { return true; }
        if (m_host.GrantTable.TryGetDriveGate(bodyIndex: owner.Body, gateRow: out _)) { return false; }
        var subject = GrantSubject.Body(index: owner.Body);
        var actor = (owner.Controller?.Actor ?? owner.Principal);

        if (actor.Kind == PrincipalKind.World) {
            return ((owner.Controller is { } program) && IsAuthoredProducer(actor: actor, body: owner.Body, source: program.Source));
        }
        if (!m_host.GrantTable.Allows(capability: WorldCapability.Drive, principal: actor, subject: subject).IsAllowed) { return false; }
        if (m_host.GrantTable.TryGetInputReach(principal: actor, selection: out var selection, subject: subject)) { reach = selection; }
        return true;
    }
    private void RefreshPermissions(Owner owner) {
        if ((owner.Controller is { Actor.Kind: PrincipalKind.World } program) &&
            !IsAuthoredProducer(actor: program.Actor, body: owner.Body, source: program.Source) && m_host.GrantTable.IsLiveSession(principal: owner.Principal)) {
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(session: owner.Principal);
            owner.Producer = null;
            return;
        }
        var permitted = Permitted(owner: owner, reach: out var reach);

        if (permitted && (reach is null)) { return; }
        var channels = m_host.Population.InputCatalog.Channels;

        foreach (var source in owner.Input.Sources) {
            if ((source.RawValue != 0) && (!permitted || !reach!.Value.Contains(name: channels[source.Source.Channel].Name))) {
                Reset(owner.Principal);
                return;
            }
        }
        for (var index = 0; (index < owner.Input.Count); index++) {
            var record = owner.Input.Record(index: index);

            if ((record.Epoch == owner.Input.Epoch) && (record.Event.Kind != WorldInputEventKind.Cancel) &&
                (!permitted || !reach!.Value.Contains(name: channels[record.Event.Channel].Name))) {
                Reset(owner.Principal);
                return;
            }
        }
    }

    /// <summary>Cancels histories and levels after grant, membership or gate changes, before gameplay consumes them.</summary>
    public void RefreshPermissions() {
        foreach (var owner in m_ordered) { RefreshPermissions(owner: owner); }
    }

    private Principal RoutingPrincipal(Owner owner) {
        // Body engagement belongs to its occupant even before a physical input profile is admitted.
        // A scripted command's actor still owns authorization; its private history session owns no route.
        if ((owner.Body >= 0) && m_host.Population.IsHumanOccupied(bodyIndex: owner.Body)) {
            return ((owner.Body < m_host.Population.LocalSeatCount) ? Principal.Seat(slot: owner.Body) : m_host.Population.PeerPrincipal(index: owner.Body));
        }
        if ((owner.Controller is not null) && TryGetBodyOwner(body: owner.Body, principal: out var primaryIdentity) &&
            (primaryIdentity != owner.Principal) && m_owners.TryGetValue(key: primaryIdentity, value: out var primary) && (primary.Controller is null)) { return primaryIdentity; }
        return (owner.Controller?.Actor ?? owner.Principal);
    }
    private bool TrySelect(Owner owner, out WorldInputMapping? mapping, out string reason, IReadOnlyList<ControlApplication>? applications = null) {
        var catalog = m_host.Population.InputCatalog;
        var body = ((owner.Body >= 0) ? m_host.Body(index: owner.Body) : null);
        var routingPrincipal = RoutingPrincipal(owner: owner);

        if ((owner.Controller is not null) && TryGetBodyOwner(body: owner.Body, principal: out var primaryIdentity) &&
            (primaryIdentity != owner.Principal) && m_owners.TryGetValue(key: primaryIdentity, value: out var primary) && (primary.Controller is null)) {
            routingPrincipal = primaryIdentity;
            foreach (var cell in primary.SeatStates) { owner.SeatStates[cell.Key] = cell.Value; }
        }
        for (var family = 0; (family < catalog.Selectors.Length); family++) {
            var selector = catalog.Selectors[family];
            var state = selector.Default;
            var known = true;

            switch (selector.Kind) {
                case WorldInputModeSelectorKind.ActiveKit when (body is not null):
                    known = selector.TrySelect(m_host.Population.CandidateKitName(bodyIndex: owner.Body, definition: m_host.Document.Definition), out state);
                    break;
                case WorldInputModeSelectorKind.Engagement:
                    known = selector.TrySelect(Engagement(applications: applications, body: owner.Body, principal: routingPrincipal), out state);
                    break;
                case WorldInputModeSelectorKind.BodyState when (body is not null):
                    known = (m_host.Population.TryCandidateInputState(bodyIndex: owner.Body, definition: m_host.Document.Definition, name: selector.Name!, value: out var value) && selector.TrySelect(state: out state, value: value));
                    break;
                case WorldInputModeSelectorKind.SeatState:
                    known = (owner.SeatStates.TryGetValue(key: selector.Name!, value: out var label) && selector.TrySelect(state: out state, value: label));
                    break;
            }
            if (!known) { mapping = null; reason = $"input.mode: family '{catalog.Families[family].Name}' refuses an unknown selector value"; return false; }
            owner.Selection[family] = state;
        }
        if (!catalog.TryGetMapping(mapping: out mapping, states: owner.Selection)) { reason = "input.mode: selected tuple is forbidden by combinations"; return false; }
        reason = string.Empty;
        return true;
    }
    private string Engagement(Principal principal, int body, IReadOnlyList<ControlApplication>? applications = null) {
        foreach (var application in (applications ?? m_host.GrantTable.Applications(principal: principal))) {
            var target = application.Target;

            if (target.Kind == GrantSubjectKind.Placement) { return "tabletop"; }
            if (target.Kind == GrantSubjectKind.Screen) { return (m_host.Machines.HasMachine(index: target.Value) ? "machine" : "screen"); }
            if ((target.Kind == GrantSubjectKind.Body) && (target.Value != body)) { return "body"; }
        }
        return "none";
    }
}
