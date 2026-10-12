using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Analyzers;

/// <summary>
/// Computes the <c>csharp-tokens-v1</c> fingerprint of a source reach: a SHA-256 over every non-trivia token of each
/// named declaration, with its semantic bindings.
/// </summary>
/// <remarks>
/// The framing is fixed and portable: each token contributes its raw kind as a little-endian <see cref="int"/>, then
/// its UTF-8 byte count as a little-endian <see cref="int"/>, then those bytes. Writing the integers in the running
/// machine's own byte order would make a versioned algorithm name promise an interchange it does not deliver, so the
/// order is spelled out here rather than inherited from the host.
/// </remarks>
public static class TokenFingerprint {
    /// <summary>
    /// Opens the declaration section. <see cref="SyntaxKind"/> is a <see cref="ushort"/>-valued enum, so no token's
    /// raw kind can ever be negative and no token stream can be mistaken for the start of this section.
    /// </summary>
    private const int DependencySectionMarker = -1;

    private static void AppendTokens(IEnumerable<SyntaxToken> tokens, Stream stream, CancellationToken cancellationToken) {
        foreach (var token in tokens) {
            cancellationToken.ThrowIfCancellationRequested();

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
    /// <summary>Writes <paramref name="value"/> little-endian, so the fingerprint does not depend on the host's byte order.</summary>
    private static void WriteInt32(Stream stream, int value) {
        var bytes = new byte[] {
            ((byte)value),
            ((byte)(value >> 8)),
            ((byte)(value >> 16)),
            ((byte)(value >> 24)),
        };

        stream.Write(
            buffer: bytes,
            offset: 0,
            count: bytes.Length
        );
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

    private static bool IsTypePart(SyntaxNode? node) => node switch {
        EnumMemberDeclarationSyntax => true,
        BaseFieldDeclarationSyntax field => (!field.Modifiers.Any(kind: SyntaxKind.ConstKeyword) && field.Declaration.Variables.Any(predicate: variable => (variable.Initializer is not null))),
        PropertyDeclarationSyntax { Initializer: not null } => true,
        MemberDeclarationSyntax => false,
        _ => true,
    };

    /// <summary>Returns a declaration's own syntax. Type syntax retains ordered initializers and enum members,
    /// whose execution or implicit values depend on source order, but excludes other member bodies.</summary>
    /// <param name="declaration">A resolved source declaration.</param>
    /// <returns>The syntax roots whose tokens and bindings belong to the declaration.</returns>
    public static IEnumerable<SyntaxNode> DeclarationParts(SyntaxNode declaration) {
        if (declaration is BaseTypeDeclarationSyntax type) {
            return type.ChildNodes().Where(predicate: IsTypePart);
        }
        if (declaration is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } variable }) {
            return field.AttributeLists.Cast<SyntaxNode>().Concat(second: new SyntaxNode[] { variable.Type, declaration });
        }
        return new[] { declaration };
    }

    private static SyntaxToken[] DeclarationTokens(SyntaxNode declaration) {
        if (declaration is BaseTypeDeclarationSyntax type) {
            return type.ChildNodesAndTokens()
                .Where(predicate: child => IsTypePart(node: child.AsNode()))
                .SelectMany(selector: child => (child.IsToken ? new[] { child.AsToken() } : child.AsNode()!.DescendantTokens(descendIntoTrivia: false)))
                .ToArray();
        }
        if (declaration is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } variable }) {
            return field.AttributeLists.SelectMany(selector: list => list.DescendantTokens(descendIntoTrivia: false))
                .Concat(second: field.Modifiers)
                .Concat(second: variable.Type.DescendantTokens(descendIntoTrivia: false))
                .Concat(second: declaration.DescendantTokens(descendIntoTrivia: false)).ToArray();
        }
        return declaration.DescendantTokens(descendIntoTrivia: false).ToArray();
    }

    /// <summary>Hashes a source reach with the csharp-tokens-v1 token framing. Declaration identities and partial
    /// fragments are ordered ordinally, independent of source paths, declaration order and traversal order.</summary>
    /// <param name="declarations">Assembly-qualified declaration ids and all their source declarations.</param>
    /// <param name="bindings">Ordered semantic identities for each fragment, paired with its tokens before canonical sorting.</param>
    /// <param name="cancellationToken">Cancels token enumeration.</param>
    /// <returns>The lowercase SHA-256 fingerprint.</returns>
    public static string ComputeDeclarations(IEnumerable<KeyValuePair<string, IReadOnlyList<SyntaxNode>>> declarations, IReadOnlyDictionary<SyntaxNode, IReadOnlyList<string>> bindings, CancellationToken cancellationToken = default) {
        using var sha256 = SHA256.Create();
        using var stream = new CryptoStream(mode: CryptoStreamMode.Write, stream: Stream.Null, transform: sha256);
        var ordered = declarations.OrderBy(keySelector: pair => pair.Key, comparer: StringComparer.Ordinal).ToArray();

        WriteInt32(stream: stream, value: DependencySectionMarker);
        WriteInt32(stream: stream, value: ordered.Length);
        foreach (var declaration in ordered) {
            WriteUtf8(stream: stream, text: declaration.Key);
            var fragments = declaration.Value.Select(selector: node => {
                using var bytes = new MemoryStream();
                var tokens = DeclarationTokens(declaration: node);

                WriteInt32(stream: bytes, value: tokens.Length);
                AppendTokens(cancellationToken: cancellationToken, stream: bytes, tokens: tokens);
                var identities = bindings[node];

                WriteInt32(stream: bytes, value: identities.Count);
                foreach (var identity in identities) { WriteUtf8(stream: bytes, text: identity); }
                return bytes.ToArray();
            }).OrderBy(keySelector: bytes => Convert.ToBase64String(inArray: bytes), comparer: StringComparer.Ordinal).ToArray();

            WriteInt32(stream: stream, value: fragments.Length);
            foreach (var fragment in fragments) {
                stream.Write(buffer: fragment, offset: 0, count: fragment.Length);
            }
        }
        stream.FlushFinalBlock();
        return string.Concat(values: sha256.Hash!.Select(selector: value => value.ToString(format: "x2")));
    }
}
