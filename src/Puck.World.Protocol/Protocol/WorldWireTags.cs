using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>The one byte&lt;-&gt;enum mapping for every enum a binary codec over this project's types carries:
/// <see cref="WorldCapability"/>, <see cref="PrincipalKind"/>, <see cref="GranteeKind"/>, <see cref="GrantSubjectKind"/>, <see cref="WorldSection"/>,
/// <see cref="WorldRebuildKind"/>, and <see cref="SnapPoseMode"/>. The submission wire (<see cref="WorldSubmissionCodec"/>),
/// the <c>.puckreplay</c> tape, the authority checkpoint, and the federation frames all map through here, so no codec
/// derives a byte from an enum's declaration ordinal by a cast that would reorder on a member insertion. A byte this
/// type does not name is refused like any other undeclared value.</summary>
public static class WorldWireTags {
    /// <summary>Maps a wire byte back to its <see cref="WorldCapability"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The capability, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a capability.</returns>
    public static bool TryFromWire(byte wire, out WorldCapability value) {
        switch (wire) {
            case 0: value = WorldCapability.Drive; return true;
            case 1: value = WorldCapability.Observe; return true;
            case 2: value = WorldCapability.Control; return true;
            case 3: value = WorldCapability.Mutate; return true;
            case 4: value = WorldCapability.Edit; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a wire byte back to its <see cref="PrincipalKind"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The principal kind, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a live principal kind.</returns>
    public static bool TryFromWire(byte wire, out PrincipalKind value) {
        switch (wire) {
            case 0: value = PrincipalKind.Console; return true;
            case 1: value = PrincipalKind.Seat; return true;
            case 2: value = PrincipalKind.Addon; return true;
            case 3: value = PrincipalKind.Peer; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a wire byte back to its <see cref="GranteeKind"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The grantee kind, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a live grantee kind.</returns>
    public static bool TryFromWire(byte wire, out GranteeKind value) {
        switch (wire) {
            case 0: value = GranteeKind.Principal; return true;
            case 1: value = GranteeKind.Group; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a wire byte back to its <see cref="GrantSubjectKind"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The subject kind, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a subject kind.</returns>
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
            case 8: value = GrantSubjectKind.Creation; return true;
            case 9: value = GrantSubjectKind.Placement; return true;
            case 10: value = GrantSubjectKind.Adjacency; return true;
            case 11: value = GrantSubjectKind.Machine; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a wire byte back to its <see cref="WorldSection"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The section, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a declared member.</returns>
    public static bool TryFromWire(byte wire, out WorldSection value) {
        value = ((WorldSection)wire);

        return Enum.IsDefined(value: value);
    }
    /// <summary>Maps a wire byte back to its <see cref="WorldRebuildKind"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The rebuild kind, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a rebuild kind.</returns>
    public static bool TryFromWire(byte wire, out WorldRebuildKind value) {
        switch (wire) {
            case 0: value = WorldRebuildKind.Reset; return true;
            case 1: value = WorldRebuildKind.Load; return true;
            case 2: value = WorldRebuildKind.Reload; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a wire byte back to its <see cref="SnapPoseMode"/>.</summary>
    /// <param name="wire">The wire byte.</param>
    /// <param name="value">The snap mode, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a snap mode.</returns>
    public static bool TryFromWire(byte wire, out SnapPoseMode value) {
        switch (wire) {
            case 0: value = SnapPoseMode.Pose; return true;
            default: value = default; return false;
        }
    }
    /// <summary>Maps a <see cref="WorldCapability"/> to its wire byte.</summary>
    /// <param name="value">The capability.</param>
    /// <param name="wire">The wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a wire value.</returns>
    public static bool TryToWire(WorldCapability value, out byte wire) {
        switch (value) {
            case WorldCapability.Drive: wire = 0; return true;
            case WorldCapability.Observe: wire = 1; return true;
            case WorldCapability.Control: wire = 2; return true;
            case WorldCapability.Mutate: wire = 3; return true;
            case WorldCapability.Edit: wire = 4; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a <see cref="PrincipalKind"/> to its wire byte. <see cref="PrincipalKind.World"/> has no live wire
    /// value — the world's own program is stamped structurally, never carried on a submission — and
    /// <see cref="PrincipalKind.Unspecified"/> is no identity, so both fail here.</summary>
    /// <param name="value">The principal kind.</param>
    /// <param name="wire">The wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a live wire value.</returns>
    public static bool TryToWire(PrincipalKind value, out byte wire) {
        switch (value) {
            case PrincipalKind.Console: wire = 0; return true;
            case PrincipalKind.Seat: wire = 1; return true;
            case PrincipalKind.Addon: wire = 2; return true;
            case PrincipalKind.Peer: wire = 3; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a <see cref="GranteeKind"/> to its wire byte. <see cref="GranteeKind.Document"/> has no live wire
    /// value — a document's grant row is read off the owning document by the cross-document write-back channel, never
    /// held in the runtime table — so it fails here.</summary>
    /// <param name="value">The grantee kind.</param>
    /// <param name="wire">The wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a live wire value.</returns>
    public static bool TryToWire(GranteeKind value, out byte wire) {
        switch (value) {
            case GranteeKind.Principal: wire = 0; return true;
            case GranteeKind.Group: wire = 1; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a <see cref="GrantSubjectKind"/> to its wire byte.</summary>
    /// <param name="value">The subject kind.</param>
    /// <param name="wire">The wire byte, on success.</param>
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
            case GrantSubjectKind.Creation: wire = 8; return true;
            case GrantSubjectKind.Placement: wire = 9; return true;
            case GrantSubjectKind.Adjacency: wire = 10; return true;
            case GrantSubjectKind.Machine: wire = 11; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a <see cref="WorldSection"/> to its wire byte — its own declaration ordinal, validated rather
    /// than assumed.</summary>
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
    /// <summary>Maps a <see cref="WorldRebuildKind"/> to its wire byte.</summary>
    /// <param name="value">The rebuild kind.</param>
    /// <param name="wire">The wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a wire value.</returns>
    public static bool TryToWire(WorldRebuildKind value, out byte wire) {
        switch (value) {
            case WorldRebuildKind.Reset: wire = 0; return true;
            case WorldRebuildKind.Load: wire = 1; return true;
            case WorldRebuildKind.Reload: wire = 2; return true;
            default: wire = default; return false;
        }
    }
    /// <summary>Maps a <see cref="SnapPoseMode"/> to its wire byte.</summary>
    /// <param name="value">The snap mode.</param>
    /// <param name="wire">The wire byte, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> has a wire value.</returns>
    public static bool TryToWire(SnapPoseMode value, out byte wire) {
        switch (value) {
            case SnapPoseMode.Pose: wire = 0; return true;
            default: wire = default; return false;
        }
    }
}
