using Puck.Assets;
using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

/// <summary>One mounted guest's live cost-surface reading — the <c>world.addons</c> read
/// (<see cref="WorldAddonRuntime.DescribeCost"/>). Diagnostic only: never simulation state, never on a hashed path —
/// fuel consumed is measured every tick regardless (the guarantee determinism needs), this is only where the already-
/// measured number stops being discarded.</summary>
/// <param name="Name">The addon's identifying name.</param>
/// <param name="State">The instance's current lifecycle state.</param>
/// <param name="FuelPerTick">The per-tick fuel budget the instance runs under.</param>
/// <param name="LastTickFuelConsumed">Fuel consumed by the most recent tick this guest actually ran; zero on a tick
/// it was skipped (faulted, disabled, or enabled-but-unadmitted).</param>
/// <param name="TotalFuelConsumed">Fuel consumed across every tick since this guest was first mounted — a lifetime
/// figure that survives a disable/enable cycle and a live reload (see <see cref="WorldAddonRuntime.Reload"/>), never
/// silently restarting while the guest stays mounted under the same name. Saturates at <see cref="ulong.MaxValue"/>
/// rather than wrapping.</param>
/// <param name="TotalAnswersDropped">Answer groups dropped with no verdict cell at all, across this guest's whole
/// lifetime (same survives-reload, saturating-lifetime shape as <paramref name="TotalFuelConsumed"/>) — the ring's
/// own hard ceiling, past which even a single-cell <see cref="AddonVerdict.QuotaExhausted"/> squeeze has no room.
/// Nonzero here means this guest is asking for more per tick than its declared <c>puck_in_cap</c> can ever answer.</param>
/// <param name="EventGaps">Event cells dropped across this mount's lifetime because a row's event budget or the input
/// ring had no room. Saturates at <see cref="ulong.MaxValue"/>.</param>
/// <param name="EventCellsDelivered">World-event and memory-watch cells delivered across the mount's lifetime.</param>
/// <param name="RouteEventsDelivered">Route-engaged/disengaged cells delivered across the mount's lifetime.</param>
/// <param name="CollisionEventsDelivered">Collision begin/end cells delivered across the mount's lifetime.</param>
/// <param name="FaultDetail">The sticky fault detail, or <see langword="null"/> when healthy.</param>
public readonly record struct AddonCostReport(string Name, AddonState State, long FuelPerTick, ulong LastTickFuelConsumed, ulong TotalFuelConsumed, ulong TotalAnswersDropped, ulong EventGaps, ulong EventCellsDelivered, ulong RouteEventsDelivered, ulong CollisionEventsDelivered, string? FaultDetail);
public sealed partial class WorldAddonRuntime {
    private static int CountMaterialized(MountedAddon addon, WorldCapability capability, GrantSubject[] subjects) {
        var count = 0;

        for (var index = 0; (index < subjects.Length); ++index) {
            if (IsMaterialized(
                addon: addon,
                capability: capability,
                subject: subjects[index]
            )) {
                ++count;
            }
        }

        return count;
    }
    private void EmitDisclosureSet(MountedAddon addon, WorldCapability capability, GrantSubject[] subjects) {
        var handles = m_server.Grants.HandleTable(
            principal: addon.Principal,
            capability: capability
        );

        for (var index = 0; (index < subjects.Length); ++index) {
            if (
                !IsMaterialized(
                addon: addon,
                capability: capability,
                subject: subjects[index]
            ) ||
                !handles.TryMint(
                handle: out var handle,
                index: index
            )
            ) {
                continue;
            }

            // A handle whose index or generation exceeds the wire's u16 lanes is UNMATERIALIZABLE, never truncated:
            // a wrapped value would collide with a live handle at the same wire index and hand the guest authority
            // over whatever that one designates.
            if (!TryPack(
                addon: addon,
                generation: out var wireGeneration,
                handle: handle,
                index: out var wireIndex
            )) {
                continue;
            }

            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: wireIndex,
                HandleGeneration: wireGeneration,
                Verdict: AddonVerdict.None,
                Verb: ((byte)AddonAbi.ObservationVerbs.GrantedBody),
                A: ((long)WorldAddonWire.CapabilityBit(capability: capability)),
                B: subjects[index].Value
            );
        }
    }
    // The disclosure push: one GrantedBody observation per (capability, body) the addon's principal holds AND its
    // manifest requested, in projection order, emitted whole the first time and re-emitted whole whenever the
    // materialized set MOVES. The newest set is authoritative; a handle a revoke invalidated also fails on its very next
    // use with StaleHandle, so the two mechanisms agree without either being load-bearing alone.
    private void EmitDisclosures(MountedAddon addon, int budget) {
        var grants = m_server.Grants;
        var generation = addon.Instance.Generation;
        // A disable/enable cycle re-instantiates the SAME AddonInstance in place (fresh store, wiped linear
        // memory, every handle the guest learned gone) without the grant table ever being written, so the
        // revision-only check below would read that as "already disclosed" and leave a recovered guest blind
        // forever. Keying the shortcut on the instance's own Generation as well — never on which lifecycle verb
        // ran — is what makes this hold for a reload's wholesale MountedAddon replacement (a fresh object, whose
        // Generation already disagrees with any DisclosedGeneration default) and for any future re-instantiation
        // path the same way.
        var sameGeneration = (addon.DisclosedGeneration == generation);

        // The projection can only change when the grant table is written, so an unchanged revision is a total answer —
        // this is what keeps the per-tick path free of the array ProjectSubjects allocates. Deliberately NOT sufficient
        // on its own: the revision is process-global, so it moves for writes touching other principals entirely, and the
        // sequence compare below is what decides whether THIS addon's projection actually moved. An overflowed set is
        // gated on the same coordinate: only a grant-table write can shrink it (the budget is fixed per instance), so
        // re-projecting every tick while it stays oversized would be two array allocations per tick forever.
        if (
            sameGeneration &&
            ((addon.DisclosedRevision == grants.Revision) || (addon.OverflowedAtRevision == grants.Revision))
        ) {
            return;
        }

        var drive = grants.ProjectSubjects(
            principal: addon.Principal,
            capability: WorldCapability.Drive
        );
        var observe = grants.ProjectSubjects(
            principal: addon.Principal,
            capability: WorldCapability.Observe
        );

        if (
            sameGeneration &&
            addon.Disclosed &&
            Same(
            a: addon.DisclosedDrive,
            b: drive
        ) &&
            Same(
            a: addon.DisclosedObserve,
            b: observe
        )
        ) {
            addon.DisclosedRevision = grants.Revision;

            return;
        }

        var count = (CountMaterialized(
            addon: addon,
            capability: WorldCapability.Drive,
            subjects: drive
        ) +
            CountMaterialized(
            addon: addon,
            capability: WorldCapability.Observe,
            subjects: observe
        ));

        if (count > budget) {
            // Disclosures are placed FIRST and therefore against the whole budget, so "does not fit this tick" and
            // "can never fit this guest's declared input ring" are the same condition here. Deferring keeps the
            // last-disclosed state untouched so the set retries on the next grant-table write (the only thing that
            // can shrink it); the line prints once so a permanently-oversized projection does not flood stderr.
            addon.OverflowedAtRevision = grants.Revision;
            addon.OverflowedAtGeneration = generation;

            if (!addon.DisclosureOverflowReported) {
                addon.DisclosureOverflowReported = true;
                Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} materializes {count} disclosable grant(s) but its input ring carries only {budget} beyond the tick cell — the disclosure is deferred and will not fit until the ring grows or the grants narrow]");
            }

            return;
        }

        EmitDisclosureSet(
            addon: addon,
            capability: WorldCapability.Drive,
            subjects: drive
        );
        EmitDisclosureSet(
            addon: addon,
            capability: WorldCapability.Observe,
            subjects: observe
        );

        addon.Disclosed = true;
        addon.DisclosedDrive = drive;
        addon.DisclosedObserve = observe;
        addon.DisclosedRevision = grants.Revision;
        addon.DisclosedGeneration = generation;
    }
    private static bool IsMaterialized(MountedAddon addon, WorldCapability capability, GrantSubject subject) =>
        ((subject.Kind == GrantSubjectKind.Body) && IsRequested(
            addon: addon,
            capability: capability,
            subject: subject
        ));
    // Requesting is not receiving, but the reverse gap is the dangerous one: reporting only the intersection of
    // "requested" and "held" lets a capability the addon holds but never declared go completely unmentioned. So this
    // reports what the settled table says the addon's OWN principal HOLDS, right now, across every capability (via
    // WorldGrants.Held), and separately annotates the manifest's own wish list against that held set: granted
    // (requested and held), withheld (requested, not held), and unrequested (held, never requested). The unrequested
    // list is the INERT one: a hold outside the manifest materializes no handle (see IsRequested). Called for EVERY
    // row regardless of whether it declares Requests, because a document can grant an addon's principal something
    // directly without the row ever asking for it.
    private static void ReportCapabilityDisclosure(string name, IReadOnlyList<WorldCapabilityRequest>? requests, IWorldGrantsView grants) {
        var principal = WorldPrincipal.Addon(name: name);
        var held = grants.Held(principal: principal);

        if (
            ((requests is null) || (requests.Count == 0)) &&
            (held.Count == 0)
        ) {
            return;
        }

        var heldLabels = new HashSet<string>(capacity: held.Count);

        foreach (var (capability, subject) in held) {
            _ = heldLabels.Add(item: $"{capability.ToString().ToLowerInvariant()}/{subject.Describe()}");
        }

        var granted = new List<string>();
        var withheld = new List<string>();

        if (requests is { Count: > 0 }) {
            foreach (var request in requests) {
                var label = $"{request.Capability.ToString().ToLowerInvariant()}/{request.Subject.Describe()}";

                (heldLabels.Contains(item: label)
                    ? granted
                    : withheld).Add(item: label);
            }
        }

        // Held but never named in Requests — the unrequested-authority case. Excludes every label already accounted
        // for as "granted" above, so the same hold is never listed twice.
        var unrequested = new List<string>();

        foreach (var (capability, subject) in held) {
            var label = $"{capability.ToString().ToLowerInvariant()}/{subject.Describe()}";

            if (!granted.Contains(item: label)) {
                unrequested.Add(item: label);
            }
        }

        var requestCount = (requests?.Count ?? 0);

        Console.Error.WriteLine(value: ((((string)$"[world.addon: {name} requested {requestCount} capabilit{((requestCount == 1)
            ? "y"
            : "ies")} — granted: {((granted.Count > 0)
            ? string.Join(
                separator: ", ",
                values: granted
            )
            : "(none)")}; ") +
            $"withheld: {((withheld.Count > 0)
            ? string.Join(
                separator: ", ",
                values: withheld
            )
            : "(none)")}; ") +
            $"holds beyond its manifest (inert — never materialized): {((unrequested.Count > 0)
            ? string.Join(
                separator: ", ",
                values: unrequested
            )
            : "(none)")}]"));
    }
    // The mount-time report point 2 of the channel-name re-key requires: one line naming every declared channel
    // this guest's names did NOT resolve against the host table — report-and-inert, never a mount fault (see
    // AddonChannelBinding). Runs once, right after the "mounted" line, over the handshake's own decoded bindings,
    // so it can never drift from what the guest actually declared.
    private static void ReportInertChannelDeclarations(ReadOnlySpan<AddonChannelBinding> bindings, string name) {
        var inert = new List<string>();

        foreach (var binding in bindings) {
            if (!binding.Resolved) {
                inert.Add(item: binding.Name);
            }
        }

        if (inert.Count > 0) {
            Console.Error.WriteLine(value: $"[world.addon: {name} declares {inert.Count} channel name(s) the host table does not recognize — inert, never faults the mount: {string.Join(
                separator: ", ",
                values: inert
            )}]");
        }
    }
    // Resolve an addon module path: absolute as-is, else relative to the executable directory (Assets/** is
    // Content-copied beside the output, exactly how the world document itself is found at boot).
    private static string ResolvePath(string modulePath) {
        return (Path.IsPathRooted(path: modulePath)
            ? modulePath
            : Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: modulePath
            )
        );
    }
    // The Response channel is host-written only, so its index is fixed at handshake and resolved once here rather
    // than re-scanned every tick. Hoisted out of MountedAddon (which still calls it, unqualified — a nested class
    // sees every member of its enclosing type) so the constructor's mount-time gate above can ask the same question
    // before a MountedAddon is ever created for a permanently-undeliverable row.
    private static int ResolveResponseChannel(AddonInstance instance) {
        var channels = instance.Channels;

        for (var index = 0; (index < channels.Length); ++index) {
            if (channels[index].Kind == AddonChannelKind.Response) {
                return index;
            }
        }

        return -1;
    }
    private static bool Same(GrantSubject[] a, GrantSubject[] b) {
        if (a.Length != b.Length) {
            return false;
        }

        for (var index = 0; (index < a.Length); ++index) {
            if (a[index] != b[index]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the runtime over a boot world document and attaches it to the server that will pump it. Mounting
    /// runs here, which is after the server's constructor applied <see cref="WorldDefinition.Grants"/> — the disclosure
    /// must report a settled table, because a report that lies at boot is worse than no report.</summary>
    /// <param name="definition">The boot world definition (its <c>addons</c> rows).</param>
    /// <param name="server">The authoritative server the guests act against and that drives the three pump points.</param>
    /// <returns>The attached runtime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="server"/> is <see langword="null"/>.</exception>
    public static WorldAddonRuntime Create(WorldDefinition definition, WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: server);

        var runtime = new WorldAddonRuntime(
            definition: definition,
            server: server
        );

        server.AttachAddons(runtime: runtime);

        return runtime;
    }
    /// <summary>Describes the live per-guest cost surface, in mount order — the <c>world.addons</c> read. Allocates one array per
    /// call; a console read, never the tick path.</summary>
    /// <returns>The per-guest cost reports, in mount order.</returns>
    public IReadOnlyList<AddonCostReport> DescribeCost() {
        var report = new AddonCostReport[m_mounted.Count];

        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];
            var instance = addon.Instance;

            report[index] = new AddonCostReport(
                Name: instance.Name,
                State: instance.State,
                FuelPerTick: instance.FuelPerTick,
                LastTickFuelConsumed: addon.LastTickFuelConsumed,
                TotalFuelConsumed: addon.TotalFuelConsumed,
                TotalAnswersDropped: addon.TotalAnswersDropped,
                EventGaps: addon.EventGapCount,
                EventCellsDelivered: addon.EventCellsDelivered,
                RouteEventsDelivered: addon.RouteEventsDelivered,
                CollisionEventsDelivered: addon.CollisionEventsDelivered,
                FaultDetail: ((instance.Fault.Kind == AddonFaultKind.None)
                ? null
                : instance.Fault.Detail)
            );
        }

        return report;
    }
    /// <summary>Reports the channels a co-driving grant confers on <paramref name="principal"/> that the guest never
    /// declares, so a consent row naming a channel its holder cannot reach says so at the door instead of sitting
    /// inert. Returns <see langword="null"/> when every granted channel is one the guest declared, when the principal
    /// names no mounted guest, or when the grant carries no channel mask.</summary>
    /// <remarks>Both halves were already known and never compared: the guest's declared
    /// channel names are decoded once at the handshake, and the grant's channel mask is validated once at the door —
    /// each against the world's channel table, and neither against the other. So a grant could name a real channel the
    /// holder simply never emits, be accepted in full, and drive nothing; the operator's only evidence was an absence
    /// of motion, which reads identically to a pool set too low or a body that will not move. It is reported and never
    /// refused, because a guest may legitimately gain the channel on a later reload — the row is a standing intent, not
    /// a lie. Nothing here changes what is granted; it only stops the grant table and the guest disagreeing in
    /// silence.</remarks>
    /// <param name="principal">The principal the grant confers on.</param>
    /// <param name="reach">The grant's channel reach, when it carries one.</param>
    /// <param name="channels">The world's channel table, for naming the ordinals.</param>
    /// <returns>A description of the granted-but-undeclared channels, or <see langword="null"/> when every granted
    /// channel is declared, the principal names no mounted guest, or the grant carries no channel mask.</returns>
    public string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels) {
        if (reach is not { } mask) {
            return null;
        }

        var index = m_mounted.FindIndex(match: candidate => candidate.Principal.Equals(other: principal));

        if (index < 0) {
            return null;
        }

        var declared = default(ChannelDeclaredMask);

        foreach (var binding in m_mounted[index].Instance.ChannelBindings) {
            if (binding.Resolved) {
                declared = declared.With(ordinal: binding.Ordinal);
            }
        }

        var undeclared = mask.Without(declared: declared);

        if (undeclared.IsEmpty) {
            return null;
        }

        var names = new List<string>();

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if ((undeclared.Bits & (1UL << ordinal)) != 0UL) {
                names.Add(item: (channels.Name(ordinal: ordinal) ?? ordinal.ToString()));
            }
        }

        return string.Join(
            separator: ", ",
            values: names
        );
    }
    /// <summary>Live-mounts a new guest — the runtime half of the <c>world.addon.mount</c> lifecycle submission
    /// (<see cref="WorldServer"/>'s <c>AddonLifecycle</c> drain arm, reached only through the ordered domain: a live
    /// mount now lands at the same defined tick-boundary point a document mutation does, and rides the replay tape
    /// through <see cref="WorldSubmissionCodec"/>'s shared leaf — see <c>references/replay.md</c>). Mirrors the
    /// boot-time per-row mount sequence this type's constructor runs: lazily builds the Wasmtime host (an addon-free
    /// world still pays nothing until its first mount, live or boot), compiles under the declared hash pin, gates
    /// the Response-channel-required-for-Requests rule, discloses capabilities against the settled grant table, and
    /// admits under fuel — every gate the boot path runs, run here instead of duplicated by inspection: a live mount
    /// and a boot mount must refuse identically. <b>Mount never re-admits an existing guest</b> — a name already
    /// tracked in the mounted set refuses; <see cref="Reload"/> is the recovery/refresh verb for that case, kept
    /// deliberately distinct so an operator's "bring this up" and "restart what is already up" never collide on one
    /// name.</summary>
    /// <param name="name">The addon's identifying name — must be unique among mounted guests.</param>
    /// <param name="modulePath">The WASM module file path (machine-local, resolved exactly as a boot row's is).</param>
    /// <param name="hash">The required content-address integrity pin (<c>sha256-64/{16 hex}</c>) — an unpinned guest
    /// makes state depend on a file on disk, a determinism hole before a security one.</param>
    /// <param name="fuel">The per-tick fuel budget; <c>0</c> selects <see cref="AddonAbi.DefaultFuelPerTick"/>.</param>
    /// <param name="requests">The addon's manifest — what it asks for, as data; null/empty means it asks for
    /// nothing and therefore reaches nothing (deny-by-default holds regardless).</param>
    /// <returns>A human-readable status line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="modulePath"/>, or
    /// <paramref name="hash"/> is <see langword="null"/>.</exception>
    public string Mount(string name, string modulePath, string hash, ulong fuel, IReadOnlyList<WorldCapabilityRequest>? requests) {
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: modulePath);
        ArgumentNullException.ThrowIfNull(argument: hash);

        if (m_mounted.Exists(match: candidate => string.Equals(
            a: candidate.Instance.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ))) {
            return $"'{name}' is already a mounted guest — mount never re-admits an existing guest; use world.addon.reload {name} to refresh it or world.addon.unmount {name} first";
        }

        // Deferred host construction, exactly as the constructor's own boot loop: only pay the Wasmtime engine when
        // a guest actually mounts, live or boot.
        if (m_host is null) {
            var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);

            m_host = new AddonHost(
                channelResolver: new WorldAddonChannelResolver(channels: m_channels),
                engine: engine,
                loader: new WasmModuleLoader(
                    engine: engine,
                    assetSource: new FileSystemAssetSource()
                )
            );
        }

        var descriptor = new AddonDescriptor(
            Name: name,
            ModulePath: ResolvePath(modulePath: modulePath),
            ModuleHash: (string.IsNullOrEmpty(value: hash)
            ? null
            : hash),
            FuelPerTick: ((fuel == 0UL)
            ? null
            : (long)fuel),
            Enabled: true
        );

        m_host.Add(descriptor: in descriptor);

        if (!m_host.TryGet(
            instance: out var instance,
            name: name
        )) {
            // Unreachable — Add registers under exactly this name — kept as the constructor's own defensive line is.
            return $"'{name}' did not register under its own name after Add — not mounted";
        }

        if (instance.State != AddonState.Enabled) {
            return $"'{name}' faulted — {instance.Fault.Detail}";
        }

        if (
            (requests is { Count: > 0 }) &&
            (ResolveResponseChannel(instance: instance) < 0)
        ) {
            instance.Disable();

            return $"'{name}' refused — requests {requests.Count} capabilit{((requests.Count == 1)
                ? "y"
                : "ies")} but declares no Response channel, so no verdict or disclosure could ever reach it and no requested handle could ever be learned; not mounted";
        }

        ReportCapabilityDisclosure(
            name: name,
            requests: requests,
            grants: m_server.Grants
        );
        instance.Admit();

        if (instance.State != AddonState.Enabled) {
            return $"'{name}' faulted on admit — {instance.Fault.Detail}";
        }

        m_mounted.Add(item: new MountedAddon(
            instance: instance,
            requests: requests,
            populationCapacity: m_server.Population.Capacity
        ));
        m_receipts.Add(item: new WorldAddonReceipt(
            Name: instance.Name,
            Hash: instance.Hash.ToString(),
            Fuel: ((ulong)instance.FuelPerTick)
        ));
        ReportInertChannelDeclarations(
            bindings: instance.ChannelBindings,
            name: name
        );

        return $"mounted {name} ({instance.Hash}) fuel {instance.FuelPerTick} — grant it capabilities to drive/observe, e.g. world.grant addon:{name} drive body:<n> budget:<n>";
    }
    /// <summary>Reloads a mounted guest from its declared module path and re-runs the admit sequence —
    /// <see cref="AddonHost.Reload"/>'s own doc names the whole admit sequence as owed and, until this verb, unowned:
    /// re-reads and recompiles the module through <see cref="AddonHost.Reload"/>, then, when the fresh instance
    /// enabled, re-reports the capability disclosure against the same manifest and re-runs
    /// <see cref="AddonInstance.Admit"/> before the guest can tick again — closing the enabled-but-unadmitted gap
    /// <see cref="TickAddons"/> otherwise has to skip defensively. The receipt <see cref="Receipts"/> reports is
    /// updated only on a successful re-admit, so a failed reload leaves the last-known-good receipt in place rather
    /// than overwriting it with a fault. The tracked <c>MountedAddon</c> is replaced wholesale (its per-tick buffers
    /// are sized to the fresh instance's channel geometry), but its <c>TotalFuelConsumed</c> lifetime counter carries
    /// forward from the instance it replaces — a reload recovers the same guest, it does not start a new one.</summary>
    /// <remarks>A row that never reached the mounted set (a boot load fault) is out of this verb's
    /// reach: reviving one from nothing would re-run the whole boot mount sequence for a row this runtime never added
    /// to <see cref="Receipts"/>, a bigger surface than this pass owes. <b>Tape caveat</b>: a saved replay pins its
    /// mounted guests' receipts once, at record-start (<c>WorldReplaySnapshot</c>) — a live reload during an active
    /// recording changes what is actually running without the tape ever learning of it. This method does not detect or
    /// warn about that; the console verb that calls it does (see <c>WorldAddonCommandModule</c>).</remarks>
    /// <param name="name">The addon name.</param>
    /// <returns>A human-readable status line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public string Reload(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        if (m_host is null) {
            return $"'{name}' — this world enables no addon; there is no host to reload against";
        }

        var mountedIndex = m_mounted.FindIndex(match: candidate => string.Equals(
            a: candidate.Instance.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));

        if (mountedIndex < 0) {
            return $"'{name}' is not a mounted guest — a load-faulted row never joined the tick set and is out of this verb's reach";
        }

        var previous = m_mounted[mountedIndex];
        var status = m_host.Reload(name: name);

        if (!m_host.TryGet(
            instance: out var fresh,
            name: name
        )) {
            // Unreachable — AddonHost.Reload always re-registers under the same name — but this runtime must never
            // silently drop a mounted guest from its own tracked set on the strength of an assumption alone.
            return status;
        }

        if (ReferenceEquals(
            objA: fresh,
            objB: previous.Instance
        )) {
            // AddonHost.Reload's ONE no-swap outcome: a declared moduleHash pin refused the content change and left
            // the running instance completely untouched — still admitted from before, so there is nothing to
            // re-admit and calling Admit() again would throw (an instance may only be admitted once per store).
            return status;
        }

        var outcome = string.Empty;

        if (fresh.State == AddonState.Enabled) {
            // Mirrors the mount-time sequence exactly (see the constructor): report the disclosure against the SAME
            // manifest the row mounted with, then admit under the fresh store's own fuel budget.
            ReportCapabilityDisclosure(
                name: name,
                requests: previous.Requests,
                grants: m_server.Grants
            );
            fresh.Admit();

            if (fresh.State == AddonState.Enabled) {
                outcome = ", re-admitted";

                var receiptIndex = m_receipts.FindIndex(match: receipt => string.Equals(
                    a: receipt.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                ));

                if (receiptIndex >= 0) {
                    // The receipt is taken from the INSTANCE, never the row — see the constructor's own remark; a
                    // reload's receipt update follows the identical rule.
                    m_receipts[receiptIndex] = new WorldAddonReceipt(
                        Name: fresh.Name,
                        Hash: fresh.Hash.ToString(),
                        Fuel: ((ulong)fresh.FuelPerTick)
                    );
                }
            } else {
                outcome = $", but puck_init faulted on re-admit — {fresh.Fault.Detail}";
            }
        }

        // Replace the tracked guest wholesale rather than mutate it in place: a reload is a fresh instance with its
        // own channel geometry, so every per-tick buffer this type preallocates (Batch, Answers, Contributions, the
        // dispatch/disclosure scratch) must be re-sized and re-zeroed exactly as mount does, never patched onto
        // stale state sized for the PREVIOUS instance. The fuel/drop/event counters below carry forward because the
        // guest's NAME is what world.addons reports against, and a reload recovers that same guest, not a new one.
        m_mounted[mountedIndex] = new MountedAddon(
            instance: fresh,
            requests: previous.Requests,
            populationCapacity: m_server.Population.Capacity,
            memoryWatches: previous.MemoryWatches
        ) {
            CollisionEventsDelivered = previous.CollisionEventsDelivered,
            EventCellsDelivered = previous.EventCellsDelivered,
            EventGapCount = previous.EventGapCount,
            RouteEventsDelivered = previous.RouteEventsDelivered,
            TotalAnswersDropped = previous.TotalAnswersDropped,
            TotalFuelConsumed = previous.TotalFuelConsumed,
        };

        return (status + outcome);
    }
    /// <summary>Enables or disables a mounted guest. Disabling releases nothing, because a contribution is per-tick and
    /// expires on its own — a disabled guest simply stops producing one. Enabling re-instantiates the same
    /// <see cref="AddonInstance"/> in place
    /// (<see cref="AddonHost.SetEnabled"/>) and then re-runs the admit sequence this runtime's mount-time constructor
    /// runs — <see cref="AddonInstance.Enable"/> itself does not, by its own doc, which is the "Enable is uncalled,
    /// and nothing recovers a fault" gap this verb closes. A load fault (missing file, bad bytes, a hash-pin mismatch)
    /// constructed the instance with no module at all, so enabling it is permanently a no-op — reported honestly
    /// rather than claimed fixed; <see cref="Reload"/> (which re-reads the module from disk) is the recovery for
    /// that case.</summary>
    /// <param name="name">The addon name.</param>
    /// <param name="enabled">Whether to enable or disable it.</param>
    /// <returns>A human-readable status line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public string SetEnabled(string name, bool enabled) {
        ArgumentNullException.ThrowIfNull(argument: name);

        if (m_host is null) {
            return $"'{name}' — this world enables no addon; there is no host to {(enabled
                ? "enable"
                : "disable")}";
        }

        var mountedIndex = m_mounted.FindIndex(match: candidate => string.Equals(
            a: candidate.Instance.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));

        if (mountedIndex < 0) {
            return $"'{name}' is not a mounted guest — {(enabled
                ? "enable"
                : "disable")} has nothing to act on (a load-faulted row never joined the tick set)";
        }

        var addon = m_mounted[mountedIndex];
        var instance = addon.Instance;
        // Snapshot BEFORE SetEnabled mutates the instance in place, so a load fault can be told apart from every
        // other faulted state after re-instantiation has already overwritten it.
        var wasLoadFault = ((instance.State == AddonState.Faulted) && (instance.Fault.Kind is AddonFaultKind.HashMismatch or AddonFaultKind.BadExport));

        if (!m_host.SetEnabled(
            enabled: enabled,
            name: name
        )) {
            // Unreachable — the mounted-index check above already confirmed the host tracks this name.
            return $"unknown addon '{name}'";
        }

        if (!enabled) {
            // Nothing to release. A contribution is per-tick and expires on its own (the channel model's ruling), so a
            // disabled guest simply stops producing one — the host holds no per-channel state between ticks that could
            // outlive it, on this path or any other.
            return $"{name} disabled";
        }

        if (instance.State != AddonState.Enabled) {
            return (wasLoadFault
                ? $"{name} — a LOAD fault ({instance.Fault.Kind}) constructed no module to re-instantiate; enable cannot recover it — fix the module and run world.addon.reload {name} instead"
                : $"{name} — still faulted after re-instantiation: {instance.Fault.Detail}"
            );
        }

        ReportCapabilityDisclosure(
            name: name,
            requests: addon.Requests,
            grants: m_server.Grants
        );
        instance.Admit();

        return ((instance.State == AddonState.Enabled)
            ? $"{name} enabled and re-admitted"
            : $"{name} — puck_init faulted on re-admit: {instance.Fault.Detail}"
        );
    }
    /// <summary>Fully unmounts a guest by name — the runtime half of the <c>world.addon.unmount</c> lifecycle
    /// submission. Stronger than <see cref="SetEnabled"/>'s disable: the guest leaves <see cref="Receipts"/> and
    /// <see cref="MountedCount"/> entirely rather than staying tracked-but-skipped, so a later
    /// <c>world.addons</c> read no longer lists it at all. Disables the instance first (releases nothing beyond what
    /// disable already releases — a contribution is per-tick and expires on its own, per <see cref="SetEnabled"/>'s
    /// own remarks) purely so the underlying <see cref="AddonHost"/> instance stops being tickable before this type
    /// drops its own tracking of it; the host itself has no removal surface (<c>Puck.Scripting</c> keeps no such API)
    /// so its record persists there, inert, exactly as a disabled-forever guest's would.</summary>
    /// <param name="name">The addon name.</param>
    /// <returns>A human-readable status line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public string Unmount(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        var mountedIndex = m_mounted.FindIndex(match: candidate => string.Equals(
            a: candidate.Instance.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));

        if (mountedIndex < 0) {
            return $"'{name}' is not a mounted guest — unmount has nothing to act on";
        }

        var addon = m_mounted[mountedIndex];

        if (addon.Instance.State == AddonState.Enabled) {
            addon.Instance.Disable();
        }

        m_mounted.RemoveAt(index: mountedIndex);

        var receiptIndex = m_receipts.FindIndex(match: receipt => string.Equals(
            a: receipt.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));

        if (receiptIndex >= 0) {
            m_receipts.RemoveAt(index: receiptIndex);
        }

        return $"{name} unmounted";
    }
}
