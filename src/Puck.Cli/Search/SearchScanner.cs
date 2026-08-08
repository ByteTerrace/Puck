using System.Text;
using Puck.Cli.Source;
using Resharp;

namespace Puck.Cli.Search;

// One file's scan result. Count is the number `-c` prints — matching lines, or spans under `-s` — except at the
// Existence detail (-q/-l), where the scan stops at the first match and it is 1. Lines and Hits are filled only for
// the line-block modes that print them, Spans only for span mode. Produced by SearchScanner, consumed by SearchEmitter.
internal sealed record SearchFileResult(string Path, int Count, string[]? Lines, List<int>? Hits, List<(int Start, int End)>? Spans);

// File discovery and per-file scanning. The engine builds its DFA lazily and is NOT safe under concurrent access to one
// Regex, so callers give each worker its own instance via MakeRegex.
internal static class SearchScanner {
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    public static Regex MakeRegex(string pattern, bool ignoreCase) {
        var options = ResharpOptions.HighThroughputDefaults;

        options.IgnoreCase = ignoreCase;

        return new Regex(pattern: pattern, options: options);
    }

    // ---- file enumeration -------------------------------------------------------------------

    // Null when a path argument names nothing on disk; SearchCommand turns that into a usage error.
    public static List<string>? EnumerateFiles(SearchOptions opt) =>
        FileWalk.Enumerate(verb: "search", roots: opt.Paths, include: opt.Include, exclude: opt.Exclude, admit: Admit);

    // Whether a candidate file joins the search set: binary files (a NUL in the first 4 KiB) drop silently, unreadable
    // ones drop with a warning.
    private static bool Admit(string path) {
        try {
            using var fs = File.OpenRead(path: path);
            Span<byte> buf = stackalloc byte[4096];
            var read = fs.Read(buffer: buf);

            for (var i = 0; (i < read); i++) {
                if (buf[i] == 0) {
                    return false;
                }
            }

            return true;
        } catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException)) {
            WarnUnreadable(path: path, ex: ex);

            return false;
        }
    }
    private static void WarnUnreadable(string path, Exception ex) =>
        FileWalk.WarnUnreadable(verb: "search", path: path, exception: ex);

    // ---- scanning ---------------------------------------------------------------------------

    public static SearchFileResult? ScanLines(string path, Regex regex, SearchOptions opt) {
        var text = ReadText(path: path);

        if (text is null) {
            return null;
        }

        // -q/-l need only existence and -c only the number, so those modes match the lines in place and retain none of
        // them; only the line-block modes, which print the text, materialize it.
        if (opt.Detail != SearchDetail.Locations) {
            var count = CountMatchingLines(text: text, regex: regex, stopAtFirstMatch: (opt.Detail == SearchDetail.Existence));

            return ((count == 0) ? null : new SearchFileResult(Path: path, Count: count, Lines: null, Hits: null, Spans: null));
        }

        var lines = SplitLines(text: text);
        var hits = new List<int>();

        for (var i = 0; (i < lines.Length); i++) {
            if (regex.IsMatch(input: lines[i])) {
                hits.Add(item: i);
            }
        }

        return ((hits.Count == 0) ? null : new SearchFileResult(Path: path, Count: hits.Count, Lines: lines, Hits: hits, Spans: null));
    }
    public static SearchFileResult? ScanSpan(string path, Regex regex, SearchOptions opt) {
        var text = ReadText(path: path);

        if (text is null) {
            return null;
        }

        if (opt.Detail == SearchDetail.Existence) {
            return (regex.IsMatch(input: text) ? new SearchFileResult(Path: path, Count: 1, Lines: null, Hits: null, Spans: null) : null);
        }

        var matches = regex.Matches(input: text);

        if (matches.Length == 0) {
            return null;
        }

        // Only the locator mode needs offsets resolved to line numbers.
        if (opt.Detail == SearchDetail.Count) {
            return new SearchFileResult(Path: path, Count: matches.Length, Lines: null, Hits: null, Spans: null);
        }

        var starts = LineStartOffsets(text: text);
        var spans = new List<(int Start, int End)>(capacity: matches.Length);

        foreach (var m in matches) {
            var startLine = LineOf(starts: starts, offset: m.Index);
            var endLine = LineOf(starts: starts, offset: (m.Index + Math.Max(val1: 0, val2: (m.Length - 1))));

            spans.Add(item: (startLine, endLine));
        }

        return new SearchFileResult(Path: path, Count: spans.Count, Lines: null, Hits: null, Spans: spans);
    }

    // ---- helpers ----------------------------------------------------------------------------

    // The file as UTF-8 text with a leading byte-order mark removed, so '^' anchors line 1 and span mode's raw text
    // starts at the first real character. Null means unreadable, already warned.
    private static string? ReadText(string path) {
        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException)) {
            WarnUnreadable(path: path, ex: ex);

            return null;
        }

        ReadOnlySpan<byte> span = bytes;

        return Utf8.GetString(bytes: (span.StartsWith(value: Utf8Bom) ? span[Utf8Bom.Length..] : span));
    }

    // Walks the same lines SplitLines produces without materializing any of them.
    private static int CountMatchingLines(string text, Regex regex, bool stopAtFirstMatch) {
        var count = 0;
        var remaining = text.AsSpan();
        var isFirst = true;

        while (true) {
            var newline = remaining.IndexOf(value: '\n');
            var line = ((newline < 0) ? remaining : remaining[..newline]);

            if ((line.Length > 0) && (line[^1] == '\r')) {
                line = line[..^1];
            }

            // A trailing newline yields a spurious empty final element; drop it. A 0-byte file is still one empty line.
            if ((newline < 0) && (line.Length == 0) && !isFirst) {
                break;
            }

            if (regex.IsMatch(input: line)) {
                count++;

                if (stopAtFirstMatch) {
                    break;
                }
            }

            if (newline < 0) {
                break;
            }

            isFirst = false;
            remaining = remaining[(newline + 1)..];
        }

        return count;
    }
    private static string[] SplitLines(string text) {
        var parts = text.Split(separator: '\n');

        for (var i = 0; (i < parts.Length); i++) {
            if ((parts[i].Length > 0) && (parts[i][^1] == '\r')) {
                parts[i] = parts[i][..^1];
            }
        }

        // A trailing newline yields a spurious empty final element; drop it.
        if ((parts.Length > 1) && (parts[^1].Length == 0)) {
            Array.Resize(array: ref parts, newSize: (parts.Length - 1));
        }

        return parts;
    }
    private static int[] LineStartOffsets(string text) {
        var starts = new List<int> { 0 };

        for (var i = 0; (i < text.Length); i++) {
            if (text[i] == '\n') {
                starts.Add(item: (i + 1));
            }
        }

        return starts.ToArray();
    }
    private static int LineOf(int[] starts, int offset) {
        var lo = 0;
        var hi = (starts.Length - 1);

        while (lo < hi) {
            var mid = (((lo + hi) + 1) >> 1);

            if (starts[mid] <= offset) {
                lo = mid;
            } else {
                hi = (mid - 1);
            }
        }

        return lo;
    }
}
