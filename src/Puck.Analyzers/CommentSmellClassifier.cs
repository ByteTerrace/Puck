using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Puck.Analyzers;

/// <summary>
/// Buckets a non-XML inline comment by the kind of weakness it represents. The premise is that self-documenting code
/// needs no inline comment, so every one is a smell to triage. Buckets, first match wins:
/// <see cref="SyncCoupling"/>, <see cref="DebtMarker"/>, <see cref="BannerDivider"/>,
/// <see cref="CommentedOutCode"/>, <see cref="NarrativeHistory"/>, <see cref="DeadProvenance"/>, and
/// <see cref="Unclassified"/> for the rest. Every bucket but <see cref="Unclassified"/> is a named smell, which is
/// what <see cref="CountSmells"/> counts for <c>CommentSmells.json</c>. The classification is syntactic; whether an
/// unclassified comment is true is a claim only a reader of the code can settle.
/// </summary>
public static class CommentSmellClassifier {
    /// <summary>A prose guardrail over a constraint the compiler cannot see (source order) or cannot span (a
    /// C#-to-shader contract). These mark a missing single source of truth rather than a documentation gap.</summary>
    public const string SyncCoupling = "sync-coupling";
    /// <summary>A deferred-work marker: TODO, FIXME, HACK, XXX, KLUDGE, REVISIT, or "for now".</summary>
    public const string DebtMarker = "debt-marker";
    /// <summary>A run of four or more rule glyphs: structure, not information.</summary>
    public const string BannerDivider = "banner-divider";
    /// <summary>A body that parses as a C# statement and carries a real code signal: dead code in a comment.</summary>
    public const string CommentedOutCode = "commented-out-code";
    /// <summary>Past-tense narration of the code's own history, or a date. Git answers it better.</summary>
    public const string NarrativeHistory = "narrative-history";
    /// <summary>A citation of a process artifact (a plan phase, a review round, a numbered decision) that a reader
    /// outside that conversation cannot resolve.</summary>
    public const string DeadProvenance = "dead-provenance";
    /// <summary>Every comment no named bucket matches: the work list a reader judges by hand.</summary>
    public const string Unclassified = "unclassified";

    // `must match/mirror` is gated by a nearby structure word so a plain API fact ("must match the framebuffer")
    // stays out.
    private static readonly Regex SyncPattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"keep[\s\w]{0,16}in sync|kept in sync|stay(?:s|ing)? in sync|in lockstep|do not (?:alphabetize|reorder|re-order|sort)|load-bearing|same order as|must (?:match|mirror)\b[^.\n]*\b(?:layout|order|struct|block|offset|enum|field|kernel|glsl|shader|push-constant)\b|mirror of the"
    );
    private static readonly Regex DebtPattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"\b(?:TODO|FIXME|HACK|XXX|KLUDGE|REVISIT)\b|\bfor now\b"
    );
    private static readonly Regex BannerPattern = new(
        options: RegexOptions.Compiled,
        pattern: @"[-=*#_~]{4,}"
    );
    // Deliberately narrow: only phrases that can describe nothing but a previous state of the code, so a sentence
    // about what the code does now stays out even when it is in the past tense.
    private static readonly Regex HistoryPattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"\bused to (?:be|live|lived|carry|carried|do|have|had|call|called|read|return|returned|apply|applied|enforce|enforced|hold|held|sit|sat|mean|meant|work|fire|fired|refuse|refused|report|reported|resolve|resolved|write|wrote|store|stored|track|gate|gated|allow|allowed|throw|threw|log|push|pull|set|clear|reset|land|landed|check|checked)\b|\bthe former\b|\bpreviously (?:did|was|were|had|lived|carried|returned|read|applied|held|split|named)\b|\bprior to (?:this|that)\b|\bthe original (?:landing|implementation|version|shape|code|pass|fix)\b|\bbefore (?:this|the) (?:fix|change|landing|correction|review)\b|\bthe first pass\b|\bround-(?:one|two)\b|\bearlier draft\b|\bsince (?:deleted|renamed|retired|removed)\b|\bsuperseded\b|\b20\d{2}-\d{2}-\d{2}\b"
    );
    private static readonly Regex ProvenancePattern = new(
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled,
        pattern: @"\bopen decision \d|\badversarial[-\s]review\b|\bperf plan\b|\bdesign round\b|\bunit \d+[a-z]?\b|\bsurvey #\d|\bfinding \d|\bphase \d+\.\d|\bthe plan's\b"
    );
    // A trailing terminator, a leading statement keyword, or an operator prose rarely carries. It keeps an English
    // sentence that happens to parse as a bare expression out of the commented-out-code bucket.
    private static readonly Regex CodeSignalPattern = new(
        options: RegexOptions.Compiled,
        pattern: @";\s*$|^\s*(?:return|if|for|foreach|while|var|using|throw|await|public|private|internal|protected)\b|=>|==|!=|&&|\|\|"
    );

    private static bool LooksLikeCode(string body) {
        if (
            (body.Length == 0) ||
            !CodeSignalPattern.IsMatch(input: body)
        ) {
            return false;
        }

        return !SyntaxFactory.ParseStatement(text: body).GetDiagnostics().Any(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>Returns a comment's body: its text without the <c>//</c> or <c>/* */</c> delimiters, trimmed.</summary>
    /// <param name="text">The comment trivia's text.</param>
    /// <returns>The prose or code the comment carries.</returns>
    public static string Body(string text) {
        var trimmed = text.Trim();

        if (trimmed.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "//"
        )) {
            trimmed = trimmed.Substring(startIndex: 2);
        } else if (trimmed.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "/*"
        )) {
            trimmed = trimmed.Substring(startIndex: 2);

            if (trimmed.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: "*/"
            )) {
                trimmed = trimmed.Substring(
                    length: (trimmed.Length - 2),
                    startIndex: 0
                );
            }
        }

        return trimmed.Trim();
    }
    /// <summary>Classifies a comment body (<see cref="Body"/>) into its first matching bucket.</summary>
    /// <param name="body">The comment's body, without delimiters.</param>
    /// <returns>One of the bucket names this class declares.</returns>
    public static string Classify(string body) {
        if (SyncPattern.IsMatch(input: body)) {
            return SyncCoupling;
        }

        if (DebtPattern.IsMatch(input: body)) {
            return DebtMarker;
        }

        if (BannerPattern.IsMatch(input: body)) {
            return BannerDivider;
        }

        if (LooksLikeCode(body: body)) {
            return CommentedOutCode;
        }

        if (HistoryPattern.IsMatch(input: body)) {
            return NarrativeHistory;
        }

        if (ProvenancePattern.IsMatch(input: body)) {
            return DeadProvenance;
        }

        return Unclassified;
    }
    /// <summary>Counts a syntax tree's inline comments that fall in a named smell bucket — every bucket but
    /// <see cref="Unclassified"/>. XML documentation comments are not inline comments and are never counted.</summary>
    /// <param name="root">The tree's root node.</param>
    /// <returns>The file's comment-smell count.</returns>
    public static int CountSmells(SyntaxNode root) {
        var count = 0;

        foreach (var trivia in root.DescendantTrivia()) {
            if (
                IsInlineComment(trivia: trivia) &&
                !string.Equals(
                a: Classify(body: Body(text: trivia.ToString())),
                b: Unclassified,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                count++;
            }
        }

        return count;
    }
    /// <summary>Returns whether a trivia is an inline comment: a <c>//</c> or <c>/* */</c> comment, never XML
    /// documentation.</summary>
    /// <param name="trivia">The trivia to test.</param>
    /// <returns><see langword="true"/> for a single-line or multi-line comment.</returns>
    public static bool IsInlineComment(SyntaxTrivia trivia) =>
        (trivia.IsKind(kind: SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(kind: SyntaxKind.MultiLineCommentTrivia));
}
