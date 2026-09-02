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

    // The world's own channel table — every guest's declared names resolve against it, at boot AND at every later
    // prepare, so an act's ordinal IS a PlayerIntent ordinal on either path and the fold never needs a second
    // mapping of its own. Reassigned WHOLESALE by Commit, alongside m_mounted/m_receipts, when a prepare pass finds
    // the candidate's own channel declarations changed (a whole-document rebuild/undo, never a live mutation — no
    // live mutation kind touches the channels section for a running server's whole life).
    private WorldChannelTable m_channels;
    // The exact declaration list m_channels was last compiled from — TryPrepare's own channel-table dependency
    // check compares a candidate's Channels against this by content, never by reference (a freshly deserialized
    // rebuild/undo candidate never shares object identity with what booted).
    private IReadOnlyList<WorldChannel> m_channelsSource;

    private readonly WorldServer m_server;

    private bool m_disposed;
    private AddonHost? m_host;
    // The MountedAddon.InstanceId source: pre-incremented every time TryPrepare instantiates a genuinely fresh
    // guest (never on a reuse, which carries the existing object and its existing id forward). Advances even inside
    // a plan that is later discarded (a refused mutation, an undo probe) — the exact value is never printed or
    // hashed, only compared for identity, so a gap left by a discarded plan costs nothing.
    private long m_nextInstanceId;
    // Both reassigned WHOLESALE by Commit — a single reference swap, never mutated element-by-element — so an
    // unrelated read mid-tick (Mutations/Queries/Events/Pump, all in this partial class) always sees either the
    // fully-prior or the fully-new set, never a half-built one.
    private List<MountedAddon> m_mounted = [];
    // Recorded AT MOUNT, in mount order, for every guest that actually reached the tickable set — a row that faulted
    // or failed to prepare produces no receipt, which is what makes a missing receipt the honest report of a mount
    // that did not happen.
    private List<WorldAddonReceipt> m_receipts = [];
    // The addon mutation seam's GLOBAL per-tick byte meter — shared across every mounted addon, reset at the top of
    // each TickAddons call. AddonAbi.MaxMutationBytesPerTickAllAddons bounds host-side JSON decode work per tick
    // regardless of how many guests are mounted or how their individual per-addon budgets are set.
    private int m_mutationBytesThisTickAllAddons;

    private WorldAddonRuntime(WorldDefinition definition, WorldServer server) {
        m_server = server;
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_channelsSource = definition.Channels;
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
