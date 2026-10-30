using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A world's declaration for one companion role: who drives a body that holds the role here and what it
/// wears. A companion is admitted only where its role is declared; a destination that declares nothing for its role
/// refuses it at reservation, and it stays with its custodian.</summary>
/// <param name="Name">The role's name, unique within the document.</param>
/// <param name="Driver">The <see cref="WorldAddonRow.Name"/> of this world's own addon that drives the role: the same
/// pinned driver a visitor prefers, or one the author substitutes. Exactly one of this and <paramref name="Accept"/>.</param>
/// <param name="Accept">Whether this world runs a visitor's own preferred driver, resolved by its pin.</param>
/// <param name="Kit">The <see cref="WorldKit.Name"/> an arriving companion moves under. Absent: the default peer kit. A
/// companion re-adopted by its own home placement wears that placement's kit instead.</param>
/// <param name="Look">The look an arriving companion wears. Absent: the default look.</param>
public sealed record WorldRole(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Driver = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Accept = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Look = null
);
