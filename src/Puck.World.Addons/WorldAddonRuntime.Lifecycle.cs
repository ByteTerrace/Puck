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
/// figure that survives an unrelated reprepare pass reusing this guest untouched, never silently restarting while
/// the guest stays mounted under the same name. Saturates at <see cref="ulong.MaxValue"/> rather than wrapping.</param>
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

        // The projection can only change when the grant table is written, so an unchanged revision is a total answer —
        // this is what keeps the per-tick path free of the array ProjectSubjects allocates. (A guest whose store was
        // wiped arrives here as a FRESH MountedAddon — every change replaces the object wholesale — so its -1 defaults
        // always project the first time.) Deliberately NOT sufficient
        // on its own: the revision is process-global, so it moves for writes touching other principals entirely, and the
        // sequence compare below is what decides whether THIS addon's projection actually moved. An overflowed set is
        // gated on the same coordinate: only a grant-table write can shrink it (the budget is fixed per instance), so
        // re-projecting every tick while it stays oversized would be two array allocations per tick forever.
        if ((addon.DisclosedRevision == grants.Revision) || (addon.OverflowedAtRevision == grants.Revision)) {
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
    }
    private static bool IsMaterialized(MountedAddon addon, WorldCapability capability, GrantSubject subject) =>
        ((subject.Kind == GrantSubjectKind.Body) && IsRequested(
            addon: addon,
            capability: capability,
            subject: subject
        ));
    // Requesting is not receiving, but the reverse gap is the dangerous one: reporting only the intersection of
    // "requested" and "held" lets a capability the addon holds but never declared go completely unmentioned. So this
    // builds what the settled table says the addon's OWN principal HOLDS, right now, across every capability (via
    // WorldGrants.Held), and separately annotates the manifest's own wish list against that held set: granted
    // (requested and held), withheld (requested, not held), and unrequested (held, never requested). The unrequested
    // list is the INERT one: a hold outside the manifest materializes no handle (see IsRequested). Called for EVERY
    // row regardless of whether it declares Requests, because a document can grant an addon's principal something
    // directly without the row ever asking for it. Capability disclosure is narration: computed here, against the
    // settled table as of PREPARE, but the caller stages the returned text and prints it only once the plan that
    // produced it actually commits — a refused prepare must never have printed a mount claim that never became true.
    private static string? BuildCapabilityDisclosureNarration(string name, IReadOnlyList<WorldCapabilityRequest>? requests, IWorldGrantsView grants) {
        var principal = WorldPrincipal.Addon(name: name);
        var held = grants.Held(principal: principal);

        if (
            ((requests is null) || (requests.Count == 0)) &&
            (held.Count == 0)
        ) {
            return null;
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

        return ((((string)$"[world.addon: {name} requested {requestCount} capabilit{((requestCount == 1)
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
            : "(none)")}]");
    }
    // The mount-time report point 2 of the channel-name re-key requires: one line naming every declared channel
    // this guest's names did NOT resolve against the host table — report-and-inert, never a mount fault (see
    // AddonChannelBinding). Built once, right after the "mounted" line, over the handshake's own decoded bindings,
    // so it can never drift from what the guest actually declared; staged and printed only once the plan commits,
    // same as the capability disclosure above.
    private static string? BuildInertChannelDeclarationNarration(ReadOnlySpan<AddonChannelBinding> bindings, string name) {
        var inert = new List<string>();

        foreach (var binding in bindings) {
            if (!binding.Resolved) {
                inert.Add(item: binding.Name);
            }
        }

        return ((inert.Count > 0)
            ? $"[world.addon: {name} declares {inert.Count} channel name(s) the host table does not recognize — inert, never faults the mount: {string.Join(
                separator: ", ",
                values: inert
            )}]"
            : null);
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

    /// <summary>Builds the runtime over a boot world document and attaches it to the server that will pump it —
    /// through the SAME prepare/commit contract a live mutation uses. See <see cref="TryCreate"/> for the
    /// non-throwing form the composition root uses to turn a refusal into an ordinary boot refusal.</summary>
    /// <param name="definition">The boot world definition (its <c>addons</c> rows).</param>
    /// <param name="server">The authoritative server the guests act against and that drives the three pump points.</param>
    /// <returns>The attached, committed runtime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="server"/> is <see langword="null"/>.</exception>
    /// <exception cref="WorldAddonInstallRefusedException">An enabled row could not prepare — the world
    /// installation is refused rather than silently booting without it.</exception>
    public static WorldAddonRuntime Create(WorldDefinition definition, WorldServer server) {
        if (!TryCreate(
            definition: definition,
            server: server,
            runtime: out var runtime,
            reason: out var reason
        )) {
            throw new WorldAddonInstallRefusedException(message: $"world installation refused — addon {reason}");
        }

        return runtime!;
    }
    /// <summary>The non-throwing form of <see cref="Create"/> — the composition root's own boot-refusal seam: a
    /// failed prepare leaves nothing to dispose (<see cref="TryPrepare"/>'s own ownership guard already released
    /// everything it staged) and returns <see langword="false"/> with a reason, matching every sibling boot gate's
    /// <c>false</c> + printed-reason shape instead of an unhandled exception through DI resolution.</summary>
    /// <param name="definition">The boot world definition (its <c>addons</c> rows).</param>
    /// <param name="server">The authoritative server the guests act against and that drives the three pump points.</param>
    /// <param name="runtime">The attached, committed runtime, on success.</param>
    /// <param name="reason">Why the installation was refused, on failure.</param>
    /// <returns><see langword="true"/> when every enabled row prepared and the runtime attached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="server"/> is <see langword="null"/>.</exception>
    public static bool TryCreate(WorldDefinition definition, WorldServer server, out WorldAddonRuntime? runtime, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: server);

        var candidate = new WorldAddonRuntime(
            definition: definition,
            server: server
        );

        if (!candidate.TryPrepare(
            current: null,
            candidate: definition,
            plan: out var plan,
            reason: out reason
        )) {
            runtime = null;

            return false;
        }

        candidate.Commit(plan: plan!);
        server.AttachAddons(runtime: candidate);
        candidate.Finish(plan: plan!);
        runtime = candidate;
        reason = null;

        return true;
    }
    /// <inheritdoc/>
    public void Commit(IWorldAddonPreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        if (plan is not PreparedAddonInstall install) {
            throw new ArgumentException(
                message: $"this runtime received a plan of type '{plan.GetType().Name}' it did not prepare.",
                paramName: nameof(plan)
            );
        }

        // Pure reference adoption: five field assignments (the host reference, the host's own three-collection
        // registry swap, the mounted/receipt lists, and the channel table pair) — no I/O, allocation, compilation,
        // guest execution, or recoverable failure anywhere in this method. Narration and superseded disposal wait
        // for Finish, which the caller runs only after ITS OWN publication (document install, journal write) is
        // itself durable.
        m_host = install.Host;
        // Null exactly when no row has ever enabled (an addon-free world, or every row disabled) — the Wasmtime
        // host is lazily constructed, so there is nothing to adopt a registry into yet.
        m_host?.Adopt(
            byName: install.HostByName,
            descriptors: install.HostDescriptors,
            instances: install.HostInstances
        );
        m_mounted = install.Mounted;
        m_receipts = install.Receipts;
        m_channels = install.Channels;
        m_channelsSource = install.ChannelsSource;
        install.MarkCommitted();
    }
    /// <inheritdoc/>
    public void Finish(IWorldAddonPreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        if (plan is not PreparedAddonInstall install) {
            throw new ArgumentException(
                message: $"this runtime received a plan of type '{plan.GetType().Name}' it did not prepare.",
                paramName: nameof(plan)
            );
        }

        // Narration only NOW: a mount/disclosure line staged during a prepare that was never committed must never
        // print, because it would claim a mount that never became true. Each entry is invoked here, not before —
        // see PreparedAddonInstall.Narration — so a capability disclosure reads whatever grant table is live at
        // THIS instant, never the one that was live when TryPrepare ran.
        try {
            foreach (var line in install.Narration) {
                if (line() is { } text) {
                    Console.Error.WriteLine(value: text);
                }
            }
        }
        finally {
            // Replaced/removed guests are unreachable from the tick path the instant Commit's swap landed —
            // retirement is unconditional once publication landed, so it runs even when a narration thunk or the
            // console write throws; disposal happens here, after publication, never before.
            foreach (var superseded in install.Superseded) {
                superseded.Instance.Dispose();
            }

            // A channel-table change replaces the WHOLE host (see TryPrepare) rather than any individual guest, so
            // its disposal is wholesale too — never entered into Superseded, which would double-dispose every
            // instance the old host's own Dispose() already tears down.
            install.SupersededHost?.Dispose();
        }
    }
    /// <inheritdoc/>
    public bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: candidate);

        // A candidate whose channel declarations differ from what the currently-mounted set was compiled against
        // invalidates every row's reuse eligibility at once, even a row that compares structurally equal to its own
        // prior self — an unchanged manifest still resolves its declared names against a table that just moved.
        // Only a whole-document rebuild or undo can move this (no live mutation kind touches the channels section
        // for a running server's whole life — see AffectsAddons' own remarks), so the live-mutation path pays this
        // SequenceEqual's cost only on those rare calls, never on an ordinary UpsertAddon/RemoveAddon.
        var channelsChanged = !m_channelsSource.SequenceEqual(second: candidate.Channels);
        var stagedChannels = (channelsChanged
            ? WorldChannelTable.Compile(channels: candidate.Channels)
            : m_channels);

        // Keyed by name so a row this pass reuses (or supersedes) can be looked up in O(1); whatever remains once
        // every candidate row has been considered is exactly the set this pass replaces or drops entirely. Left
        // empty when channels changed: every row reprepares below regardless of its own structural equality, so
        // nothing here is ever a reuse candidate — see the loop's own remarks.
        var previouslyMounted = new Dictionary<string, MountedAddon>(comparer: StringComparer.Ordinal);

        if (
            (current is not null) &&
            !channelsChanged
        ) {
            foreach (var addon in m_mounted) {
                previouslyMounted[addon.Instance.Name] = addon;
            }
        }

        var mounted = new List<MountedAddon>();
        var receipts = new List<WorldAddonReceipt>();
        var freshlyPrepared = new List<(AddonDescriptor Descriptor, AddonInstance Instance)>();
        // Deferred, not pre-built: each entry resolves its own text lazily, on the SAME thread, when Finish actually
        // prints it — never at prepare time, when a caller (a rebuild) may still move the grant table this row's
        // own disclosure line depends on before its plan ever commits.
        var narration = new List<Func<string?>>();
        // Every row this pass visits and does not reuse (a changed row still enabled) is moved here explicitly; a
        // row this pass never visits at all (removed from the candidate, or authored disabled) is picked up after
        // the loop from whatever the dictionary above still holds — the two paths together are the full superseded
        // set, disposed by the caller only AFTER the new state publishes. Stays empty when channels changed — see
        // supersededHost below for that case's own disposal unit.
        var superseded = new List<MountedAddon>();
        // The complete replacement AddonHost registry this pass is building — every reused AND freshly-prepared
        // guest lands in all three, in mount order, so Commit's own AddonHost.Adopt call is pure reference adoption
        // over an already-complete registry (never a per-name read-modify-write, which is what left a superseded
        // name's stale entry behind before this cure).
        var hostByName = new Dictionary<string, AddonInstance>(comparer: StringComparer.Ordinal);
        var hostDescriptors = new Dictionary<string, AddonDescriptor>(comparer: StringComparer.Ordinal);
        var hostInstances = new List<AddonInstance>();
        var host = (channelsChanged
            ? null
            : m_host);
        var hostIsNew = false;
        // The host THIS pass is about to replace wholesale, only when channels moved and a host already existed —
        // disposed by Finish, never entered into `superseded` (its own Dispose() already tears down every instance
        // it still owns, so double-entering those same instances would double-dispose them).
        var supersededHost = (channelsChanged
            ? m_host
            : null);
        // Ownership guard: an exception anywhere in the loop below (an unexpected environment failure the guest
        // load path did not already convert into an ordinary fault) still reaches the finally, which releases
        // exactly what an explicit refusal already releases — every guest this pass compiled and the host it
        // itself constructed, never a host or guest the currently-committed runtime still owns.
        var owned = false;

        try {
            // Mount order is DOCUMENT order, and it stays the order every pump point walks: an addon's position in
            // the world file is the one thing an author controls about when its contribution lands relative to
            // another's.
            foreach (var row in candidate.Addons) {
                if (!row.Enabled) {
                    // Disabled rows don't compile until enabled — never staged, never counted against a fresh
                    // instance. A previously-mounted guest under this name (now disabled) is left in
                    // previouslyMounted, picked up by the leftover sweep below.
                    continue;
                }

                if (previouslyMounted.TryGetValue(
                    key: row.Name,
                    value: out var existing
                )) {
                    previouslyMounted.Remove(key: row.Name);

                    if (RowsStructurallyEqual(
                        a: existing.SourceRow,
                        b: row
                    )) {
                        // Unchanged row: reused whole, keeping its memory AND its fault state alike — runtime
                        // fault is not a preparation dependency, so a sticky-faulted guest stays faulted (never
                        // silently recovered by an unrelated mutation, and never restarted by resubmitting the
                        // identical row) until ITS OWN row's structural identity actually moves. No compile, no
                        // admit, no reference this pass touches at all.
                        mounted.Add(item: existing);
                        receipts.Add(item: new WorldAddonReceipt(
                            Name: existing.Instance.Name,
                            Hash: existing.Instance.Hash.ToString(),
                            Fuel: ((ulong)existing.Instance.FuelPerTick)
                        ));
                        hostByName[existing.Instance.Name] = existing.Instance;
                        hostDescriptors[existing.Instance.Name] = DescriptorFor(row: row);
                        hostInstances.Add(item: existing.Instance);

                        continue;
                    }

                    // Same name, but the row changed (or the existing guest is no longer healthy) — the currently-
                    // mounted guest is superseded by whatever this pass prepares below.
                    superseded.Add(item: existing);
                }

                // Deferred host construction: only pay the Wasmtime engine when a world enables an addon, live or
                // boot. hostIsNew tracks whether THIS pass is the one that built it, so a discarded plan disposes
                // exactly the host it created and none it merely reused.
                if (host is null) {
                    var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);

                    host = new AddonHost(
                        channelResolver: new WorldAddonChannelResolver(channels: stagedChannels),
                        engine: engine,
                        loader: new WasmModuleLoader(
                            engine: engine,
                            assetSource: new FileSystemAssetSource()
                        )
                    );
                    hostIsNew = true;
                }

                var descriptor = DescriptorFor(row: row);

                // Grown BEFORE the store exists so the Add below cannot allocate (and so cannot throw) — otherwise
                // the instant between Prepare returning and ownership registering could leak the store.
                freshlyPrepared.EnsureCapacity(capacity: (freshlyPrepared.Count + 1));

                var instance = host.Prepare(descriptor: in descriptor);

                // Ownership transfers to this pass the instant the store exists — before any further gate below is
                // even reached — so the finally's DiscardPrepared releases it on EVERY non-commit exit from here on,
                // including an exception thrown by a gate below, and none of those gates needs its own dispose call.
                freshlyPrepared.Add(item: (descriptor, instance));

                if (instance.State != AddonState.Enabled) {
                    plan = null;
                    reason = $"'{row.Name}' could not prepare — {instance.Fault.Detail}";

                    return false;
                }

                // A manifest that requests a capability but declares no Response channel can never receive a
                // verdict, disclosure, or minted handle — every such answer routes through the Response channel —
                // so refuse rather than admit a guest permanently incapable of using anything it might be granted.
                // A row with no requests at all is unaffected.
                if (
                    (row.Requests is { Count: > 0 }) &&
                    (ResolveResponseChannel(instance: instance) < 0)
                ) {
                    plan = null;
                    reason = $"'{row.Name}' refused — requests {row.Requests.Count} capabilit{((row.Requests.Count == 1)
                        ? "y"
                        : "ies")} but declares no Response channel, so no verdict or disclosure could ever reach it and no requested handle could ever be learned";

                    return false;
                }

                // The capability disclosure — the whole point (the capability-channels campaign's "a manifest
                // requests; a grant approves a subset; nothing is implicit"). NOT built now: a whole-document
                // rebuild can move the grant table itself, AFTER this prepare pass but BEFORE Finish ever prints
                // anything (see WorldServer.ApplyRebuild), so a string built against the table as it stands here
                // could already be stale by the time it is safe to print. Deferred to a thunk this row's own
                // narration entry evaluates lazily, against WHATEVER m_server.Grants IS at the moment Finish
                // actually invokes it — the live, settled table for a plain mutation/undo (nothing moves it
                // between here and Finish on those paths), the candidate's own newly-installed table for a rebuild.
                narration.Add(item: () => BuildCapabilityDisclosureNarration(
                    grants: m_server.Grants,
                    name: row.Name,
                    requests: row.Requests
                ));

                // Admission runs the guest's optional puck_init under the fuel budget, against the staged guest's
                // own private memory only — no host imports, contributions, handles, or output escape this early.
                instance.Admit();

                if (instance.State != AddonState.Enabled) {
                    plan = null;
                    reason = $"'{row.Name}' faulted on puck_init — {instance.Fault.Detail}";

                    return false;
                }

                mounted.Add(item: new MountedAddon(
                    instanceId: ++m_nextInstanceId,
                    instance: instance,
                    sourceRow: row,
                    requests: row.Requests,
                    populationCapacity: m_server.Population.Capacity,
                    memoryWatches: row.MemoryWatches
                ));
                // The receipt is taken from the INSTANCE, never from the row: the row is the author's pin, the
                // instance is what prepared under it.
                receipts.Add(item: new WorldAddonReceipt(
                    Name: instance.Name,
                    Hash: instance.Hash.ToString(),
                    Fuel: ((ulong)instance.FuelPerTick)
                ));
                hostByName[instance.Name] = instance;
                hostDescriptors[instance.Name] = descriptor;
                hostInstances.Add(item: instance);

                var mountedLine = $"[world.addon: mounted {row.Name} ({instance.Hash}) fuel {instance.FuelPerTick} — grant it a body to drive, e.g. world.grant addon:{row.Name} drive body:1 budget:60 (and observe body:1 budget:60 to let it read its pose — both are untrusted-principal dispatch budgets and are required)]";

                narration.Add(item: () => mountedLine);

                var inertLine = BuildInertChannelDeclarationNarration(
                    bindings: instance.ChannelBindings,
                    name: row.Name
                );

                narration.Add(item: () => inertLine);
            }

            // The leftover sweep: whatever the dictionary still holds is a row this pass never visited at all —
            // removed from the candidate entirely, or authored disabled — joining whatever was explicitly
            // superseded above.
            superseded.AddRange(collection: previouslyMounted.Values);

            plan = new PreparedAddonInstall(
                channels: stagedChannels,
                channelsSource: candidate.Channels,
                freshlyPrepared: freshlyPrepared,
                host: host,
                hostByName: hostByName,
                hostDescriptors: hostDescriptors,
                hostInstances: hostInstances,
                hostIsNew: hostIsNew,
                mounted: mounted,
                narration: narration,
                receipts: receipts,
                superseded: superseded,
                supersededHost: supersededHost
            );
            reason = null;
            owned = true;

            return true;
        } finally {
            if (!owned) {
                DiscardPrepared(
                    freshlyPrepared: freshlyPrepared,
                    host: host,
                    hostIsNew: hostIsNew
                );
            }
        }
    }
    // Reconstructs the neutral load descriptor a row prepares under — identical for a freshly-prepared row and a
    // reused one (structural equality already proved every field, including Name/ModulePath/Hash/Fuel, matches),
    // so the SAME helper serves both branches of TryPrepare's loop without a live AddonHost read.
    private static AddonDescriptor DescriptorFor(WorldAddonRow row) => new(
        Name: row.Name,
        ModulePath: ResolvePath(modulePath: row.ModulePath),
        // The document gate requires a hash; the empty→null translation stays defensive, because the neutral
        // descriptor is reachable from hosts that have no document gate in front of them.
        ModuleHash: (string.IsNullOrEmpty(value: row.Hash)
        ? null
        : row.Hash),
        FuelPerTick: ((row.Fuel == 0UL)
        ? null
        : (long)row.Fuel),
        Enabled: true
    );
    // Explicit structural row equality — never generated record equality, whose Requests/MemoryWatches fields are
    // interface-typed collections that default to reference equality. Runtime fault state deliberately does NOT
    // participate: an unchanged faulted guest stays faulted until ITS OWN row's structural identity changes, never
    // because a DIFFERENT row's mutation happened to run this comparison.
    private static bool RowsStructurallyEqual(WorldAddonRow a, WorldAddonRow b) =>
        (ReferenceEquals(
            objA: a,
            objB: b
        ) ||
        ((a.Name == b.Name) &&
        (a.ModulePath == b.ModulePath) &&
        (a.Hash == b.Hash) &&
        (a.Fuel == b.Fuel) &&
        (a.Enabled == b.Enabled) &&
        (a.Revision == b.Revision) &&
        CapabilityRequestsEqual(
            a: a.Requests,
            b: b.Requests
        ) &&
        MemoryWatchesEqual(
            a: a.MemoryWatches,
            b: b.MemoryWatches
        )));
    private static bool CapabilityRequestsEqual(IReadOnlyList<WorldCapabilityRequest>? a, IReadOnlyList<WorldCapabilityRequest>? b) {
        if (ReferenceEquals(
            objA: a,
            objB: b
        )) {
            return true;
        }

        var aCount = (a?.Count ?? 0);
        var bCount = (b?.Count ?? 0);

        if (aCount != bCount) {
            return false;
        }

        for (var index = 0; (index < aCount); index++) {
            if (!a![index].Equals(other: b![index])) {
                return false;
            }
        }

        return true;
    }
    private static bool MemoryWatchesEqual(IReadOnlyList<WorldAddonMemoryWatch>? a, IReadOnlyList<WorldAddonMemoryWatch>? b) {
        if (ReferenceEquals(
            objA: a,
            objB: b
        )) {
            return true;
        }

        var aCount = (a?.Count ?? 0);
        var bCount = (b?.Count ?? 0);

        if (aCount != bCount) {
            return false;
        }

        for (var index = 0; (index < aCount); index++) {
            if (!a![index].Equals(other: b![index])) {
                return false;
            }
        }

        return true;
    }
    // Shared discard exit for TryPrepare's ownership guard: disposes every instance this pass has prepared but not
    // yet returned as a plan — every instance the loop constructs joins this list the instant it exists, before any
    // gate that might refuse it or throw — and the host this pass itself constructed, if any, never a host this
    // pass merely reused, which the currently-committed runtime still owns.
    private static void DiscardPrepared(List<(AddonDescriptor Descriptor, AddonInstance Instance)> freshlyPrepared, AddonHost? host, bool hostIsNew) {
        foreach (var (_, instance) in freshlyPrepared) {
            instance.Dispose();
        }

        if (hostIsNew) {
            host?.Dispose();
        }
    }
    /// <summary>The prepare/commit transaction handle this runtime produces from <see cref="TryPrepare"/> and
    /// consumes in <see cref="Commit"/>/<see cref="Finish"/> — every collection a successful prepare pass built,
    /// plus what disposing an uncommitted plan must release. Linear ownership: exactly one of <see cref="Commit"/>
    /// or <see cref="Dispose"/> runs against a given instance, never both and never neither.</summary>
    private sealed class PreparedAddonInstall(WorldChannelTable channels, IReadOnlyList<WorldChannel> channelsSource, List<(AddonDescriptor Descriptor, AddonInstance Instance)> freshlyPrepared, AddonHost? host, Dictionary<string, AddonInstance> hostByName, Dictionary<string, AddonDescriptor> hostDescriptors, List<AddonInstance> hostInstances, bool hostIsNew, List<MountedAddon> mounted, List<Func<string?>> narration, List<WorldAddonReceipt> receipts, List<MountedAddon> superseded, AddonHost? supersededHost) : IWorldAddonPreparedPlan {
        private bool m_committed;
        private bool m_disposed;

        /// <summary>Gets the channel table <see cref="Commit"/> adopts — the runtime's existing table when
        /// unchanged, or a freshly compiled one over the candidate's own declarations when the channels section
        /// moved.</summary>
        public WorldChannelTable Channels { get; } = channels;
        /// <summary>Gets the exact declaration list <see cref="Channels"/> was compiled from — what the NEXT
        /// prepare pass compares a future candidate's own channels against.</summary>
        public IReadOnlyList<WorldChannel> ChannelsSource { get; } = channelsSource;
        /// <summary>Gets the guests this pass compiled and admitted but has not yet registered — already folded
        /// into <see cref="HostByName"/>/<see cref="HostDescriptors"/>/<see cref="HostInstances"/> for
        /// <see cref="Commit"/>'s own adoption; this list exists only so an uncommitted plan's <see cref="Dispose"/>
        /// knows exactly which instances it, rather than the currently-committed runtime, owns.</summary>
        public List<(AddonDescriptor Descriptor, AddonInstance Instance)> FreshlyPrepared { get; } = freshlyPrepared;
        /// <summary>Gets the host this plan commits into — the runtime's existing host when this pass reused one, or
        /// the host this pass itself constructed when <see cref="HostIsNew"/>.</summary>
        public AddonHost? Host { get; } = host;
        /// <summary>Gets the complete replacement name-to-instance map <see cref="Puck.Scripting.AddonHost.Adopt"/>
        /// installs by reference — every mounted guest, reused and fresh alike, and nothing a superseded name left
        /// behind.</summary>
        public Dictionary<string, AddonInstance> HostByName { get; } = hostByName;
        /// <summary>Gets the complete replacement name-to-descriptor map, parallel to <see cref="HostByName"/>.</summary>
        public Dictionary<string, AddonDescriptor> HostDescriptors { get; } = hostDescriptors;
        /// <summary>Gets the complete replacement instance list, in mount order, parallel to <see cref="Mounted"/>.</summary>
        public List<AddonInstance> HostInstances { get; } = hostInstances;
        /// <summary>Gets a value indicating whether this pass constructed <see cref="Host"/> — the difference
        /// between a discard that must dispose it and one that must leave a shared, already-committed host alone.</summary>
        public bool HostIsNew { get; } = hostIsNew;
        /// <summary>Gets the full ordered mounted set this plan installs — reused guests and freshly-prepared ones
        /// interleaved in candidate document order.</summary>
        public List<MountedAddon> Mounted { get; } = mounted;
        /// <inheritdoc/>
        public int MountedCount => Mounted.Count;
        /// <summary>Gets the deferred stderr line producers (mount confirmations, capability disclosures,
        /// inert-channel reports), evaluated and printed by <see cref="WorldAddonRuntime.Finish"/> only, never by
        /// this type. A capability disclosure entry re-reads the grant table at the moment it is invoked, so a
        /// caller that moves the table between this plan's own prepare pass and its eventual <c>Finish</c> call
        /// (a whole-document rebuild replaying its candidate's own <c>Grants</c> section) still prints a line that
        /// matches what actually installed; an entry that resolves to <see langword="null"/> prints nothing.</summary>
        public List<Func<string?>> Narration { get; } = narration;
        /// <summary>Gets the receipts parallel to <see cref="Mounted"/>, in the same order.</summary>
        public List<WorldAddonReceipt> Receipts { get; } = receipts;
        /// <summary>Gets the guests this plan replaces or drops by name (the channel table unchanged) — disposed by
        /// <see cref="WorldAddonRuntime.Finish"/> only after the new set is published, never by this type (they are
        /// still the live, committed guests until then).</summary>
        public List<MountedAddon> Superseded { get; } = superseded;
        /// <summary>Gets the host this plan replaces WHOLESALE (the channel table moved), or <see langword="null"/>
        /// when <see cref="Host"/> is reused or freshly built with nothing to replace. Disposed by
        /// <see cref="WorldAddonRuntime.Finish"/>, never entered into <see cref="Superseded"/>.</summary>
        public AddonHost? SupersededHost { get; } = supersededHost;

        /// <summary>Disposes every freshly-prepared guest store this plan was never committed with, and the host this
        /// plan itself constructed, if any. A no-op once <see cref="MarkCommitted"/> has run, or on a second call.</summary>
        public void Dispose() {
            if (
                m_disposed ||
                m_committed
            ) {
                m_disposed = true;

                return;
            }

            m_disposed = true;

            foreach (var (_, instance) in FreshlyPrepared) {
                instance.Dispose();
            }

            if (HostIsNew) {
                Host?.Dispose();
            }
        }
        /// <summary>Marks this plan committed, so a later <see cref="Dispose"/> call (a defensive
        /// try/finally at the call site) becomes a no-op rather than tearing down state <see cref="Commit"/> just
        /// published.</summary>
        public void MarkCommitted() => m_committed = true;
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
}
