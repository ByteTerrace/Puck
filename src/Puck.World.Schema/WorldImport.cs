using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>One entry of the document root's <c>imports</c> list: the fragment document to fan in, and the optional
/// alias every name the fragment declares composes under (see <see cref="WorldModuleNamespace"/>). An entry with no
/// alias composes the fragment's names as authored, which is how a host that owns its modules' vocabulary imports
/// them; an aliased entry lets the same fragment compose twice, or lets two fragments spelling the same name sit
/// side by side.</summary>
/// <param name="Document">The fragment's file path, resolved against the importing document's own directory exactly
/// like <c>basis</c>.</param>
/// <param name="As">The alias, or <see langword="null"/> to compose the fragment's names unchanged. An alias is a
/// bare identifier — a letter or underscore, then letters, digits, and underscores — so an aliased name still lexes
/// as one bare name inside an infix expression and still parses as a <see cref="CellName"/>
/// (<see cref="TryValidateAlias"/>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldImport(
    string Document,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? As = null
) {
    /// <summary>The JSON member naming the fragment document.</summary>
    public const string DocumentMemberName = "document";
    /// <summary>The JSON member naming the alias.</summary>
    public const string AsMemberName = "as";

    /// <summary>Validates an alias: non-empty, a letter or underscore first, then letters, digits, and underscores
    /// only — the intersection of what a <see cref="CellName"/> admits, what <see cref="ExpressionSpelling"/> lexes
    /// as one bare name, and what cannot be mistaken for the reserved <c>$</c> vocabulary.</summary>
    /// <param name="alias">The candidate alias.</param>
    /// <param name="reason">Why it was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the alias is admissible.</returns>
    public static bool TryValidateAlias(string? alias, out string reason) {
        if (string.IsNullOrEmpty(value: alias)) {
            reason = "an import alias must be a non-empty bare identifier";

            return false;
        }

        if (!char.IsAsciiLetter(c: alias[0]) && (alias[0] != '_')) {
            reason = $"an import alias must start with a letter or underscore; '{alias}' starts with '{alias[0]}'";

            return false;
        }

        foreach (var character in alias) {
            if (!char.IsAsciiLetterOrDigit(c: character) && (character != '_')) {
                reason = $"an import alias carries only letters, digits, and underscores; '{alias}' carries '{character}'";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
}
