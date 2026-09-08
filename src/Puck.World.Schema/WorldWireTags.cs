namespace Puck.World.Protocol;

/// <summary>The one pinned byte&lt;-&gt;enum mapping for the grant vocabulary every wire-facing codec shares:
/// <see cref="WorldCapability"/>, <see cref="PrincipalKind"/>, <see cref="GrantSubjectKind"/>, and
/// <see cref="WorldSection"/>. <c>Puck.World.Protocol.WorldSubmissionCodec</c> — the submission wire, loopback, and
/// replay-tape leaf reuse all route through it — is the canonical source these tables were extracted from; the
/// checkpoint codec and the replay tape's own principal encoding used to re-derive their own byte mapping by casting
/// each enum's C# declaration ordinal directly, which silently disagreed with the values below (a raw cast of
/// <see cref="WorldCapability.Control"/> is 2 — the wire value this type retires and refuses — and a raw cast of
/// <see cref="PrincipalKind.Group"/> is 6, not the pinned 4) and, for the replay tape, could not represent
/// <see cref="PrincipalKind.Group"/> at all. A retired wire value is never reassigned — see each map's own remarks —
/// so every codec that touches this vocabulary calls here rather than re-deriving a cast that reorders on a future
/// member insertion.</summary>
public static class WorldWireTags {
    /// <summary>Maps a <see cref="WorldCapability"/> to its pinned wire byte. Wire value 2 is retired (the removed
    /// <c>Present</c> capability) and is never reassigned.</summary>
    /// <param name="value">The capability.</param>
    /// <param name="wire">The pinned wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a wire value.</returns>
    public static bool TryToWire(WorldCapability value, out byte wire) {
        switch (value) {
            case WorldCapability.Drive: wire = 0; return true;
            case WorldCapability.Observe: wire = 1; return true;
            case WorldCapability.Control: wire = 3; return true;
            case WorldCapability.Mutate: wire = 4; return true;
            case WorldCapability.Edit: wire = 5; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a pinned wire byte back to its <see cref="WorldCapability"/>. Fails for the retired value 2 the
    /// same as for any other undeclared byte — callers that want a distinct "retired" message check
    /// <see cref="IsRetiredCapabilityWire"/> first.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The capability, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a live capability.</returns>
    public static bool TryFromWire(byte wire, out WorldCapability value) {
        switch (wire) {
            case 0: value = WorldCapability.Drive; return true;
            case 1: value = WorldCapability.Observe; return true;
            case 3: value = WorldCapability.Control; return true;
            case 4: value = WorldCapability.Mutate; return true;
            case 5: value = WorldCapability.Edit; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Gets a value indicating whether <paramref name="wire"/> is the retired <see cref="WorldCapability"/>
    /// wire value (2) — distinct from an undeclared byte for callers that report the two differently.</summary>
    /// <param name="wire">The wire byte.</param>
    public static bool IsRetiredCapabilityWire(byte wire) => (wire == 2);
    /// <summary>Maps a <see cref="PrincipalKind"/> to its pinned wire byte. <see cref="PrincipalKind.Document"/> and
    /// <see cref="PrincipalKind.World"/> have no live wire value — a document principal never acts, and the world's
    /// own program is stamped structurally, never carried on a submission — so both fail here.</summary>
    /// <param name="value">The principal kind.</param>
    /// <param name="wire">The pinned wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a live wire value.</returns>
    public static bool TryToWire(PrincipalKind value, out byte wire) {
        switch (value) {
            case PrincipalKind.Seat: wire = 0; return true;
            case PrincipalKind.Console: wire = 1; return true;
            case PrincipalKind.Addon: wire = 2; return true;
            case PrincipalKind.Peer: wire = 3; return true;
            case PrincipalKind.Group: wire = 4; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a pinned wire byte back to its <see cref="PrincipalKind"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The principal kind, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a live principal kind.</returns>
    public static bool TryFromWire(byte wire, out PrincipalKind value) {
        switch (wire) {
            case 0: value = PrincipalKind.Seat; return true;
            case 1: value = PrincipalKind.Console; return true;
            case 2: value = PrincipalKind.Addon; return true;
            case 3: value = PrincipalKind.Peer; return true;
            case 4: value = PrincipalKind.Group; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a <see cref="GrantSubjectKind"/> to its pinned wire byte. Wire value 8 is retired (the removed
    /// <c>Table</c> subject) and is never reassigned.</summary>
    /// <param name="value">The subject kind.</param>
    /// <param name="wire">The pinned wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a wire value.</returns>
    public static bool TryToWire(GrantSubjectKind value, out byte wire) {
        switch (value) {
            case GrantSubjectKind.All: wire = 0; return true;
            case GrantSubjectKind.Body: wire = 1; return true;
            case GrantSubjectKind.Screen: wire = 2; return true;
            case GrantSubjectKind.Section: wire = 3; return true;
            case GrantSubjectKind.Composition: wire = 4; return true;
            case GrantSubjectKind.State: wire = 5; return true;
            case GrantSubjectKind.Region: wire = 6; return true;
            case GrantSubjectKind.Seat: wire = 7; return true;
            case GrantSubjectKind.Creation: wire = 9; return true;
            case GrantSubjectKind.Placement: wire = 10; return true;
            case GrantSubjectKind.Adjacency: wire = 11; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a pinned wire byte back to its <see cref="GrantSubjectKind"/>. Fails for the retired value 8 the
    /// same as for any other undeclared byte — callers that want a distinct "retired" message check
    /// <see cref="IsRetiredGrantSubjectWire"/> first.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The subject kind, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a live subject kind.</returns>
    public static bool TryFromWire(byte wire, out GrantSubjectKind value) {
        switch (wire) {
            case 0: value = GrantSubjectKind.All; return true;
            case 1: value = GrantSubjectKind.Body; return true;
            case 2: value = GrantSubjectKind.Screen; return true;
            case 3: value = GrantSubjectKind.Section; return true;
            case 4: value = GrantSubjectKind.Composition; return true;
            case 5: value = GrantSubjectKind.State; return true;
            case 6: value = GrantSubjectKind.Region; return true;
            case 7: value = GrantSubjectKind.Seat; return true;
            case 9: value = GrantSubjectKind.Creation; return true;
            case 10: value = GrantSubjectKind.Placement; return true;
            case 11: value = GrantSubjectKind.Adjacency; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Gets a value indicating whether <paramref name="wire"/> is the retired <see cref="GrantSubjectKind"/>
    /// wire value (8) — distinct from an undeclared byte for callers that report the two differently.</summary>
    /// <param name="wire">The wire byte.</param>
    public static bool IsRetiredGrantSubjectWire(byte wire) => (wire == 8);
    /// <summary>Maps a <see cref="WorldSection"/> to its wire byte — its own declaration ordinal, validated rather
    /// than assumed, so a future retirement in this enum has exactly one place to gain a gap. No member is retired
    /// today.</summary>
    /// <param name="value">The section.</param>
    /// <param name="wire">The wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a declared member.</returns>
    public static bool TryToWire(WorldSection value, out byte wire) {
        if (Enum.IsDefined(value: value)) {
            wire = ((byte)value);

            return true;
        }
        wire = default;

        return false;
    }
    /// <summary>Maps a wire byte back to its <see cref="WorldSection"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The section, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a declared member.</returns>
    public static bool TryFromWire(byte wire, out WorldSection value) {
        value = ((WorldSection)wire);

        return Enum.IsDefined(value: value);
    }
}
