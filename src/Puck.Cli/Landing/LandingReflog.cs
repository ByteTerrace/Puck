namespace Puck.Cli.Landing;

/// <summary>
/// Reads the branch reflog for the most recent rebase base, so a refusal can SUGGEST the answer instead of merely
/// demanding one.
/// </summary>
/// <remarks>A suggestion the human confirms is safe where a default the tool assumes is not — the base is the one
/// input this check cannot get wrong and still be worth running, and the failure it catches is itself a wrong belief
/// about the base. Offering the reflog's answer turns "supply --base" into "is this the tree you worked from?"
/// without the tool ever deciding.</remarks>
internal static class LandingReflog {
    /// <summary>Finds the target of the most recent <c>rebase (start): checkout &lt;ref&gt;</c> reflog entry.</summary>
    /// <param name="suggestion">The suggested base commit, on success.</param>
    /// <param name="when">When that rebase happened, for the human reading the refusal.</param>
    public static bool TrySuggestBase(out string suggestion, out string when) {
        suggestion = string.Empty;
        when = string.Empty;

        var output = Git.Capture("reflog", "--date=iso", "--format=%h|%gd|%gs|%cd", "-60");

        foreach (var line in output.Split(separator: '\n')) {
            var text = line.TrimEnd(trimChar: '\r');
            var fields = text.Split(separator: '|');

            if ((fields.Length < 4) || !fields[2].StartsWith(value: "rebase (start): checkout ", comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            // The entry's own hash IS the commit the rebase checked out — the base the work replayed onto.
            suggestion = fields[0];
            when = fields[3];

            return true;
        }

        return false;
    }
}
