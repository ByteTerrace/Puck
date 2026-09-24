using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>What a <see cref="Grantee"/> names.</summary>
public enum GranteeKind : byte {
    /// <summary>An actor — <see cref="Grantee.Principal"/> holds it.</summary>
    Principal = 0,

    /// <summary>A group — <see cref="Grantee.Name"/> is the group's stable id (<see cref="Puck.World.WorldGroup.Id"/>).
    /// A grant naming a group is held by every current member at check time, never baked into a member's own rows,
    /// so a member who leaves loses the hold on its very next check.</summary>
    Group = 1,

    /// <summary>Another world document — <see cref="Grantee.Name"/> is its id. Such a row is read off the owning
    /// document by the cross-document write-back channel, never off the runtime grant table.</summary>
    Document = 2,
}
/// <summary>
/// What a grant row names as its holder: an actor (<see cref="Principal"/>), a group, or another document. A group and
/// a document never act — no ingress can stamp either, because an acting identity is a <see cref="Principal"/> and
/// this type is never one.
/// </summary>
/// <remarks>Every principal is a grantee (the implicit conversion), so a grant for an actor is written with the actor
/// itself. The token grammar extends <see cref="PrincipalTokens.Grammar"/> with <c>group:&lt;id&gt;</c> and
/// <c>document:&lt;id&gt;</c>.</remarks>
/// <param name="Kind">What the grantee names.</param>
/// <param name="Principal">The actor for <see cref="GranteeKind.Principal"/>; <see langword="default"/> otherwise.</param>
/// <param name="Name">The group or document id for <see cref="GranteeKind.Group"/>/<see cref="GranteeKind.Document"/>;
/// <see langword="null"/> otherwise.</param>
public readonly record struct Grantee(GranteeKind Kind, Principal Principal, string? Name) {
    /// <summary>The grantee token grammar <see cref="TryParse"/> accepts, for a console verb's unknown-token refusal to
    /// interpolate rather than hand-spell.</summary>
    public const string TokenGrammar = (PrincipalTokens.Grammar + "|group:<id>|document:<id>");

    /// <summary>Returns the grantee naming an actor.</summary>
    /// <param name="principal">The actor.</param>
    public static implicit operator Grantee(Principal principal) => new(
        Kind: GranteeKind.Principal,
        Name: null,
        Principal: principal
    );

    /// <summary>Describes a short stable label — the principal's own label, <c>group:&lt;id&gt;</c>, or
    /// <c>document:&lt;id&gt;</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        GranteeKind.Principal => Principal.Describe(),
        GranteeKind.Group => $"group:{Name}",
        GranteeKind.Document => $"document:{Name}",
        _ => "?",
    };
    /// <summary>Returns the grantee naming another world document.</summary>
    /// <param name="id">The document's id.</param>
    /// <returns>The document grantee.</returns>
    public static Grantee Document(string id) => new(
        Kind: GranteeKind.Document,
        Name: id,
        Principal: default
    );
    /// <summary>Returns the grantee naming a group.</summary>
    /// <param name="id">The group's stable id.</param>
    /// <returns>The group grantee.</returns>
    public static Grantee Group(string id) => new(
        Kind: GranteeKind.Group,
        Name: id,
        Principal: default
    );
    /// <summary>Determines whether this grantee is canonical — one <see cref="TryParse"/> produces from its own
    /// <see cref="Describe"/> label.</summary>
    /// <returns><see langword="true"/> when this grantee's own label parses back to this exact value.</returns>
    public bool IsCanonical() => (TryParse(
        grantee: out var parsed,
        token: Describe()
    ) && (parsed == this));
    /// <summary>Parses a grantee token (<see cref="TokenGrammar"/>), ignoring case.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="grantee">The parsed grantee, on success.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> token, out Grantee grantee) {
        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "document:"
        ) &&
            (token.Length > 9)
        ) {
            grantee = Document(id: token[9..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "group:"
        ) &&
            (token.Length > 6)
        ) {
            grantee = Group(id: token[6..].ToString());

            return true;
        }

        var parsed = PrincipalTokens.TryParse(
            principal: out var principal,
            token: token
        );

        grantee = principal;

        return parsed;
    }
    /// <summary>Parses a grantee token and requires it to be the exact spelling <see cref="Describe"/> produces.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="grantee">The parsed grantee, on success.</param>
    /// <returns><see langword="true"/> when the token parsed and is that grantee's own label.</returns>
    public static bool TryParseCanonical(ReadOnlySpan<char> token, out Grantee grantee) => (TryParse(
        grantee: out grantee,
        token: token
    ) && token.SequenceEqual(other: grantee.Describe()));
    /// <summary>Returns the actor this grantee names, when it names one.</summary>
    /// <param name="principal">The actor, on success; <see langword="default"/> otherwise.</param>
    /// <returns><see langword="true"/> when <see cref="Kind"/> is <see cref="GranteeKind.Principal"/>.</returns>
    public bool TryGetPrincipal(out Principal principal) {
        principal = Principal;

        return (Kind == GranteeKind.Principal);
    }
}
