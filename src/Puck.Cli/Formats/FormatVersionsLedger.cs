using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Puck.Cli.Format;
using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Formats;

/// <summary>One strictly versioned format in <c>FormatVersions.json</c>.</summary>
/// <param name="Id">The format's name: its declaring type and member, such as <c>WorldFederationCodec.WireKey</c>.</param>
/// <param name="Source">The repository-relative path of the file that declares the token.</param>
/// <param name="Shape">A digest of the format's canonical source (<see cref="FormatVersionsLedger.ShapeOf"/>).</param>
/// <param name="Token">The format's current token.</param>
internal sealed record FormatEntry(string Id, string Source, string Shape, string Token);
/// <summary>
/// <c>FormatVersions.json</c>: every strictly versioned wire, persisted, or cache format the source declares, its current
/// token, and the file that declares it. The ledger is generated from the source: <see cref="Discover"/> reads every
/// declaration of a recognized shape and <c>puck formats</c> writes the result, so the constants stay the one source
/// of truth and the ledger is their checked-in mirror.
/// <para>
/// A format bumped on two branches collides in the ledger's lines, not in the code: each entry spells its token on
/// its own line, and every format carries a digest of its canonical source on a line of its own. Two
/// branches that bump to the same new token merge their token lines cleanly, but they changed the codec differently,
/// so their digest lines differ and conflict. A branch that changes the codec without a bump moves the digest and
/// fails <c>puck formats --check</c> until the author re-records it, which is the moment to ask whether the encoding
/// changed and the token should too.
/// </para>
/// </summary>
internal static partial class FormatVersionsLedger {
    /// <summary>The ledger's file name at the repository root.</summary>
    public const string FileName = "FormatVersions.json";
    /// <summary>The ledger's own shape version.</summary>
    public const int Format = 2;

    // Members that carry a binary format's token when their initializer holds exactly one literal. A document
    // schema is recognised by its value instead (SchemaToken), whatever the member is called.
    private static readonly HashSet<string> TokenMembers = new(comparer: StringComparer.Ordinal) {
        "AbiVersion",
        "CompilerVersion",
        "CurrentVersion",
        "Format",
        "FormatVersion",
        "JournalMagic",
        "JournalVersion",
        "Magic",
        "PackMagic",
        "PackVersion",
        "ProtocolKey",
        "Revision",
        "ShapeToken",
        "SupportedFormat",
        "SupportedVersion",
        "TokenAlgorithm",
        "Version",
        "WireKey",
        "WireProtocolKey",
    };

    [GeneratedRegex(pattern: @"^puck\.[a-z0-9]+([.-][a-z0-9]+)*\.v[0-9]+\z", options: RegexOptions.CultureInvariant)]
    private static partial Regex SchemaToken();
    private static bool IsTokenField(MemberDeclarationSyntax member) {
        var modifiers = member.Modifiers.Select(selector: static modifier => modifier.Text).ToHashSet(comparer: StringComparer.Ordinal);

        return member switch {
            FieldDeclarationSyntax => (modifiers.Contains(item: "const") || (modifiers.Contains(item: "static") && modifiers.Contains(item: "readonly"))),
            PropertyDeclarationSyntax property => (modifiers.Contains(item: "static") || (property.ExpressionBody is not null)),
            _ => false,
        };
    }
    private static IEnumerable<(string Name, ExpressionSyntax Value)> Initializers(MemberDeclarationSyntax member) {
        switch (member) {
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables) {
                    if (variable.Initializer is { } initializer) {
                        yield return (variable.Identifier.Text, initializer.Value);
                    }
                }

                break;
            case PropertyDeclarationSyntax property:
                if (property.ExpressionBody is { } body) {
                    yield return (property.Identifier.Text, body.Expression);
                } else if (property.Initializer is { } propertyInitializer) {
                    yield return (property.Identifier.Text, propertyInitializer.Value);
                }

                break;
            default:
                break;
        }
    }
    // A numeric token whose bytes, least significant first, are printable ASCII and long enough to be a name rather
    // than a count is a four-character code or key, and is spelled as that text: 0x354445464B435550 is "PUCKFED5".
    private static string SpellNumber(object? value) {
        var number = Convert.ToUInt64(
            provider: CultureInfo.InvariantCulture,
            value: value
        );
        var bytes = new List<byte>();

        for (var rest = number; (rest != 0); rest >>= 8) {
            bytes.Add(item: ((byte)(rest & 0xFF)));
        }

        return (((bytes.Count >= 4) && bytes.All(predicate: static item => ((item >= 0x20) && (item <= 0x7E))))
            ? Encoding.ASCII.GetString(bytes: [.. bytes])
            : number.ToString(provider: CultureInfo.InvariantCulture));
    }
    private static bool TryTokenOf(string memberName, ExpressionSyntax value, out string token) {
        while (value is ParenthesizedExpressionSyntax parentheses) { value = parentheses.Expression; }
        var tokens = value.DescendantTokens().ToArray();
        var strings = tokens.Where(predicate: static item => item.IsKind(kind: SyntaxKind.StringLiteralToken)).ToArray();

        token = string.Empty;

        if (
            (value is LiteralExpressionSyntax) &&
            (strings.Length == 1) &&
            SchemaToken().IsMatch(input: strings[0].ValueText)
        ) {
            token = strings[0].ValueText;

            return true;
        }
        if (!TokenMembers.Contains(item: memberName)) {
            return false;
        }

        var spelled = tokens.Where(predicate: static item => (item.IsKind(kind: SyntaxKind.StringLiteralToken) || item.IsKind(kind: SyntaxKind.Utf8StringLiteralToken) || item.IsKind(kind: SyntaxKind.NumericLiteralToken))).ToArray();

        if (spelled.Length != 1) {
            return false;
        }

        // A string that names a version spells a number; an export name or a message that happens to share a member
        // name does not.
        if (
            !spelled[0].IsKind(kind: SyntaxKind.NumericLiteralToken) &&
            !spelled[0].ValueText.Any(predicate: char.IsAsciiDigit) &&
            !spelled[0].IsKind(kind: SyntaxKind.Utf8StringLiteralToken)
        ) {
            return false;
        }

        token = (spelled[0].IsKind(kind: SyntaxKind.NumericLiteralToken)
            ? SpellNumber(value: spelled[0].Value)
            : spelled[0].ValueText);

        return true;
    }
    private static string StemOf(string path) {
        var name = path[(path.LastIndexOf(value: '/') + 1)..];
        var dot = name.IndexOf(value: '.');

        return name[..dot];
    }
    private static string DirectoryOf(string path) => path[..(path.LastIndexOf(value: '/') + 1)];

    /// <summary>Digests canonical syntax of the declaring file, partial siblings, data types and shared codec
    /// dependencies. Formatting, trivia and local renames do not move it; operator grouping, argument binding,
    /// evaluation order and serialized member names remain significant.</summary>
    /// <param name="files">Every source file's text, by repository-relative path.</param>
    /// <param name="source">The declaring file.</param>
    /// <returns>Sixteen lowercase hexadecimal digits of a SHA-256.</returns>
    public static string ShapeOf(IReadOnlyDictionary<string, string> files, string source) => new Shapes(files: files).Of(source: source);

    private sealed class Shapes {
        private readonly IReadOnlyDictionary<string, string> m_files;
        private readonly Dictionary<string, SyntaxTree> m_trees;

        private readonly Dictionary<string, string> m_hashes = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_fragments = new(comparer: StringComparer.Ordinal);

        private CSharpCompilation? m_compilation;

        public Shapes(IReadOnlyDictionary<string, string> files) {
            m_files = files;
            m_trees = files.ToDictionary(pair => pair.Key, pair => ((SyntaxTree)CSharpSyntaxTree.ParseText(
                text: pair.Value, path: pair.Key, options: CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview))), StringComparer.Ordinal);
        }

        private SemanticModel Model(SyntaxTree tree) {
            m_compilation ??= CSharpCompilation.Create(
                assemblyName: "FormatShapes",
                syntaxTrees: m_trees.Values.Append(element: CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;", CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview))),
                references: ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                    .Select(selector: path => MetadataReference.CreateFromFile(path)),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            return m_compilation.GetSemanticModel(tree);
        }

        public string Of(string source) {
            if (m_hashes.TryGetValue(key: source, value: out var hash)) { return hash; }
            var builder = new StringBuilder();

            foreach (var path in Sources(source: source)) {
                builder.Append(value: Fragment(path: path)).Append(value: '\u0001');
            }
            hash = Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: builder.ToString())))[..16].ToLowerInvariant();
            m_hashes[source] = hash;
            return hash;
        }

        private IEnumerable<string> Sources(string source) {
            var sources = ShapeSources(files: m_files, source: source).ToHashSet(comparer: StringComparer.Ordinal);
            var pending = new Queue<string>(collection: sources);

            while (pending.TryDequeue(result: out var path)) {
                var tree = m_trees[path];
                var model = Model(tree: tree);

                foreach (var node in tree.GetRoot().DescendantNodes()) {
                    var type = node switch {
                        PropertyDeclarationSyntax property => property.Type,
                        FieldDeclarationSyntax field => field.Declaration.Type,
                        ParameterSyntax parameter when (parameter.Parent?.Parent is TypeDeclarationSyntax) => parameter.Type,
                        BaseTypeSyntax basis => basis.Type,
                        _ => null,
                    };

                    if (type is not null) { AddType(type: model.GetTypeInfo(type).Type); }
                }
            }
            return sources.Order(comparer: StringComparer.Ordinal);

            void AddType(ITypeSymbol? type) {
                if (type is IArrayTypeSymbol array) { AddType(type: array.ElementType); }
                if (type is not INamedTypeSymbol named) { return; }
                foreach (var argument in named.TypeArguments) { AddType(type: argument); }
                foreach (var declaration in named.OriginalDefinition.DeclaringSyntaxReferences) {
                    var path = declaration.SyntaxTree.FilePath;

                    if (m_trees.ContainsKey(key: path) && sources.Add(item: path)) { pending.Enqueue(item: path); }
                }
            }
        }
        private string Fragment(string path) {
            if (m_fragments.TryGetValue(key: path, value: out var fragment)) { return fragment; }
            var builder = new StringBuilder();
            var tree = m_trees[path];
            var root = new NullPatternRewriter(model: Model(tree: tree)).Visit(node: tree.GetRoot())!;
            // Run the existing syntactic normalizers on a trivia-free copy. A comment cannot decide whether a
            // declaration or initializer is sorted in the fingerprint.
            root = root.ReplaceTokens(root.DescendantTokens(), static (token, _) => token.WithoutTrivia()).NormalizeWhitespace();
            foreach (var pass in FormatPasses.All.Where(predicate: pass => (pass.Default && pass.Syntactic))) {
                root = pass.Apply(node: root);
                root = CSharpSyntaxTree.ParseText(root.ToFullString(), CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview)).GetRoot();
            }
            var canonicalTree = CSharpSyntaxTree.ParseText(root.ToFullString(), CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview), path);

            _ = Model(tree: tree);
            var model = m_compilation!.ReplaceSyntaxTree(newTree: canonicalTree, oldTree: tree).GetSemanticModel(canonicalTree);
            var names = new Dictionary<ISymbol, string>(comparer: SymbolEqualityComparer.Default);

            foreach (var node in canonicalTree.GetRoot().DescendantNodes()) {
                if ((node is ParameterSyntax) && (node.Parent?.Parent is RecordDeclarationSyntax)) { continue; }
                if ((node is ParameterSyntax or VariableDeclaratorSyntax or SingleVariableDesignationSyntax or ForEachStatementSyntax or CatchDeclarationSyntax)
                    && (model.GetDeclaredSymbol(node) is ILocalSymbol or IParameterSymbol)) {
                    var symbol = model.GetDeclaredSymbol(node)!;

                    names.TryAdd(key: symbol, value: $"local{names.Count.ToString(provider: CultureInfo.InvariantCulture)}");
                }
            }
            AppendShape(canonicalTree.GetRoot(), builder, model, names);
            fragment = builder.ToString();
            m_fragments[path] = fragment;
            return fragment;
        }
    }

    private static void AppendShape(SyntaxNode node, StringBuilder builder, SemanticModel model, IReadOnlyDictionary<ISymbol, string> names) {
        if ((node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
            && (model.GetConstantValue(node) is { HasValue: true, Value: string nameOf })) {
            builder.Append(value: "nameof:").Append(value: nameOf.Length.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':');
            AppendText(builder: builder, text: nameOf);
            return;
        }
        if (node is ParenthesizedExpressionSyntax parentheses) {
            AppendShape(parentheses.Expression, builder, model, names);
            return;
        }
        builder.Append(value: node.RawKind.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '{');
        if ((node is ArgumentListSyntax list) && (ArgumentBindings(list: list, model: model) is { } bindings)) {
            var arguments = bindings.AsEnumerable();

            if (!arguments.Any(predicate: binding => ExpressionSafety.HasSideEffect(expression: binding.Argument.Expression, model: model))) {
                arguments = arguments.OrderBy(keySelector: binding => binding.Ordinal);
            }
            foreach (var (argument, ordinal) in arguments) {
                builder.Append(value: ordinal.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':');
                builder.Append(value: argument.RefKindKeyword.ValueText).Append(value: ':');
                AppendShape(argument.Expression, builder, model, names);
            }
        } else {
            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.IsNode) {
                    AppendShape(child.AsNode()!, builder, model, names);
                } else {
                    var token = child.AsToken();

                    if (token.IsKind(kind: SyntaxKind.CommaToken)) { continue; }
                    var text = token.ValueText;

                    if (token.IsKind(kind: SyntaxKind.IdentifierToken)) {
                        var symbol = (model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol);

                        if ((symbol is not null) && names.TryGetValue(key: symbol, value: out var name)) { text = name; }
                    }
                    builder.Append(value: token.RawKind.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':')
                        .Append(value: text.Length.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':');
                    AppendText(builder: builder, text: text);
                    builder.Append(value: ';');
                }
            }
        }
        builder.Append(value: '}');
    }
    private static void AppendText(string text, StringBuilder builder) {
        foreach (var character in text) { builder.Append(value: ((int)character).ToString(format: "x4", provider: CultureInfo.InvariantCulture)); }
    }
    private static IReadOnlyList<(ArgumentSyntax Argument, int Ordinal)>? ArgumentBindings(ArgumentListSyntax list, SemanticModel model) {
        if (model.GetSymbolInfo(list.Parent!).Symbol is not IMethodSymbol method) { return null; }
        var bindings = new List<(ArgumentSyntax, int)>();

        for (var index = 0; (index < list.Arguments.Count); index++) {
            var argument = list.Arguments[index];
            var parameter = ((argument.NameColon is { } name)
                ? method.Parameters.FirstOrDefault(predicate: parameter => (parameter.Name == name.Name.Identifier.ValueText))
                : method.Parameters.ElementAtOrDefault(index: index));

            if ((parameter is null) || parameter.IsParams) { return null; }
            bindings.Add(item: (argument, parameter.Ordinal));
        }
        return bindings;
    }
    private static IEnumerable<string> ShapeSources(IReadOnlyDictionary<string, string> files, string source) {
        var directory = DirectoryOf(path: source);
        var stem = StemOf(path: source);

        return files.Keys.Where(predicate: path => ((
            path.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: directory
            ) &&
            (path.IndexOf(
                startIndex: directory.Length,
                value: '/'
            ) < 0) &&
            (path[directory.Length..].Equals(
                comparisonType: StringComparison.Ordinal,
                value: $"{stem}.cs"
            ) || path[directory.Length..].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{stem}."
            ))
        ) || IsShapeDependency(path: path, source: source))).Order(comparer: StringComparer.Ordinal);
    }
    private static bool IsShapeDependency(string source, string path) {
        var sharedWorldLeaves = (path is "src/Puck.World.Protocol/Protocol/WorldWireCodec.cs" or "src/Puck.World.Protocol/Protocol/WorldWireTags.cs" or "src/Puck.Networking/WireCodec.cs");

        if (source is "src/Puck.World.Protocol/Protocol/WorldProtocol.cs") {
            return (sharedWorldLeaves || path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/Puck.World.Protocol/Protocol/WorldSubmissionCodec")
                || (path is "src/Puck.World.Protocol/Protocol/WorldFrameCodec.cs" or "src/Puck.World.Protocol/Codecs/WorldPeerWireFormat.cs"));
        }
        if (source is "src/Puck.World.Server/WorldReplaySnapshot.cs" or "src/Puck.World.Server/WorldFederationCodec.cs"
            or "src/Puck.World.Server/WorldAuthorityCheckpointCodec.cs" or "src/Puck.World.Protocol/Codecs/WorldAuthorityStoreWireCodec.cs") {
            return (sharedWorldLeaves || path.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/Puck.World.Protocol/Protocol/WorldSubmissionCodec"));
        }
        if (source is "src/Puck.HumbleGamingBrick/MachineSnapshot.cs" or "src/Puck.AdvancedGamingBrick/AgbMachineSnapshot.cs"
            or "src/Puck.HumbleGamingDeck/HgdMachineSnapshot.cs") {
            return (path.StartsWith(DirectoryOf(path: source), StringComparison.Ordinal)
                || (path is "src/Puck.Machines/StateWriter.cs" or "src/Puck.Machines/StateReader.cs" or "src/Puck.Machines/SnapshotImage.cs"
                    or "src/Puck.Machines/SnapshotSection.cs"));
        }
        return false;
    }

    /// <summary>Finds every strict format the source declares.</summary>
    /// <param name="files">Every source file's text, by repository-relative path with forward slashes.</param>
    /// <returns>The formats in ordinal id order. A declaring type and member that two files share are told apart by
    /// appending <c>@</c> and the path to both ids.</returns>
    public static IReadOnlyList<FormatEntry> Discover(IReadOnlyDictionary<string, string> files) {
        var found = new List<(string Id, string Source, string Token)>();
        var shapes = new Shapes(files: files);

        foreach (var (path, text) in files.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            // A Post stage's magic numbers frame its test ROMs and probes, not a format the engine reads back.
            if (path.Contains(
                comparisonType: StringComparison.Ordinal,
                value: ".Post/"
            )) {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(
                options: CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview),
                text: text
            ).GetRoot();

            foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(predicate: static member => IsTokenField(member: member))) {
                if (member.Parent is not BaseTypeDeclarationSyntax owner) {
                    continue;
                }

                foreach (var (name, value) in Initializers(member: member)) {
                    if (TryTokenOf(
                        memberName: name,
                        token: out var token,
                        value: value
                    )) {
                        found.Add(item: ($"{owner.Identifier.Text}.{name}", path, token));
                    }
                }
            }
        }

        var shared = found.GroupBy(
            keySelector: static item => item.Id,
            comparer: StringComparer.Ordinal
        ).Where(predicate: static group => (group.Count() > 1)).Select(selector: static group => group.Key).ToHashSet(comparer: StringComparer.Ordinal);

        return [.. found.Select(selector: item => new FormatEntry(
            Id: (shared.Contains(item: item.Id)
                ? $"{item.Id}@{item.Source}"
                : item.Id),
            Shape: shapes.Of(source: item.Source),
            Source: item.Source,
            Token: item.Token
        )).OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Id
        )];
    }
    /// <summary>Renders the ledger in its one spelling: entries in ordinal id order, members in ordinal order, four-space
    /// indentation, one value per line, one final line feed.</summary>
    /// <param name="entries">The formats.</param>
    /// <returns>The ledger text.</returns>
    public static string Render(IReadOnlyList<FormatEntry> entries) {
        var builder = new StringBuilder();

        builder.Append(value: $"{{\n    \"format\": {Format.ToString(provider: CultureInfo.InvariantCulture)},\n    \"formats\": {{\n");

        for (var index = 0; (index < entries.Count); index++) {
            var entry = entries[index];

            builder.Append(value: $"        {Quote(text: entry.Id)}: {{\n");

            builder.Append(value: $"            \"shape\": {Quote(text: entry.Shape)},\n");

            builder.Append(value: $"            \"source\": {Quote(text: entry.Source)},\n");
            builder.Append(value: $"            \"token\": {Quote(text: entry.Token)}\n");
            builder.Append(value: ((index == (entries.Count - 1))
                ? "        }\n"
                : "        },\n"));
        }

        builder.Append(value: "    }\n}\n");

        return builder.ToString();
    }

    private static string Quote(string text) => JsonSerializer.Serialize(
        options: new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
        value: text
    );

    /// <summary>Parses a recorded ledger strictly.</summary>
    /// <param name="json">The ledger text.</param>
    /// <param name="entries">The recorded formats, or empty.</param>
    /// <param name="error">Why the text is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the text is a ledger.</returns>
    public static bool TryParse(string json, out IReadOnlyList<FormatEntry> entries, out string error) {
        var parsed = new List<FormatEntry>();

        entries = parsed;

        try {
            using var document = JsonDocument.Parse(json: json);
            var root = document.RootElement;

            if (
                (root.ValueKind != JsonValueKind.Object) ||
                !root.EnumerateObject().Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: ["format", "formats"]) ||
                !root.GetProperty(propertyName: "format").TryGetInt32(value: out var format) ||
                (format != Format) ||
                (root.GetProperty(propertyName: "formats").ValueKind != JsonValueKind.Object)
            ) {
                error = $"the ledger must be an object of exactly format ({Format}) and formats";

                return false;
            }

            foreach (var member in root.GetProperty(propertyName: "formats").EnumerateObject()) {
                if (
                    (member.Value.ValueKind != JsonValueKind.Object) ||
                    !member.Value.EnumerateObject().Select(selector: item => item.Name).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: ["shape", "source", "token"]) ||
                    member.Value.EnumerateObject().Any(predicate: static item => (item.Value.ValueKind != JsonValueKind.String))
                ) {
                    error = $"'{member.Name}' must be an object of exactly the string members shape, source and token";

                    return false;
                }

                parsed.Add(item: new FormatEntry(
                    Id: member.Name,
                    Shape: member.Value.GetProperty(propertyName: "shape").GetString()!,
                    Source: member.Value.GetProperty(propertyName: "source").GetString()!,
                    Token: member.Value.GetProperty(propertyName: "token").GetString()!
                ));
            }

            error = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException)) {
            error = exception.Message;

            return false;
        }
    }
    /// <summary>The <c>--check</c> verdict on a recorded ledger against what the source declares now.</summary>
    /// <param name="recorded">The recorded formats.</param>
    /// <param name="recordedText">The ledger file's text.</param>
    /// <param name="current">The formats <see cref="Discover"/> finds now.</param>
    /// <returns>Every problem, each naming its fix; empty when the ledger holds.</returns>
    public static IReadOnlyList<string> Check(IReadOnlyList<FormatEntry> recorded, string recordedText, IReadOnlyList<FormatEntry> current) {
        var problems = new List<string>();
        var recordedById = recorded.ToDictionary(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Id
        );
        var currentById = current.ToDictionary(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Id
        );

        foreach (var entry in current) {
            if (!recordedById.TryGetValue(
                key: entry.Id,
                value: out var was
            )) {
                problems.Add(item: $"unrecorded: '{entry.Id}' ({entry.Source}) declares {entry.Token} and the ledger does not list it");
            } else if (!string.Equals(
                a: was.Token,
                b: entry.Token,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"bumped: '{entry.Id}' ({entry.Source}) declares {entry.Token} but the ledger records {was.Token}");
            } else if (!string.Equals(
                a: was.Shape,
                b: entry.Shape,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"reshaped: the source of '{entry.Id}' ({entry.Source}) changed while its token stayed {entry.Token}; if the encoding changed, bump the token, then record the new digest");
            } else if (!string.Equals(
                a: was.Source,
                b: entry.Source,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"moved: '{entry.Id}' is declared in {entry.Source} but the ledger records {was.Source}");
            }
        }
        foreach (var entry in recorded.Where(predicate: entry => !currentById.ContainsKey(key: entry.Id))) {
            problems.Add(item: $"stale: '{entry.Id}' ({entry.Source}) is recorded but no longer declared");
        }

        if (
            (problems.Count == 0) &&
            !string.Equals(
                a: recordedText,
                b: Render(entries: current),
                comparisonType: StringComparison.Ordinal
            )
        ) {
            problems.Add(item: $"not canonical: '{FileName}' differs from the form the writer produces");
        }

        return problems;
    }
}
