using System.Numerics;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Maths;

namespace Puck.Cli.Scan.Analyzers;

// What marks a technique in C# source, read from syntax alone: the facts the duplicates analyzer matches one declaration
// against another by, because the same technique under another name shares these and not its identifiers.
//   constants  — a numeric literal that is not a round number, a power of two or a mask (`0x9E3779B97F4A7C15`, a prime modulus);
//   markers    — a BCL type or call that only a technique reaches for (`Math.BigMul`, `UInt128`, `Interlocked`, `ArrayPool<T>`,
//                `IEqualityComparer<long>`);
//   idioms     — a shape of expression that names the technique (`ulong.MaxValue / d`, the reciprocal multiply's divisor).
// A shape is the declaration's token stream with identifiers, strings and ordinary numbers abstracted, so a renamed copy has
// the same shape; the marker names stay, since they are the technique and not a choice of the author's.
internal static class TechniqueFeatures {
    // A helper whose name or whose markers say it is a general technique, with the shared home an author searches first.
    internal sealed record HelperCategory(string Name, Regex Names, string Home, string[] Markers);

    // The shingle length of a shape: long enough that a shared run is a shared statement, short enough that a renamed
    // copy with one extra operand still shares most of them.
    public const int ShingleLength = 5;
    // The longest literal, quotes included, a shape keeps verbatim.
    private const int ShortLiteralLength = 18;

    // A name regex over words of an identifier: each stem matches capitalized anywhere in it (`BitMix`, `FineDivisor`), or
    // lowercase only where no letter precedes it (`mixKey`, never `remix`), so a word that merely contains the stem's
    // letters (`HasHardware` for `hash`) does not match.
    private static Regex Words(params string[] stems) =>
        new(
            pattern: $"(?:{string.Join(separator: "|", values: stems.Select(selector: static stem => $"(?<![A-Za-z]){stem}|{char.ToUpperInvariant(c: stem[0])}{stem[1..]}"))})",
            options: RegexOptions.CultureInvariant
        );

    // The technique helpers a `private` or `internal` declaration may be a second copy of. `Home` is the area the shared one
    // lives in, named so the author searches it before keeping the helper.
    public static readonly HelperCategory[] Categories = [
        new(
            Name: "divisor",
            Names: Words("divisor", "reciprocal", "quotient", "remainder", "modulo", "divMod", "ceilDiv", "magic"),
            Home: "Puck.Maths",
            Markers: ["idiom:reciprocal", "use:Math.BigMul", "use:Math.DivRem"]
        ),
        new(
            Name: "hash",
            Names: Words("hash", "mix", "fnv", "fibonacci", "murmur", "splitMix", "avalanche", "checksum", "digest"),
            Home: "Puck.Maths",
            // The golden-ratio multiplier, the FNV-1a offset and prime, and the splitmix and murmur finalizer multipliers.
            Markers: ["use:HashCode", "use:SHA256", "const:0x9E3779B97F4A7C15", "const:0xCBF29CE484222325", "const:0x100000001B3", "const:0xBF58476D1CE4E5B9", "const:0x94D049BB133111EB", "const:0xFF51AFD7ED558CCD", "const:0xC4CEB9FE1A85EC53"]
        ),
        new(
            Name: "comparer",
            Names: Words("comparer"),
            Home: "Puck.Maths",
            Markers: ["use:IEqualityComparer", "use:IComparer"]
        ),
        new(
            Name: "pool",
            Names: Words("pool", "freeList", "recycl"),
            Home: "the shared pools (search `ArrayPool` and `Pool` across src)",
            Markers: ["use:ArrayPool", "use:MemoryPool"]
        ),
        new(
            Name: "random",
            Names: Words("rng", "random", "prng", "pcg", "xorShift", "splitMix"),
            Home: "Puck.Maths",
            Markers: []
        ),
        new(
            Name: "saturating or rounding arithmetic",
            Names: Words("saturat", "isqrt", "gcd", "lcm", "alignUp", "roundUp", "roundDown", "ceilingDiv", "popCount"),
            Home: "Puck.Maths",
            Markers: []
        ),
        new(
            Name: "retry",
            Names: Words("retry", "backoff"),
            Home: "the process and network helpers (search `Retry` across src)",
            Markers: []
        ),
        new(
            Name: "cache",
            Names: Words("cache", "memo", "lru", "intern"),
            Home: "the shared caches (search `Cache` and `Lru` across src)",
            Markers: ["use:ConcurrentDictionary", "use:FrozenDictionary", "use:Lazy"]
        ),
    ];

    // BCL types a technique reaches for; the type name, and for a generic one its type arguments, is the marker.
    private static readonly HashSet<string> MarkerTypes = new(comparer: StringComparer.Ordinal) {
        "ArrayPool", "BigInteger", "BinaryPrimitives", "BitOperations", "ConcurrentDictionary", "ConcurrentQueue", "FrozenDictionary",
        "FrozenSet", "HashCode", "IComparer", "IEqualityComparer", "Int128", "Interlocked", "Lazy", "MemoryMarshal", "MemoryPool",
        "SHA1", "SHA256", "UInt128", "Unsafe", "Vector128", "Vector256", "Vector512",
    };
    // BCL members of a general type that mark a technique (`Math` itself is everywhere; its widening and division-with-remainder
    // calls are not).
    private static readonly HashSet<string> MarkerMembers = new(comparer: StringComparer.Ordinal) {
        "Math.BigMul", "Math.DivRem", "Math.FusedMultiplyAdd", "Math.ILogB", "Math.ReciprocalEstimate",
    };
    // Names a shape keeps verbatim rather than abstracts: the markers above and the numeric limits an idiom divides by.
    private static readonly HashSet<string> KeptNames = new(
        collection: [.. MarkerTypes, .. MarkerMembers.Select(selector: static member => member[(member.IndexOf(value: '.') + 1)..]), "Math", "MaxValue", "MinValue"],
        comparer: StringComparer.Ordinal
    );
    private static readonly ulong IdentifierHash = Fnv1aHash.Compute(values: "\u0002I");
    private static readonly ulong NumberHash = Fnv1aHash.Compute(values: "\u0002N");
    private static readonly ulong StringHash = Fnv1aHash.Compute(values: "\u0002S");

    // A literal worth matching on: a number of at least 2^20 whose bits do not form one run (a power of two, a mask) and that
    // is not a round decimal.
    private static string? DistinctiveConstant(SyntaxToken token) {
        var value = token.Value switch {
            ulong unsigned => unsigned,
            long signed when (signed > 0) => ((ulong)signed),
            uint unsigned => unsigned,
            int signed when (signed > 0) => ((ulong)signed),
            _ => 0UL,
        };

        if (value < 0x10_0000UL) {
            return null;
        }

        var run = (value >> BitOperations.TrailingZeroCount(value: value));

        if ((run & (run + 1)) == 0) {
            return null;
        }

        var hex = token.Text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "0x");

        return ((!hex && ((value % 1000UL) == 0))
            ? null
            : $"const:0x{value:X}");
    }
    private static bool IsMaxDivide(ExpressionSyntax expression) {
        while (expression is ParenthesizedExpressionSyntax parenthesized) {
            expression = parenthesized.Expression;
        }

        return (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.DivideExpression, Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MaxValue" } });
    }
    private static string? Marker(SyntaxNode node) {
        switch (node) {
            case GenericNameSyntax generic when MarkerTypes.Contains(item: generic.Identifier.ValueText):
                return $"use:{generic.Identifier.ValueText}<{string.Join(separator: ",", values: generic.TypeArgumentList.Arguments.Select(selector: static argument => argument.ToString()))}>";
            case IdentifierNameSyntax identifier when MarkerTypes.Contains(item: identifier.Identifier.ValueText):
                return $"use:{identifier.Identifier.ValueText}";
            case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: var owner }, Name.Identifier.ValueText: var member } when MarkerMembers.Contains(item: $"{owner}.{member}"):
                return $"use:{owner}.{member}";
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.DivideExpression, Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MaxValue" } }:
                return "idiom:max-divide";
            // `MaxValue / d + 1`: the multiplier of a division by a fixed divisor done as one multiply.
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum when (IsMaxDivide(expression: sum.Left) || IsMaxDivide(expression: sum.Right)):
                return "idiom:reciprocal";
            default:
                return null;
        }
    }
    private static ulong TokenHash(SyntaxToken token) => token.Kind() switch {
        SyntaxKind.IdentifierToken => (KeptNames.Contains(item: token.ValueText)
            ? Fnv1aHash.Compute(values: token.ValueText.AsSpan())
            : IdentifierHash),
        SyntaxKind.NumericLiteralToken => ((DistinctiveConstant(token: token) is { } constant)
            ? Fnv1aHash.Compute(values: constant.AsSpan())
            : NumberHash),
        // A short literal is structure (a format string, a separator); a long one is a message a copy rewords.
        SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.CharacterLiteralToken => ((token.Text.Length > ShortLiteralLength)
            ? StringHash
            : Fnv1aHash.Compute(values: token.Text.AsSpan())),
        _ => Fnv1aHash.Compute(values: token.Text.AsSpan()),
    };

    // Whether a path lies in the area a category names as its home, so a helper already there is not flagged.
    public static bool InHome(HelperCategory category, string relative) =>
        (category.Home.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.Maths") && relative.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.Maths/"));
    // The technique facts of a declaration, or null when it has none.
    public static HashSet<string>? Features(SyntaxNode node) {
        HashSet<string>? features = null;

        foreach (var descendant in node.DescendantNodesAndTokens()) {
            string? feature;

            if (descendant.IsToken) {
                feature = ((descendant.AsToken().IsKind(kind: SyntaxKind.NumericLiteralToken))
                    ? DistinctiveConstant(token: descendant.AsToken())
                    : null);
            } else {
                feature = Marker(node: descendant.AsNode()!);
            }

            if (feature is not null) {
                (features ??= new HashSet<string>(comparer: StringComparer.Ordinal)).Add(item: feature);

                // A generic marker also counts as its bare type, so `IEqualityComparer<long>` meets `IEqualityComparer<int>`.
                if (feature.EndsWith(value: '>')) {
                    _ = features.Add(item: feature[..feature.IndexOf(value: '<')]);
                }
            }
        }

        return features;
    }
    // The distinct shingles of a declaration's shape, ascending: one hash per run of `ShingleLength` consecutive tokens.
    public static ulong[] Shingles(SyntaxNode node, out int tokenCount) {
        var hashes = new List<ulong>();

        foreach (var token in node.DescendantTokens()) {
            hashes.Add(item: TokenHash(token: token));
        }

        tokenCount = hashes.Count;

        var shingles = new List<ulong>();

        for (var start = 0; ((start + ShingleLength) <= hashes.Count); start++) {
            var hash = Fnv1aHash.Create();

            for (var offset = 0; (offset < ShingleLength); offset++) {
                hash.Add(value: hashes[(start + offset)]);
            }

            shingles.Add(item: hash.Value);
        }

        shingles.Sort();

        return [.. shingles.Where(predicate: (shingle, index) => ((index == 0) || (shingle != shingles[(index - 1)])))];
    }
}
