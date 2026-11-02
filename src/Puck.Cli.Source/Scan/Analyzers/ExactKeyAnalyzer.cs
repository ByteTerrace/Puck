using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Scan.Analyzers;

// Lists cache keys and memo checks that compare continuous inputs exactly, for a person to judge whether a tolerance
// or a quantization is meant: a key over a float, a vector or a fixed-point value that moves a little every frame never
// matches, so the cache costs its lookups and helps nothing. Each record is one site:
//   key-type       — a type serving as a cache key (its name ends in Query, Memo, Cache or Signature; it is the key
//                    type of a dictionary or set declared under a cache's name; or its name ends in Key and it declares a
//                    Matches) that holds continuous members and compares them exactly: a record, or a type that
//                    declares Equals, Matches or operator ==.
//   bit-equality   — an equality between two BitConverter bit patterns of floats (SingleToInt32Bits and its kin), the
//                    spelling of "exactly the inputs it was computed for".
//   cached-compare — an == or != against a field named as a remembered value (cached*, last*, previous*, memo*) whose
//                    declared type is continuous.
// Detection is syntactic: types are matched by the name a declaration spells, so an alias or a var-typed local is
// missed, and a key whose exactness is intended (a value that is copied, never recomputed) is a false positive to
// triage.
internal sealed class ExactKeyAnalyzer : ISourceAnalyzer {
    private static readonly HashSet<string> ContinuousTypes = new(comparer: StringComparer.Ordinal) {
        "float", "double", "Half", "Single", "Double",
        "Vector2", "Vector3", "Vector4", "Quaternion", "Matrix3x2", "Matrix4x4", "Plane",
        "FixedQ4816", "FixedQ3232", "FixedVector2", "FixedVector3", "FixedQuaternion", "FixedPosition", "FixedRigidTransform", "FixedComplex", "FixedDual",
    };
    // Collections that compare their keys by value; a ConditionalWeakTable compares by reference and is left out.
    private static readonly HashSet<string> KeyedCollections = new(comparer: StringComparer.Ordinal) {
        "Dictionary", "ConcurrentDictionary", "FrozenDictionary", "ImmutableDictionary", "SortedDictionary", "HashSet", "FrozenSet", "ImmutableHashSet",
    };
    // A collection counts as a cache by the name it is declared under; a dictionary of authored documents deduplicated
    // by value compares copies, which exactness serves.
    private static readonly Regex CacheName = new(pattern: "(?i)(cache|memo|answers|reuse|recent)");
    private static readonly HashSet<string> BitPatterns = new(comparer: StringComparer.Ordinal) {
        "SingleToInt32Bits", "SingleToUInt32Bits", "DoubleToInt64Bits", "DoubleToUInt64Bits", "HalfToInt16Bits", "HalfToUInt16Bits",
    };
    // A name that says cache key on its own; a bare `Key` suffix also names keyframes and input keys, so it counts only
    // with a cache that keys by it or a Matches of its own.
    private static readonly Regex KeyName = new(pattern: "(Query|Memo|Cache|Cached|Signature)$");
    private static readonly Regex RememberedName = new(pattern: "^(m_|s_|_)?(cached|last|previous|prev|memo)[A-Z_]", options: RegexOptions.IgnoreCase);

    private static string Simple(TypeSyntax type) {
        var name = type switch {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            NullableTypeSyntax nullable => Simple(type: nullable.ElementType),
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
            _ => type.ToString(),
        };

        return name;
    }
    // The continuous members a type declares: fields, auto-properties and a record's positional parameters.
    private static List<string> ContinuousMembers(TypeDeclarationSyntax type) {
        var members = new List<string>();

        if (type.ParameterList is { } parameters) {
            members.AddRange(collection: parameters.Parameters.Where(predicate: static parameter => (parameter.Type is { } spelled) && ContinuousTypes.Contains(item: Simple(type: spelled))).Select(selector: static parameter => parameter.Identifier.ValueText));
        }
        foreach (var member in type.Members) {
            switch (member) {
                case FieldDeclarationSyntax field when !field.Modifiers.Any(kind: SyntaxKind.ConstKeyword) && !field.Modifiers.Any(kind: SyntaxKind.StaticKeyword) && ContinuousTypes.Contains(item: Simple(type: field.Declaration.Type)):
                    members.AddRange(collection: field.Declaration.Variables.Select(selector: static variable => variable.Identifier.ValueText));
                    break;
                case PropertyDeclarationSyntax property when !property.Modifiers.Any(kind: SyntaxKind.StaticKeyword) && (property.AccessorList?.Accessors.All(predicate: static accessor => (accessor.Body is null) && (accessor.ExpressionBody is null)) ?? false) && ContinuousTypes.Contains(item: Simple(type: property.Type)):
                    members.Add(item: property.Identifier.ValueText);
                    break;
            }
        }

        return members;
    }
    // The name a collection type is declared under: the field, property or local it types.
    private static string DeclaredName(TypeSyntax type) => type.Ancestors().FirstOrDefault(predicate: static node => node is VariableDeclarationSyntax or PropertyDeclarationSyntax) switch {
        VariableDeclarationSyntax variable => string.Join(separator: ",", values: variable.Variables.Select(selector: static declarator => declarator.Identifier.ValueText)),
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        _ => "",
    };
    private static bool HasMatches(TypeDeclarationSyntax type) =>
        type.Members.OfType<MethodDeclarationSyntax>().Any(predicate: static method => method.Identifier.ValueText is "Matches" or "SameAs" or "Same");
    private static bool ComparesItself(TypeDeclarationSyntax type) =>
        (type is RecordDeclarationSyntax) ||
        type.Members.Any(predicate: static member => member switch {
            MethodDeclarationSyntax method => method.Identifier.ValueText is "Equals" or "Matches" or "SameAs" or "Same",
            OperatorDeclarationSyntax op => op.OperatorToken.IsKind(kind: SyntaxKind.EqualsEqualsToken),
            _ => false,
        });
    private static bool IsBitPattern(ExpressionSyntax expression) =>
        (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } }) && BitPatterns.Contains(item: name);
    // The continuous fields each type declares, by name, for the cached-compare check.
    private static Dictionary<string, string> ContinuousFields(TypeDeclarationSyntax type) {
        var fields = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>()) {
            if (!ContinuousTypes.Contains(item: Simple(type: field.Declaration.Type))) {
                continue;
            }
            foreach (var variable in field.Declaration.Variables) {
                fields[variable.Identifier.ValueText] = field.Declaration.Type.ToString();
            }
        }

        return fields;
    }
    private static string? OperandName(ExpressionSyntax operand) => operand switch {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var name } => name.Identifier.ValueText,
        _ => null,
    };

    public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options) {
        var keyed = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var parsed in corpus.Files) {
            foreach (var generic in parsed.Root.DescendantNodes().OfType<GenericNameSyntax>()) {
                if (KeyedCollections.Contains(item: generic.Identifier.ValueText) && (generic.TypeArgumentList.Arguments.Count > 0) && CacheName.IsMatch(input: DeclaredName(type: generic))) {
                    _ = keyed.Add(item: Simple(type: generic.TypeArgumentList.Arguments[0]));
                }
            }
        }

        var sites = new ScanSites();

        foreach (var parsed in corpus.Files) {
            foreach (var node in parsed.Root.DescendantNodes()) {
                switch (node) {
                    case TypeDeclarationSyntax type when (KeyName.IsMatch(input: type.Identifier.ValueText) || keyed.Contains(item: type.Identifier.ValueText) || (type.Identifier.ValueText.EndsWith(value: "Key", comparisonType: StringComparison.Ordinal) && HasMatches(type: type))) && ComparesItself(type: type): {
                            var continuous = ContinuousMembers(type: type);

                            if (continuous.Count == 0) {
                                break;
                            }

                            var (line, _) = ScanJsonl.LineRange(location: type.Identifier.GetLocation());
                            var role = (keyed.Contains(item: type.Identifier.ValueText) ? "keys a cache" : "is named as a cache key");

                            sites.Add(
                                endLine: line,
                                file: parsed.Relative,
                                kind: "key-type",
                                line: line,
                                text: $"{type.Identifier.ValueText} {role} and compares {string.Join(separator: ", ", values: continuous)} exactly"
                            );

                            break;
                        }
                    case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.NotEqualsExpression } binary: {
                            var (line, endLine) = ScanJsonl.LineRange(location: binary.GetLocation());

                            if (IsBitPattern(expression: binary.Left) && IsBitPattern(expression: binary.Right)) {
                                sites.Add(endLine: endLine, file: parsed.Relative, kind: "bit-equality", line: line, text: binary.ToString());
                                break;
                            }

                            var fields = ((binary.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { } holder) ? ContinuousFields(type: holder) : []);
                            var remembered = new[] { binary.Left, binary.Right }
                                .Select(selector: OperandName)
                                .FirstOrDefault(predicate: name => (name is not null) && RememberedName.IsMatch(input: name) && fields.ContainsKey(key: name));

                            if (remembered is not null) {
                                sites.Add(endLine: endLine, file: parsed.Relative, kind: "cached-compare", line: line, text: $"{binary} ({remembered} is {fields[remembered]})");
                            }

                            break;
                        }
                }
            }
        }

        return sites.Finish(
            digest: $"scan[exact-keys]: {sites.Count} exact comparisons of continuous inputs ({sites.Breakdown()}) across {sites.FileCount} files (of {corpus.FileCount} scanned).",
            maxPerChunk: options.MaxPerChunk
        );
    }
}
