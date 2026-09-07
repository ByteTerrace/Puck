using System.Globalization;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private readonly Lock m_reflowReviewGate = new();
    private readonly Dictionary<WorldPrincipal, ReflowReview> m_reflowReviews = [];
    // Host authoring cache bounds; they never determine a simulation result.
    private const int ReflowReviewCapacity = 64;
    private const long ReflowReviewLifetimeMilliseconds = 300_000;
    private sealed record ReflowReview(Task<(WorldPlacementProposal? Proposal, string Reason)> Pending, long Created, bool Reviewed = false);

    private void ExpireReflowReviews() {
        var now = Environment.TickCount64;
        foreach (var key in m_reflowReviews.Where(pair => now - pair.Value.Created >= ReflowReviewLifetimeMilliseconds).Select(pair => pair.Key).ToArray()) {
            m_reflowReviews.Remove(key);
        }
    }

    private QueryAnswer AnswerReflowQuery(WorldQuery query, WorldPrincipal principal) {
        lock (m_reflowReviewGate) {
            ExpireReflowReviews();
            if (query is WorldQuery.ReflowCancel) {
                m_reflowReviews.Remove(principal);
                return new QueryAnswer("[world.reflow: cancelled; no layout change or payment]");
            }
            if (query is WorldQuery.ReflowPreview start) {
                if (start.Request is null) {
                    return new QueryAnswer("[world.reflow: a request is required]", Refused: true);
                }
                if (!TryStartReflowPreview(start.Request, principal, out var pending, out var reason)) {
                    return new QueryAnswer($"[world.reflow: {reason}]", Refused: true);
                }
                m_reflowReviews.Remove(principal);
                if (m_reflowReviews.Count >= ReflowReviewCapacity) {
                    m_reflowReviews.Remove(m_reflowReviews.MinBy(pair => pair.Value.Created).Key);
                }
                m_reflowReviews[principal] = new ReflowReview(pending!, Environment.TickCount64);
                return new QueryAnswer("[world.reflow: planning; read world.reflow.status for the proposal]");
            }
            if (!m_reflowReviews.TryGetValue(principal, out var entry)) {
                return new QueryAnswer("[world.reflow: preview a layout first]", Refused: true);
            }
            if (!entry.Pending.IsCompleted) { return new QueryAnswer("[world.reflow: planning]"); }
            if (!entry.Pending.IsCompletedSuccessfully) {
                m_reflowReviews.Remove(principal);
                return new QueryAnswer("[world.reflow: preview worker failed; preview again]", Refused: true);
            }
            var (proposal, refusal) = entry.Pending.Result;
            if (proposal is null) {
                m_reflowReviews.Remove(principal);
                return new QueryAnswer($"[world.reflow: {refusal}]", Refused: true);
            }
            m_reflowReviews[principal] = entry with { Reviewed = true };
            var changes = proposal.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>().Select(edit =>
                string.Create(CultureInfo.InvariantCulture, $"{edit.Placement.Id} -> local ({edit.Placement.Position.X:0.###}, {edit.Placement.Position.Y:0.###}, {edit.Placement.Position.Z:0.###}), scale={edit.Placement.Scale:0.###}"));
            return new QueryAnswer($"[world.reflow: {proposal.Moved} moved; cost={proposal.Cost}; checks={proposal.Candidates}; {string.Join("; ", changes)}; {string.Join("; ", proposal.Constraints)}]", Payload: proposal);
        }
    }

    /// <summary>Consumes the actor's reviewed authoring plan. The caller must submit its batch through the
    /// simulation mutation door; this method itself changes no world state.</summary>
    public bool TryTakeReviewedReflow(WorldPrincipal principal, out WorldPlacementProposal? proposal, out string reason) {
        lock (m_reflowReviewGate) {
            ExpireReflowReviews();
            proposal = null;
            if (!m_reflowReviews.TryGetValue(principal, out var entry) || !entry.Reviewed) {
                reason = "read world.reflow.status before committing";
                return false;
            }
            m_reflowReviews.Remove(principal);
            proposal = entry.Pending.Result.Proposal;
            reason = string.Empty;
            return proposal is not null;
        }
    }
}
