using System.Globalization;
using System.Text.Json.Serialization;

using Puck.Abstractions.Documents;

using Puck.World.Protocol;

namespace Puck.World;

/// <summary>An issuer-qualified participant identity used when a membership is verified outside the local
/// authority. The issuer and subject are length-prefixed in <see cref="Describe"/>, so a separator or delimiter in
/// either claim cannot forge another identity. <see cref="World"/> optionally scopes the claim to one world.</summary>
/// <param name="Issuer">The identity issuer. It is non-empty and free of control characters.</param>
/// <param name="Subject">The issuer's subject claim. It is non-empty and free of control characters.</param>
/// <param name="World">The optional validated world name for which the claim was attested.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldMemberIdentity(string Issuer, string Subject, SafeName? World) {
    /// <summary>The token prefix for a verified participant identity.</summary>
    public const string TokenPrefix = "member:";

    /// <summary>Gets a canonical, injective token for this identity.</summary>
    public string Describe() {
        if (!TryValidateComponent(value: Issuer) ||
            !TryValidateComponent(value: Subject) ||
            (World is { } scopedWorld && !SafeName.TryParse(candidate: scopedWorld.Value, name: out _, reason: out _))) {
            return "?";
        }

        return string.Concat(TokenPrefix, Segment(value: Issuer), Segment(value: Subject), World is { } world ? Segment(value: world.Value) : string.Empty);
    }

    /// <summary>Determines whether this value is the exact value represented by its own canonical token.</summary>
    public bool IsCanonical() => TryParse(token: Describe(), identity: out var parsed) && (parsed == this);

    /// <summary>Parses a token and requires its exact canonical spelling.</summary>
    public static bool TryParseCanonical(ReadOnlySpan<char> token, out WorldMemberIdentity identity) => TryParse(token: token, identity: out identity) && token.SequenceEqual(other: identity.Describe());

    /// <summary>Parses the length-prefixed <c>member:</c> token. The parser accepts a case-insensitive prefix and
    /// non-canonical decimal length spelling; callers crossing an identity boundary should use
    /// <see cref="TryParseCanonical"/>.</summary>
    public static bool TryParse(ReadOnlySpan<char> token, out WorldMemberIdentity identity) {
        identity = default;

        if (!token.StartsWith(value: TokenPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var offset = TokenPrefix.Length;

        if (!TryReadSegment(token: token, offset: ref offset, value: out var issuer) ||
            !TryReadSegment(token: token, offset: ref offset, value: out var subject) ||
            !TryValidateComponent(value: issuer) ||
            !TryValidateComponent(value: subject)) {
            return false;
        }

        SafeName? world = null;

        if (offset < token.Length) {
            if (!TryReadSegment(token: token, offset: ref offset, value: out var worldText) ||
                (offset != token.Length) ||
                !SafeName.TryParse(candidate: worldText, name: out var worldName, reason: out _)) {
                return false;
            }

            world = worldName;
        }

        identity = new WorldMemberIdentity(Issuer: issuer, Subject: subject, World: world);
        return true;
    }

    private static string Segment(string value) => string.Concat(value.Length.ToString(provider: CultureInfo.InvariantCulture), "~", value);

    private static bool TryReadSegment(ReadOnlySpan<char> token, ref int offset, out string value) {
        value = string.Empty;

        if ((offset >= token.Length) || !char.IsAsciiDigit(c: token[offset])) {
            return false;
        }

        var separator = token[offset..].IndexOf(value: '~');
        if (separator <= 0) {
            return false;
        }

        separator += offset;

        if (!int.TryParse(s: token[offset..separator], style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var length) || (length < 1)) {
            return false;
        }

        var start = separator + 1;
        if ((start > token.Length) || (length > (token.Length - start))) {
            return false;
        }

        value = token.Slice(start: start, length: length).ToString();
        offset = start + length;
        return true;
    }

    private static bool TryValidateComponent(string? value) {
        if (string.IsNullOrEmpty(value: value)) {
            return false;
        }

        foreach (var character in value) {
            if (char.IsControl(c: character)) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Distinguishes a locally addressed principal from an issuer-qualified verified identity.</summary>
[JsonConverter(typeof(StrictEnumConverter<MemberRefKind>))]
public enum MemberRefKind : byte {
    /// <summary>The member is addressed by a local canonical <see cref="WorldPrincipal"/>.</summary>
    Local,
    /// <summary>The member is addressed by a verified issuer-qualified identity.</summary>
    Verified,
}

/// <summary>The allocation-free local-or-verified membership key. Exactly one payload is populated according to
/// <see cref="Kind"/>; a default value is intentionally not a member.</summary>
/// <param name="Kind">The populated payload arm.</param>
/// <param name="Principal">The local principal, only for <see cref="MemberRefKind.Local"/>.</param>
/// <param name="Verified">The verified identity, only for <see cref="MemberRefKind.Verified"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldMemberRef(MemberRefKind Kind, WorldPrincipal? Principal = null, WorldMemberIdentity? Verified = null) {
    /// <summary>Creates a local membership reference.</summary>
    public static WorldMemberRef Local(WorldPrincipal principal) => new(Kind: MemberRefKind.Local, Principal: principal);

    /// <summary>Creates a verified membership reference.</summary>
    public static WorldMemberRef VerifiedIdentity(WorldMemberIdentity identity) => new(Kind: MemberRefKind.Verified, Verified: identity);

    /// <summary>Determines whether exactly one canonical payload matches the tagged arm.</summary>
    public bool IsCanonical() => Kind switch {
        MemberRefKind.Local => Principal is { } principal && (Verified is null) && principal.IsCanonical(),
        MemberRefKind.Verified => (Principal is null) && Verified is { } identity && identity.IsCanonical(),
        _ => false,
    };

    /// <summary>Gets the canonical local-or-verified token, or <c>?</c> for a malformed value.</summary>
    public string Describe() => IsCanonical()
        ? Kind switch {
            MemberRefKind.Local => $"local:{Principal!.Value.Describe()}",
            MemberRefKind.Verified => $"verified:{Verified!.Value.Describe()}",
            _ => "?",
        }
        : "?";

    /// <summary>Parses a canonical local-or-verified token.</summary>
    public static bool TryParseCanonical(ReadOnlySpan<char> token, out WorldMemberRef member) {
        member = default;

        if (token.StartsWith(value: "local:", comparisonType: StringComparison.Ordinal) &&
            WorldPrincipal.TryParseCanonical(token: token[6..], principal: out var principal)) {
            member = Local(principal: principal);
            return true;
        }

        if (token.StartsWith(value: "verified:", comparisonType: StringComparison.Ordinal) &&
            WorldMemberIdentity.TryParseCanonical(token: token[9..], identity: out var identity)) {
            member = VerifiedIdentity(identity: identity);
            return true;
        }

        return false;
    }
}

/// <summary>One member of a group's roster, including its declared role, stable succession ordinal, and optional
/// tags used by membership selectors.</summary>
/// <param name="Ref">The local or verified member identity.</param>
/// <param name="Role">The declared role name, or <see langword="null"/> for no role-scoped capability; validators must not infer a role.</param>
/// <param name="JoinOrdinal">The monotonic succession ordinal assigned when this member joined the group.</param>
/// <param name="Tags">Optional non-empty tags attached to this member.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGroupMember(WorldMemberRef Ref, string? Role, int JoinOrdinal, IReadOnlyList<string>? Tags = null);
