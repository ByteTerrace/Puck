using System.Text;
using Puck.Assets;
using Puck.Maths;
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
/// <summary>
/// The server-side addon runtime — the one host every guest mounts into and the one caller that drives
/// it. It owns the <see cref="AddonHost"/> (and therefore the Wasmtime engine), mounts every enabled row of the boot
/// world document, and is pumped by <see cref="WorldServer.Step"/> at three pinned points inside one tick:
/// <see cref="TickAddons"/> at the very top (write the guest's input ring, run <c>puck_on_tick</c>, decode and
/// vocabulary-validate through the Simulation adapter), <see cref="ApplyContributions"/> after the intent drain (resolve
/// Drive handles, check authority, submit the folded intent), and <see cref="ResolveReads"/> after the population
/// advances (disclosures, world events, asks, and pose queries answered against the post-step authoritative state, staged for the next
/// tick's batch).
/// </summary>
/// <remarks>
/// <para><b>A guest reaches the world through typed channels only.</b> There is no roster slot, no
/// <c>InputDeviceId</c>, and no binding page anywhere on this path: an input-channel act names a Drive handle and a
/// declared channel name, resolved once at handshake against the world document's channel table through
/// <see cref="WorldAddonChannelResolver"/>, and the server writes each validated record into
/// <see cref="PlayerIntent"/>'s channel vector at that same resolved ordinal (<see cref="Fold"/>) — the guest's own
/// declaration index never reaches this type. Unlike the source vocabulary this replaces, an
/// unresolvable declared name is never a whole-mount refusal — it is report-and-inert (one line at mount, then a
/// per-act <see cref="AddonVerdict.AttenuatedToEmpty"/> if the guest ever acts through it) — so nothing a guest
/// may declare can fault the mount, but nothing unrecognized silently does anything either.</para>
/// <para><b>Authority materializes at requests ∧ grants.</b> A handle is minted for a (capability, subject) pair only
/// when the row's manifest (<see cref="WorldAddonRow.Requests"/>) asked for it and the settled table grants it. A hold
/// the manifest never named is real in the table and inert for the guest — it is disclosed to the operator at mount as
/// "holds beyond its manifest" and is never handed across the ABI, so an authority nobody reviewed cannot arrive by
/// surprise. A guest asking for an unrequested pair reads <see cref="AddonVerdict.AttenuatedToEmpty"/>: the attenuation
/// is AND, so asking for more than the manifest declared yields less, never more.</para>
/// <para><b>The principal comes from the mount, never from a record.</b> It is captured here beside the instance and no
/// cell carries a field for it, so a guest has no way to name one. Authority is checked at application — every act
/// resolves its handle against the live table and every submission runs the same
/// <see cref="WorldServer.ApplyIntentSubmission"/> a seat's submission runs — never at decode, which would re-open the
/// revoked-between-decode-and-apply window the handle generation exists to close.</para>
/// <para><b>Refusal is data.</b> A refused record answers with its verdict on the guest's Response channel; an allowed
/// act produces nothing, because silence is the positive signal. A guest declaring no Response channel can be handed no
/// answers at all, which is reported loudly once rather than dropped silently.</para>
/// <para>Single-threaded on the host tick, like every simulation type here. Per-tick state is preallocated at mount:
/// the batch, pending, answer, and contribution buffers are fixed arrays with counts, so a tick allocates nothing.</para>
/// </remarks>
public sealed class WorldAddonRuntime : IWorldAddonHost {
    // The ActBody sentinel for "this act resolved no body" — a stale handle, or a slot naming something other than a
    // body. Such an act was already answered when it was folded and takes no part in the second pass.
    private const int NoBody = -1;

    // The world's own channel table, compiled once at construction — every guest's declared names resolve against
    // it, at boot AND at a live Mount, so an act's ordinal IS a PlayerIntent ordinal on either path and the fold
    // never needs a second mapping of its own.
    private readonly WorldChannelTable m_channels;
    private readonly List<MountedAddon> m_mounted = [];
    // Recorded AT MOUNT, in mount order, for every guest that actually reached the tickable set — a row that faulted
    // or failed to register produces no receipt, which is what makes a missing receipt the honest report of a mount
    // that did not happen.
    private readonly List<WorldAddonReceipt> m_receipts = [];
    private readonly WorldServer m_server;

    private bool m_disposed;
    private AddonHost? m_host;
    // The addon mutation seam's GLOBAL per-tick byte meter — shared across every mounted addon, reset at the top of
    // each TickAddons call. AddonAbi.MaxMutationBytesPerTickAllAddons bounds host-side JSON decode work per tick
    // regardless of how many guests are mounted or how their individual per-addon budgets are set.
    private int m_mutationBytesThisTickAllAddons;

    private WorldAddonRuntime(WorldDefinition definition, WorldServer server) {
        m_server = server;

        // The world's own channel table is what every guest's declared names resolve against — compiled once here
        // and handed to the host as the resolver, so an act's ordinal IS a PlayerIntent ordinal and the fold needs
        // no second mapping of its own.
        var channels = WorldChannelTable.Compile(channels: definition.Channels);

        m_channels = channels;

        // Mount order is DOCUMENT order, and it stays the order every pump point walks: an addon's position in the
        // world file is the one thing an author controls about when its contribution lands relative to another's.
        foreach (var row in definition.Addons) {
            if (!row.Enabled) {
                continue;
            }

            // Deferred host construction: only pay the Wasmtime engine when a world enables an addon. The host owns
            // the engine and the loader shares it (the loader compiles modules the host instantiates); the host
            // disposes the engine on Dispose.
            if (m_host is null) {
                var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);

                m_host = new AddonHost(
                    channelResolver: new WorldAddonChannelResolver(channels: channels),
                    engine: engine,
                    loader: new WasmModuleLoader(
                        engine: engine,
                        assetSource: new FileSystemAssetSource()
                    )
                );
            }

            var descriptor = new AddonDescriptor(
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

            m_host.Add(descriptor: in descriptor);

            if (!m_host.TryGet(
                instance: out var instance,
                name: row.Name
            )) {
                // Unreachable — Add registers under exactly this name — but the only mount outcome with no line
                // would otherwise be this one, and a silent non-mount is the disease this surface exists to kill.
                Console.Error.WriteLine(value: $"[world.addon: {row.Name} did not register under its own name after Add — not mounted]");

                continue;
            }

            if (instance.State != AddonState.Enabled) {
                Console.Error.WriteLine(value: $"[world.addon: {row.Name} faulted — {instance.Fault.Detail}]");

                continue;
            }

            // A manifest that requests a capability but declares no Response channel can never receive a verdict,
            // disclosure, or minted handle — every such answer routes through the Response channel (StageBatch
            // below) — so refuse the mount rather than admit a guest permanently incapable of using anything it
            // might be granted. A row with no requests at all is unaffected.
            if (
                (row.Requests is { Count: > 0 }) &&
                (ResolveResponseChannel(instance: instance) < 0)
            ) {
                instance.Disable();
                Console.Error.WriteLine(value: $"[world.addon: {row.Name} refused — requests {row.Requests.Count} capabilit{((row.Requests.Count == 1)
                    ? "y"
                    : "ies")} but declares no Response channel, so no verdict or disclosure could ever reach it and no requested handle could ever be learned; not mounted]");

                continue;
            }

            // The capability disclosure — the whole point (the capability-channels campaign's "a manifest requests; a
            // grant approves a subset; nothing is implicit"): reports what this addon's principal actually HOLDS in the
            // SETTLED table (the permissive seed — empty for an addon — plus any WorldDefinition.Grants row the
            // server's constructor already applied), matched against what the row's manifest asked for. Runs BEFORE
            // Admit, so the report describes the table the guest's own puck_init will act against.
            ReportCapabilityDisclosure(
                name: row.Name,
                requests: row.Requests,
                grants: server.Grants
            );

            // Admission runs the guest's optional puck_init under the fuel budget, after every mount gate is in place.
            // A trap here faults the instance exactly like a tick trap.
            instance.Admit();

            if (instance.State != AddonState.Enabled) {
                Console.Error.WriteLine(value: $"[world.addon: {row.Name} faulted — {instance.Fault.Detail}]");

                continue;
            }

            m_mounted.Add(item: new MountedAddon(
                instance: instance,
                requests: row.Requests,
                populationCapacity: m_server.Population.Capacity,
                memoryWatches: row.MemoryWatches
            ));
            // The receipt is taken from the INSTANCE, never from the row: the row is the author's pin, the instance is
            // what mounted under it.
            m_receipts.Add(item: new WorldAddonReceipt(
                Name: instance.Name,
                Hash: instance.Hash.ToString(),
                Fuel: ((ulong)instance.FuelPerTick)
            ));
            Console.Error.WriteLine(value: $"[world.addon: mounted {row.Name} ({instance.Hash}) fuel {instance.FuelPerTick} — grant it a body to drive, e.g. world.grant addon:{row.Name} drive body:1 budget:60 (and observe body:1 budget:60 to let it read its pose — both are untrusted-principal dispatch budgets and are required)]");
            ReportInertChannelDeclarations(
                bindings: instance.ChannelBindings,
                name: row.Name
            );
        }
    }

    /// <summary>Gets a value indicating whether any mounted addon has ever had an admitted execution attempted — the OR of every mounted
    /// entry's <see cref="MountedAddon.HasEverPumped"/>. See <see cref="WorldServer.AnyAddonEverPumped"/>, which
    /// forwards this.</summary>
    public bool AnyEverPumped => m_mounted.Exists(match: static addon => addon.HasEverPumped);
    /// <summary>Gets the number of guests that mounted and were admitted — the count
    /// <see cref="WorldServer.AttachAddons"/> sizes its per-tick contention tracking against.</summary>
    public int MountedCount => m_mounted.Count;
    /// <summary>Gets the mounted set as <see cref="WorldAddonReceipt"/>s, in mount order — the recorded-at-mount facts a
    /// tape pins its guests against. Populated at mount and never written afterwards, so a reader always sees the whole
    /// settled set.</summary>
    public IReadOnlyList<WorldAddonReceipt> Receipts => m_receipts;

    // This tick's contribution restricted to the COMPOSITION ordinals — the held-device image's own convention (see
    // WorldBody.SetHeldChannels and SeatController.HeldChannels): a movement role rides the submitted intent and is
    // ignored on this path, so publishing one here would be a value nothing reads. Stack-only: ChannelValues is an
    // InlineArray, so this allocates nothing.
    private static PlayerIntent CompositionChannels(ChannelValues values, WorldChannelTable channels) {
        var composition = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ++ordinal) {
            if (!channels.IsRole(ordinal: ordinal)) {
                composition[ordinal] = values[ordinal];
            }
        }

        return new PlayerIntent(Channels: composition);
    }
    // Locate (or open) the accumulator for a body, keeping the array sorted ascending by body index. Bounded by the
    // guest's output capacity, so the insertion shift is over at most that many entries and never allocates.
    private static int Contribution(MountedAddon addon, int bodyIndex) {
        var contributions = addon.Contributions;
        var count = addon.ContributionCount;
        var slot = 0;

        while (
            (slot < count) &&
            (contributions[slot].BodyIndex < bodyIndex)
        ) {
            ++slot;
        }

        if (
            (slot < count) &&
            (contributions[slot].BodyIndex == bodyIndex)
        ) {
            return slot;
        }

        for (var index = count; (index > slot); --index) {
            contributions[index] = contributions[(index - 1)];
        }

        contributions[slot] = new BodyContribution(bodyIndex: bodyIndex);
        addon.ContributionCount = (count + 1);

        return slot;
    }
    // Mirrors StageContribution's own trusted-addon acceptance gate: a document-mounted addon is trusted-by-authorship
    // (added outside the pool), but still gated by its OWN declared Reach (WorldGrants.TryGetChannelReach) — there is
    // no occupying-seat ceiling to consult, unlike a genuinely untrusted (pooled) contributor. Recomputed here rather
    // than read back from the fold, because ApplyIntentSubmission's verdict answers Drive authority only.
    private bool ContributionAccepted(int bodyIndex, WorldPrincipal principal, in ChannelValues values) {
        if (!m_server.Grants.TryGetChannelReach(
            principal: principal,
            subject: GrantSubject.Body(index: bodyIndex),
            mask: out var reach
        )) {
            return false;
        }

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (
                (values[ordinal].Value != 0L) &&
                reach.Contains(ordinal: ordinal)
            ) {
                return true;
            }
        }

        return false;
    }
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
    // The world's four collected event families, filtered per-addon, plus this guest's own machine-memory watches
    // (addon-scoped, materializing through the same requested ∧ granted rule — see WorldAddonRow.MemoryWatches'
    // own doc). OVERFLOW DOCTRINE: edges arrive already in PINNED SIM ORDER (WorldEventFeed.Collect's own order —
    // seats, regions, collisions, routes — then this guest's own watches in declaration order); once the ring runs
    // out of room the REST of this tick's qualifying edges drop, NEWEST first by construction, so the guest always
    // sees a consistent ORDERED PREFIX, never a mid-stream hole. Each qualifying edge also charges the first gate row
    // with remaining EventBudget; a row whose allowance is spent drops the edge through the same gap path. Every
    // drop increments the per-mount, saturating, LIFETIME EventGapCount; whenever that count has moved since the
    // last batch that reported it, ONE EventGap summary cell is appended — a nonzero count is the guest's "resync by
    // polling the level state you already observe" signal, never a request to replay the missed edges.
    private void EmitEvents(MountedAddon addon, int budget) {
        var edges = m_server.Events.Edges;
        var dropped = 0;

        for (var index = 0; (index < edges.Count); ++index) {
            var edge = edges[index];
            var verb = MapEventVerb(family: edge.Family);

            if (verb < 0) {
                // Unreachable: every WorldEventFamily maps to a verb. Defensive rather than throwing — a guest's
                // batch must never fault over a host-side bug in an unrelated family.
                continue;
            }

            var gateStatus = SelectEventGate(
                addon: addon,
                gateA: edge.GateA,
                gateB: edge.GateB,
                chargedSubject: out var chargedSubject
            );

            if (gateStatus == EventGateStatus.None) {
                continue;
            }

            if (
                (gateStatus == EventGateStatus.Exhausted) ||
                ((budget - addon.PendingCount) <= 0)
            ) {
                ++dropped;

                continue;
            }

            addon.EventCounts[chargedSubject] = (addon.EventCounts.GetValueOrDefault(key: chargedSubject) + 1);

            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: 0,
                HandleGeneration: 0,
                Verdict: AddonVerdict.None,
                Verb: ((byte)verb),
                A: edge.A,
                B: edge.B
            );
            addon.EventCellsDelivered = ((addon.EventCellsDelivered == ulong.MaxValue)
                ? ulong.MaxValue
                : (addon.EventCellsDelivered + 1UL)
            );

            if (edge.Family is WorldEventFamily.CollisionBegin or WorldEventFamily.CollisionEnd) {
                addon.CollisionEventsDelivered = ((addon.CollisionEventsDelivered == ulong.MaxValue)
                    ? ulong.MaxValue
                    : (addon.CollisionEventsDelivered + 1UL)
                );
            } else if (edge.Family is WorldEventFamily.RouteEngaged or WorldEventFamily.RouteDisengaged) {
                addon.RouteEventsDelivered = ((addon.RouteEventsDelivered == ulong.MaxValue)
                    ? ulong.MaxValue
                    : (addon.RouteEventsDelivered + 1UL)
                );
            }
        }

        dropped += EmitMemoryWatchEvents(
            addon: addon,
            budget: budget
        );

        if (dropped > 0) {
            addon.EventGapCount = ((addon.EventGapCount > (ulong.MaxValue - ((ulong)dropped)))
                ? ulong.MaxValue
                : (addon.EventGapCount + ((ulong)dropped))
            );
        }

        if (
            (addon.EventGapCount != addon.LastReportedEventGap) &&
            ((budget - addon.PendingCount) > 0)
        ) {
            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: 0,
                HandleGeneration: 0,
                Verdict: AddonVerdict.None,
                Verb: ((byte)AddonAbi.ObservationVerbs.EventGap),
                A: ((long)addon.EventGapCount),
                B: 0L
            );
            addon.LastReportedEventGap = addon.EventGapCount;
        }
        // else: no room this tick even for the summary cell — the count is saturating and monotonic, so the next
        // batch with any room reports the up-to-date total; nothing is lost, only delayed.
    }
    // Machine-memory watches: addon-scoped (each row declares its own), materializing through Observe/screen:<n>
    // WITH an event budget — the same requested ∧ granted rule every other capability here enforces.
    // m_server.Machines is always present (machines boot and step server-side in every boot shape), so this family
    // publishes in a headless host too. Returns the number of changed-value edges dropped for event-budget or
    // ring-capacity reasons, folded into the SAME gap counter EmitEvents tracks — one gap surface per mount, not
    // one per family.
    private int EmitMemoryWatchEvents(MountedAddon addon, int budget) {
        if (addon.MemoryWatches is not { Count: > 0 } watches) {
            return 0;
        }


        var dropped = 0;

        for (var index = 0; (index < watches.Count); ++index) {
            var watch = watches[index];

            var subject = GrantSubject.Screen(index: watch.Screen);
            var gateStatus = SelectEventGate(
                addon: addon,
                chargedSubject: out var chargedSubject,
                gateA: subject,
                gateB: null
            );

            if (gateStatus == EventGateStatus.None) {
                continue;
            }

            if (!TryReadWatch(
                peek: m_server.Machines,
                watch: watch,
                value: out var value
            )) {
                continue;
            }

            ref var state = ref addon.MemoryWatchState![index];

            if (!state.Initialized) {
                // The first successful peek establishes the baseline WITHOUT emitting — there is no "previous"
                // value to have changed from.
                state = new MountedAddon.WatchState(
                    Initialized: true,
                    Value: value
                );

                continue;
            }

            if (state.Value == value) {
                continue;
            }

            state = new MountedAddon.WatchState(
                Initialized: true,
                Value: value
            );

            if (
                (gateStatus == EventGateStatus.Exhausted) ||
                ((budget - addon.PendingCount) <= 0)
            ) {
                ++dropped;

                continue;
            }

            addon.EventCounts[chargedSubject] = (addon.EventCounts.GetValueOrDefault(key: chargedSubject) + 1);

            addon.Pending[addon.PendingCount++] = new AddonInCell(
                Kind: AddonInCellKind.Observation,
                Channel: ((byte)addon.ResponseChannel),
                Ordinal: 0,
                HandleIndex: 0,
                HandleGeneration: 0,
                Verdict: AddonVerdict.None,
                Verb: ((byte)AddonAbi.ObservationVerbs.EventMachineMemoryChanged),
                A: (((long)watch.Screen) << 32) | ((uint)watch.Address),
                B: value
            );
            addon.EventCellsDelivered = ((addon.EventCellsDelivered == ulong.MaxValue)
                ? ulong.MaxValue
                : (addon.EventCellsDelivered + 1UL)
            );
        }

        return dropped;
    }
    private EventGateStatus EventGate(MountedAddon addon, GrantSubject subject) {
        if (
            !IsRequested(
            addon: addon,
            capability: WorldCapability.Observe,
            subject: subject
        ) ||
            !m_server.Grants.Allows(
            principal: addon.Principal,
            capability: WorldCapability.Observe,
            subject: subject
        ).IsAllowed ||
            !m_server.Grants.TryGetEventBudget(
            principal: addon.Principal,
            capability: WorldCapability.Observe,
            subject: subject,
            out var eventBudget
        )
        ) {
            return EventGateStatus.None;
        }

        return ((addon.EventCounts.GetValueOrDefault(key: subject) < eventBudget)
            ? EventGateStatus.Available
            : EventGateStatus.Exhausted
        );
    }
    // The read-only twin of Contribution: locate an existing accumulator, never open one.
    private static int FindContribution(MountedAddon addon, int bodyIndex) {
        for (var slot = 0; (slot < addon.ContributionCount); ++slot) {
            if (addon.Contributions[slot].BodyIndex == bodyIndex) {
                return slot;
            }
        }

        return -1;
    }
    // Fold one validated act into its body's accumulating channel vector, at the ordinal the guest's declared name
    // resolved to at handshake — the WORLD table's ordinal, and therefore a PlayerIntent ordinal directly. Every
    // channel is DECLARATIVE, this tick only: the host holds no channel state between ticks, so a channel a guest
    // stops emitting reads zero the very next tick, like a seat's own analog clear. The pump already refused a
    // duplicate ordinal within one batch as a protocol fault, so no act can overwrite another's channel here.
    private static void Fold(MountedAddon addon, int slot, in AddonActSubmission act) {
        ref var contribution = ref addon.Contributions[slot];

        // No hidden negation or axis remapping: the world channel's documented convention IS the wire convention.
        // The Rust guest is the one that must emit the correctly-signed value (see wasm/puck-addon-default) — the
        // old raw-stick negation lived here only because that guest used to speak raw stick space, not the intent's.
        contribution.Values[act.ChannelOrdinal] = FixedQ4816.FromRawBits(value: act.Value);
    }
    // PUMP POINT 2, per addon. Two passes over the staged acts with the submissions between them: the first resolves
    // handles and accumulates per-body axes, the second answers every ordinal whose body refused. Two passes rather than
    // one because a refusal is a property of the BODY (missing, or denied), and the acts that contributed to it are only
    // known once the whole batch has been folded.
    private void FoldActs(MountedAddon addon, ulong tick) {
        var acts = addon.Pump.Acts;
        var principal = addon.Principal;
        var handles = m_server.Grants.HandleTable(
            capability: WorldCapability.Drive,
            principal: principal
        );

        addon.ContributionCount = 0;
        // Per-tick Drive dispatch meter reset — the same shape as StageBatch's DispatchCounts clear for Observe,
        // just reset here because FoldActs (pump point 2) runs before StageBatch (pump point 3) within one tick.
        Array.Clear(array: addon.DriveDispatchCounts);
        // Set the moment any subject exhausts its drive budget THIS tick — read by the edge-trigger reset in the
        // finally below. The method body is wrapped in try/finally so an unexpected throw partway through still
        // runs the reset decision, rather than leaving the latch stuck armed.
        var driveExhaustedThisTick = false;

        try {
            for (var index = 0; (index < acts.Length); ++index) {
                ref readonly var act = ref acts[index];

                addon.ActBody[index] = NoBody;

                // Report-and-inert: a declared channel the host table doesn't recognize answers the SAME attenuation
                // verdict an unrequested subject does, reusing that posture rather than inventing a second one — the
                // act was well-formed, it simply names authority (a channel) that does not exist to grant. This is a
                // property of the ACT, known without any handle lookup, so it is checked before resolving one.
                if (!act.Resolved) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.AttenuatedToEmpty
                    );

                    continue;
                }

                // A handle DESIGNATES; it never decides. Resolution failure is the revoked/re-sorted case the generation
                // check exists for, and it is deliberately distinct from a denial: withdrawn and never-granted are
                // different states. The kind is checked after resolving because a table guarantees only that a slot names
                // one instance of the capability's domain, never which kind that instance is.
                if (
                    !handles.TryResolve(
                    handle: new WorldHandle(
                        Index: act.HandleIndex,
                        Generation: act.HandleGeneration,
                        TablePrincipal: principal,
                        TableCapability: WorldCapability.Drive
                    ),
                    subject: out var subject
                ) ||
                    (subject.Kind != GrantSubjectKind.Body)
                ) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.StaleHandle
                    );
                    ReportStaleHandle(addon: addon);

                    continue;
                }

                // The manifest gate, at application: minting filters requested ∧ granted, but the projection table
                // resolves ANY (index, generation) pair that matches a live slot — generations start at 0 and climb
                // slowly, so a guest can fabricate a plausible handle it was never handed. A resolve that lands on a
                // subject the manifest never requested is therefore refused as attenuation, exactly as an Ask for it
                // would be — never applied on the strength of the table alone.
                if (!IsRequested(
                    addon: addon,
                    capability: WorldCapability.Drive,
                    subject: subject
                )) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.AttenuatedToEmpty
                    );
                    ReportUnrequestedAct(
                        addon: addon,
                        subject: subject,
                        via: "drove"
                    );

                    continue;
                }

                // BUDGET CHECK — the Drive twin of ResolveQueries' Observe budget, same charge order (resolve ->
                // requested -> budget -> dispatch/fold): Drive's own "allowed" check happens once per BODY at Submit
                // (below), not once per act, so the budget meters the compute this act's resolve+fold already spent.
                // A row with no recorded budget is unreachable by construction: every principal reaching here is a
                // mounted addon's own untrusted Principal, and TryGrant's Conflicts gate refuses an untrusted Drive
                // hold with no budget before it can be added — so this refuses rather than dispatching unmetered.
                if (m_server.Grants.TryGetBudget(
                    principal: principal,
                    capability: WorldCapability.Drive,
                    subject: subject,
                    out var driveBudget
                )) {
                    if (addon.DriveDispatchCounts[subject.Value] >= driveBudget) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: act.Ordinal,
                            verdict: AddonVerdict.QuotaExhausted
                        );
                        driveExhaustedThisTick = true;

                        if (!addon.DriveDispatchBudgetExhaustedReported) {
                            addon.DriveDispatchBudgetExhaustedReported = true;
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} exceeded its drive/{subject.Describe()} dispatch budget ({driveBudget}/tick) — ordinal {act.Ordinal} refused QuotaExhausted]");
                        }

                        continue;
                    }

                    addon.DriveDispatchCounts[subject.Value]++;
                } else {
                    QueueAnswer(
                        addon: addon,
                        ordinal: act.Ordinal,
                        verdict: AddonVerdict.NoHold
                    );

                    if (!addon.DriveMissingBudgetReported) {
                        addon.DriveMissingBudgetReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} holds drive over {subject.Describe()} with no recorded dispatch budget — an authority-table inconsistency (unreachable by construction); ordinal {act.Ordinal} refused NoHold rather than dispatched unmetered]");
                    }

                    continue;
                }

                var bodyIndex = subject.Value;
                var slot = Contribution(
                    addon: addon,
                    bodyIndex: bodyIndex
                );

                addon.ActBody[index] = bodyIndex;
                Fold(
                    act: in act,
                    addon: addon,
                    slot: slot
                );
            }

            // Ascending body index, always — the contribution array is kept sorted on insert, so the order two acts land in
            // never depends on which handle the guest happened to name first.
            for (var slot = 0; (slot < addon.ContributionCount); ++slot) {
                Submit(
                    addon: addon,
                    slot: slot,
                    tick: tick
                );
            }

            for (var index = 0; (index < acts.Length); ++index) {
                var bodyIndex = addon.ActBody[index];

                if (bodyIndex == NoBody) {
                    continue;
                }

                // Every ActBody value here was set by a Contribution call in the first pass, so this always resolves.
                var outcome = addon.Contributions[FindContribution(
                    addon: addon,
                    bodyIndex: bodyIndex
                )].Outcome;

                // An allowed contribution answers nothing: silence is the positive signal.
                if (outcome != AddonVerdict.None) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: acts[index].Ordinal,
                        verdict: outcome
                    );
                }
            }
        } finally {
            // DriveDispatchBudgetExhaustedReported is EDGE-TRIGGERED per exhaustion episode (reset here the moment a
            // tick exhausts no drive budget), never a once-per-process-lifetime latch — the same shape as
            // MergeAnswers' QuotaDropReported, for the identical reason: a second, later saturation episode must be
            // able to say so again rather than staying silent forever after the first. The finally makes this run
            // even if the try above threw, rather than leaving the latch wherever the last successful tick left it.
            if (!driveExhaustedThisTick) {
                addon.DriveDispatchBudgetExhaustedReported = false;
            }
        }
    }
    private static bool IsMaterialized(MountedAddon addon, WorldCapability capability, GrantSubject subject) =>
        ((subject.Kind == GrantSubjectKind.Body) && IsRequested(
            addon: addon,
            capability: capability,
            subject: subject
        ));
    // Requests ∧ grants: a hold materializes for the guest only where the row's manifest asked for it. A manifest
    // naming the wildcard covers every subject of the capability, which is the one shape a row may legally carry that is
    // broader than a body.
    private static bool IsRequested(MountedAddon addon, WorldCapability capability, GrantSubject subject) {
        var requests = addon.Requests;

        if (requests is null) {
            return false;
        }

        for (var index = 0; (index < requests.Count); ++index) {
            var request = requests[index];

            if (
                (request.Capability == capability) &&
                ((request.Subject == subject) || (request.Subject.Kind == GrantSubjectKind.All))
            ) {
                return true;
            }
        }

        return false;
    }
    private static int MapEventVerb(WorldEventFamily family) => family switch {
        WorldEventFamily.RegionEnter => AddonAbi.ObservationVerbs.EventRegionEnter,
        WorldEventFamily.RegionExit => AddonAbi.ObservationVerbs.EventRegionExit,
        WorldEventFamily.SeatJoin => AddonAbi.ObservationVerbs.EventSeatJoin,
        WorldEventFamily.SeatLeave => AddonAbi.ObservationVerbs.EventSeatLeave,
        WorldEventFamily.CollisionBegin => AddonAbi.ObservationVerbs.EventCollisionBegin,
        WorldEventFamily.CollisionEnd => AddonAbi.ObservationVerbs.EventCollisionEnd,
        WorldEventFamily.RouteEngaged => AddonAbi.ObservationVerbs.EventRouteEngaged,
        WorldEventFamily.RouteDisengaged => AddonAbi.ObservationVerbs.EventRouteDisengaged,
        _ => -1,
    };
    // Sort the tick's answers into (ordinal, part) order and place them behind the disclosures, whole groups at a time:
    // a multi-part answer is atomic, because half a pose is a value the guest cannot tell apart from a whole one. A
    // group that no longer fits collapses to a single QuotaExhausted cell so the guest reads a refusal rather than
    // inferring one from an answer that never came. Once even that one cell does not fit, the remaining groups drop
    // with no verdict cell at all — the ring is physically full, and the ABI's ordinal contract rules out inventing a
    // many-to-one aggregate cell to say so on the wire without a real ABI change. addon.TotalAnswersDropped turns the
    // magnitude into a DURABLE, host-observable quantity (world.addons) rather than a fact that only ever existed on
    // a stderr line the instant it scrolled past. QuotaDropReported is EDGE-TRIGGERED per saturation episode (reset
    // in the finally below the moment a tick does not drop anything), never a once-per-process-lifetime latch, and
    // is wrapped in try/finally so the caller can never leave it stuck on the strength of a throw it didn't plan for.
    private static void MergeAnswers(MountedAddon addon, int budget) {
        SortAnswers(
            answers: addon.Answers,
            count: addon.AnswerCount
        );

        var index = 0;
        var refusing = false;
        var droppedGroupCount = 0;

        try {
            while (index < addon.AnswerCount) {
                var ordinal = addon.Answers[index].Ordinal;
                var end = index;

                while (
                    (end < addon.AnswerCount) &&
                    (addon.Answers[end].Ordinal == ordinal)
                ) {
                    ++end;
                }

                var size = (end - index);
                var remaining = (budget - addon.PendingCount);

                // Once one group fails to fit whole, EVERY later group is refused too — "that request and all later
                // ones" — never let a smaller later group slip through whole, or which answers a guest receives would
                // depend on the SIZES of its earlier requests rather than their order.
                if (
                    !refusing &&
                    (size > remaining)
                ) {
                    refusing = true;
                }

                if (!refusing) {
                    for (var part = index; (part < end); ++part) {
                        addon.Pending[addon.PendingCount++] = addon.Answers[part];
                    }
                } else if (remaining >= 1) {
                    addon.Pending[addon.PendingCount++] = new AddonInCell(
                        Kind: AddonInCellKind.Answer,
                        Channel: ((byte)addon.ResponseChannel),
                        Ordinal: ordinal,
                        HandleIndex: 0,
                        HandleGeneration: 0,
                        Verdict: AddonVerdict.QuotaExhausted,
                        Verb: 0,
                        A: 0L,
                        B: 0L
                    );
                } else {
                    ++droppedGroupCount;
                }

                index = end;
            }

            if (droppedGroupCount > 0) {
                addon.TotalAnswersDropped = ((addon.TotalAnswersDropped > (ulong.MaxValue - ((ulong)droppedGroupCount)))
                    ? ulong.MaxValue
                    : (addon.TotalAnswersDropped + ((ulong)droppedGroupCount))
                );

                if (!addon.QuotaDropReported) {
                    addon.QuotaDropReported = true;
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} filled its {budget}-cell answer budget — {droppedGroupCount} request group(s) this tick got no verdict cell at all (lifetime total {addon.TotalAnswersDropped}, see world.addons); shrink the batch or grow puck_in_cap]");
                }
            }
        } finally {
            if (droppedGroupCount == 0) {
                addon.QuotaDropReported = false;
            }
        }
    }
    private static void QueueAnswer(MountedAddon addon, ushort ordinal, AddonVerdict verdict, ushort handleIndex = 0, ushort handleGeneration = 0) {
        addon.Answers[addon.AnswerCount++] = new AddonInCell(
            Kind: AddonInCellKind.Answer,
            Channel: ((byte)((addon.ResponseChannel < 0)
            ? 0
            : addon.ResponseChannel)),
            Ordinal: ordinal,
            HandleIndex: handleIndex,
            HandleGeneration: handleGeneration,
            Verdict: verdict,
            Verb: 0,
            A: 0L,
            B: 0L
        );
    }
    private static void QueuePart(MountedAddon addon, ushort ordinal, AddonVerdict verdict, byte part, long a, long b) {
        addon.Answers[addon.AnswerCount++] = new AddonInCell(
            Kind: AddonInCellKind.Answer,
            Channel: ((byte)addon.ResponseChannel),
            Ordinal: ordinal,
            HandleIndex: 0,
            HandleGeneration: 0,
            Verdict: verdict,
            Verb: part,
            A: a,
            B: b
        );
    }
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
    private static void ReportDiscrepancy(MountedAddon addon, string detail) {
        if (addon.DiscrepancyReported) {
            return;
        }

        addon.DiscrepancyReported = true;
        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} — {detail}]");
    }
    private static void ReportFault(MountedAddon addon) {
        // A Disabled instance carries no fault detail; only a genuine fault has something to say.
        if (
            addon.FaultReported ||
            (addon.Instance.Fault.Kind == AddonFaultKind.None)
        ) {
            return;
        }

        addon.FaultReported = true;
        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Fault.Detail}]");
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
    private static void ReportStaleHandle(MountedAddon addon) {
        if (addon.StaleHandleReported) {
            return;
        }

        addon.StaleHandleReported = true;
        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} drove through a handle that no longer designates a body — refused with the stale-handle verdict; the grant it was minted from was revoked or re-sorted, so re-ask for one]");
    }
    private static void ReportUnrequestedAct(MountedAddon addon, GrantSubject subject, string via) {
        if (addon.UnrequestedActReported) {
            return;
        }

        addon.UnrequestedActReported = true;
        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} {via} through a handle over {subject.Describe()}, which its manifest never requested — refused as attenuated-to-empty; a fabricated or guessed handle materializes nothing beyond requested ∧ granted]");
    }
    // Asks: mint a handle over a subject the guest NAMES, resolved requested ∧ granted. The mask is single-bit and
    // defined (the pump proved both), the subject is range- then existence-checked here, and the mint is by requested
    // subject — the host projects, the guest never names a table position.
    private void ResolveAsks(MountedAddon addon) {
        var asks = addon.Pump.Asks;
        var grants = m_server.Grants;

        for (var index = 0; (index < asks.Length); ++index) {
            ref readonly var ask = ref asks[index];

            if (!WorldAddonWire.TryCapability(
                mask: ask.CapabilityMask,
                capability: out var capability
            )) {
                // Unreachable: the pump admits only the guest-maskable bits. If it ever fires, the wire mapping and
                // the pump's own mask set have drifted apart and the guest must not be left guessing.
                if (!addon.DiscrepancyReported) {
                    ReportDiscrepancy(
                        addon: addon,
                        detail: $"ask ordinal {ask.Ordinal} carries capability mask 0x{ask.CapabilityMask:x}, which maps to no engine capability"
                    );
                }

                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoHold
                );

                continue;
            }

            // The subject shape is per-KIND (Body pairs with Drive/Observe, Section pairs with Mutate — the pump
            // already enforced that pairing at TryValidateAsk), so the RANGE check and the GrantSubject construction
            // both branch on it here. A subject kind neither the pump nor this switch recognizes falls to the safe
            // default: out of range.
            //
            // Section is NAME-KEYED, never ordinal-keyed: a guest sends its section's declared NAME (UTF-8 bytes in
            // its own linear memory, ptr+len in the ask's A/C lanes — the same convention SubmitMutation uses for a
            // payload), and the host resolves it against the live WorldSection vocabulary here. There is no ordinal
            // for a guest to bake stale, and an unresolvable name refuses LOUDLY, quoting the name, rather than
            // silently minting authority over an unintended member.
            bool inRange;
            GrantSubject subject;

            if (ask.SubjectKind == AddonSubjectKind.Body) {
                inRange = ((ask.SubjectIndex >= 0L) && (ask.SubjectIndex < m_server.Population.Capacity));
                subject = (inRange
                    ? GrantSubject.Body(index: ((int)ask.SubjectIndex))
                    : GrantSubject.All
                );
            } else if (ask.SubjectKind == AddonSubjectKind.Section) {
                if (!TryResolveSectionAskName(
                    addon: addon,
                    ask: in ask,
                    error: out var copyError,
                    name: out var sectionName,
                    refusal: out var copyRefusal
                )) {
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} ask ordinal {ask.Ordinal} refused {copyRefusal} — {copyError}]");
                    QueueAnswer(
                        addon: addon,
                        ordinal: ask.Ordinal,
                        verdict: copyRefusal!.Value
                    );

                    continue;
                }

                // No manifest-gate deferral here, unlike Body's liveness check below: the WorldSection vocabulary is
                // a fixed, PUBLIC set (every member name ships in this repository's own docs and console grammar),
                // so answering "no such section" for an unresolvable name leaks nothing a body-liveness answer
                // would — there is no enumeration oracle to protect against for a static enum.
                if (!GrantSubject.TryParseSectionName(
                    name: sectionName,
                    section: out var resolvedSection
                )) {
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} ask ordinal {ask.Ordinal} refused NoSuchSubject — unknown section name '{sectionName}']");
                    QueueAnswer(
                        addon: addon,
                        ordinal: ask.Ordinal,
                        verdict: AddonVerdict.NoSuchSubject
                    );

                    continue;
                }

                inRange = true;
                subject = GrantSubject.Section(section: resolvedSection);
            } else {
                inRange = false;
                subject = GrantSubject.All;
            }

            // The MANIFEST gate runs before any further inspection, including the liveness check below: answering
            // NoSuchSubject before this gate would make the verdict a body-enumeration oracle (live body vs empty
            // slot leaks off the difference) for a zero-grant guest. An index the manifest could not have named
            // (out of range, or an unrecognized kind) is attenuated for the same reason.
            if (
                !inRange ||
                !IsRequested(
                addon: addon,
                capability: capability,
                subject: subject
            )
            ) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.AttenuatedToEmpty
                );

                continue;
            }

            // Liveness only ever answers for a subject the guest's OWN manifest names — no oracle — and only ever
            // applies to a BODY subject: a document section is a fixed enum member, not a live population entry, so
            // it has no analogous "does not exist right now" state to check.
            if (
                (ask.SubjectKind == AddonSubjectKind.Body) &&
                (m_server.Body(index: ((int)ask.SubjectIndex)) is null)
            ) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoSuchSubject
                );

                continue;
            }

            var verdict = grants.Allows(
                principal: addon.Principal,
                capability: capability,
                subject: subject
            );

            if (!verdict.IsAllowed) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: WorldAddonWire.FromRule(rule: verdict.Rule)
                );

                continue;
            }

            // Mint by requested subject through the table's own cached projection — no per-ask array allocation, no
            // linear re-scan of a projection the table already holds.
            if (!grants.HandleTable(
                principal: addon.Principal,
                capability: capability
            ).TryMintFor(
                handle: out var handle,
                subject: subject
            )) {
                // Allowed but unprojected — a wildcard hold, which the grant door refuses outright for an addon, so
                // this is unreachable today. Answering NoHold rather than minting a handle over a subject no slot names
                // is the safe half of the discrepancy; the line is the other half.
                if (!addon.DiscrepancyReported) {
                    ReportDiscrepancy(
                        addon: addon,
                        detail: $"holds {capability.ToString().ToLowerInvariant()} over {subject.Describe()} by {verdict.Describe()} but no handle slot projects it — no handle was minted"
                    );
                }

                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoHold
                );

                continue;
            }

            if (!TryPack(
                addon: addon,
                generation: out var wireGeneration,
                handle: handle,
                index: out var wireIndex
            )) {
                QueueAnswer(
                    addon: addon,
                    ordinal: ask.Ordinal,
                    verdict: AddonVerdict.NoHold
                );

                continue;
            }

            QueueAnswer(
                addon: addon,
                ordinal: ask.Ordinal,
                verdict: WorldAddonWire.FromRule(rule: verdict.Rule),
                handleIndex: wireIndex,
                handleGeneration: wireGeneration
            );
        }
    }
    // PUMP POINT 1, per addon, run right after a successful Pump: the addon mutation seam's six-stage dispatch door
    // over every SubmitMutation act in THIS batch. Stages: (1) manifest, (2+3) THE SHARED ADMISSION PREDICATE —
    // WorldServer.TryAdmitMutation, the one owner of hold ∧ verb mask ∧ budget for every mutation ingress, called
    // here rather than reimplemented, and called BEFORE decode so a malformed payload still spends its dispatch —
    // (4) the reserved answer cell (bookkeeping only: the ABI handshake's outCap <= inCap-1 relation already proves
    // this can never overflow ReservedAnswers), (5) pointer safety (unsigned ptr/len, the payload-size ceilings, an
    // immediate host-side copy), (6) the per-kind decode (WorldAddonMutationDecoder). A cleared act ENQUEUES a
    // PendingOp.Mutate — it is NEVER applied here; application (compose -> revalidate -> swap) runs later THIS SAME
    // Step, at WorldServer.Step's DrainPendingOps, before intents. Every other outcome is DECIDED here but not yet
    // DELIVERED: every reserved slot's verdict is staged into the guest's next input batch by ResolveReads/
    // StageBatch — never here, and never by DrainPendingOps directly.
    private void ResolveMutations(MountedAddon addon, int addonIndex, ulong tick) {
        addon.ReservedCount = 0;
        addon.MutateBytesThisTick = 0;

        var queries = addon.Pump.Queries;
        var grants = m_server.Grants;
        var handles = grants.HandleTable(
            principal: addon.Principal,
            capability: WorldCapability.Mutate
        );
        var dispatchExhaustedThisTick = false;
        var byteExhaustedThisTick = false;

        try {
            for (var index = 0; (index < queries.Length); ++index) {
                ref readonly var query = ref queries[index];

                if (query.Verb != AddonAbi.RequestVerbs.SubmitMutation) {
                    continue;
                }

                // STAGE 4 — the reservation. Bookkeeping only: the slot's Verdict starts at AddonVerdict.None (the
                // "still pending" sentinel between decode and drain within this same Step) and every branch below
                // either overwrites it immediately or leaves it for CompleteMutation to overwrite at drain.
                var slot = addon.ReservedCount++;

                addon.ReservedAnswers[slot] = new AddonInCell(
                    Kind: AddonInCellKind.Answer,
                    Channel: ((byte)((addon.ResponseChannel < 0)
                    ? 0
                    : addon.ResponseChannel)),
                    Ordinal: query.Ordinal,
                    HandleIndex: 0,
                    HandleGeneration: 0,
                    Verdict: AddonVerdict.None,
                    Verb: 0,
                    A: 0L,
                    B: 0L
                );

                // The handle designates a SECTION subject; it never decides. Resolution failure is the revoked/
                // re-sorted case the generation check exists for — deliberately distinct from a denial.
                if (
                    !handles.TryResolve(
                    handle: new WorldHandle(
                        Index: query.HandleIndex,
                        Generation: query.HandleGeneration,
                        TablePrincipal: addon.Principal,
                        TableCapability: WorldCapability.Mutate
                    ),
                    subject: out var subject
                ) ||
                    (subject.Kind != GrantSubjectKind.Section)
                ) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.StaleHandle)
                    );
                    ReportStaleHandle(addon: addon);

                    continue;
                }

                // STAGE 1 — the manifest gate: requests ∧ grants. Checked before ANY further inspection, the same
                // enumeration-is-a-capability posture ResolveAsks/FoldActs already carry.
                if (!IsRequested(
                    addon: addon,
                    capability: WorldCapability.Mutate,
                    subject: subject
                )) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.NotRequested)
                    );
                    ReportUnrequestedAct(
                        addon: addon,
                        subject: subject,
                        via: "mutated"
                    );

                    continue;
                }

                // STAGE 2+3 — THE SHARED ADMISSION PREDICATE. The bare Mutate hold, the DECIDING row's verb mask, and
                // the per-tick dispatch budget are ONE rule, owned by WorldServer.TryAdmitMutation and merely CALLED
                // here, before decode, so a malformed payload still spends its dispatch and a guest cannot probe the
                // decoder for free. Everything is re-checked live: a cached decision would go stale the moment
                // another principal reserves the section exclusively.
                //
                // rowScopedEditSubject is null: a state write's Edit/state:<name> subject is only knowable AFTER the
                // decode this pre-flight deliberately precedes, so that gate runs at apply (WorldServer's own
                // TryApplyMutation, later THIS same Step) over the identical predicate.
                //
                // meter: true — THIS is the metering point for a guest act; the apply path knows not to charge it
                // again (PendingOp.Mutate carries SourceAddonIndex for exactly that).
                var kindOrdinal = ((int)query.A);
                var section = ((WorldSection)subject.Value);

                if (!m_server.TryAdmitMutation(
                    principal: addon.Principal,
                    section: section,
                    kindOrdinal: kindOrdinal,
                    rowScopedEditSubject: null,
                    meter: true,
                    admission: out var admission
                )) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: ToWireVerdict(admission: in admission)
                    );

                    if (admission.Rule == WorldMutationAdmissionRule.BudgetExhausted) {
                        dispatchExhaustedThisTick = true;

                        if (!addon.MutateDispatchBudgetExhaustedReported) {
                            addon.MutateDispatchBudgetExhaustedReported = true;
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} {admission.Describe()} — ordinal {query.Ordinal} refused QuotaExhausted]");
                        }
                    } else if (
                        (admission.Rule == WorldMutationAdmissionRule.MissingBudget) &&
                        !addon.MutateMissingBudgetReported
                    ) {
                        addon.MutateMissingBudgetReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} {admission.Describe()}; ordinal {query.Ordinal} refused NoHold rather than dispatched unmetered]");
                    }

                    continue;
                }

                // STAGE 5 — pointer safety: the per-payload ceiling, then the per-addon and global per-tick byte
                // ceilings (all THREE are size refusals, checked before a single guest-memory byte is read), then
                // an IMMEDIATE host-owned copy (AddonInstance.TryCopyMemory bounds-checks ptr/len against the
                // guest's ACTUAL memory length, unsigned throughout, overflow-checked end).
                //
                // query.C crosses the ABI as a signed i64 lane REINTERPRETED UNSIGNED (see
                // AddonSimulationPump.TryValidateQuery's remarks) — compared as ulong here, BEFORE any narrowing
                // cast, so a negative-reinterpreted-as-huge length reads as "too large" rather than wrapping into a
                // small or negative `int` that could slip under the ceiling check.
                var lengthUnsigned = unchecked((ulong)query.C);

                if (lengthUnsigned > ((ulong)AddonAbi.MaxMutationPayloadBytes)) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.PayloadTooLarge)
                    );

                    continue;
                }

                // Safe to narrow now: the check above already proved lengthUnsigned <= MaxMutationPayloadBytes
                // (8192), which fits in an int with room to spare.
                var length = ((int)lengthUnsigned);

                if ((addon.MutateBytesThisTick + length) > AddonAbi.MaxMutationBytesPerTickPerAddon) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.AddonByteBudgetExhausted)
                    );
                    byteExhaustedThisTick = true;

                    if (!addon.MutateByteBudgetExhaustedReported) {
                        addon.MutateByteBudgetExhaustedReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} exceeded its per-tick mutation-payload byte budget ({AddonAbi.MaxMutationBytesPerTickPerAddon} bytes) — ordinal {query.Ordinal} refused QuotaExhausted]");
                    }

                    continue;
                }

                if ((m_mutationBytesThisTickAllAddons + length) > AddonAbi.MaxMutationBytesPerTickAllAddons) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.GlobalByteBudgetExhausted)
                    );
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name}'s mutation payload ({length} bytes) would exceed the GLOBAL per-tick ceiling ({AddonAbi.MaxMutationBytesPerTickAllAddons} bytes, all addons summed) — ordinal {query.Ordinal} refused QuotaExhausted]");

                    continue;
                }

                addon.MutateBytesThisTick += length;
                m_mutationBytesThisTickAllAddons += length;

                if (!addon.Instance.TryCopyMemory(
                    pointer: query.B,
                    length: length,
                    destination: addon.MutationPayloadBuffer.AsSpan(
                        length: length,
                        start: 0
                    ),
                    error: out var copyError
                )) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.PointerOutOfBounds)
                    );
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} submit-mutation ordinal {query.Ordinal} refused MalformedPayload — {copyError}]");

                    continue;
                }

                var payload = addon.MutationPayloadBuffer.AsMemory(
                    length: length,
                    start: 0
                );

                // STAGE 6 — the per-kind hand-walked decode. On success the mutation is NOT applied here: it
                // enqueues as a PendingOp with this act's (addonIndex, ordinal) completion fields, drained the
                // SAME Step at WorldServer.Step's DrainPendingOps (before intents) through the identical
                // compose->revalidate->swap path a console-submitted mutation runs — CompleteMutation stages the
                // outcome (Applied or Rejected) into this slot once that drain decides it.
                if (
                    !WorldAddonMutationDecoder.TryDecode(
                    kindOrdinal: kindOrdinal,
                    section: section,
                    payload: payload,
                    principal: addon.Principal,
                    mutation: out var mutation,
                    error: out var decodeError
                ) ||
                    (mutation is null)
                ) {
                    SetReservedVerdict(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.DecodeFailed)
                    );
                    Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} submit-mutation ordinal {query.Ordinal} refused MalformedPayload — {decodeError}]");

                    continue;
                }

                m_server.EnqueueMutation(
                    mutation: mutation,
                    connectionId: SubmissionEnvelope.LocalConnectionId,
                    correlationId: 0,
                    sourceAddonIndex: addonIndex,
                    actOrdinal: query.Ordinal
                );
            }
        } finally {
            // Edge-triggered per exhaustion episode, the same shape every other dispatch-budget latch in this file
            // uses — reset the moment a tick exhausts neither ceiling, so a LATER episode can report again.
            if (!dispatchExhaustedThisTick) {
                addon.MutateDispatchBudgetExhaustedReported = false;
            }

            if (!byteExhaustedThisTick) {
                addon.MutateByteBudgetExhaustedReported = false;
            }
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
    // Queries: a pose read through an Observe handle. Four answer cells share the request's ordinal on the guest's
    // Response channel, each repeating the SAME allowing verdict and carrying the part index in the Verb byte, with both
    // handle lanes zero — a pose grants no handle. Host-written explicit framing, never an implied pairing the guest has
    // to reconstruct.
    private void ResolveQueries(MountedAddon addon) {
        var queries = addon.Pump.Queries;
        var grants = m_server.Grants;
        var handles = grants.HandleTable(
            principal: addon.Principal,
            capability: WorldCapability.Observe
        );
        var driveHandles = grants.HandleTable(
            principal: addon.Principal,
            capability: WorldCapability.Drive
        );
        // Set the moment any subject exhausts its observe budget THIS tick — read by the edge-trigger reset in the
        // finally below, the same shape as MergeAnswers' QuotaDropReported. The loop is wrapped in try/finally so an
        // unexpected throw partway through still runs the reset decision, the same hardening FoldActs' Drive twin
        // carries — an episode's caller must never be able to leave the latch stuck on the strength of an exception
        // it didn't plan for.
        var exhaustedThisTick = false;

        try {
            for (var index = 0; (index < queries.Length); ++index) {
                ref readonly var query = ref queries[index];

                // SubmitMutation acts already ran the WHOLE six-stage dispatch door at decode time
                // (TickAddons -> ResolveMutations, pump point 1) — their reserved answer cell is staged directly by
                // StageBatch, never through this method's Observe-only path. Skipping here (rather than falling
                // into the "verb not served" discrepancy branch below) keeps that branch meaning what it says: a
                // verb this host genuinely does not recognize, not one a DIFFERENT stage already answered.
                if (query.Verb == AddonAbi.RequestVerbs.SubmitMutation) {
                    continue;
                }

                if (query.Verb == AddonAbi.RequestVerbs.Designate) {
                    var sourceHandle = new WorldHandle(
                        Index: query.HandleIndex,
                        Generation: query.HandleGeneration,
                        TablePrincipal: addon.Principal,
                        TableCapability: WorldCapability.Drive
                    );

                    if (
                        !driveHandles.TryResolve(
                        handle: sourceHandle,
                        subject: out var sourceSubject
                    ) ||
                        (sourceSubject.Kind != GrantSubjectKind.Body)
                    ) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.StaleHandle
                        );
                        continue;
                    }
                    if (!IsRequested(
                        addon: addon,
                        capability: WorldCapability.Drive,
                        subject: sourceSubject
                    )) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.AttenuatedToEmpty
                        );
                        ReportUnrequestedAct(
                            addon: addon,
                            subject: sourceSubject,
                            via: "designated"
                        );
                        continue;
                    }

                    var targetSubject = GrantSubject.Body(index: ((int)query.A));

                    if (!IsRequested(
                        addon: addon,
                        capability: WorldCapability.Observe,
                        subject: targetSubject
                    )) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.AttenuatedToEmpty
                        );
                        ReportUnrequestedAct(
                            addon: addon,
                            subject: targetSubject,
                            via: "designated"
                        );
                        continue;
                    }

                    var registerIndex = ((int)query.B);

                    if (((uint)registerIndex) >= ((uint)m_server.Population.TargetRegisters.Count)) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.Rejected
                        );
                        continue;
                    }

                    var applied = m_server.ApplyDesignation(
                        designation: new WorldDesignation(
                            EntityIndex: sourceSubject.Value,
                            Register: m_server.Population.TargetRegisters.Name(index: registerIndex),
                            Subject: targetSubject
                        ),
                        principal: addon.Principal
                    );

                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: (applied
                        ? AddonVerdict.Applied
                        : AddonVerdict.Rejected)
                    );
                    continue;
                }

                if (
                    !handles.TryResolve(
                    handle: new WorldHandle(
                        Index: query.HandleIndex,
                        Generation: query.HandleGeneration,
                        TablePrincipal: addon.Principal,
                        TableCapability: WorldCapability.Observe
                    ),
                    subject: out var subject
                ) ||
                    (subject.Kind != GrantSubjectKind.Body)
                ) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.StaleHandle
                    );

                    continue;
                }

                // The manifest gate, at the read exactly as at the act: the projection table resolves any fabricated
                // (index, generation) pair that lands on a live slot, so a granted-but-unrequested body would otherwise be
                // readable through a guessed handle even though disclosure and Ask both withhold it.
                if (!IsRequested(
                    addon: addon,
                    capability: WorldCapability.Observe,
                    subject: subject
                )) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.AttenuatedToEmpty
                    );
                    ReportUnrequestedAct(
                        addon: addon,
                        subject: subject,
                        via: "read"
                    );

                    continue;
                }

                // The handle designated the subject; the grant table decides whether the read may happen, re-checked here
                // because a cached decision would go stale the moment another principal reserves the subject exclusively.
                var verdict = grants.Allows(
                    principal: addon.Principal,
                    capability: WorldCapability.Observe,
                    subject: subject
                );

                if (!verdict.IsAllowed) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: WorldAddonWire.FromRule(rule: verdict.Rule)
                    );

                    continue;
                }

                // BUDGET CHECK — charge order is resolve -> IsRequested -> Allows -> budget -> dispatch: after the
                // authority verdicts (so a denial stays precise and costs no budget) and before the dispatch it
                // meters (the read below, and later a spatial verb's raymarch). Read fresh per query, like Allows and
                // for the identical staleness reason: a re-grant with a different budget takes effect on THIS query.
                //
                // A row with NO recorded budget is UNREACHABLE BY CONSTRUCTION: every principal reaching
                // ResolveQueries is a mounted addon's own untrusted Principal, and TryGrant's own Conflicts gate
                // already refuses an untrusted Observe grant that carries no budget before it can be added — so an
                // Observe hold for this principal cannot exist without a matching budget entry. If this branch ever
                // fires, the grant table itself has gone inconsistent, so it REFUSES the query rather than
                // dispatching it unmetered. It reuses the Allows-denied branch's NoHold verdict and reports through
                // its OWN latch so it can never be starved by DiscrepancyReported firing first at an unrelated site.
                if (grants.TryGetBudget(
                    principal: addon.Principal,
                    capability: WorldCapability.Observe,
                    subject: subject,
                    out var budget
                )) {
                    if (addon.DispatchCounts[subject.Value] >= budget) {
                        QueueAnswer(
                            addon: addon,
                            ordinal: query.Ordinal,
                            verdict: AddonVerdict.QuotaExhausted
                        );
                        exhaustedThisTick = true;

                        if (!addon.DispatchBudgetExhaustedReported) {
                            addon.DispatchBudgetExhaustedReported = true;
                            Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} exceeded its observe/{subject.Describe()} dispatch budget ({budget}/tick) — ordinal {query.Ordinal} refused QuotaExhausted]");
                        }

                        continue;
                    }

                    addon.DispatchCounts[subject.Value]++;
                } else {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.NoHold
                    );

                    if (!addon.MissingBudgetReported) {
                        addon.MissingBudgetReported = true;
                        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} holds observe over {subject.Describe()} with no recorded dispatch budget — an authority-table inconsistency (unreachable by construction); ordinal {query.Ordinal} refused NoHold rather than dispatched unmetered]");
                    }

                    continue;
                }

                if (m_server.Body(index: subject.Value) is not { } body) {
                    QueueAnswer(
                        addon: addon,
                        ordinal: query.Ordinal,
                        verdict: AddonVerdict.NoSuchSubject
                    );

                    continue;
                }

                if (query.Verb != AddonAbi.RequestVerbs.BodyPose) {
                    // Unreachable: the pump range-checks the verb against the guest's declared count, which the closed
                    // request vocabulary bounds. A verb this host cannot serve is named loudly rather than answered with a
                    // verdict that would misdescribe it as an authority outcome.
                    if (!addon.DiscrepancyReported) {
                        ReportDiscrepancy(
                            addon: addon,
                            detail: $"request verb {query.Verb} at ordinal {query.Ordinal} is not served by this host — no answer was produced"
                        );
                    }

                    continue;
                }

                var allowed = WorldAddonWire.FromRule(rule: verdict.Rule);
                var position = body.FixedPosition;
                var orientation = body.FixedOrientation;

                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 0,
                    a: position.X.Value,
                    b: position.Y.Value
                );
                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 1,
                    a: position.Z.Value,
                    b: 0L
                );
                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 2,
                    a: orientation.X.Value,
                    b: orientation.Y.Value
                );
                QueuePart(
                    addon: addon,
                    ordinal: query.Ordinal,
                    verdict: allowed,
                    part: 3,
                    a: orientation.Z.Value,
                    b: orientation.W.Value
                );
            }
        } finally {
            // DispatchBudgetExhaustedReported is EDGE-TRIGGERED per exhaustion episode (reset here the moment a tick
            // exhausts no observe budget), never a once-per-process-lifetime latch — the same shape as MergeAnswers'
            // QuotaDropReported, for the identical reason: a second, later saturation episode must be able to say so
            // again rather than staying silent forever after the first.
            if (!exhaustedThisTick) {
                addon.DispatchBudgetExhaustedReported = false;
            }
        }
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
    // This tick's contribution restricted to the MOVEMENT-ROLE ordinals — the submitted intent's own convention (see
    // SeatController.HeldIntent). Stack-only: ChannelValues is an InlineArray, so this allocates nothing.
    private static PlayerIntent RoleChannels(ChannelValues values, WorldChannelTable channels) {
        var roles = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ++ordinal) {
            if (channels.IsRole(ordinal: ordinal)) {
                roles[ordinal] = values[ordinal];
            }
        }

        return new PlayerIntent(Channels: roles);
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
    // Picks the first requested, granted event row with remaining allowance. GateA precedes GateB, so an edge visible
    // through both rows charges one cell to A until A is full, then B. None means the guest cannot observe the edge;
    // Exhausted means it could, but every qualifying row spent its allowance and the edge must enter the gap count.
    private EventGateStatus SelectEventGate(MountedAddon addon, GrantSubject gateA, GrantSubject? gateB, out GrantSubject chargedSubject) {
        var statusA = EventGate(
            addon: addon,
            subject: gateA
        );

        if (statusA == EventGateStatus.Available) {
            chargedSubject = gateA;
            return statusA;
        }

        var statusB = ((gateB is { } subjectB)
            ? EventGate(
                addon: addon,
                subject: subjectB
            )
            : EventGateStatus.None
        );

        if (statusB == EventGateStatus.Available) {
            chargedSubject = gateB!.Value;
            return statusB;
        }

        chargedSubject = default;
        return (((statusA == EventGateStatus.Exhausted) || (statusB == EventGateStatus.Exhausted))
            ? EventGateStatus.Exhausted
            : EventGateStatus.None
        );
    }
    // Overwrites the reserved Answer cell for `ordinal` with its decided verdict — shared by a stage 1-5 refusal
    // decided at decode time and by CompleteMutation's stage-6-onward outcome decided later at drain. A miss (no
    // reservation for this ordinal) is silently ignored rather than thrown: a caller passing an unreserved ordinal
    // is a programming error to catch by review, not a runtime condition to crash a live session over.
    private static void SetReservedVerdict(MountedAddon addon, ushort ordinal, AddonVerdict verdict) {
        for (var index = 0; (index < addon.ReservedCount); ++index) {
            if (addon.ReservedAnswers[index].Ordinal == ordinal) {
                addon.ReservedAnswers[index] = addon.ReservedAnswers[index] with { Verdict = verdict };
                return;
            }
        }
    }
    // Insertion sort by (ordinal, part). The answers arrive as at most three already-ascending runs (act refusals, ask
    // answers, query parts), so this is near-linear in practice and never allocates; a stable order matters because a
    // pose's four parts must reach the guest in part order.
    private static void SortAnswers(AddonInCell[] answers, int count) {
        for (var index = 1; (index < count); ++index) {
            var candidate = answers[index];
            var slot = (index - 1);

            while (
                (slot >= 0) &&
                ((answers[slot].Ordinal > candidate.Ordinal) || ((answers[slot].Ordinal == candidate.Ordinal) && (answers[slot].Verb > candidate.Verb)))
            ) {
                answers[(slot + 1)] = answers[slot];
                --slot;
            }

            answers[(slot + 1)] = candidate;
        }
    }
    // PUMP POINT 3, per addon: disclosures, then events, then asks, then queries, then the budgeted merge into the next tick's batch.
    private void StageBatch(MountedAddon addon, ulong tick) {
        addon.PendingCount = 0;
        // Per-tick dispatch meter reset, beside the other per-tick scratch above — a fresh tick owes each budgeted
        // row its full allowance again.
        Array.Clear(array: addon.DispatchCounts);
        addon.EventCounts.Clear();

        // A guest that declared no Response channel can be handed nothing: every answer and every grant disclosure is
        // undeliverable by construction, which also means it can never learn a handle and therefore can never reach a
        // body. Loud once rather than a silent drop — silence here reads as "the grant did not work" and sends the
        // reader to the grant table instead of to the guest's channel declarations.
        if (addon.ResponseChannel < 0) {
            if (!addon.UndeliverableReported) {
                addon.UndeliverableReported = true;
                Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} declares no response channel (first seen at tick {tick}) — every verdict and every grant disclosure is undeliverable and is dropped, so it can never learn a handle and can never reach a body; declare a response channel beside its request channel]");
            }

            addon.AnswerCount = 0;

            return;
        }

        var budget = (addon.Instance.InputCellCapacity - 1);

        // THE RESERVATION, realized: this tick's protected mutation-answer cells install FIRST, before
        // EmitDisclosures/MergeAnswers ever run — they may never consume a reserved cell. Each slot's Verdict is
        // whatever ResolveMutations/CompleteMutation already decided (a stage 1-5 refusal decided at decode time, or
        // Applied/Rejected decided at drain); AddonVerdict.None can only mean a decode-time-enqueued act whose drain
        // has not yet run, which is unreachable here — DrainPendingOps always runs before ResolveReads within one
        // Step (see WorldServer.Step's own pinned order).
        for (var index = 0; (index < addon.ReservedCount); ++index) {
            addon.Pending[addon.PendingCount++] = (addon.ReservedAnswers[index] with { Channel = ((byte)addon.ResponseChannel) });
        }

        // EmitDisclosures' own "does it fit" check compares its count against a bare budget number (it does not
        // look at PendingCount), so it receives the budget ALREADY REDUCED by the reservation above. MergeAnswers
        // computes its own remaining room as (budget - addon.PendingCount) dynamically, and PendingCount already
        // reflects both the reservation and whatever EmitDisclosures just added by the time it runs — so it
        // receives the FULL budget, unreduced, and the two calls agree on how much room is actually left.
        EmitDisclosures(
            addon: addon,
            budget: (budget - addon.ReservedCount)
        );
        // World events (four families) plus this guest's own machine-memory watches (the fifth), within
        // whatever ring room remains after reservations/disclosures — see EmitEvents' own remarks for the
        // overflow doctrine (ordered prefix, drop-newest, per-mount gap counter).
        EmitEvents(
            addon: addon,
            budget: budget
        );
        ResolveAsks(addon: addon);
        ResolveQueries(addon: addon);
        MergeAnswers(
            addon: addon,
            budget: budget
        );
        addon.AnswerCount = 0;
    }
    // Submit one body's folded intent under the addon's own principal, recording the outcome so the acts that fed it can
    // be answered. A denial is not reported here — WorldServer.ApplyIntentSubmission already prints it once per denial
    // episode, attributed to the body that lost its grant.
    private void Submit(MountedAddon addon, int slot, ulong tick) {
        ref var contribution = ref addon.Contributions[slot];
        var bodyIndex = contribution.BodyIndex;

        if (m_server.Body(index: bodyIndex) is not { } body) {
            contribution.Outcome = AddonVerdict.NoSuchSubject;

            return;
        }

        var submission = new IntentSubmission(
            Tick: tick,
            EntityIndex: bodyIndex,
            Intent: RoleChannels(
                values: contribution.Values,
                channels: m_server.Population.Channels
            ),
            Principal: addon.Principal,
            // The same split as SeatController: movement roles ride Intent; composition ordinals ride the held-device
            // image. The held image overlays a tape-driven body, so a guest's press reaches it like a human's held
            // button; WorldBody.Advance consumes it after one tick.
            HeldChannels: CompositionChannels(
                values: contribution.Values,
                channels: m_server.Population.Channels
            )
        );
        var verdict = m_server.ApplyIntentSubmission(
            body: body,
            submission: in submission
        );

        if (!verdict.IsAllowed) {
            contribution.Outcome = WorldAddonWire.FromRule(rule: verdict.Rule);

            return;
        }

        contribution.Outcome = AddonVerdict.None;

        // Nudge a granted body Live the first tick it is not, mirroring a fresh seat's own default so a newly-granted
        // addon does not sit waiting on a wander/idle producer to yield. Applied DIRECTLY, never through the loopback:
        // this is re-derived by re-running the guest under replay's re-run posture, so it must never be recorded as
        // server input. ApplyCommand re-checks Drive itself — a handle designates, it never decides.
        //
        // ApplyIntentSubmission's ALLOWED verdict answers only "does this principal hold Drive reach over the body" —
        // on a HUMAN-OCCUPIED body that says nothing about whether the fold actually accepted anything from this
        // addon: StageContribution still refuses a channel this document-mounted addon never declared Reach over,
        // silently, and a submission that clears every ordinal that way must not be allowed to cancel the seat's own
        // Idle/Wander/Attend control. So the nudge is gated a second time, narrower than Drive authority: an
        // UNOCCUPIED body is nudged exactly as before (a bot at full authority); a HUMAN-OCCUPIED body is nudged
        // only when this contribution actually reached its OWN declared Reach on at least one channel.
        if (
            (body.Source != IntentSource.Live) &&
            (!m_server.Population.IsHumanOccupied(bodyIndex: bodyIndex) || ContributionAccepted(
            bodyIndex: bodyIndex,
            principal: addon.Principal,
            values: in contribution.Values
        ))
        ) {
            m_server.ApplyCommand(command: new WorldCommand.SetControl(
                Principal: addon.Principal,
                EntityIndex: bodyIndex,
                Source: IntentSource.Live
            ));
        }
    }
    // The one-directional map from the shared admission predicate's decided rule onto this door's own cataloged
    // refusal, and from there onto the wire verdict staged into the guest's reserved answer cell. Total over the
    // rules this pre-flight can actually reach — the two ROW-scoped rules cannot fire here (it passes no row-scoped
    // Edit subject; that gate runs at apply), so a rule arriving from them is a wiring change to make deliberately
    // rather than a value to map by default.
    private static AddonVerdict ToWireVerdict(in WorldMutationAdmission admission) => admission.Rule switch {
        WorldMutationAdmissionRule.SectionDenied => WorldAddonWire.FromRule(rule: admission.Verdict.Rule),
        // A hold whose mask does not cover this kind answers as attenuation — "requested more than the mask admits"
        // attenuates to nothing, exactly like an unrequested Ask. A hold with NO mask is a DIFFERENT case now and
        // never reaches here: an absent mask means full reach at the predicate, and the grant door refuses a maskless
        // untrusted Mutate/section row outright, so this door can no longer be handed one.
        WorldMutationAdmissionRule.MaskedKind => AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.MaskedKind),
        WorldMutationAdmissionRule.MissingBudget => AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.MissingBudget),
        WorldMutationAdmissionRule.BudgetExhausted => AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.DispatchBudgetExhausted),
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(admission),
        actualValue: admission.Rule,
        message: "unmapped mutation-admission rule at the addon pre-flight — extend the mapping deliberately, never default it"
    ),
    };
    // The wire carries a handle in two u16 lanes while the table counts in int. A value past either lane cannot be
    // expressed and must never be truncated into one that can: the wrapped pair would be a LIVE handle naming something
    // else. Unreachable on today's table sizes (a projection is bounded by the population), which is exactly why it is
    // worth a check rather than a comment.
    private static bool TryPack(MountedAddon addon, WorldHandle handle, out ushort index, out ushort generation) {
        if (
            (((uint)handle.Index) > ushort.MaxValue) ||
            (((uint)handle.Generation) > ushort.MaxValue)
        ) {
            index = 0;
            generation = 0;

            if (!addon.DiscrepancyReported) {
                ReportDiscrepancy(
                    addon: addon,
                    detail: $"handle (index {handle.Index}, generation {handle.Generation}) exceeds the wire's 16-bit lanes — it cannot be handed across the ABI without aliasing another handle, so it was withheld"
                );
            }

            return false;
        }

        index = ((ushort)handle.Index);
        generation = ((ushort)handle.Generation);

        return true;
    }
    // Reads a watch's whole byte range as one little-endian, zero-extended i64 — fails the WHOLE watch (no partial
    // value, no baseline update) if any byte in the range cannot be peeked, so a transient "screen has no machine"
    // state never smuggles a half-composed value into the comparison.
    private static bool TryReadWatch(IWorldMachineMemoryPeek peek, WorldAddonMemoryWatch watch, out long value) {
        value = 0L;

        for (var offset = 0; (offset < watch.Length); ++offset) {
            if (!peek.TryPeek(
                screen: watch.Screen,
                address: (watch.Address + offset),
                value: out var b
            )) {
                return false;
            }

            value |= (((long)b) << (offset * 8));
        }

        return true;
    }
    // Copies a Section ask's name bytes out of the guest's OWN linear memory — the pointer-safety stage of the
    // name-keyed ask boundary, mirroring ResolveMutations' identical copy for a SubmitMutation payload: a length
    // ceiling check (AddonAbi.MaxSectionNameBytes) before a single byte is read, then an immediate host-owned copy
    // via AddonInstance.TryCopyMemory (bounds-checked against the guest's actual memory length). Both failure modes
    // are refused on THIS ask alone, never a whole-instance fault — a guest naming a bad pointer or an oversized
    // length gets a same-shape refusal on the ask, exactly like a malformed mutation payload does.
    private static bool TryResolveSectionAskName(MountedAddon addon, in AddonAskSubmission ask, out string name, out AddonVerdict? refusal, out string error) {
        var lengthUnsigned = unchecked((ulong)ask.NameLength);

        if (lengthUnsigned > ((ulong)AddonAbi.MaxSectionNameBytes)) {
            name = "";
            refusal = AddonVerdict.PayloadTooLarge;
            error = $"section-name length {ask.NameLength} exceeds {AddonAbi.MaxSectionNameBytes}";

            return false;
        }

        var length = ((int)lengthUnsigned);

        Span<byte> buffer = stackalloc byte[length];

        if (!addon.Instance.TryCopyMemory(
            pointer: ask.SubjectIndex,
            length: length,
            destination: buffer,
            error: out var copyError
        )) {
            name = "";
            refusal = AddonVerdict.MalformedPayload;
            error = copyError;

            return false;
        }

        name = Encoding.UTF8.GetString(bytes: buffer);
        refusal = null;
        error = "";

        return true;
    }

    /// <summary>Resolves each staged input act through the guest's own Drive handle table — pump point 2, after the
    /// intent drain and before the population advances — folds the acts into one <see cref="PlayerIntent"/> per
    /// contributed body, and submits each through the same authority path a seat's submission runs
    /// (<see cref="WorldServer.ApplyIntentSubmission"/>). <b>What happens next to a body a seat co-drives is no longer a
    /// plain overwrite</b> (<see cref="FixedContributionFold"/>): on a human-occupied body the
    /// submission routes into that tick's per-body contribution set instead — both halves of it, the intent and the
    /// held-channel composition image — bounded by this guest's own declared reach
    /// (<see cref="WorldGrant.Reach"/>) and by the ceiling the occupying seat authored per channel on its own
    /// row (<see cref="WorldGrant.Ceiling"/>), and folded with the seat's own value by
    /// <see cref="WorldServer"/>'s own channel-contribution fold — never tracked as contention, because a consented (or
    /// default-denied) contribution is the feature, not a race. An unoccupied body is untouched: it still applies
    /// exactly as this paragraph used to describe for every body, contention reporting included, because occupancy is
    /// what makes a pool exist at all. Every channel — movement role and composition alike — is folded fresh from this
    /// tick's acts only: the host holds no cross-tick channel state, so a guest that stops acting on a body simply
    /// stops contributing to it, the same way a seat's analog clear works.</summary>
    /// <param name="tick">The tick the submissions are for.</param>
    public void ApplyContributions(ulong tick) {
        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];

            if (addon.Instance.State != AddonState.Enabled) {
                // FoldActs did not run this tick, so its own bottom-of-method latch reset did not run either — clear
                // it here so a guest disabled or faulted between an exhaustion and its next FoldActs call does not
                // carry an armed-but-orphaned latch into a later enable and swallow the next real exhaustion.
                addon.DriveDispatchBudgetExhaustedReported = false;

                continue;
            }

            FoldActs(
                addon: addon,
                tick: tick
            );
        }
    }
    /// <summary>Routes an addon-sourced mutation's decided outcome back to its originating guest's reserved answer
    /// cell — called by <see cref="WorldServer.Step"/>'s <c>DrainPendingOps</c>, in the
    /// same Step the act was decoded, immediately after the mutation's compose→revalidate→swap ran. Never applies
    /// anything itself; it only records which verdict <see cref="ResolveReads"/> will stage into the guest's next
    /// batch. A no-op if <paramref name="addonIndex"/> no longer names a mounted guest (defensive — lifecycle verbs
    /// refuse while a recording is armed, but a reload/enable/disable between decode and drain within an
    /// unarmed session is not itself refused, and a stale index must never write into the wrong guest).</summary>
    /// <param name="addonIndex">The mounted addon index the act was decoded from.</param>
    /// <param name="actOrdinal">The addon's own output-batch ordinal the act answers.</param>
    /// <param name="applied">Whether the document-apply pipeline accepted the decoded mutation.</param>
    public void CompleteMutation(int addonIndex, ushort actOrdinal, bool applied) {
        if (((uint)addonIndex) >= ((uint)m_mounted.Count)) {
            return;
        }

        SetReservedVerdict(
            addon: m_mounted[addonIndex],
            ordinal: actOrdinal,
            verdict: (applied
            ? AddonVerdict.Applied
            : AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.ApplyRejected))
        );
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
    /// <summary>Disposes the addon host — every guest store plus the owned Wasmtime engine (native resources).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        m_host?.Dispose();
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
    /// <summary>Resolves each guest's disclosures, world-event pushes, and queued asks/pose queries — pump point 3,
    /// after the population advances and before the snapshot is emitted. This is the pinned
    /// drain point: a verdict, a minted handle, and a pose all reflect the grant table and the authoritative state as of
    /// the step of the tick the record was written in. Disclosures are pushed first (the guest's bootstrap — enumeration
    /// is itself a capability, so a guest cannot know a body index until the host hands it one), then world events
    /// (four families plus the guest's own machine-memory watches), then asks and pose queries are answered, and the
    /// whole result is budgeted into the guest's input batch for the next tick.</summary>
    /// <param name="tick">The tick whose reads are being resolved.</param>
    public void ResolveReads(ulong tick) {
        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];

            if (addon.Instance.State != AddonState.Enabled) {
                // StageBatch — and therefore ResolveQueries and MergeAnswers — did not run this tick, so neither
                // latch's own bottom-of-method reset ran either. Same reasoning as ApplyContributions' twin above,
                // for both episodes this stage owns: a guest that stops being pumped has no open episode left to
                // report on, on either axis, regardless of why it stopped.
                addon.DispatchBudgetExhaustedReported = false;
                addon.QuotaDropReported = false;

                continue;
            }

            StageBatch(
                addon: addon,
                tick: tick
            );
        }
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
    /// <summary>Composes each guest's input batch (the tick cell, then the disclosures and answers staged at the end
    /// of the previous tick), runs <c>puck_on_tick</c>, and decodes plus vocabulary-validates the returned batch
    /// through the Simulation adapter — pump point 1, the top of <see cref="WorldServer.Step"/>, before the
    /// pending-edit and intent drains. Nothing is applied here: a validated batch is only staged, so a guest's pose
    /// reads and its acts both resolve at their own pinned points later in the same tick.</summary>
    /// <param name="tick">The tick the batch reports — the same tick number a seat's submission carries.</param>
    public void TickAddons(ulong tick) {
        // The addon mutation seam's GLOBAL byte meter resets once per Step, before any addon's acts are decoded —
        // see AddonAbi.MaxMutationBytesPerTickAllAddons.
        m_mutationBytesThisTickAllAddons = 0;

        for (var index = 0; (index < m_mounted.Count); ++index) {
            var addon = m_mounted[index];
            var instance = addon.Instance;

            if (instance.State != AddonState.Enabled) {
                addon.LastTickFuelConsumed = 0UL;
                ReportFault(addon: addon);

                continue;
            }

            // Enabled-but-unadmitted is a host sequencing state Admit runs at mount, and Reload/SetEnabled (below)
            // re-run it immediately after re-instantiating — so this is unreachable through this runtime's own
            // lifecycle verbs. It is kept as a defensive skip, not an armed trap, for any FUTURE caller that reaches
            // AddonHost.Reload/SetEnabled directly rather than through this type's wrappers (ticking an unadmitted
            // instance throws by contract).
            if (!instance.Admitted) {
                addon.LastTickFuelConsumed = 0UL;

                if (!addon.DiscrepancyReported) {
                    ReportDiscrepancy(
                        addon: addon,
                        detail: "instance is enabled but was never re-admitted after a reload/enable — skipped every tick (a caller bypassed WorldAddonRuntime.Reload/SetEnabled, which re-admit)"
                    );
                }

                continue;
            }

            // The boot-anchored replay arm predicate's own latch: an admitted execution is about to be ATTEMPTED,
            // unconditionally, regardless of what the tick below does — see MountedAddon.HasEverPumped's own doc.
            addon.HasEverPumped = true;

            var batch = addon.Batch;

            batch[0] = new AddonInCell(
                A: ((long)tick),
                B: 0L,
                Channel: 0,
                HandleGeneration: 0,
                HandleIndex: 0,
                Kind: AddonInCellKind.Tick,
                Ordinal: 0,
                Verb: 0,
                Verdict: AddonVerdict.None
            );

            for (var pending = 0; (pending < addon.PendingCount); ++pending) {
                batch[(pending + 1)] = addon.Pending[pending];
            }

            // Guaranteed within the guest's declared capacity: ResolveReads budgets the pending buffer to capacity - 1,
            // and the tick cell is the one this reserves.
            var count = (addon.PendingCount + 1);

            addon.PendingCount = 0;

            var pumped = addon.Pump.Pump(
                instance: instance,
                input: batch.AsSpan(
                    length: count,
                    start: 0
                )
            );

            // Fuel spent THIS tick, whether the tick succeeded or trapped (a trap that burns the whole budget before
            // faulting is the spinning-guest case an operator needs to see) — read from the pump, the one crossing
            // that already reads the tick result. Accumulated into the running total saturating rather than
            // wrapping: a document may admit per-tick fuel up to long.MaxValue, so faulting ticks could otherwise
            // overflow the ulong total and run it backwards.
            var fuelConsumedThisTick = addon.Pump.FuelConsumed;

            addon.LastTickFuelConsumed = fuelConsumedThisTick;
            addon.TotalFuelConsumed = ((addon.TotalFuelConsumed > (ulong.MaxValue - fuelConsumedThisTick))
                ? ulong.MaxValue
                : (addon.TotalFuelConsumed + fuelConsumedThisTick)
            );

            if (!pumped) {
                // The pump returns false only when the instance faulted (a trap, or a whole-batch vocabulary refusal);
                // neither prints anything of its own, so the attribution belongs here.
                ReportFault(addon: addon);

                addon.ReservedCount = 0;

                continue;
            }

            // The addon mutation seam's I1: SubmitMutation acts in THIS batch are decoded and dispatch-gated
            // (stages 1-5 of the six-stage door) right here, at whole-batch decode time — before EmitDisclosures/
            // MergeAnswers (pump point 3) ever see the remaining answer budget, and before DrainPendingOps (later
            // in this SAME Step, before intents) applies whatever cleared the door.
            ResolveMutations(
                addon: addon,
                addonIndex: index,
                tick: tick
            );
        }
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

    private enum EventGateStatus : byte {
        None,
        Available,
        Exhausted,
    }
    // One body's accumulating contribution for a tick — the world's channel VECTOR, every ordinal declarative and
    // per-tick, the host holding no state across ticks for any of them. Values is opened fresh per (tick, body) by
    // Contribution and written by ordinal in Fold (deterministic FixedQ4816 throughout — no float ever crosses),
    // then split at submission: movement roles become Intent and composition ordinals become IntentSubmission's
    // one-tick held-channel overlay (WorldBody.Advance clears the image it consumed, so no host-side release
    // bookkeeping is needed here — the body's own one-tick contract already provides it). Outcome is filled by Submit
    // and read by the answering pass, with None meaning "allowed, answer nothing".
    private struct BodyContribution(int bodyIndex) {
        public int BodyIndex = bodyIndex;
        public ChannelValues Values = default;
        public AddonVerdict Outcome = AddonVerdict.None;
    }
    // One mounted guest's whole host-side state. Every per-tick buffer is allocated once here, at mount, and reused with
    // a count — a tick allocates nothing on this path.
    private sealed class MountedAddon {
        public MountedAddon(AddonInstance instance, IReadOnlyList<WorldCapabilityRequest>? requests, int populationCapacity, IReadOnlyList<WorldAddonMemoryWatch>? memoryWatches = null) {
            ActBody = new int[AddonAbi.MaxOutCells];
            // A pose answer is the widest response shipping, so the worst case is every output cell being a query.
            Answers = new AddonInCell[(AddonAbi.MaxOutCells * AddonAbi.RequestVerbs.BodyPoseAnswerParts)];
            Batch = new AddonInCell[AddonAbi.MaxInCells];
            Contributions = new BodyContribution[AddonAbi.MaxOutCells];
            DisclosedDrive = [];
            DisclosedObserve = [];
            // Per-body query dispatch count THIS TICK — a query's subject is always a Body (ResolveQueries refuses
            // anything else as a stale handle before it reaches the budget check), so a body-indexed array is the
            // zero-alloc counter. Reset once per tick in StageBatch, beside AnswerCount.
            DispatchCounts = new int[populationCapacity];
            EventCounts = new Dictionary<GrantSubject, int>();
            // The Drive twin of DispatchCounts — per-body act dispatch count THIS TICK, reset once per tick in
            // FoldActs (pump point 2 runs before StageBatch's own reset, which serves pump point 3's Observe meter).
            DriveDispatchCounts = new int[populationCapacity];
            Instance = instance;
            // The host-owned copy destination for stage 5's pointer-safety read — sized to the payload ceiling so
            // one buffer serves every act this guest ever submits, reused tick to tick, never per-act allocated.
            MutationPayloadBuffer = new byte[AddonAbi.MaxMutationPayloadBytes];
            // The RESERVATION buffer: one slot per SubmitMutation act decoded THIS tick, reserved at whole-batch
            // decode time (TickAddons -> ResolveMutations) before EmitDisclosures/MergeAnswers ever see the
            // remaining budget. Sized to MaxOutCells, the same worst case Answers uses, because the ABI handshake
            // already proves a guest's whole batch (every kind of act combined) cannot exceed its declared outCap.
            ReservedAnswers = new AddonInCell[AddonAbi.MaxOutCells];
            Pending = new AddonInCell[AddonAbi.MaxInCells];
            Principal = WorldPrincipal.Addon(name: instance.Name);
            Pump = new AddonSimulationPump();
            Requests = requests;
            ResponseChannel = ResolveResponseChannel(instance: instance);
            MemoryWatches = memoryWatches;
            MemoryWatchState = (((memoryWatches is { Count: > 0 })
                ? new WatchState[memoryWatches.Count]
                : null));
        }

        /// <summary>One machine-memory watch's own state: whether a baseline value has been observed yet (the first
        /// successful peek establishes it without emitting an edge — there is no "previous" to compare against), and
        /// the last observed value.</summary>
        public readonly record struct WatchState(bool Initialized, long Value);

        /// <summary>Gets the body each staged act resolved to, by act index, or <see cref="NoBody"/>.</summary>
        public int[] ActBody { get; }
        public int AnswerCount { get; set; }
        /// <summary>Gets this tick's unsorted, unbudgeted answer cells.</summary>
        public AddonInCell[] Answers { get; }
        /// <summary>Gets the composed input batch handed to <c>puck_on_tick</c>.</summary>
        public AddonInCell[] Batch { get; }
        /// <summary>Gets the collision begin/end cells delivered across this mount's lifetime; diagnostic only.</summary>
        public ulong CollisionEventsDelivered { get; set; }
        public int ContributionCount { get; set; }
        /// <summary>Gets the per-body folded contributions, kept sorted ascending by body index.</summary>
        public BodyContribution[] Contributions { get; }
        public bool Disclosed { get; set; }
        public GrantSubject[] DisclosedDrive { get; set; }
        // The instance Generation (AddonInstance.Generation) the disclosure above was computed against. A
        // disable/enable cycle re-instantiates the SAME AddonInstance object in place (Puck.Scripting.AddonHost
        // reuses it; only AddonHost.Reload swaps the object), so DisclosedRevision alone cannot tell a live,
        // still-known guest apart from one whose linear memory was just wiped. -1 so the very first resolve always
        // projects.
        public int DisclosedGeneration { get; set; } = -1;
        public GrantSubject[] DisclosedObserve { get; set; }
        // -1 so the very first resolve always projects, even against a fresh table whose own Revision starts at 0.
        public int DisclosedRevision { get; set; } = -1;
        public bool DisclosureOverflowReported { get; set; }
        public bool DiscrepancyReported { get; set; }
        // Latches the once-per-episode QuotaExhausted host line separately from every other Reported flag above —
        // this one names a per-row budget and its offending subject, which none of the shared-latch discrepancy
        // lines do, so it earns its own gate rather than silently sharing (and starving) DiscrepancyReported's.
        public bool DispatchBudgetExhaustedReported { get; set; }
        /// <summary>Gets this tick's per-body Observe query dispatch count, indexed by body index — the meter
        /// <see cref="WorldGrants.TryGetBudget"/>'s per-row budget is charged against. Cleared once per tick in
        /// <c>StageBatch</c>, the same sweep that resets <see cref="AnswerCount"/>.</summary>
        public int[] DispatchCounts { get; }
        // The Drive twins of DispatchBudgetExhaustedReported/MissingBudgetReported, beside them for the same reason
        // (a shared latch with the Observe pair would let one capability's line starve the other's).
        public bool DriveDispatchBudgetExhaustedReported { get; set; }
        /// <summary>Gets this tick's per-body Drive act dispatch count, indexed by body index — the Drive twin of
        /// <see cref="DispatchCounts"/>. Cleared once per tick in <c>FoldActs</c>, since pump point 2 runs before
        /// <c>StageBatch</c>'s own reset.</summary>
        public int[] DriveDispatchCounts { get; }
        public bool DriveMissingBudgetReported { get; set; }
        /// <summary>Gets the world-event and memory-watch cells delivered across this mount's lifetime; diagnostic only.</summary>
        public ulong EventCellsDelivered { get; set; }
        /// <summary>Gets this tick's event-cell charge count by Observe subject. Cleared once per tick in
        /// <c>StageBatch</c>.</summary>
        public Dictionary<GrantSubject, int> EventCounts { get; }
        /// <summary>Gets this tick's lifetime event-gap counter (the overflow doctrine's per-mount summary — see
        /// <c>Scripting.AddonAbi.ObservationVerbs.EventGap</c>'s own doc) — saturating, never reset. Every edge
        /// dropped because its per-row event budget or the ring ran out of room (world events or a memory-watch change
        /// alike) adds here.</summary>
        public ulong EventGapCount { get; set; }
        public bool FaultReported { get; set; }
        /// <summary>Gets a value indicating whether an admitted execution of this guest has ever been attempted — set unconditionally the
        /// first time <see cref="TickAddons"/> reaches the point of driving it (regardless of whether the tick
        /// traps), never cleared. The boot-anchored replay arm predicate: offline replay
        /// creates fresh guests at sim-counter zero, so a guest's accumulated memory/tick state before this latch
        /// was set is exactly what a recording begun after it cannot re-establish.</summary>
        public bool HasEverPumped { get; set; }
        /// <summary>Gets the guest instance.</summary>
        public AddonInstance Instance { get; }
        /// <summary>Gets the <see cref="EventGapCount"/> value last actually staged into a batch — an <c>EventGap</c>
        /// cell is only worth re-emitting when the count has moved since the last one that fit.</summary>
        public ulong LastReportedEventGap { get; set; }
        // The cost surface: fuel consumed is measured every tick. LastTickFuelConsumed is this tick's spend (0 on a
        // tick the guest did not run — faulted, disabled, or skipped enabled-but-unadmitted); TotalFuelConsumed is a
        // LIFETIME figure for this guest's name, since it was first mounted — SetEnabled never touches this object
        // (a disable/enable mutates the same Instance in place), and Reload's wholesale MountedAddon replacement
        // carries the prior value forward explicitly rather than re-zeroing it. DIAGNOSTIC ONLY: read by the
        // world.addons verb, never by simulation state and never on a hashed path. Saturates at ulong.MaxValue
        // rather than wrapping.
        public ulong LastTickFuelConsumed { get; set; }
        /// <summary>Gets the per-watch last-observed state, parallel to <see cref="MemoryWatches"/> by index; null when the
        /// row declares no watches.</summary>
        public WatchState[]? MemoryWatchState { get; }
        /// <summary>Gets the row's machine-memory watch declarations (the fifth event family — addon-scoped, unlike the
        /// other four; see <see cref="WorldEventFeed"/>'s own remarks). Null/empty means none.</summary>
        public IReadOnlyList<WorldAddonMemoryWatch>? MemoryWatches { get; }
        // Mirrors DispatchBudgetExhaustedReported for the SAME reason, beside it: the missing-budget host line in
        // ResolveQueries names a per-row subject too, so it gets its own latch rather than sharing (and risking
        // starvation from) DiscrepancyReported's.
        public bool MissingBudgetReported { get; set; }
        public bool MutateByteBudgetExhaustedReported { get; set; }
        /// <summary>Gets this tick's running mutation-payload byte total for this addon, metered against
        /// <see cref="AddonAbi.MaxMutationBytesPerTickPerAddon"/>.</summary>
        public int MutateBytesThisTick { get; set; }
        // The Mutate twins of DispatchBudgetExhaustedReported/MissingBudgetReported, beside them for the identical
        // reason (a shared latch across capabilities would let one starve another's line).
        public bool MutateDispatchBudgetExhaustedReported { get; set; }
        public bool MutateMissingBudgetReported { get; set; }
        /// <summary>Gets the host-owned scratch buffer stage 5's pointer-safety copy reads a <c>SubmitMutation</c>
        /// payload into, reused every act and every tick.</summary>
        public byte[] MutationPayloadBuffer { get; }
        // The Generation twin of OverflowedAtRevision, for the identical reason DisclosedGeneration exists beside
        // DisclosedRevision — a fresh store still deserves one honest attempt to fit, even if the previous
        // (now-wiped) instance last overflowed at the same grant-table revision.
        public int OverflowedAtGeneration { get; set; } = -1;
        // The revision an oversized disclosure set overflowed at — re-projection waits for the next grant-table
        // write, the only event that can shrink the set (-1 = never overflowed).
        public int OverflowedAtRevision { get; set; } = -1;
        /// <summary>Gets the cells staged for the next tick's batch (disclosures, then answers).</summary>
        public AddonInCell[] Pending { get; }
        public int PendingCount { get; set; }
        /// <summary>Gets the mount-bound acting identity — never carried on a record.</summary>
        public WorldPrincipal Principal { get; }
        /// <summary>Gets the adapter crossing that drives this guest and validates its vocabulary.</summary>
        public AddonSimulationPump Pump { get; }
        public bool QuotaDropReported { get; set; }
        /// <summary>Gets the row's manifest — the left half of requests ∧ grants. Null means the row asked for nothing, so
        /// nothing materializes.</summary>
        public IReadOnlyList<WorldCapabilityRequest>? Requests { get; }
        /// <summary>Gets this tick's reserved answer cells — one per <c>SubmitMutation</c> act
        /// decoded this tick, reserved at whole-batch decode time before <c>EmitDisclosures</c>/<c>MergeAnswers</c>
        /// ever see the remaining budget. <see cref="ResolveMutations"/> sets each slot's <c>Verdict</c> the
        /// moment it is known (synchronously for a stage 1-5 refusal; later, via <c>CompleteMutation</c>, for an
        /// act that reached the pending queue) — <see cref="AddonVerdict.None"/> is the "still pending" sentinel
        /// between decode and drain within the same Step.</summary>
        public AddonInCell[] ReservedAnswers { get; }
        /// <summary>Gets how many of <see cref="ReservedAnswers"/> are live this tick.</summary>
        public int ReservedCount { get; set; }
        /// <summary>Gets the guest's declared Response channel index, or <c>-1</c> when it declares none.</summary>
        public int ResponseChannel { get; }
        /// <summary>Gets the route-engaged/disengaged cells delivered across this mount's lifetime; diagnostic only.</summary>
        public ulong RouteEventsDelivered { get; set; }
        public bool StaleHandleReported { get; set; }
        // A LIFETIME count of answer groups MergeAnswers found no cell for at all (the ring's own hard ceiling —
        // never recoverable by a bigger squeeze), the same saturating-ulong shape as TotalFuelConsumed and for the
        // identical reason: the stderr line QuotaDropReported gates is per-episode and scrolls away, so without a
        // durable counter an operator who was not watching the console at that instant has no way to learn this ever
        // happened. DIAGNOSTIC ONLY, read by world.addons — never simulation state, never on a hashed path.
        public ulong TotalAnswersDropped { get; set; }
        public ulong TotalFuelConsumed { get; set; }
        public bool UndeliverableReported { get; set; }
        public bool UnrequestedActReported { get; set; }
    }
}
