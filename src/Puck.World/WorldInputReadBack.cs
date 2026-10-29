using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Asks the authority that owns a body for its input read-back, as the acting principal. The answer crosses
/// the authority's <see cref="WorldCapability.Observe"/> check over that body, so a verb open to every caller shows
/// only the input its caller may see, and a refusal reads back as a refusal.</summary>
internal static class WorldInputReadBack {
    /// <summary>Submits a query through a link as <paramref name="caller"/> when the link attributes queries, else
    /// through the link's own door (a federated door stamps the traveler's principal itself).</summary>
    /// <param name="link">The submission door of the authority that answers.</param>
    /// <param name="caller">The acting principal whose Observe grant is checked.</param>
    /// <param name="query">The read-back query.</param>
    /// <returns>The authority's answer, which may be a refusal.</returns>
    internal static QueryAnswer Ask(IServerLink link, Principal caller, WorldQuery query) {
        var answer = new QueryAnswer(Text: "[query unanswered]", Refused: true);

        if (link is IPrincipalServerLink principals) {
            principals.Query(
                completion: result => answer = result,
                principal: caller,
                query: query
            );
        } else {
            link.Query(
                completion: result => answer = result,
                query: query
            );
        }

        return answer;
    }
    /// <summary>Asks the authority a seat is routed to about the body it holds there: the boot world's link for a
    /// seat on the boot claim, the claim's own endpoint after a crossing.</summary>
    /// <param name="bootLink">The boot world's submission door.</param>
    /// <param name="router">The seat authority router holding each seat's current claim.</param>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="caller">The acting principal whose Observe grant is checked.</param>
    /// <param name="query">Builds the query from the 0-based body index in the answering authority.</param>
    /// <returns>The authority's answer, which may be a refusal.</returns>
    internal static QueryAnswer ForSeat(IServerLink bootLink, WorldSeatAuthorityRouter router, int slot, Principal caller, Func<int, WorldQuery> query) {
        if (
            (router.TryRoute(slot: slot) is { } route) &&
            !string.Equals(
            a: route.Endpoint.Identity,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return Ask(
                caller: caller,
                link: route.Endpoint.Submissions,
                query: query(route.EntityIndex)
            );
        }

        return Ask(
            caller: caller,
            link: bootLink,
            query: query(slot)
        );
    }
    /// <summary>Renders an answer as a console result, a refusal as an error.</summary>
    /// <param name="answer">The authority's answer.</param>
    /// <returns>The result.</returns>
    internal static CommandResult Result(in QueryAnswer answer) => new(Output: answer.Text) {
        IsError = answer.Refused,
    };
}
