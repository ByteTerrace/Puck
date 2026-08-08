using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers;

/// <summary>
/// Enforces that a <c>[VerifiedCode]</c>-branded method, constructor, class, or struct keeps matching the
/// fingerprint recorded for it in the repository-root <c>VerifiedCode.json</c>. Editing branded source without
/// updating the brand is a build error (<see cref="Ver001FingerprintMismatch"/>); deleting the brand (attribute
/// removal, member deletion, id rename, or excluding the manifest file) without deleting its manifest entry is
/// also a build error (<see cref="Ver002UnclaimedManifestEntry"/>); a declaration shape the fingerprint algorithm
/// cannot safely handle is refused outright rather than silently under-fingerprinted
/// (<see cref="Ver003UnfingerprintableDeclaration"/>); an attribute <c>Basis</c> that disagrees with its manifest
/// entry's <c>basis</c> is a build error (<see cref="Ver004BasisMismatch"/>); an unclaimed entry owned by an
/// assembly that does not declare its own namesake namespace is reported as unattributable rather than swept
/// (<see cref="Ver005AssemblyConventionViolated"/>); a manifest that cannot be read, or an entry within it that
/// cannot be trusted, is refused rather than absorbed (<see cref="Ver006ManifestUnusable"/>); a brand on a
/// declaration this analyzer cannot record is refused rather than left standing unenforced
/// (<see cref="Ver007UnsupportedBrandPlacement"/>); a declaration claiming an entry recorded for a different symbol
/// is a build error (<see cref="Ver008EntrySymbolMismatch"/>); an entry claimed by more than one declaration is
/// a build error (<see cref="Ver009DuplicateEntryClaim"/>); and an entry naming a dependency that cannot be resolved
/// or walked is refused rather than folded as nothing (<see cref="Ver010UnresolvableDependency"/>).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VerifiedCodeAnalyzer : DiagnosticAnalyzer {
    private const string AttributeMetadataName = "Puck.VerifiedCodeAttribute";
    private const string Category = "Puck.VerifiedCode";
    private const string ManifestFileName = "VerifiedCode.json";

    /// <summary>The <see cref="Diagnostic.Properties"/> key carrying a mismatch's brand id, so the code fix never parses display text.</summary>
    public const string BrandIdProperty = "VerifiedCodeId";

    /// <summary>The <see cref="Diagnostic.Properties"/> key carrying a mismatch's recomputed fingerprint.</summary>
    public const string RecomputedHashProperty = "VerifiedCodeHash";

    /// <summary>VER001: a branded symbol's <c>csharp-tokens-v1</c> fingerprint no longer matches its recorded manifest entry.</summary>
    public static readonly DiagnosticDescriptor Ver001FingerprintMismatch = new(
        id: "VER001",
        title: "Verified code no longer matches its recorded brand",
        messageFormat: "'{0}' is branded verified code (id '{1}') but its fingerprint no longer matches VerifiedCode.json; it must be re-verified and the brand's recorded hash updated to '{2}', or the brand removed",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A [VerifiedCode] declaration's tokens changed since its fingerprint was last recorded. Re-verify the change and update the manifest entry's sha256 (the code fix does this), or remove the [VerifiedCode] attribute and its manifest entry together.");

    /// <summary>VER002: a manifest entry whose id no branded symbol in this compilation ever carried.</summary>
    public static readonly DiagnosticDescriptor Ver002UnclaimedManifestEntry = new(
        id: "VER002",
        title: "Verified code manifest entry has no matching brand",
        messageFormat: "VerifiedCode.json entry '{0}' (recorded for '{1}') was never encountered by the compiler; the branded declaration was deleted, its [VerifiedCode] attribute was removed, its id was renamed, or the manifest file was excluded from the build — delete this manifest entry, or restore the brand",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every VerifiedCode.json entry must be claimed by exactly one branded declaration each build, so a brand can never silently disappear.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>VER003: a branded declaration shape <c>csharp-tokens-v1</c> refuses to fingerprint.</summary>
    public static readonly DiagnosticDescriptor Ver003UnfingerprintableDeclaration = new(
        id: "VER003",
        title: "Verified code declaration cannot be fingerprinted",
        messageFormat: "'{0}' is branded verified code (id '{1}') but the csharp-tokens-v1 fingerprint refuses to cover it because {2}; restructure the branded declaration, or remove the brand",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "csharp-tokens-v1 walks a declaration's own tokens, with the brand's own attribute syntax excluded. A shape that defeats either half — a partial declaration this walk cannot see whole, a preprocessor directive that makes the compiled tokens depend on symbols the walk does not read, or a brand whose syntax does not sit inside the declaration it brands — is refused rather than risk under-covering an edit.");

    /// <summary>VER004: a branded declaration's <c>[VerifiedCode(Basis = ...)]</c> disagrees with its manifest entry's recorded <c>basis</c>.</summary>
    public static readonly DiagnosticDescriptor Ver004BasisMismatch = new(
        id: "VER004",
        title: "Verified code brand's Basis disagrees with its manifest entry",
        messageFormat: "'{0}' is branded verified code (id '{1}') with Basis '{2}' but VerifiedCode.json records basis [{3}] for this entry; keep the attribute's Basis and the manifest's basis array in agreement",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A [VerifiedCode] attribute's optional Basis property and its manifest entry's basis array both assert why a brand is trusted, and can drift independently — one edited without the other. When the attribute carries a Basis, it and the manifest entry's basis array must name the same set (comma-separated vs. JSON array, order and whitespace ignored). Omitting Basis from the attribute entirely is not itself a problem; only a recorded disagreement is.");

    /// <summary>VER005: an unclaimed manifest entry this assembly owns, in an assembly that declares no namespace matching its own name.</summary>
    public static readonly DiagnosticDescriptor Ver005AssemblyConventionViolated = new(
        id: "VER005",
        title: "Verified code manifest entry cannot be attributed to any compilation",
        messageFormat: "VerifiedCode.json entry '{0}' (recorded for '{1}') was never encountered, and it records assembly '{2}' — but assembly '{2}' declares no '{2}' namespace, so the namespace-equals-assembly-name convention this entry's recorded symbol rests on does not hold here; restore the brand under a '{2}' namespace, correct the manifest entry, or fix the convention break",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A manifest entry names its owning compilation, and its recorded symbol must name a member of that assembly under the repository's namespace-equals-assembly-name convention. That convention is silently wrong when an assembly does not declare a namespace matching its own name — a symbol recorded under such an assembly names a member no namespace tree can back up. This diagnostic makes the broken assumption loud instead of asserting a deletion it cannot stand behind.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>VER006: <c>VerifiedCode.json</c> is missing, unreadable, off-schema, ambiguous, or carries an entry that cannot be trusted.</summary>
    public static readonly DiagnosticDescriptor Ver006ManifestUnusable = new(
        id: "VER006",
        title: "Verified code manifest cannot be trusted",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "VerifiedCode.json is the only record that a brand ever existed, so a manifest that cannot be read must fail the build on the manifest rather than degrade to an empty ledger — an empty ledger sweeps nothing, and a compilation that deleted its brands and its manifest reference together would then pass in silence. Document-level failures (unreadable text, a root that is not an object, a schema version this analyzer does not implement, a repeated member, an ambiguous file name) discard every entry; an entry-level failure discards only that entry, so the rest of the sweep stays honest.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>VER007: the brand sits somewhere this analyzer cannot fingerprint or record.</summary>
    public static readonly DiagnosticDescriptor Ver007UnsupportedBrandPlacement = new(
        id: "VER007",
        title: "Verified code brand sits where it cannot be enforced",
        messageFormat: "A [VerifiedCode] brand (id '{0}') sits on {1}, which this analyzer cannot fingerprint or record in VerifiedCode.json; move the branded body to a method, constructor, accessor, class, or struct declaration, or remove the brand",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A brand that is present and unenforced is worse than no brand at all: it asserts a proof that nothing checks. Every placement the attribute is legal on is therefore either analyzed or refused here. A local function or lambda has no documentation-comment id, so no manifest entry can name it and no sweep can notice its deletion.");

    /// <summary>VER008: a declaration claims a manifest entry recorded for a different symbol.</summary>
    public static readonly DiagnosticDescriptor Ver008EntrySymbolMismatch = new(
        id: "VER008",
        title: "Verified code brand claims an entry recorded for another symbol",
        messageFormat: "'{0}' claims VerifiedCode.json entry '{1}', but that entry was recorded for '{2}' and this declaration is '{3}'; a proof is recorded for one member, so move the brand back, record a new entry, or correct the entry's symbol",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An entry records the proof of one named member. Matching on the brand id alone lets the identical declaration move to another type, or another declaration take the id over, while the recorded fingerprint still matches — so the entry's recorded symbol is compared with the claiming declaration's documentation-comment id.");

    /// <summary>VER009: more than one declaration claims one manifest entry.</summary>
    public static readonly DiagnosticDescriptor Ver009DuplicateEntryClaim = new(
        id: "VER009",
        title: "Verified code brand id is claimed more than once",
        messageFormat: "VerifiedCode.json entry '{0}' is claimed by {1} declarations ({2}); one entry records the proof of exactly one member, so give each declaration its own brand id and its own entry",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Counting claims is what makes an entry the record of one proof rather than of a shape anything may match: an entry satisfied by mere set membership would be satisfied by any number of declarations, so a second one could take an id over while the recorded fingerprint still matched.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>VER010: a manifest entry names a dependency this compilation cannot resolve to one walkable declaration.</summary>
    public static readonly DiagnosticDescriptor Ver010UnresolvableDependency = new(
        id: "VER010",
        title: "Verified code brand declares a dependency that cannot be sealed",
        messageFormat: "'{0}' is branded verified code (id '{1}') whose VerifiedCode.json entry declares a dependency on '{2}', but that dependency cannot be sealed because {3}; correct the entry's dependencies, or restructure the declaration it names",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A brand's seal covers its own declaration plus the declarations its entry names in 'dependencies' — one level, listed by hand, because the transitive closure of what a body's behaviour rests on is unbounded. A dependency that resolves to nothing, to more than one declaration, or to a shape the walk cannot cover would contribute nothing to the hash while reading as though it did, which is exactly the gap the list exists to close.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            Ver001FingerprintMismatch,
            Ver002UnclaimedManifestEntry,
            Ver003UnfingerprintableDeclaration,
            Ver004BasisMismatch,
            Ver005AssemblyConventionViolated,
            Ver006ManifestUnusable,
            Ver007UnsupportedBrandPlacement,
            Ver008EntrySymbolMismatch,
            Ver009DuplicateEntryClaim,
            Ver010UnresolvableDependency);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        // A brand in a generated tree is still a brand: leaving generated code unanalyzed would let the marker
        // stand with nothing behind it, which is the one state this feature must never allow.
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(action: AnalyzeCompilationStart);
    }

    private static void AnalyzeCompilationStart(CompilationStartAnalysisContext context) {
        // The manifest sweep runs whether or not this compilation can see the marker attribute. A compilation that
        // cannot resolve Puck.VerifiedCodeAttribute carries no brands the compiler will ever hand us, but the
        // entries it owns still record proofs, and their deletion still has to be reported by someone.
        var attributeType = context.Compilation.GetTypeByMetadataName(fullyQualifiedMetadataName: AttributeMetadataName);
        var manifest = LoadManifest(options: context.Options, cancellationToken: context.CancellationToken);
        var claims = new ConcurrentDictionary<string, ConcurrentQueue<string>>(comparer: StringComparer.Ordinal);

        context.RegisterSymbolAction(
            action: symbolContext => AnalyzeSymbol(symbolContext: symbolContext, attributeType: attributeType, manifest: manifest, claims: claims),
            symbolKinds: ImmutableArray.Create(item1: SymbolKind.Method, item2: SymbolKind.NamedType));

        context.RegisterSyntaxNodeAction(
            action: nodeContext => AnalyzeUnrecordableDeclaration(nodeContext: nodeContext, attributeType: attributeType),
            // A simple (unparenthesized) lambda cannot carry an attribute at all — CS8916 — so only the
            // parenthesized form needs watching.
            syntaxKinds: ImmutableArray.Create(item1: SyntaxKind.LocalFunctionStatement, item2: SyntaxKind.ParenthesizedLambdaExpression));

        context.RegisterCompilationEndAction(action: endContext => AnalyzeCompilationEnd(endContext: endContext, manifest: manifest, claims: claims));
    }

    /// <summary>Everything one compilation knows about <c>VerifiedCode.json</c>, including where to point when it is wrong.</summary>
    /// <param name="Location">The manifest file itself, or <see cref="Microsoft.CodeAnalysis.Location.None"/> when there is no file to point at.</param>
    /// <param name="Reading">The entries that read cleanly and every fault found.</param>
    private sealed record ManifestState(Location Location, VerifiedCodeManifestReading Reading);

    private static ManifestState LoadManifest(AnalyzerOptions options, CancellationToken cancellationToken) {
        var candidates = options.AdditionalFiles
            .Where(predicate: file => string.Equals(a: Path.GetFileName(path: file.Path), b: ManifestFileName, comparisonType: StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 0) {
            return new ManifestState(
                Location: Location.None,
                Reading: VerifiedCodeManifest.Unusable(message: $"No {ManifestFileName} was supplied to this compilation as an AdditionalFile, so no brand can be checked and no deleted brand can be reported; restore the manifest to the build."));
        }

        if (candidates.Length > 1) {
            var paths = string.Join(separator: ", ", values: candidates.Select(selector: file => file.Path).OrderBy(keySelector: path => path, comparer: StringComparer.Ordinal));

            return new ManifestState(
                Location: FileLocation(file: candidates[0], cancellationToken: cancellationToken),
                Reading: VerifiedCodeManifest.Unusable(message: $"{candidates.Length} files named {ManifestFileName} were supplied to this compilation ({paths}); which one is the ledger is ambiguous, so none of them is read."));
        }

        var additionalFile = candidates[0];
        var text = additionalFile.GetText(cancellationToken: cancellationToken);

        if (text is null) {
            return new ManifestState(
                Location: FileLocation(file: additionalFile, cancellationToken: cancellationToken),
                Reading: VerifiedCodeManifest.Unusable(message: $"{ManifestFileName} was supplied to this compilation but its text could not be read, so no brand can be checked and no deleted brand can be reported."));
        }

        return new ManifestState(
            Location: FileLocation(file: additionalFile, cancellationToken: cancellationToken),
            Reading: VerifiedCodeManifest.Read(json: text.ToString()));
    }

    /// <summary>A location on the manifest file itself, so a manifest failure is reported where the manifest is, not on some brand site.</summary>
    private static Location FileLocation(AdditionalText file, CancellationToken cancellationToken) {
        var text = file.GetText(cancellationToken: cancellationToken);
        var span = new TextSpan(start: 0, length: 0);

        var lineSpan = ((text is null)
            ? new Microsoft.CodeAnalysis.Text.LinePositionSpan()
            : text.Lines.GetLinePositionSpan(span: span));

        return Location.Create(filePath: file.Path, textSpan: span, lineSpan: lineSpan);
    }
    private static void AnalyzeSymbol(SymbolAnalysisContext symbolContext, INamedTypeSymbol? attributeType, ManifestState manifest, ConcurrentDictionary<string, ConcurrentQueue<string>> claims) {
        if (attributeType is null) {
            return;
        }

        var symbol = symbolContext.Symbol;

        var attributeData = symbol.GetAttributes()
            .FirstOrDefault(predicate: candidate => SymbolEqualityComparer.Default.Equals(x: candidate.AttributeClass, y: attributeType));

        if (attributeData is null) {
            return;
        }

        if ((attributeData.ConstructorArguments.Length == 0) || (attributeData.ConstructorArguments[0].Value is not string id) || string.IsNullOrEmpty(value: id)) {
            // No usable id: the compiler already reports its own error for a missing required constructor
            // argument, and there is nothing to fingerprint or track without one.
            return;
        }

        var symbolDisplay = symbol.ToDisplayString();
        var location = (symbol.Locations.FirstOrDefault() ?? Location.None);
        var documentationId = symbol.GetDocumentationCommentId();

        if (string.IsNullOrEmpty(value: documentationId)) {
            symbolContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver007UnsupportedBrandPlacement, location: location, id, $"'{symbolDisplay}', a declaration with no documentation-comment id"));

            return;
        }

        claims
            .GetOrAdd(key: id, valueFactory: _ => new ConcurrentQueue<string>())
            .Enqueue(item: symbolDisplay);

        // The entry is read before the fingerprint because the entry says what the fingerprint covers: its
        // 'dependencies' are folded into the hash. A brand with no believable entry is fingerprinted over its own
        // declaration alone, which is exactly what it currently declares itself to rest on.
        var believable = (manifest.Reading.Usable && !manifest.Reading.FaultedIds.Contains(item: id));
        var entry = ((believable && manifest.Reading.Entries.TryGetValue(key: id, value: out var recorded)) ? recorded : null);

        var fingerprint = TokenFingerprint.Compute(
            symbol: symbol,
            brand: attributeData,
            compilation: symbolContext.Compilation,
            dependencies: (entry?.Dependencies ?? []),
            cancellationToken: symbolContext.CancellationToken);

        if (fingerprint.Refusal is not null) {
            var descriptor = ((fingerprint.DependencyId is null) ? Ver003UnfingerprintableDeclaration : Ver010UnresolvableDependency);

            object[] messageArgs = ((fingerprint.DependencyId is null)
                ? [symbolDisplay, id, fingerprint.Refusal]
                : [symbolDisplay, id, fingerprint.DependencyId, fingerprint.Refusal]);

            symbolContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: descriptor, location: location, messageArgs: messageArgs));

            return;
        }

        if (!manifest.Reading.Usable) {
            // The ledger itself is the failure, and it is already reported on the manifest. Blaming every brand for
            // a fingerprint drift that never happened would name the wrong place and teach people to re-record.
            return;
        }

        if (manifest.Reading.FaultedIds.Contains(item: id)) {
            // This entry's own fault already says why it cannot be believed.
            return;
        }

        if (entry is null) {
            symbolContext.ReportDiagnostic(diagnostic: CreateMismatch(location: location, symbolDisplay: symbolDisplay, id: id, hash: fingerprint.Hash!));

            return;
        }

        if (!string.Equals(a: entry.Symbol, b: documentationId, comparisonType: StringComparison.Ordinal)) {
            symbolContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver008EntrySymbolMismatch, location: location, symbolDisplay, id, entry.Symbol, documentationId));
        }

        if (!string.Equals(a: entry.Sha256, b: fingerprint.Hash, comparisonType: StringComparison.Ordinal)) {
            symbolContext.ReportDiagnostic(diagnostic: CreateMismatch(location: location, symbolDisplay: symbolDisplay, id: id, hash: fingerprint.Hash!));
        }

        CheckBasisAgreement(symbolContext: symbolContext, attributeData: attributeData, entry: entry, symbolDisplay: symbolDisplay, id: id, location: location);
    }

    /// <summary>
    /// Builds VER001 with the brand id and recomputed hash carried as diagnostic properties. The code fix reads
    /// those; the message is for people, and an id containing an apostrophe must not be able to break the repair.
    /// </summary>
    private static Diagnostic CreateMismatch(Location location, string symbolDisplay, string id, string hash) =>
        Diagnostic.Create(
            descriptor: Ver001FingerprintMismatch,
            location: location,
            properties: ImmutableDictionary<string, string?>.Empty
                .Add(key: BrandIdProperty, value: id)
                .Add(key: RecomputedHashProperty, value: hash),
            messageArgs: [symbolDisplay, id, hash]);

    /// <summary>Refuses a brand on a local function or lambda: legal C#, but nothing the manifest can name or the sweep can miss.</summary>
    private static void AnalyzeUnrecordableDeclaration(SyntaxNodeAnalysisContext nodeContext, INamedTypeSymbol? attributeType) {
        if (attributeType is null) {
            return;
        }

        var (attributeLists, description) = nodeContext.Node switch {
            LocalFunctionStatementSyntax localFunction => (localFunction.AttributeLists, "a local function"),
            LambdaExpressionSyntax lambda => (lambda.AttributeLists, "a lambda"),
            _ => (default(SyntaxList<AttributeListSyntax>), string.Empty),
        };

        if (attributeLists.Count == 0) {
            return;
        }

        foreach (var attributeList in attributeLists) {
            foreach (var attribute in attributeList.Attributes) {
                var attributeSymbol = nodeContext.SemanticModel.GetTypeInfo(attributeSyntax: attribute, cancellationToken: nodeContext.CancellationToken).Type;

                if (!SymbolEqualityComparer.Default.Equals(x: attributeSymbol, y: attributeType)) {
                    continue;
                }

                nodeContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver007UnsupportedBrandPlacement, location: attribute.GetLocation(), BrandId(nodeContext: nodeContext, attribute: attribute), description));

                return;
            }
        }
    }

    /// <summary>The id a brand names, read off its first argument; the display text falls back to the argument's source when it is not a constant.</summary>
    private static string BrandId(SyntaxNodeAnalysisContext nodeContext, AttributeSyntax attribute) {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();

        if (argument is null) {
            return string.Empty;
        }

        var constant = nodeContext.SemanticModel.GetConstantValue(node: argument.Expression, cancellationToken: nodeContext.CancellationToken);

        return ((constant.HasValue ? (constant.Value as string) : null) ?? argument.Expression.ToString());
    }
    private static void CheckBasisAgreement(SymbolAnalysisContext symbolContext, AttributeData attributeData, VerifiedCodeManifestEntry entry, string symbolDisplay, string id, Location location) {
        // The attribute's Basis is an init-only property, so a source use that sets it (`Basis = "..."`) surfaces
        // here as a named argument rather than a constructor argument. An attribute that never mentions Basis at
        // all is deliberately left alone — the gap this closes is a recorded DISAGREEMENT, not an unstated basis.
        var basisArgument = attributeData.NamedArguments
            .FirstOrDefault(predicate: pair => string.Equals(a: pair.Key, b: "Basis", comparisonType: StringComparison.Ordinal));

        if ((basisArgument.Key is null) || (basisArgument.Value.Value is not string attributeBasis)) {
            return;
        }

        if (BasisSetsAgree(attributeBasis: attributeBasis, manifestBasis: entry.Basis)) {
            return;
        }

        var manifestBasisText = string.Join(separator: ", ", values: entry.Basis);

        symbolContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver004BasisMismatch, location: location, symbolDisplay, id, attributeBasis, manifestBasisText));
    }

    /// <summary>
    /// Compares the attribute's comma-separated <c>Basis</c> against the manifest's JSON <c>basis</c> array as
    /// sets: order must not matter (an author may reorder either side), and whitespace around each comma-separated
    /// term must not matter (the attribute's string is hand-typed prose, not machine-formatted).
    /// </summary>
    private static bool BasisSetsAgree(string attributeBasis, IReadOnlyList<string> manifestBasis) {
        // netstandard2.0 (this project's hard target — see Puck.Analyzers.csproj) has no Enumerable.ToHashSet;
        // the HashSet<T>(IEnumerable<T>, IEqualityComparer<T>) constructor has been available since .NET Framework.
        var attributeTerms = new HashSet<string>(
            collection: attributeBasis.Split(separator: ',').Select(selector: term => term.Trim()).Where(predicate: term => (term.Length > 0)),
            comparer: StringComparer.Ordinal);

        var manifestTerms = new HashSet<string>(
            collection: manifestBasis.Select(selector: term => term.Trim()).Where(predicate: term => (term.Length > 0)),
            comparer: StringComparer.Ordinal);

        return attributeTerms.SetEquals(other: manifestTerms);
    }
    private static void AnalyzeCompilationEnd(CompilationAnalysisContext endContext, ManifestState manifest, ConcurrentDictionary<string, ConcurrentQueue<string>> claims) {
        // Manifest faults are properties of the file, not of any one compilation's brands, so every compilation
        // that reads the file reports them. That is what makes the owner check repo-wide: an entry whose recorded
        // symbol and recorded assembly disagree is caught wherever the manifest is read, rather than waiting for a
        // compilation whose name happens to match a prefix — which, for an assembly that does not exist, never comes.
        foreach (var fault in manifest.Reading.Faults) {
            endContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver006ManifestUnusable, location: manifest.Location, fault.Message));
        }

        if (!manifest.Reading.Usable) {
            return;
        }

        foreach (var claim in claims.OrderBy(keySelector: pair => pair.Key, comparer: StringComparer.Ordinal)) {
            var claimants = claim.Value.OrderBy(keySelector: name => name, comparer: StringComparer.Ordinal).ToArray();

            if (claimants.Length < 2) {
                continue;
            }

            endContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver009DuplicateEntryClaim, location: manifest.Location, claim.Key, claimants.Length, string.Join(", ", claimants)));
        }

        // VerifiedCode.json is linked into every project (like CodeMetricsConfig.txt), but a symbol is only ever
        // encountered in the one project that declares it — RegisterSymbolAction fires for the current compilation
        // alone. Each entry therefore records the assembly responsible for sweeping it, and a compilation sweeps
        // exactly the entries recorded against its own name.
        var assemblyName = (endContext.Compilation.AssemblyName ?? string.Empty);
        var assemblyDeclaresOwnNamespace = ((assemblyName.Length == 0) || AssemblyDeclaresNamespace(assembly: endContext.Compilation.Assembly, dottedName: assemblyName));

        foreach (var entry in manifest.Reading.Entries.Values) {
            if (claims.ContainsKey(key: entry.Id)) {
                continue;
            }

            if (!string.Equals(a: entry.Assembly, b: assemblyName, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (!assemblyDeclaresOwnNamespace) {
                endContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver005AssemblyConventionViolated, location: manifest.Location, entry.Id, entry.Symbol, assemblyName));

                continue;
            }

            endContext.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Ver002UnclaimedManifestEntry, location: manifest.Location, entry.Id, entry.Symbol));
        }
    }

    /// <summary>
    /// Walks <paramref name="dottedName"/> (an assembly name, dot-segmented like a namespace path) down from
    /// <paramref name="assembly"/>'s own global namespace, scoped to namespaces <paramref name="assembly"/> itself
    /// declares (not ones merged in only from its references) — the same sense in which "namespace equals assembly
    /// name" is meant to hold.
    /// </summary>
    private static bool AssemblyDeclaresNamespace(IAssemblySymbol assembly, string dottedName) {
        var current = assembly.GlobalNamespace;

        foreach (var segment in dottedName.Split('.')) {
            var next = current.GetNamespaceMembers()
                .FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: segment, comparisonType: StringComparison.Ordinal));

            if (next is null) {
                return false;
            }

            current = next;
        }

        return true;
    }
}
