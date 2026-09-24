namespace Puck.Testing;

/// <summary>A position that prints a name and reads it back, for the two identifier spelling laws.</summary>
/// <param name="Label">The position's name in a violation.</param>
/// <param name="Carries">Whether the position's value can hold the name at all.</param>
/// <param name="Print">The printer's text for the name.</param>
/// <param name="Bare">The text the name has written bare, which the position's reader is handed.</param>
/// <param name="PrintedBare">Whether printed text wrote the name bare.</param>
/// <param name="Read">The name a text reads back as, or <see langword="null"/> when it does not read as that one
/// name.</param>
/// <param name="Rule">Whether the one identifier rule admits the name here — an identifier, or a name when the
/// position admits the sigil — or <see langword="null"/> for a name the position's own layered walk (a dotted path, a
/// reserved channel's segments, a numeric key) decides.</param>
/// <param name="Reserved">Whether the name is in the position's own named reservation set, which a printer quotes
/// whatever its reader would make of it.</param>
/// <param name="Quoted">The text the name has written quoted, when the position has a quoted spelling.</param>
internal sealed record SpellingPosition(
    string Label,
    Func<string, bool> Carries,
    Func<string, string> Print,
    Func<string, string> Bare,
    Func<string, string, bool> PrintedBare,
    Func<string, string?> Read,
    Func<string, bool?> Rule,
    Func<string, bool> Reserved,
    Func<string, string>? Quoted = null
);
/// <summary>The two identifier spelling laws, and the drifted printers their mutation proofs run them against.
/// The round-trip law: a printed name reads back as itself. The agreement law: a printer writes a name bare exactly
/// when its own reader takes that bare text as the name and the name is not in the position's reservation set; and
/// a name the one rule admits is one the reader takes bare or the position reserves, while a name it refuses is
/// neither.</summary>
internal static class SpellingLaws {
    /// <summary>Returns every name the position prints but does not read back as itself.</summary>
    /// <param name="position">The position.</param>
    /// <param name="names">The names swept.</param>
    /// <returns>One line per violation.</returns>
    public static List<string> RoundTripViolations(SpellingPosition position, IEnumerable<string> names) {
        var violations = new List<string>();

        foreach (var name in names) {
            if (!position.Carries(arg: name)) {
                continue;
            }

            var printed = position.Print(arg: name);
            var read = position.Read(arg: printed);

            if (!string.Equals(a: read, b: name, comparisonType: StringComparison.Ordinal)) {
                violations.Add(item: $"{position.Label}: {Show(text: name)} printed as {Show(text: printed)} reads back as {Show(text: read)}");
            }
        }

        return violations;
    }
    /// <summary>Returns every name whose printed text is bare where its reader or the position's reservations would
    /// not have it bare, or quoted where both would; and every name the rule and the reader disagree about.</summary>
    /// <param name="position">The position.</param>
    /// <param name="names">The names swept.</param>
    /// <returns>One line per violation.</returns>
    public static List<string> AgreementViolations(SpellingPosition position, IEnumerable<string> names) {
        var violations = new List<string>();

        foreach (var name in names) {
            if (!position.Carries(arg: name)) {
                continue;
            }

            var printedBare = position.PrintedBare(arg1: name, arg2: position.Print(arg: name));
            var readsBare = string.Equals(a: position.Read(arg: position.Bare(arg: name)), b: name, comparisonType: StringComparison.Ordinal);
            var reserved = position.Reserved(arg: name);
            var rule = position.Rule(arg: name);

            if ((printedBare != (readsBare && !reserved)) || ((rule is { } admitted) && (admitted != (readsBare || reserved)))) {
                violations.Add(item: $"{position.Label}: {Show(text: name)} prints {(printedBare ? "bare" : "quoted")}, its reader {(readsBare ? "reads" : "does not read")} it bare, the position {(reserved ? "reserves" : "does not reserve")} it, and the rule {rule switch { true => "admits it", false => "refuses it", null => "leaves it to the position" }}");
            }
        }

        return violations;
    }
    /// <summary>Returns the message a failing law reports: the violation count and the first few violations.</summary>
    /// <param name="violations">The violations.</param>
    /// <returns>The message.</returns>
    public static string Describe(List<string> violations) => $"{violations.Count} violation(s):{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: violations.Take(count: 25))}";
    /// <summary>Returns the position with a printer that writes bare every name the Unicode copy of the rule admits:
    /// Unicode letters and digits, <c>_</c>, and the sigil anywhere, which is the copy the document printer carried.
    /// The round-trip law must catch it.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The drifted position.</returns>
    public static SpellingPosition WithUnicodeCopy(SpellingPosition position) => position with {
        Print = name => (UnicodeCopy(name: name)
            ? position.Bare(arg: name)
            : position.Print(arg: name)
        ),
    };
    /// <summary>Returns the position with a printer that quotes every name carrying an underscore, which its reader
    /// and the rule take bare. The agreement law must catch it.</summary>
    /// <param name="position">The position, which must have a quoted spelling.</param>
    /// <returns>The drifted position.</returns>
    public static SpellingPosition WithUnderscoresQuoted(SpellingPosition position) => position with {
        Print = name => (name.Contains(value: '_')
            ? position.Quoted!(arg: name)
            : position.Print(arg: name)
        ),
    };

    private static bool UnicodeCopy(string name) => (
        (name.Length > 0) &&
        (char.IsLetter(c: name[0]) || (name[0] is '_' or '$')) &&
        name.All(predicate: static character => (char.IsLetterOrDigit(c: character) || (character is '_' or '$')))
    );
    // Every character outside printable ASCII is shown as its code, so a violation names the exact name that broke.
    private static string Show(string? text) => ((text is null)
        ? "(nothing)"
        : $"\"{string.Concat(values: text.Select(selector: static character => (((character < ' ') || (character > '~'))
            ? $"\\u{((int)character):X4}"
            : character.ToString()
        )))}\""
    );
}
