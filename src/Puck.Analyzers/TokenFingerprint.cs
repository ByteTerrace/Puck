using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers;

/// <summary>The outcome of attempting to fingerprint a branded declaration.</summary>
/// <param name="Hash">The lowercase-hex SHA-256 fingerprint, present only when <see cref="Refusal"/> is <see langword="null"/>.</param>
/// <param name="Refusal">
/// Why <c>csharp-tokens-v1</c> declined to cover this declaration, phrased to complete the sentence "cannot be
/// fingerprinted because …", or <see langword="null"/> when it produced a hash.
/// </param>
/// <param name="DependencyId">
/// The declared dependency the refusal is about, or <see langword="null"/> when the refusal is about the branded
/// declaration itself. The two are separate failures — one is a shape the walk cannot cover, the other a ledger
/// entry naming something the walk cannot find.
/// </param>
internal sealed record TokenFingerprintResult(string? Hash, string? Refusal, string? DependencyId);

/// <summary>
/// Computes the <c>csharp-tokens-v1</c> fingerprint: a SHA-256 over every non-trivia token of a declaration's
/// syntax, across all of its declaring syntax references, with the branding <c>[VerifiedCode(...)]</c> attribute's
/// own tokens excluded so that updating the brand never changes the hash it is recording — followed by the same
/// walk over each declaration the manifest entry names in <c>dependencies</c>.
/// </summary>
/// <remarks>
/// <para>
/// The framing is fixed and portable: each token contributes its raw kind as a little-endian <see cref="int"/>, then
/// its UTF-8 byte count as a little-endian <see cref="int"/>, then those bytes. Writing the integers in the running
/// machine's own byte order would make a versioned algorithm name promise an interchange it does not deliver, so the
/// order is spelled out here rather than inherited from the host.
/// </para>
/// <para>
/// The dependency section follows the declaration's tokens and is framed just as explicitly. It opens with
/// <see cref="DependencySectionMarker"/> — a value no <see cref="SyntaxKind"/> can take, so the boundary between the
/// two sections is unambiguous without a length prefix on the first — then the dependency count. Each dependency
/// then contributes its documentation-comment id (UTF-8 byte count, then those bytes), its token count, then its
/// tokens. Dependencies are walked in ascending ordinal order of their documentation-comment ids, so the order they
/// happen to sit in the ledger cannot move the hash. The ids themselves are inside the seal, so removing a
/// dependency from the ledger is a fingerprint drift rather than a quiet narrowing of what the brand covers.
/// </para>
/// <para>
/// The walk descends exactly one level. Chasing what a dependency in turn depends on is unbounded — every
/// arithmetic operator eventually rests on its carrier's semantics — so the contract is the list an author wrote
/// down, and nothing beyond it. An unresolvable or unwalkable dependency is refused rather than skipped: silently
/// contributing nothing is the hole the list exists to close.
/// </para>
/// </remarks>
internal static class TokenFingerprint {
    /// <summary>
    /// Opens the dependency section. <see cref="SyntaxKind"/> is a <see cref="ushort"/>-valued enum, so no token's
    /// raw kind can ever be negative and no token stream can be mistaken for the start of this section.
    /// </summary>
    private const int DependencySectionMarker = -1;

    /// <summary>Computes the fingerprint for <paramref name="symbol"/>'s declaration and its declared dependencies.</summary>
    /// <param name="symbol">The branded method, constructor, class, or struct.</param>
    /// <param name="brand">The resolved <c>[VerifiedCode(...)]</c> application, whose own syntax is excluded from the hash it records.</param>
    /// <param name="compilation">The compilation each dependency's documentation-comment id is resolved against.</param>
    /// <param name="dependencies">The documentation-comment ids the manifest entry declares this brand's proof rests on; empty when the entry declares none, or when no entry records this brand yet.</param>
    /// <param name="cancellationToken">Cancels the walk, which visits every token of every declaring reference.</param>
    /// <returns>The fingerprint, or a refusal when a declaration is a shape <c>csharp-tokens-v1</c> cannot handle.</returns>
    public static TokenFingerprintResult Compute(ISymbol symbol, AttributeData brand, Compilation compilation, IReadOnlyList<string> dependencies, CancellationToken cancellationToken) {
        // The brand is excluded by identity — the exact syntax Roslyn bound to this AttributeData — rather than by
        // matching the name it was spelled with. A name match cannot tell the brand from an unrelated attribute of
        // the same short name, and cannot recognize the brand through a using alias.
        var brandReference = brand.ApplicationSyntaxReference;

        if (brandReference is null) {
            return Refuse(reason: "its [VerifiedCode] brand has no source syntax, so the brand's own tokens cannot be told apart from the declaration's");
        }

        // Deterministic processing order: for the partial case this analyzer refuses anyway, and defensively for any
        // future symbol kind with more than one declaring reference.
        var references = symbol.DeclaringSyntaxReferences
            .OrderBy(
            keySelector: reference => reference.SyntaxTree.FilePath,
            comparer: StringComparer.Ordinal
        )
            .ThenBy(keySelector: reference => reference.Span.Start)
            .ToArray();

        if (references.Length == 0) {
            return Refuse(reason: "it has no declaring source syntax to walk");
        }

        var brandSpan = brandReference.Span;
        var brandExcluded = false;

        using var sha256 = SHA256.Create();
        using var stream = new CryptoStream(
            mode: CryptoStreamMode.Write,
            stream: Stream.Null,
            transform: sha256
        );

        foreach (var reference in references) {
            cancellationToken.ThrowIfCancellationRequested();

            var node = reference.GetSyntax(cancellationToken: cancellationToken);

            if (IsPartial(node: node)) {
                return Refuse(reason: "it is declared partial, so it may be edited from a syntax reference this fingerprint never sees");
            }

            if (node.ContainsDirectives) {
                return Refuse(reason: "it contains a preprocessor directive, so its compiled tokens can depend on symbols this fingerprint does not read");
            }

            TextSpan? excluded = null;

            if (
                (reference.SyntaxTree == brandReference.SyntaxTree) &&
                node.Span.Contains(span: brandSpan)
            ) {
                brandExcluded = true;
                excluded = brandSpan;
            }

            AppendTokens(
                tokens: node.DescendantTokens(descendIntoTrivia: false),
                excludedSpan: excluded,
                stream: stream,
                cancellationToken: cancellationToken
            );
        }

        if (!brandExcluded) {
            return Refuse(reason: "its [VerifiedCode] brand lies outside the declaration being fingerprinted, so editing the brand would change the hash the brand records");
        }

        var ordered = dependencies
            .OrderBy(
            keySelector: dependency => dependency,
            comparer: StringComparer.Ordinal
        )
            .ToArray();

        WriteInt32(
            stream: stream,
            value: DependencySectionMarker
        );
        WriteInt32(
            stream: stream,
            value: ordered.Length
        );

        foreach (var dependency in ordered) {
            cancellationToken.ThrowIfCancellationRequested();

            var resolved = ResolveDependency(
                compilation: compilation,
                documentationId: dependency,
                cancellationToken: cancellationToken
            );

            if (resolved.Refusal is not null) {
                return new TokenFingerprintResult(
                    Hash: null,
                    Refusal: resolved.Refusal,
                    DependencyId: dependency
                );
            }

            WriteUtf8(
                stream: stream,
                text: dependency
            );
            WriteInt32(
                stream: stream,
                value: resolved.Tokens.Count
            );
            AppendTokens(
                tokens: resolved.Tokens,
                excludedSpan: null,
                stream: stream,
                cancellationToken: cancellationToken
            );
        }

        stream.FlushFinalBlock();

        var hashBytes = sha256.Hash!;
        var builder = new StringBuilder(capacity: (hashBytes.Length * 2));

        foreach (var b in hashBytes) {
            builder.Append(value: b.ToString(format: "x2"));
        }

        return new TokenFingerprintResult(
            Hash: builder.ToString(),
            Refusal: null,
            DependencyId: null
        );
    }

    private static TokenFingerprintResult Refuse(string reason) =>
        new(
        Hash: null,
        Refusal: reason,
        DependencyId: null
    );

    /// <summary>The tokens one declared dependency contributes, or why it contributes none.</summary>
    /// <param name="Tokens">The dependency's own tokens, in source order.</param>
    /// <param name="Refusal">Why the dependency cannot be sealed, phrased to complete the sentence "cannot be sealed because …".</param>
    private readonly record struct DependencyWalk(IReadOnlyList<SyntaxToken> Tokens, string? Refusal) {
        public static DependencyWalk Refused(string reason) =>
            new(
            Tokens: [],
            Refusal: reason
        );
    }

    /// <summary>
    /// Resolves one documentation-comment id to the single source declaration in <paramref name="compilation"/>'s
    /// own assembly that it names, and gathers that declaration's tokens.
    /// </summary>
    /// <remarks>
    /// Resolution is narrowed to the compilation's own assembly deliberately. A dependency in a referenced assembly
    /// has no source syntax here, so folding it would fold nothing while looking like it folded something — and the
    /// entry that named it is swept by this compilation alone.
    /// </remarks>
    private static DependencyWalk ResolveDependency(Compilation compilation, string documentationId, CancellationToken cancellationToken) {
        var candidates = DocumentationCommentId
            .GetSymbolsForDeclarationId(
            id: documentationId,
            compilation: compilation
        )
            .Where(predicate: candidate => SymbolEqualityComparer.Default.Equals(
            x: candidate.ContainingAssembly,
            y: compilation.Assembly
        ))
            .ToArray();

        if (candidates.Length == 0) {
            return DependencyWalk.Refused(reason: $"it names no declaration in '{compilation.AssemblyName}'");
        }

        if (candidates.Length > 1) {
            return DependencyWalk.Refused(reason: $"it names {candidates.Length} declarations in '{compilation.AssemblyName}', so which one the brand rests on is ambiguous");
        }

        var references = candidates[0].DeclaringSyntaxReferences
            .OrderBy(
            keySelector: reference => reference.SyntaxTree.FilePath,
            comparer: StringComparer.Ordinal
        )
            .ThenBy(keySelector: reference => reference.Span.Start)
            .ToArray();

        if (references.Length == 0) {
            return DependencyWalk.Refused(reason: "it has no declaring source syntax to walk");
        }

        if (references.Length > 1) {
            return DependencyWalk.Refused(reason: "it is declared in more than one place, so no single walk sees it whole");
        }

        return WalkDependency(node: references[0].GetSyntax(cancellationToken: cancellationToken));
    }

    /// <summary>Gathers the tokens that make up one dependency's declaration, or refuses a shape this walk cannot cover.</summary>
    private static DependencyWalk WalkDependency(SyntaxNode node) {
        // A field symbol's declaring syntax is its variable declarator alone, which carries neither the type nor
        // the modifiers that decide what the constant IS — and several declarators can share one field
        // declaration. So the type and modifiers are picked up from the shared declaration while the sibling
        // declarators are left out: `public const uint FirstMultiplier = 0x7FEB352DU` is sealed whole, and adding
        // or removing a constant beside it is a sibling edit, which no fingerprint here has ever moved.
        if (
            (node is VariableDeclaratorSyntax declarator) &&
            (declarator.Parent is VariableDeclarationSyntax variableDeclaration) &&
            (variableDeclaration.Parent is BaseFieldDeclarationSyntax fieldDeclaration)
        ) {
            if (fieldDeclaration.ContainsDirectives) {
                return DependencyWalk.Refused(reason: "its declaration contains a preprocessor directive, so its compiled tokens can depend on symbols this fingerprint does not read");
            }

            var tokens = new List<SyntaxToken>();

            foreach (var attributeList in fieldDeclaration.AttributeLists) {
                tokens.AddRange(collection: attributeList.DescendantTokens(descendIntoTrivia: false));
            }

            tokens.AddRange(collection: fieldDeclaration.Modifiers);
            tokens.AddRange(collection: variableDeclaration.Type.DescendantTokens(descendIntoTrivia: false));
            tokens.AddRange(collection: declarator.DescendantTokens(descendIntoTrivia: false));

            return new DependencyWalk(
                Tokens: tokens,
                Refusal: null
            );
        }

        if (IsPartial(node: node)) {
            return DependencyWalk.Refused(reason: "it is declared partial, so it may be edited from a syntax reference this fingerprint never sees");
        }

        if (node.ContainsDirectives) {
            return DependencyWalk.Refused(reason: "it contains a preprocessor directive, so its compiled tokens can depend on symbols this fingerprint does not read");
        }

        return new DependencyWalk(
            Tokens: node.DescendantTokens(descendIntoTrivia: false).ToArray(),
            Refusal: null
        );
    }
    private static bool IsPartial(SyntaxNode node) =>
        node switch {
            MethodDeclarationSyntax method => method.Modifiers.Any(kind: SyntaxKind.PartialKeyword),
            PropertyDeclarationSyntax property => property.Modifiers.Any(kind: SyntaxKind.PartialKeyword),
            TypeDeclarationSyntax type => type.Modifiers.Any(kind: SyntaxKind.PartialKeyword),
            // Operators, conversion operators, constructors, destructors, and accessors cannot be declared partial.
            _ => false,
        };
    private static void AppendTokens(IEnumerable<SyntaxToken> tokens, TextSpan? excludedSpan, Stream stream, CancellationToken cancellationToken) {
        foreach (var token in tokens) {
            cancellationToken.ThrowIfCancellationRequested();

            if (
                (excludedSpan is TextSpan span) &&
                span.Contains(span: token.Span)
            ) {
                continue;
            }

            WriteInt32(
                stream: stream,
                value: token.RawKind
            );
            WriteUtf8(
                stream: stream,
                text: token.Text
            );
        }
    }

    /// <summary>Writes <paramref name="text"/> as a little-endian UTF-8 byte count followed by those bytes.</summary>
    private static void WriteUtf8(Stream stream, string text) {
        var textBytes = Encoding.UTF8.GetBytes(s: text);

        WriteInt32(
            stream: stream,
            value: textBytes.Length
        );
        stream.Write(
            buffer: textBytes,
            offset: 0,
            count: textBytes.Length
        );
    }

    /// <summary>Writes <paramref name="value"/> little-endian, so the fingerprint does not depend on the host's byte order.</summary>
    private static void WriteInt32(Stream stream, int value) {
        var bytes = new byte[] {
            (byte)value,
            (byte)(value >> 8),
            (byte)(value >> 16),
            (byte)(value >> 24),
        };

        stream.Write(
            buffer: bytes,
            offset: 0,
            count: bytes.Length
        );
    }
}
