using Puck.Commands;
using System.Globalization;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private readonly Lock m_reflowReviewGate = new();
    private readonly Dictionary<Principal, ReflowReview> m_reflowReviews = [];

    // Host authoring cache bounds; they never determine a simulation result.
    private const int ReflowReviewCapacity = 64;
    private const long ReflowReviewLifetimeMilliseconds = 300_000;

    private sealed record ReflowReview(Task<(WorldPlacementProposal? Proposal, string Reason)> Pending, long Created, bool Reviewed = false);

    private void ExpireReflowReviews() {
        var now = Environment.TickCount64;

        foreach (var key in m_reflowReviews.Where(predicate: pair => ((now - pair.Value.Created) >= ReflowReviewLifetimeMilliseconds)).Select(selector: pair => pair.Key).ToArray()) {
            m_reflowReviews.Remove(key: key);
        }
    }

    public QueryAnswer AnswerReflowQuery(WorldQuery query, Principal principal) {
        lock (m_reflowReviewGate) {
            ExpireReflowReviews();
            if (query is WorldQuery.ReflowCancel) {
                m_reflowReviews.Remove(key: principal);
                return new QueryAnswer("[world.reflow: cancelled; no layout change or payment]");
            }
            if (query is WorldQuery.ReflowPreview start) {
                if (start.Request is null) {
                    return new QueryAnswer(
                        "[world.reflow: a request is required]",
                        Refused: true
                    );
                }
                if (!TryStartReflowPreview(
                    start.Request,
                    principal,
                    out var pending,
                    out var reason
                )) {
                    return new QueryAnswer(
                        $"[world.reflow: {reason}]",
                        Refused: true
                    );
                }
                m_reflowReviews.Remove(key: principal);
                if (m_reflowReviews.Count >= ReflowReviewCapacity) {
                    m_reflowReviews.Remove(key: m_reflowReviews.MinBy(keySelector: pair => pair.Value.Created).Key);
                }
                m_reflowReviews[principal] = new ReflowReview(
                    pending!,
                    Environment.TickCount64
                );
                return new QueryAnswer("[world.reflow: planning; read world.reflow.status for the proposal]");
            }
            if (!m_reflowReviews.TryGetValue(
                key: principal,
                value: out var entry
            )) {
                return new QueryAnswer(
                    "[world.reflow: preview a layout first]",
                    Refused: true
                );
            }
            if (!entry.Pending.IsCompleted) { return new QueryAnswer("[world.reflow: planning]"); }
            if (!entry.Pending.IsCompletedSuccessfully) {
                m_reflowReviews.Remove(key: principal);
                return new QueryAnswer(
                    "[world.reflow: preview worker failed; preview again]",
                    Refused: true
                );
            }
            var (proposal, refusal) = entry.Pending.Result;
            if (proposal is null) {
                m_reflowReviews.Remove(key: principal);
                return new QueryAnswer(
                    $"[world.reflow: {refusal}]",
                    Refused: true
                );
            }
            m_reflowReviews[principal] = entry with { Reviewed = true };
            var changes = proposal.Mutation.Mutations.OfType<WorldMutation.UpsertPlacement>().Select(selector: edit =>
                string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Placement.Id} -> local ({edit.Placement.Position.X:0.###}, {edit.Placement.Position.Y:0.###}, {edit.Placement.Position.Z:0.###}), scale={edit.Placement.Scale:0.###}"
            ));

            return new QueryAnswer(
                $"[world.reflow: {proposal.Moved} moved; cost={proposal.Cost}; checks={proposal.Candidates}; {string.Join(
                    separator: "; ",
                    values: changes
                )}; {string.Join(
                    separator: "; ",
                    values: proposal.Constraints
                )}]",
                Payload: proposal
            );
        }
    }
    /// <summary>Returns the task of the actor's preview worker started by <see cref="WorldQuery.ReflowPreview"/>, so a
    /// caller can await the proposal instead of polling <see cref="WorldQuery.ReflowStatus"/>, which remains the only
    /// reader of its result. A faulted worker faults this task too.</summary>
    /// <param name="principal">The actor whose preview to await.</param>
    /// <returns>The pending worker, or a completed task when the actor has no preview.</returns>
    public Task ReflowPreviewCompletion(Principal principal) {
        lock (m_reflowReviewGate) {
            return (m_reflowReviews.TryGetValue(
                key: principal,
                value: out var entry
            )
                ? entry.Pending
                : Task.CompletedTask
            );
        }
    }
    /// <summary>Consumes the actor's reviewed authoring plan. The caller must submit its batch through the
    /// simulation mutation door; this method itself changes no world state.</summary>
    public bool TryTakeReviewedReflow(Principal principal, out WorldPlacementProposal? proposal, out string reason) {
        lock (m_reflowReviewGate) {
            ExpireReflowReviews();
            proposal = null;
            if (
                !m_reflowReviews.TryGetValue(
                key: principal,
                value: out var entry
            ) ||
                !entry.Reviewed
            ) {
                reason = "read world.reflow.status before committing";
                return false;
            }
            m_reflowReviews.Remove(key: principal);
            proposal = entry.Pending.Result.Proposal;
            reason = string.Empty;
            return (proposal is not null);
        }
    }
}
