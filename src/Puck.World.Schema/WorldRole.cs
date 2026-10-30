using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A world's declaration for one companion role: who drives a body that holds the role here, what it wears,
/// and the authority its driver receives. A companion is admitted only where its role is declared; a crossing toward
/// a world that declares nothing for its role is refused at reservation and the companion stays home.</summary>
/// <param name="Name">The role's name, unique within the document (<c>companion</c>).</param>
/// <param name="Driver">The <see cref="WorldAddonRow.Name"/> of this world's own addon that drives the role: the same
/// pinned driver a visitor prefers, or one the author substitutes. Exactly one of this and <paramref name="Accept"/>.</param>
/// <param name="Accept">Whether this world runs a visitor's own preferred driver, resolved by its pin from the
/// process content store and verified before it mounts, under this world's <paramref name="Fuel"/> and grants. One
/// visiting driver runs per role; a companion preferring a different pin while one runs is refused at reservation.</param>
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
    /// <summary>The addon name the visiting driver of a role mounts under: generated, so no authored row can take
    /// it.</summary>
    /// <param name="role">The role name.</param>
    /// <returns>The generated addon name.</returns>
    public static string VisitorAddonName(string role) => Puck.State.GeneratedName.Join("role", role);

    /// <summary>Gets the addon name whose guest drives this role: <see cref="Driver"/>, or the visiting driver's
    /// generated name for an <see cref="Accept"/> role.</summary>
    [JsonIgnore]
    public string DriverAddonName => (Driver ?? VisitorAddonName(role: Name));
}
