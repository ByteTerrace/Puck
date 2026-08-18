using System.Globalization;

namespace Puck.Analyzers;

/// <summary>
/// The parsed <c>FileLengths.json</c>: the repository's line ceiling and, per file already over it, the length it
/// was recorded at. Keys are repository-relative paths with forward slashes, matched ordinally.
/// </summary>
public sealed class FileLengthLedger {
    private readonly Dictionary<string, int> m_recorded;

    private FileLengthLedger(int ceiling, Dictionary<string, int> recorded) {
        Ceiling = ceiling;
        m_recorded = recorded;
    }

    /// <summary>Gets the line count no unrecorded file may exceed.</summary>
    public int Ceiling { get; }
    /// <summary>Gets every recorded path in ordinal order.</summary>
    public IEnumerable<string> RecordedKeys =>
        m_recorded.Keys.OrderBy(keySelector: key => key, comparer: StringComparer.Ordinal);

    /// <summary>Returns the ledger key for a source path: relative to the ledger's directory when the file lies under it, with forward slashes.</summary>
    public static string KeyFor(string filePath, string ledgerDirectory) {
        var normalized = filePath.Replace(newChar: '/', oldChar: '\\');
        var root = ledgerDirectory.Replace(newChar: '/', oldChar: '\\').TrimEnd(trimChars: '/');

        if ((root.Length != 0) && normalized.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: (root + "/"))) {
            return normalized.Substring(startIndex: (root.Length + 1));
        }

        return normalized;
    }
    /// <summary>Returns the recorded length for <paramref name="key"/>, or <see langword="null"/> when the file is not in the ledger.</summary>
    public int? TryGetRecordedLength(string key) =>
        (m_recorded.TryGetValue(key: key, value: out var recorded) ? recorded : null);
    /// <summary>Parses the ledger text; a missing, malformed, or off-schema document yields <see langword="false"/> and a message naming the fault.</summary>
    public static bool TryParse(string? json, out FileLengthLedger? ledger, out string? error) {
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

        if (!TryReadInteger(document: document, error: out error, name: "format", value: out var format) || (format != 1)) {
            error ??= "'format' must be 1.";

            if (format != 1) {
                error = $"'format' is {format}; this reader understands 1.";
            }

            return false;
        }

        if (!TryReadInteger(document: document, error: out error, name: "ceiling", value: out var ceiling)) {
            return false;
        }

        if (ceiling <= 0) {
            error = "'ceiling' must be a positive line count.";

            return false;
        }

        if (!document.TryGetValue(key: "recorded", value: out var recordedValue) || (recordedValue is not Dictionary<string, object?> recordedObject)) {
            error = "'recorded' must be an object of repository-relative path to recorded line count.";

            return false;
        }

        var recorded = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var pair in recordedObject) {
            if ((pair.Key.Length == 0) || pair.Key.Contains(value: '\\')) {
                error = $"recorded path '{pair.Key}' must be a non-empty repository-relative path with forward slashes.";

                return false;
            }

            if (!TryAsInteger(value: pair.Value, integer: out var length) || (length <= ceiling)) {
                error = $"recorded length for '{pair.Key}' must be an integer above the ceiling ({ceiling.ToString(provider: CultureInfo.InvariantCulture)}).";

                return false;
            }

            recorded[pair.Key] = length;
        }

        ledger = new FileLengthLedger(ceiling: ceiling, recorded: recorded);

        return true;
    }

    private static bool TryReadInteger(Dictionary<string, object?> document, string name, out int value, out string? error) {
        value = 0;
        error = null;

        if (!document.TryGetValue(key: name, value: out var raw) || !TryAsInteger(integer: out value, value: raw)) {
            error = $"'{name}' must be an integer.";

            return false;
        }

        return true;
    }
    private static bool TryAsInteger(object? value, out int integer) {
        integer = 0;

        if ((value is double number) && (number == Math.Floor(d: number)) && (number >= int.MinValue) && (number <= int.MaxValue)) {
            integer = ((int)number);

            return true;
        }

        return false;
    }
}
