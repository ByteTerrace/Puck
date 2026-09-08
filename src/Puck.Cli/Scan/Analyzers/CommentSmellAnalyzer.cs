using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// The comment-WEAKNESS classifier — the same non-XML inline-comment corpus as CommentAnalyzer, but each
// comment is bucketed by the kind of weakness it represents (premise: self-documenting code needs no
// inline comment, so every one is a smell to triage). Buckets, first match wins:
//   sync-coupling      — a prose guardrail over a constraint the compiler can't see (source order:
//                        "load-bearing; do not alphabetize") or can't span (a C#<->shader contract:
//                        "KEEP IN SYNC with <file>"). The design-smell bucket: these mark a missing
//                        single source of truth, not a documentation gap — question the design, don't
//                        just reword the comment.
//   debt-marker        — TODO/FIXME/HACK/XXX/REVISIT/"for now": tracked deferred work.
//   banner-divider     — a run of >=4 rule glyphs: structure, not information.
//   commented-out-code — the body parses as a C# statement (with a real terminator/operator/keyword):
//                        dead code left in a comment.
//   narrative-history  — past-tense narration of the code's OWN history ("used to", "no longer", "the
//                        original landing", a date). Git answers this better, so the comment is pure
//                        liability: it costs a rewrite on every behavioral change and misleads once it
//                        stops being rewritten.
//   dead-provenance    — a citation to a process artifact ("Open Decision 5", "perf plan Phase 6.1",
//                        "adversarial-review G1", "unit 4b"). Unresolvable by anyone who was not in
//                        that conversation, and the artifacts are routinely retired.
//   unclassified       — everything else: the work-list a fan-out audit judges by hand (does it lie?
//                        does it earn its keep?). Purely syntactic detection can bucket the rest;
//                        whether a comment LIES is a semantic claim only a reader of the code can settle.
// Orthogonally: every comment carries blockSize, the run of consecutive comment lines it belongs to,
// because the dominant rot is a BLOCK property no per-line rule can see — a 92-line essay is 92
// individually innocent lines. And any comment that NAMES a cross-artifact referent is resolved against
// the corpus, in two tiers that must not be conflated: a FILE referent (shader or document) that
// resolves nowhere is PROVABLE staleness, while an UPPER_SNAKE symbol absent from our sources is only
// ADVISORY — this corpus holds our code alone, so an external SDK or hardware constant is
// indistinguishable from a define that vanished, and a sweep that read the two as one deleted correct
// spec citations. Substring resolution biases toward "exists". Hand-writes its json like its siblings.
internal sealed class CommentSmellAnalyzer : ISourceAnalyzer {
    // The design smell: prose standing in for a constraint the compiler can't enforce — intra-source
    // order ("load-bearing; do not alphabetize") or a cross-language contract ("keep in sync with
    // <shader>"). `must match/mirror` is gated by a nearby structure word so a plain API fact ("must
    // match the framebuffer") stays out.
    private static readonly Regex SyncPattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"keep[\s\w]{0,16}in sync|kept in sync|stay(?:s|ing)? in sync|in lockstep|do not (?:alphabetize|reorder|re-order|sort)|load-bearing|same order as|must (?:match|mirror)\b[^.\n]*\b(?:layout|order|struct|block|offset|enum|field|kernel|glsl|shader|push-constant)\b|mirror of the");
    private static readonly Regex DebtPattern = new(options: RegexOptions.IgnoreCase | RegexOptions.Compiled, pattern: @"\b(?:TODO|FIXME|HACK|XXX|KLUDGE|REVISIT)\b|\bfor now\b");
    private static readonly Regex BannerPattern = new(options: RegexOptions.Compiled, pattern: @"[-=*#_~]{4,}");
    // Past-tense narration of the code's own history. Deliberately narrow: it targets phrases that can
    // only be describing a PREVIOUS state of this code, never a present-tense behavioral fact, so
    // "the value was clamped" (what it does) stays out and "the value used to be clamped" (what it did)
    // comes in.
    private static readonly Regex HistoryPattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"\bused to (?:be|live|lived|carry|carried|do|have|had|call|called|read|return|returned|apply|applied|enforce|enforced|hold|held|sit|sat|mean|meant|work|fire|fired|refuse|refused|report|reported|resolve|resolved|write|wrote|store|stored|track|gate|gated|allow|allowed|throw|threw|log|push|pull|set|clear|reset|land|landed|check|checked)\b|\bthe former\b|\bpreviously (?:did|was|were|had|lived|carried|returned|read|applied|held|split|named)\b|\bprior to (?:this|that)\b|\bthe original (?:landing|implementation|version|shape|code|pass|fix)\b|\bbefore (?:this|the) (?:fix|change|landing|correction|review)\b|\bthe first pass\b|\bround-(?:one|two)\b|\bearlier draft\b|\bsince (?:deleted|renamed|retired|removed)\b|\bsuperseded\b|\b20\d{2}-\d{2}-\d{2}\b");
    // A citation to a process artifact — a plan phase, a review round, a numbered decision or finding.
    // Meaningless to a reader who was not in that conversation, and the artifact it names is routinely
    // retired while the comment stays.
    private static readonly Regex ProvenancePattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"\bopen decision \d|\badversarial[-\s]review\b|\bperf plan\b|\bdesign round\b|\bunit \d+[a-z]?\b|\bsurvey #\d|\bfinding \d|\bphase \d+\.\d|\bthe plan's\b");
    // A C# code signal: a trailing terminator, a leading statement keyword, or an operator prose rarely
    // carries. The gate keeps an English sentence that happens to parse as a bare expression out of the
    // commented-out-code bucket.
    private static readonly Regex CodeSignalPattern = new(
        options: RegexOptions.Compiled,
        pattern: @";\s*$|^\s*(?:return|if|for|foreach|while|var|using|throw|await|public|private|internal|protected)\b|=>|==|!=|&&|\|\|");
    // Cross-artifact referents a stale comment would dangle: a shader file name, or an UPPER_SNAKE
    // define (>= one underscore, so single words like NOTE/RED and acronyms like RGBA never register).
    // `hlsli` precedes `hlsl` in the alternation because this tree's HLSL include extension is the one a
    // "KEEP IN SYNC with <x>.hlsli" comment cites; it must stay in step with ShaderExtensions.
    private static readonly Regex ShaderFilePattern = new(options: RegexOptions.Compiled, pattern: @"\b[\w-]+\.(?:glsl|comp|frag|vert|hlsli|hlsl)\b");
    // A cited markdown document, with or without its directory. Resolved by FILE NAME against docs/, so
    // a retired plan still cited from code reports dangling.
    private static readonly Regex DocumentFilePattern = new(options: RegexOptions.Compiled, pattern: @"\b(?:[\w./-]*/)?[\w.-]+\.md\b");

    // The run length at which a comment stops being an annotation and becomes an essay. Not a limit the
    // scan enforces — the threshold that puts a block on the audit's work list.
    private const int EssayLineCount = 6;

    private static readonly Regex SymbolPattern = new(options: RegexOptions.Compiled, pattern: @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b");
    // The shader sources whose text and file names resolve a comment's cross-artifact referents. KEEP IN
    // STEP with ShaderFilePattern: an extension the pattern cites but this set omits would be reported
    // dangling on every citation.
    private static readonly string[] ShaderExtensions = [".glsl", ".comp", ".frag", ".vert", ".hlsl", ".hlsli"];

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var haystack = BuildReferentHaystack(corpus: corpus, shaderRoot: options.ShaderRoot, shaderFileNames: out var shaderFileNames);
        var jsonl = new StringBuilder();
        var byFile = new Dictionary<string, List<(int Line, string Text)>>();
        var bucketCounts = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);
        var unresolved = 0;
        var symbolAdvisories = 0;

        var docNames = BuildDocumentNames(repositoryRoot: options.RepositoryRoot);
        var essayBlocks = 0;
        var essayLines = 0;

        foreach (var parsed in corpus.Files) {
            var comments = CollectComments(parsed: parsed);

            // Block sizes first: a run's length is a property of the whole run, so no record can be
            // emitted until the run it belongs to has ended.
            var blockSizes = MeasureBlocks(comments: comments);

            for (var index = 0; (index < comments.Count); index++) {
                var (startLine, endLine, isSingle, text) = comments[index];
                var body = StripMarkers(text: text);
                var bucket = Classify(body: body);
                var blockSize = blockSizes[index];

                bucketCounts[bucket] = (bucketCounts.GetValueOrDefault(key: bucket) + 1);

                if (blockSize >= EssayLineCount) {
                    essayLines++;

                    if ((index == 0) || (blockSizes[(index - 1)] != blockSize) || ((comments[(index - 1)].EndLine + 1) != startLine)) {
                        essayBlocks++;
                    }
                }

                var references = ResolveReferences(anySymbolUnresolved: out var anySymbolUnresolved, anyUnresolved: out var anyUnresolved, body: body, docNames: docNames, haystack: haystack, shaderFileNames: shaderFileNames);

                if (anyUnresolved) {
                    unresolved++;
                }

                if (anySymbolUnresolved) {
                    symbolAdvisories++;
                }

                jsonl.Append(value: '{')
                    .Append(value: "\"file\":").Append(value: ScanJsonl.JsonString(value: parsed.Relative)).Append(value: ',')
                    .Append(value: "\"line\":").Append(value: startLine).Append(value: ',')
                    .Append(value: "\"endLine\":").Append(value: endLine).Append(value: ',')
                    .Append(value: "\"kind\":").Append(value: (isSingle ? "\"single\"" : "\"multi\"")).Append(value: ',')
                    .Append(value: "\"bucket\":").Append(value: ScanJsonl.JsonString(value: bucket)).Append(value: ',')
                    .Append(value: "\"blockSize\":").Append(value: blockSize).Append(value: ',')
                    .Append(value: "\"text\":").Append(value: ScanJsonl.JsonString(value: text));

                if (references.Length > 0) {
                    jsonl.Append(value: ",\"references\":[").Append(value: references).Append(value: ']');
                }

                jsonl.Append(value: "}\n");

                if (!byFile.TryGetValue(key: parsed.Relative, value: out var lines)) {
                    lines = [];
                    byFile[parsed.Relative] = lines;
                }

                lines.Add(item: (startLine, text));
            }
        }

        var total = bucketCounts.Values.Sum();

        Console.Error.WriteLine(
            value: $"scan[comment-smells]: {total} inline comments classified across {byFile.Count} files (of {corpus.FileCount} scanned).");

        foreach (var (bucket, count) in bucketCounts.OrderByDescending(keySelector: static pair => pair.Value)) {
            Console.Error.WriteLine(value: $"{count,5}  {bucket}");
        }

        Console.Error.WriteLine(value: $"{unresolved,5}  (comments citing a FILE that resolves nowhere — provable staleness)");
        Console.Error.WriteLine(value: $"{symbolAdvisories,5}  (comments citing an UPPER_SNAKE symbol absent from our sources — ADVISORY: an external SDK or hardware constant looks identical)");
        Console.Error.WriteLine(value: $"{essayLines,5}  lines in {essayBlocks} blocks of {EssayLineCount}+ consecutive comment lines");

        return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile: byFile, maxPerChunk: options.MaxPerChunk));
    }

    // The comment trivia of one file in source order, as the tuple the block pass and the emitter share.
    private static List<(int StartLine, int EndLine, bool IsSingle, string Text)> CollectComments(ParsedFile parsed) {
        var comments = new List<(int, int, bool, string)>();

        foreach (var trivia in parsed.Root.DescendantTrivia()) {
            var kind = trivia.Kind();
            var isSingle = (kind == SyntaxKind.SingleLineCommentTrivia);

            if (!isSingle && (kind != SyntaxKind.MultiLineCommentTrivia)) {
                continue;
            }

            var (startLine, endLine) = ScanJsonl.LineRange(location: trivia.GetLocation());

            comments.Add(item: (startLine, endLine, isSingle, trivia.ToString().Trim()));
        }

        return comments;
    }
    // Per-comment run length: consecutive comments with no gap between them are one block, and every
    // member reports the WHOLE block's line count. A multi-line /* */ comment counts its own span.
    private static int[] MeasureBlocks(List<(int StartLine, int EndLine, bool IsSingle, string Text)> comments) {
        var sizes = new int[comments.Count];

        if (comments.Count == 0) {
            return sizes;
        }

        var start = 0;

        for (var index = 1; (index <= comments.Count); index++) {
            var breaks = ((index == comments.Count) || (comments[index].StartLine > (comments[(index - 1)].EndLine + 1)));

            if (!breaks) {
                continue;
            }

            var lineCount = ((comments[(index - 1)].EndLine - comments[start].StartLine) + 1);

            for (var member = start; (member < index); member++) {
                sizes[member] = lineCount;
            }

            start = index;
        }

        return sizes;
    }
    // Every markdown file name in the repository, which is what a comment's cited document path resolves
    // against. Name-only, matching the shader probe: a comment citing a moved-but-live document is not
    // the failure being hunted — a citation to a document that exists NOWHERE is.
    private static HashSet<string> BuildDocumentNames(string repositoryRoot) {
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        if ((repositoryRoot.Length == 0) || !Directory.Exists(path: repositoryRoot)) {
            return names;
        }

        foreach (var path in (FileWalk.Enumerate(verb: "scan", roots: [repositoryRoot], include: [], exclude: [], extension: ".md") ?? [])) {
            names.Add(item: Path.GetFileName(path: path));
        }

        return names;
    }
    private static string Classify(string body) {
        if (SyncPattern.IsMatch(input: body)) {
            return "sync-coupling";
        }

        if (DebtPattern.IsMatch(input: body)) {
            return "debt-marker";
        }

        if (BannerPattern.IsMatch(input: body)) {
            return "banner-divider";
        }

        if (LooksLikeCode(body: body)) {
            return "commented-out-code";
        }

        if (HistoryPattern.IsMatch(input: body)) {
            return "narrative-history";
        }

        if (ProvenancePattern.IsMatch(input: body)) {
            return "dead-provenance";
        }

        return "unclassified";
    }
    // True when the body parses as a C# statement with no errors AND carries a real code signal — so
    // dead code registers but an English sentence that happens to parse as a bare expression statement
    // does not.
    private static bool LooksLikeCode(string body) {
        if ((body.Length == 0) || !CodeSignalPattern.IsMatch(input: body)) {
            return false;
        }

        return !SyntaxFactory.ParseStatement(text: body).GetDiagnostics().Any(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));
    }
    // The comment text without its // or /* */ delimiters, so the classifiers see only the prose/code.
    private static string StripMarkers(string text) {
        var trimmed = text;

        if (trimmed.StartsWith(comparisonType: StringComparison.Ordinal, value: "//")) {
            trimmed = trimmed[2..];
        } else if (trimmed.StartsWith(comparisonType: StringComparison.Ordinal, value: "/*")) {
            trimmed = trimmed[2..];

            if (trimmed.EndsWith(comparisonType: StringComparison.Ordinal, value: "*/")) {
                trimmed = trimmed[..^2];
            }
        }

        return trimmed.Trim();
    }
    // One text blob of every scanned .cs file (COMMENTS REMOVED) plus every shader source, and the set
    // of shader file names — what a "does this referent still exist?" check resolves against (substring
    // existence, not exact symbol binding). Comments are excluded because the probe would otherwise
    // resolve a cited define against the very comment that cites it, making the detector a tautology.
    private static string BuildReferentHaystack(SourceCorpus corpus, string shaderRoot, out HashSet<string> shaderFileNames) {
        var builder = new StringBuilder();

        shaderFileNames = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var parsed in corpus.Files) {
            AppendWithoutComments(builder: builder, parsed: parsed);
            builder.Append(value: '\n');
        }

        if (Directory.Exists(path: shaderRoot)) {
            foreach (var path in Directory.EnumerateFiles(path: shaderRoot, searchOption: SearchOption.AllDirectories, searchPattern: "*.*")) {
                if (ShaderExtensions.Contains(value: Path.GetExtension(path: path).ToLowerInvariant(), comparer: StringComparer.Ordinal)) {
                    shaderFileNames.Add(item: Path.GetFileName(path: path));
                    builder.Append(value: File.ReadAllText(path: path)).Append(value: '\n');
                }
            }
        }

        return builder.ToString();
    }
    // The file's text with every comment span elided — code, string literals and #directives survive,
    // prose does not.
    private static void AppendWithoutComments(StringBuilder builder, ParsedFile parsed) {
        var text = parsed.Text;
        var cursor = 0;

        foreach (var trivia in parsed.Root.DescendantTrivia()) {
            if (!IsComment(trivia: trivia)) {
                continue;
            }

            var span = trivia.Span;

            if (span.Start < cursor) {
                continue;
            }

            builder.Append(value: text, startIndex: cursor, count: (span.Start - cursor));
            cursor = span.End;
        }

        builder.Append(value: text, startIndex: cursor, count: (text.Length - cursor));
    }
    private static bool IsComment(SyntaxTrivia trivia) => (trivia.Kind() is
        SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia);
    // The cross-artifact referents named in the body, each tagged resolved/dangling, as the JSON array
    // body (no brackets); sets anyUnresolved when one dangles.
    private static string ResolveReferences(string body, string haystack, HashSet<string> shaderFileNames, HashSet<string> docNames, out bool anyUnresolved, out bool anySymbolUnresolved) {
        anyUnresolved = false;
        anySymbolUnresolved = false;

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var parts = new List<string>();

        if (docNames.Count > 0) {
            foreach (Match match in DocumentFilePattern.Matches(input: body)) {
                if (!seen.Add(item: match.Value)) {
                    continue;
                }

                var resolvedDoc = docNames.Contains(item: Path.GetFileName(path: match.Value));

                anyUnresolved |= !resolvedDoc;
                parts.Add(item: Reference(token: match.Value, kind: "doc", resolved: resolvedDoc));
            }
        }

        foreach (Match match in ShaderFilePattern.Matches(input: body)) {
            if (!seen.Add(item: match.Value)) {
                continue;
            }

            // `<name>.comp` is this tree's established shorthand for the `<name>.comp.hlsl` source, so
            // the bare form must resolve or every shader citation reports dangling.
            var resolved = (shaderFileNames.Contains(item: match.Value) || shaderFileNames.Contains(item: (match.Value + ".hlsl")));

            anyUnresolved |= !resolved;
            parts.Add(item: Reference(token: match.Value, kind: "file", resolved: resolved));
        }

        foreach (Match match in SymbolPattern.Matches(input: body)) {
            if (!seen.Add(item: match.Value)) {
                continue;
            }

            var resolved = haystack.Contains(value: match.Value, comparisonType: StringComparison.Ordinal);

            // ADVISORY, never provable: this corpus holds only OUR sources, so an external SDK or
            // hardware constant (VK_…, XINPUT_…, a disassembly's OBJECT_LENGTH) is indistinguishable
            // from a define that vanished. Reported apart from file referents for that reason — a
            // sweep that treated these as staleness deleted correct spec citations.
            anySymbolUnresolved |= !resolved;
            parts.Add(item: Reference(token: match.Value, kind: "symbol", resolved: resolved));
        }

        return string.Join(separator: ",", values: parts);
    }
    private static string Reference(string token, string kind, bool resolved) =>
        $"{{\"token\":{ScanJsonl.JsonString(value: token)},\"kind\":\"{kind}\",\"resolved\":{(resolved ? "true" : "false")}}}";
}
