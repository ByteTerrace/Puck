using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

// The per-tick capture: the loopback and server taps that turn one live tick's server input into one
// WorldReplayTickInput. One capture serves two consumers — the recording a replay.record armed and the in-session
// WorldHistory — so the two never disagree about what a tick's input was, and the history stores the tape's own
// entries rather than a second format.
public sealed partial class WorldReplayTape {
    // The current tick's accumulating input, closed into one tick group at each NoteTick. One authority list, not one
    // per kind: a command and a grant that crossed the link in a given order must replay in that order, and parallel
    // lists have no relative order left to preserve.
    private List<WorldReplayEntry> m_currentAuthority = new();
    private List<IntentSubmission> m_currentIntents = new();
    // The FIFO correlation between a Mutation entry MutationTap just added (by its index into m_currentAuthority)
    // and the MutationOutcomeTap call that will patch its Outcome later the same tick — see MutationTap/
    // MutationOutcomeTap's own remarks. Always empty by the time NoteTick closes m_currentAuthority (every mutation
    // tapped this tick is also drained this tick), and cleared whenever the capture detaches.
    private readonly Queue<int> m_openMutationEntryIndices = new();
    // The recording's own leading entries for its first tick: the sessions live at the arm, re-established on the
    // tape's first tick. Never part of the history's input — its keyframes already hold those sessions.
    private readonly List<WorldReplayEntry> m_recordPrefix = [];

    private bool m_capturing;
    private int m_captureSuspensions;
    private WorldHistory? m_history;

    /// <summary>Gets whether the per-tick capture taps are attached — while a recording is armed or an in-session
    /// history is on, and never during a live drive.</summary>
    public bool Capturing => m_capturing;

    // What the capture would have to record at the next close: anything the open tick has gathered beyond a
    // rate-lever fact, which applies as a no-op, and a query, whose re-execution moves no state.
    internal bool OpenTickHoldsInput {
        get {
            if (m_currentIntents.Count != 0) {
                return true;
            }

            foreach (var entry in m_currentAuthority) {
                if (entry is not (WorldReplayEntry.RateLever or WorldReplayEntry.Query)) {
                    return true;
                }
            }

            return false;
        }
    }
    // The open tick's authority entries, read (never consumed) by the history's replay-edit.
    internal IReadOnlyList<WorldReplayEntry> OpenAuthority => m_currentAuthority;

    // Attaches every capture tap over the current per-tick accumulators.
    private void AttachTaps() {
        m_transport.IntentTap = submission => m_currentIntents.Add(item: submission);
        m_transport.CommandTap = command => m_currentAuthority.Add(item: new WorldReplayEntry.Command(Value: command));
        m_transport.DesignationTap = (designation, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Designation(
            Actor: actor,
            Value: designation
        ));
        m_transport.GrantTap = (grant, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Grant(
            Actor: actor,
            Value: grant
        ));
        m_transport.RevokeTap = (grant, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Revoke(
            Actor: actor,
            Value: grant
        ));
        m_transport.SessionTap = request => m_currentAuthority.Add(item: new WorldReplayEntry.Session(Value: request));
        // Apply-time on the server, not at the loopback: the loopback is only one of three mutation ingresses (a
        // local console/client write, an admitted socket peer, and a traveller's submission forwarded by its source
        // authority), and only the envelope dispatch sees all three with the actor each one stamped. Outcome starts
        // false — MutationOutcomeTap patches it to the real accept/refuse verdict before this same tick closes (both
        // fire from within the one server.Step this entry's own tick belongs to; see the field's own remarks).
        m_liveServer.MutationTap = (mutation, actor) => {
            m_openMutationEntryIndices.Enqueue(item: m_currentAuthority.Count);
            m_currentAuthority.Add(item: new WorldReplayEntry.Mutation(
                Actor: actor,
                Outcome: false,
                Value: mutation
            ));
        };
        m_liveServer.MutationOutcomeTap = (_, applied) => {
            // Correlates by position, never by decoding mutation content: MutationTap and MutationOutcomeTap fire in
            // the identical FIFO order — both ultimately driven by the one m_pending queue every mutation kind
            // shares — so the Nth open entry always answers the Nth outcome, even across several mutations pending
            // in the same tick.
            if (
                m_openMutationEntryIndices.TryDequeue(result: out var index) &&
                (index < m_currentAuthority.Count) &&
                (m_currentAuthority[index] is WorldReplayEntry.Mutation pending)
            ) {
                m_currentAuthority[index] = (pending with { Outcome = applied });
            }
        };
        m_liveServer.LinkDeliveryTap = adjacency => m_currentAuthority.Add(item: new WorldReplayEntry.LinkDelivery(Adjacency: adjacency));
        m_transport.UndoTap = (count, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Undo(
            Actor: actor,
            Count: count
        ));
        m_transport.CompositionTap = (composition, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Composition(
            Actor: actor,
            Value: composition
        ));
        m_transport.QueryTap = (query, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.Query(
            Actor: actor,
            Value: query
        ));
        // Apply-time, not submission-time — see WorldServer.RebuildTap's own remarks for why Reset's hash cannot be
        // known any earlier (m_base is private, server-internal state that can move between submission and drain).
        m_liveServer.RebuildTap = (request, actor, contentHash) => m_currentAuthority.Add(item: new WorldReplayEntry.Rebuild(
            Kind: request.Kind,
            Origin: request.Origin,
            Force: request.Force,
            ContentHash: contentHash,
            Actor: actor
        ));
        // Apply-time, mirroring RebuildTap exactly — a screen op the Control gate refuses is still taped (fires
        // after the outcome is known, carrying a null hash for a refusal or for any non-Insert kind).
        m_liveServer.ScreenOpTap = (op, contentHash, actor) => m_currentAuthority.Add(item: new WorldReplayEntry.ScreenOp(
            Actor: actor,
            ContentHash: contentHash,
            Value: op
        ));
        m_liveServer.ArrivalTap = arrival => m_currentAuthority.Add(item: new WorldReplayEntry.Arrival(
            Encoded: WorldAuthorityCheckpointCodec.EncodeCrossingArrival(arrival: arrival),
            SourceAuthority: arrival.Request.SourceAuthority,
            TransferId: arrival.Request.TransferId
        ));
        m_liveServer.FederatedIntentTap = held => m_currentAuthority.Add(item: new WorldReplayEntry.FederatedIntents(Held: held));
        m_liveServer.ServerEventTap = serverEvent => {
            switch (serverEvent) {
                case WorldServerEvent.PeerAdmitted admitted:
                    m_currentAuthority.Add(item: new WorldReplayEntry.PeerAdmitted(Value: admitted));
                    break;
                case WorldServerEvent.PeerDisconnected disconnected:
                    m_currentAuthority.Add(item: new WorldReplayEntry.PeerDisconnected(Value: disconnected));
                    break;
                case WorldServerEvent.SessionAdmitted or WorldServerEvent.SessionEmbodied or WorldServerEvent.SessionEnded:
                    m_currentAuthority.Add(item: new WorldReplayEntry.SessionEvent(Value: serverEvent));
                    break;
            }
        };
    }
    private void DetachTaps() {
        m_transport.IntentTap = null;
        m_transport.CommandTap = null;
        m_transport.DesignationTap = null;
        m_transport.GrantTap = null;
        m_transport.RevokeTap = null;
        m_transport.SessionTap = null;
        m_transport.UndoTap = null;
        m_transport.CompositionTap = null;
        m_transport.QueryTap = null;
        m_liveServer.LinkDeliveryTap = null;
        m_liveServer.MutationTap = null;
        m_liveServer.MutationOutcomeTap = null;
        m_liveServer.RebuildTap = null;
        m_liveServer.ScreenOpTap = null;
        m_liveServer.ServerEventTap = null;
        m_liveServer.ArrivalTap = null;
        m_liveServer.FederatedIntentTap = null;
    }
    // The one place the taps attach or detach: the capture runs while a recording is armed or a history is on, never
    // during a live drive (the drive feeds recorded input through the server's own doors) and never while a history
    // re-simulation holds it suspended. A detach drops whatever the open tick had gathered — nothing consumes it.
    private void RefreshCapture() {
        var wanted = (
            (m_captureSuspensions == 0) &&
            (m_mode != WorldReplayMode.Replaying) &&
            ((m_mode == WorldReplayMode.Recording) || (m_history is not null))
        );

        if (wanted == m_capturing) {
            return;
        }

        m_capturing = wanted;

        if (wanted) {
            AttachTaps();

            return;
        }

        DetachTaps();
        m_openMutationEntryIndices.Clear();
        m_currentAuthority.Clear();
        m_currentIntents.Clear();
    }

    // The history attaches itself here; its NoteTick then receives every closed tick the capture produces.
    internal void AttachHistory(WorldHistory history) {
        ArgumentNullException.ThrowIfNull(argument: history);

        m_history = history;
        RefreshCapture();
    }
    internal void DetachHistory() {
        m_history = null;
        RefreshCapture();
    }
    // A history re-simulation drives recorded input through the live server's own doors; the taps those doors fire
    // must not capture it a second time.
    internal void SuspendCapture() {
        m_captureSuspensions++;
        RefreshCapture();
    }
    internal void ResumeCapture() {
        m_captureSuspensions--;
        RefreshCapture();
    }
}
