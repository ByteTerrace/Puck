using Puck.Assets;
using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

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
public sealed partial class WorldAddonRuntime : IWorldAddonHost {
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
    private static void ReportDiscrepancy(MountedAddon addon, string detail) {
        if (addon.DiscrepancyReported) {
            return;
        }

        addon.DiscrepancyReported = true;
        Console.Error.WriteLine(value: $"[world.addon: {addon.Instance.Name} — {detail}]");
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

    /// <summary>Disposes the addon host — every guest store plus the owned Wasmtime engine (native resources).</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        m_host?.Dispose();
    }
}
