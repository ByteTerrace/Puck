using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A world's declaration for one companion role: who drives a body that holds the role here, what it wears,
/// and the authority its driver receives. A companion is admitted only where its role is declared; a crossing toward
/// a world that declares nothing for its role is refused at reservation and the companion stays home.</summary>
/// <param name="Name">The role's name, unique within the document (<c>companion</c>).</param>
/// <param name="Driver">The <see cref="WorldAddonRow.Name"/> of this world's own addon that drives the role: the same
/// pinned driver a visitor prefers, or one the author substitutes. Exactly one of this and <paramref name="Accept"/>.</param>
/// <param name="Accept">Whether this world runs a visitor's own preferred driver, resolved by its pin from the
/// process content store and verified before it mounts, under this world's <paramref name="Fuel"/>. One visiting
/// driver runs per verified pin and drives only the companions that prefer it.</param>
/// <param name="Kit">The <see cref="WorldKit.Name"/> an arriving companion moves under. Absent: the default peer kit.
/// A companion re-adopted by its own home placement wears that placement's kit instead.</param>
/// <param name="Look">The <see cref="WorldLook.Name"/> an arriving companion wears. Absent: the default look.</param>
/// <param name="Fuel">The per-tick fuel a visiting driver runs under; 0 charges the ABI default. Ignored for a named
/// <paramref name="Driver"/>, whose own row states its fuel.</param>
/// <param name="Drive">The per-tick Drive budget the driver holds over each body of the role.</param>
/// <param name="Observe">The per-tick Observe budget the driver holds over each body of the role and over the body
/// each follows.</param>
/// <param name="FollowReach">How far from the door its player went through, in metres, a companion of the role
/// leaving this world may start its own crossing: one that trailed further behind is set down this far from the door,
/// so it arrives in sight of its player.</param>
public sealed record WorldRole(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Driver = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Accept = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Look = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ulong Fuel = 0UL,
    ushort Drive = 4,
    ushort Observe = 1,
    double FollowReach = WorldRole.DefaultFollowReach
) {
    /// <summary>The follow reach a role that authors none holds, in metres.</summary>
    public const double DefaultFollowReach = 2.0;
    /// <summary>The addon name a visiting driver of a role mounts under: one per verified pin, generated, so no
    /// authored row can take it.</summary>
    /// <param name="role">The role name.</param>
    /// <param name="pin">The driver's pin (<c>sha256/…</c>).</param>
    /// <returns>The generated addon name.</returns>
    public static string VisitorAddonName(string role, string pin) {
        var hex = pin[(pin.LastIndexOf(value: '/') + 1)..];

        return Puck.State.GeneratedName.Join("role", role, hex[..Math.Min(val1: 16, val2: hex.Length)]);
    }
    /// <summary>Returns whether <paramref name="addonName"/> is spelled as a visiting driver of <paramref name="role"/>,
    /// which only the engine mounts.</summary>
    /// <param name="addonName">The addon name.</param>
    /// <param name="role">The role name.</param>
    /// <returns><see langword="true"/> for a generated visiting-driver name of the role.</returns>
    public static bool IsVisitorAddonName(string addonName, string role) => addonName.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: (Puck.State.GeneratedName.Join("role", role) + Puck.State.GeneratedName.Joiner)
    );

    /// <summary>Returns the addon whose guest drives a companion of this role that prefers <paramref name="pin"/>:
    /// this world's own <see cref="Driver"/>, or for an <see cref="Accept"/> role the visiting driver of that pin, or
    /// <see langword="null"/> when an accept role's companion prefers no driver.</summary>
    /// <param name="pin">The companion's preferred driver pin, or <see langword="null"/>.</param>
    /// <returns>The driving addon's name, or <see langword="null"/>.</returns>
    public string? DriverFor(string? pin) => (Driver ?? ((pin is null) ? null : VisitorAddonName(pin: pin, role: Name)));
}
