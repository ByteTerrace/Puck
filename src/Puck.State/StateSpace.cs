using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>Declares one vector embedding space with its model, revision, and component dimensions.</summary>
/// <param name="Name">The stable space name, unique within the section.</param>
/// <param name="Model">The model name, non-empty and at most 128 characters.</param>
/// <param name="Revision">The model revision, non-empty and at most 128 characters.</param>
/// <param name="Dimensions">The component dimension count, in [8, 1024].</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateSpace(
    CellName Name,
    string Model,
    string Revision,
    int Dimensions
) {
    /// <summary>The maximum character length of a space name.</summary>
    public const int MaxNameLength = 128;
    /// <summary>The maximum character length of a model identifier.</summary>
    public const int MaxModelLength = 128;
    /// <summary>The maximum character length of a revision identifier.</summary>
    public const int MaxRevisionLength = 128;

    /// <summary>Checks whether two space declarations share model, revision, and dimensions, independent of name.</summary>
    public bool HasSameIdentity(StateSpace other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        return (
            (Dimensions == other.Dimensions) &&
            string.Equals(a: Model, b: other.Model, comparisonType: StringComparison.Ordinal) &&
            string.Equals(a: Revision, b: other.Revision, comparisonType: StringComparison.Ordinal)
        );
    }
}
