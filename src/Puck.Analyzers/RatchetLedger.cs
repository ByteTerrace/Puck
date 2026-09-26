using System.Globalization;
using System.Text;

namespace Puck.Analyzers;

/// <summary>What a ratchet ledger says about one file's measured count.</summary>
public enum RatchetVerdict {
    /// <summary>The count is within what the ledger allows: at or under the ceiling and unrecorded, or over the
    /// ceiling and at or under the recorded count.</summary>
    Within,
    /// <summary>The file is not recorded and its count is over the ceiling.</summary>
    OverCeiling,
    /// <summary>The file is recorded and its count rose past the recorded count.</summary>
    Grew,
    /// <summary>The file is recorded but no longer needs to be: it is gone, or its count fell to the ceiling or
    /// under it.</summary>
    Stale,
}
/// <summary>One file a <see cref="RatchetLedger.Reconcile"/> pass found out of step with the ledger.</summary>
/// <param name="Verdict">What is wrong: <see cref="RatchetVerdict.OverCeiling"/>, <see cref="RatchetVerdict.Grew"/>
/// or <see cref="RatchetVerdict.Stale"/>.</param>
/// <param name="Key">The file's ledger key.</param>
/// <param name="Count">The file's measured count, or <see langword="null"/> when a recorded file is no longer in the
/// measured tree.</param>
/// <param name="Recorded">The count the ledger records for the file, or <see langword="null"/> when it records
/// none.</param>
public readonly record struct RatchetFinding(RatchetVerdict Verdict, string Key, int? Count, int? Recorded);
/// <summary>The outcome of reconciling a <see cref="RatchetLedger"/> against a measured tree: every finding, and the
/// ledger the tree allows next.</summary>
/// <param name="Ceiling">The ceiling the next ledger declares.</param>
/// <param name="Findings">Every file out of step with the ledger, in ordinal key order.</param>
/// <param name="Next">The next ledger's recorded counts, in ordinal key order. A grown file keeps its recorded count
/// here; it is refused, never raised.</param>
public sealed record RatchetReconciliation(int Ceiling, IReadOnlyList<RatchetFinding> Findings, IReadOnlyList<KeyValuePair<string, int>> Next) {
    /// <summary>Gets whether any finding refuses a rewrite: a grown file, or an unrecorded file over the ceiling.
    /// Only stale entries are repaired by rewriting.</summary>
    public bool Refused => Findings.Any(predicate: static finding => (finding.Verdict != RatchetVerdict.Stale));

    /// <summary>Renders <see cref="Next"/> as the ledger document.</summary>
    /// <returns>The ledger's JSON text, ending in a line feed.</returns>
    public string Render() => RatchetLedger.Render(
        ceiling: Ceiling,
        recorded: Next
    );
}
/// <summary>
/// A ratchet ledger: a repository-wide ceiling on one per-file count and, for each file already over it, the count
/// that file was recorded at. A file not in the ledger may not exceed the ceiling, a recorded file may not rise past
/// its recorded count, and an entry whose file has fallen to the ceiling or under it is stale — so every recorded
/// count only falls. <c>FileLengths.json</c> (line counts) and <c>CommentSmells.json</c> (comment-smell counts) are
/// ledgers of this one shape. Keys are repository-relative paths with forward slashes, matched ordinally.
/// </summary>
public sealed class RatchetLedger {
    /// <summary>The ledger document format this reader understands and <see cref="Render"/> writes.</summary>
    public const int Format = 1;

    private readonly Dictionary<string, int> m_recorded;

    private RatchetLedger(int ceiling, Dictionary<string, int> recorded) {
        Ceiling = ceiling;
        m_recorded = recorded;
    }

    /// <summary>Gets the count no unrecorded file may exceed.</summary>
    public int Ceiling { get; }
    /// <summary>Gets the number of recorded files.</summary>
    public int RecordedCount => m_recorded.Count;
    /// <summary>Gets every recorded path in ordinal order.</summary>
    public IEnumerable<string> RecordedKeys =>
        m_recorded.Keys.OrderBy(
            keySelector: key => key,
            comparer: StringComparer.Ordinal
        );

    private static string Spell(int value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
    private static bool TryAsInteger(object? value, out int integer) {
        integer = 0;

        if (
            (value is double number) &&
            (number == Math.Floor(d: number)) &&
            (number >= int.MinValue) &&
            (number <= int.MaxValue)
        ) {
            integer = ((int)number);

            return true;
        }

        return false;
    }
    private static bool TryReadInteger(Dictionary<string, object?> document, string name, out int value, out string? error) {
        value = 0;
        error = null;

        if (
            !document.TryGetValue(
            key: name,
            value: out var raw
        ) ||
            !TryAsInteger(
            integer: out value,
            value: raw
        )
        ) {
            error = $"'{name}' must be an integer.";

            return false;
        }

        return true;
    }

    /// <summary>Creates a ledger that records no file.</summary>
    /// <param name="ceiling">The count no file may exceed.</param>
    /// <returns>The empty ledger.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ceiling"/> is negative.</exception>
    public static RatchetLedger Create(int ceiling) {
        if (ceiling < 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: ceiling,
                message: "a ledger's ceiling must not be negative.",
                paramName: nameof(ceiling)
            );
        }

        return new RatchetLedger(
            ceiling: ceiling,
            recorded: new Dictionary<string, int>(comparer: StringComparer.Ordinal)
        );
    }
    /// <summary>Returns the ledger key for a source path: relative to the ledger's directory when the file lies under it, with forward slashes.</summary>
    /// <param name="filePath">The source file's path, as the compiler or the tree walk names it.</param>
    /// <param name="ledgerDirectory">The directory the ledger file sits in.</param>
    /// <returns>The key the ledger records the file under.</returns>
    public static string KeyFor(string filePath, string ledgerDirectory) {
        var normalized = CollapseDotSegments(path: filePath.Replace(
            newChar: '/',
            oldChar: '\\'
        ));
        var root = ledgerDirectory.Replace(
            newChar: '/',
            oldChar: '\\'
        ).TrimEnd(trimChars: '/');

        if (
            (root.Length != 0) &&
            normalized.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: (root + "/")
        )
        ) {
            return normalized.Substring(startIndex: (root.Length + 1));
        }

        return normalized;
    }

    // A file a project links from elsewhere reaches the compiler as `project/../../src/…`; the key is the file's own path.
    private static string CollapseDotSegments(string path) {
        var segments = new List<string>();

        foreach (var segment in path.Split(separator: '/')) {
            if (segment == ".") {
                continue;
            }

            if ((segment == "..") && (segments.Count != 0) && (segments[(segments.Count - 1)] != "..") && (segments[(segments.Count - 1)].Length != 0)) {
                segments.RemoveAt(index: (segments.Count - 1));
                continue;
            }

            segments.Add(item: segment);
        }

        return string.Join(
            separator: "/",
            values: segments
        );
    }

    /// <summary>Renders a ledger document: the format, the ceiling, and the recorded counts in ordinal key order,
    /// indented by four spaces and ending in a line feed.</summary>
    /// <param name="ceiling">The ceiling the document declares.</param>
    /// <param name="recorded">The recorded counts; each must be above <paramref name="ceiling"/>.</param>
    /// <returns>The ledger's JSON text.</returns>
    public static string Render(int ceiling, IEnumerable<KeyValuePair<string, int>> recorded) {
        var builder = new StringBuilder();
        var rows = recorded.OrderBy(
            keySelector: static pair => pair.Key,
            comparer: StringComparer.Ordinal
        ).ToList();

        builder.Append(value: "{\n    \"format\": ").Append(value: Spell(value: Format)).Append(value: ",\n");
        builder.Append(value: "    \"ceiling\": ").Append(value: Spell(value: ceiling)).Append(value: ",\n");

        if (rows.Count == 0) {
            builder.Append(value: "    \"recorded\": {}\n}\n");

            return builder.ToString();
        }

        builder.Append(value: "    \"recorded\": {\n");

        for (var index = 0; (index < rows.Count); index++) {
            builder.Append(value: "        \"").Append(value: rows[index].Key).Append(value: "\": ").Append(value: Spell(value: rows[index].Value));
            builder.Append(value: ((index == (rows.Count - 1))
                ? "\n"
                : ",\n"));
        }

        return builder.Append(value: "    }\n}\n").ToString();
    }
    /// <summary>Parses the ledger text; a missing, malformed, or off-schema document yields <see langword="false"/> and a message naming the fault.</summary>
    /// <param name="json">The ledger's text, or <see langword="null"/> when it could not be read.</param>
    /// <param name="ledger">The parsed ledger, or <see langword="null"/> on failure.</param>
    /// <param name="error">The fault, or <see langword="null"/> on success.</param>
    /// <returns>Whether the text is a usable ledger.</returns>
    public static bool TryParse(string? json, out RatchetLedger? ledger, out string? error) {
        ledger = null;
        error = null;

        if (json is null) {
            error = "the file could not be read.";

            return false;
        }

        object? root;

        try {
            root = MiniJson.Parse(json: json);
        } catch (FormatException exception) {
            error = $"the JSON is malformed ({exception.Message}).";

            return false;
        }

        if (root is not Dictionary<string, object?> document) {
            error = "the root must be an object with 'format', 'ceiling', and 'recorded'.";

            return false;
        }

        if (
            !TryReadInteger(
            document: document,
            error: out error,
            name: "format",
            value: out var format
        ) ||
            (format != Format)
        ) {
            error ??= $"'format' must be {Spell(value: Format)}.";

            if (format != Format) {
                error = $"'format' is {Spell(value: format)}; this reader understands {Spell(value: Format)}.";
            }

            return false;
        }

        if (!TryReadInteger(
            document: document,
            error: out error,
            name: "ceiling",
            value: out var ceiling
        )) {
            return false;
        }

        if (ceiling < 0) {
            error = "'ceiling' must not be negative.";

            return false;
        }

        if (
            !document.TryGetValue(
            key: "recorded",
            value: out var recordedValue
        ) ||
            (recordedValue is not Dictionary<string, object?> recordedObject)
        ) {
            error = "'recorded' must be an object of repository-relative path to recorded count.";

            return false;
        }

        var recorded = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var pair in recordedObject) {
            if (
                (pair.Key.Length == 0) ||
                pair.Key.Contains(value: '\\')
            ) {
                error = $"recorded path '{pair.Key}' must be a non-empty repository-relative path with forward slashes.";

                return false;
            }

            if (
                !TryAsInteger(
                value: pair.Value,
                integer: out var count
            ) ||
                (count <= ceiling)
            ) {
                error = $"recorded count for '{pair.Key}' must be an integer above the ceiling ({Spell(value: ceiling)}).";

                return false;
            }

            recorded[pair.Key] = count;
        }

        ledger = new RatchetLedger(
            ceiling: ceiling,
            recorded: recorded
        );

        return true;
    }
    /// <summary>Judges one file's measured count against the ledger.</summary>
    /// <param name="key">The file's ledger key (<see cref="KeyFor"/>).</param>
    /// <param name="count">The file's measured count.</param>
    /// <returns>The verdict; never <see cref="RatchetVerdict.Stale"/> for a file the ledger does not record.</returns>
    public RatchetVerdict Judge(string key, int count) {
        var recorded = TryGetRecorded(key: key);

        if (recorded is null) {
            return ((count > Ceiling)
                ? RatchetVerdict.OverCeiling
                : RatchetVerdict.Within
            );
        }

        if (count <= Ceiling) {
            return RatchetVerdict.Stale;
        }

        return ((count > recorded.Value)
            ? RatchetVerdict.Grew
            : RatchetVerdict.Within
        );
    }
    /// <summary>Reconciles the ledger against every file's measured count: judges each, and computes the ledger the
    /// tree allows next — stale entries removed and fallen counts lowered to what was measured.</summary>
    /// <param name="measured">Every measured file's count, by ledger key.</param>
    /// <param name="ceiling">The ceiling the next ledger declares, or <see langword="null"/> to keep
    /// <see cref="Ceiling"/>. A lower ceiling records every unrecorded file over it that was within the old ceiling,
    /// at its measured count, so each can only fall from there.</param>
    /// <returns>The findings and the next ledger.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ceiling"/> is negative or above
    /// <see cref="Ceiling"/>; a ceiling only falls.</exception>
    public RatchetReconciliation Reconcile(IReadOnlyDictionary<string, int> measured, int? ceiling = null) {
        var next = (ceiling ?? Ceiling);

        if (
            (next < 0) ||
            (next > Ceiling)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: next,
                message: $"a ledger's ceiling only falls; {Spell(value: next)} is not within 0..{Spell(value: Ceiling)}.",
                paramName: nameof(ceiling)
            );
        }

        var findings = new List<RatchetFinding>();
        var recorded = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var key in RecordedKeys) {
            var recordedCount = m_recorded[key];

            if (!measured.TryGetValue(
                key: key,
                value: out var count
            )) {
                findings.Add(item: new RatchetFinding(
                    Count: null,
                    Key: key,
                    Recorded: recordedCount,
                    Verdict: RatchetVerdict.Stale
                ));
            } else if (count <= next) {
                findings.Add(item: new RatchetFinding(
                    Count: count,
                    Key: key,
                    Recorded: recordedCount,
                    Verdict: RatchetVerdict.Stale
                ));
            } else if (count > recordedCount) {
                findings.Add(item: new RatchetFinding(
                    Count: count,
                    Key: key,
                    Recorded: recordedCount,
                    Verdict: RatchetVerdict.Grew
                ));
                recorded[key] = recordedCount;
            } else {
                recorded[key] = count;
            }
        }

        foreach (var pair in measured.OrderBy(
            keySelector: static pair => pair.Key,
            comparer: StringComparer.Ordinal
        )) {
            if (
                m_recorded.ContainsKey(key: pair.Key) ||
                (pair.Value <= next)
            ) {
                continue;
            }

            if (pair.Value > Ceiling) {
                findings.Add(item: new RatchetFinding(
                    Count: pair.Value,
                    Key: pair.Key,
                    Recorded: null,
                    Verdict: RatchetVerdict.OverCeiling
                ));
            } else {
                recorded[pair.Key] = pair.Value;
            }
        }

        findings.Sort(comparison: static (left, right) => StringComparer.Ordinal.Compare(
            x: left.Key,
            y: right.Key
        ));

        return new RatchetReconciliation(
            Ceiling: next,
            Findings: findings,
            Next: recorded.ToList()
        );
    }
    /// <summary>Returns the recorded count for <paramref name="key"/>, or <see langword="null"/> when the file is not in the ledger.</summary>
    /// <param name="key">The file's ledger key.</param>
    /// <returns>The recorded count, if any.</returns>
    public int? TryGetRecorded(string key) =>
        (m_recorded.TryGetValue(
            key: key,
            value: out var recorded
        )
            ? recorded
            : null
        );
}
