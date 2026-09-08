namespace Puck.Abstractions.Documents;

/// <summary>The socket-name grammar a probe kind's manifest names its typed input slots with (e.g. <c>color</c>,
/// <c>strobe-pair</c>): an ASCII letter, then zero or more ASCII letters, digits, or hyphens. Distinct from a
/// kebab-case identifier, which forbids a leading digit but also forbids upper-case and a trailing/doubled hyphen —
/// a socket name admits both.</summary>
public static class SocketIdentifierGrammar {
    /// <summary>Determines whether <paramref name="value"/> is a valid socket identifier.</summary>
    public static bool IsValid(string? value) {
        if (
            string.IsNullOrEmpty(value: value) ||
            !char.IsAsciiLetter(c: value[0])
        ) {
            return false;
        }

        for (var index = 1; (index < value.Length); index++) {
            var character = value[index];

            if (
                !char.IsAsciiLetterOrDigit(c: character) &&
                (character != '-')
            ) {
                return false;
            }
        }

        return true;
    }
}
