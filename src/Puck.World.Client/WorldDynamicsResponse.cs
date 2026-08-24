using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>Resolves a <c>dynamics</c> row reference into its presentation-side <see cref="SecondOrderResponse"/> —
/// the row lookup (<see cref="WorldDefinitionRows.FindDynamics"/>) plus the derivation
/// (<see cref="SecondOrderResponse.Create"/>), in one call. A missing name or a dangling row name (the validator
/// refuses an authored one; a mid-mutation document swap can transiently miss one) both resolve <see langword="false"/>
/// — a dangling reference carries no follower, it is never a refusal at this seam.</summary>
internal static class WorldDynamicsResponse {
    /// <summary>Resolves <paramref name="name"/> against <paramref name="rows"/> and derives its response.</summary>
    /// <param name="rows">The document's declared <c>dynamics</c> rows.</param>
    /// <param name="name">The referenced row's name, or <see langword="null"/> for no reference.</param>
    /// <param name="response">The derived response, or default when unresolved.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> named a live row.</returns>
    public static bool TryResolveResponse(IReadOnlyList<WorldDynamicsRow>? rows, string? name, out SecondOrderResponse response) {
        if (
            (name is not { Length: > 0 }) ||
            (WorldDefinitionRows.FindDynamics(
            dynamics: rows,
            name: name
        ) is not { } row)
        ) {
            response = default;

            return false;
        }

        var parameters = row.Parameters;

        response = SecondOrderResponse.Create(
            dampingRatio: parameters.Damping,
            frequencyHz: parameters.Frequency,
            initialResponse: parameters.Response
        );

        return true;
    }
}
