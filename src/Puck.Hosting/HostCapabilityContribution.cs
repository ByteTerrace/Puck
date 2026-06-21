namespace Puck.Hosting;

/// <summary>
/// A single capability a composition module contributes to the root <see cref="IHostContext"/>. A host
/// collects every registered contribution and assembles the root context from them, so a graphics backend
/// can publish its device and a terminal can publish its baton without either module referencing the other.
/// </summary>
/// <param name="CapabilityType">The capability's contract type, used as the lookup key on the root context.</param>
/// <param name="Instance">The capability implementation.</param>
/// <param name="IsHeld">Whether the capability is held by the root alone (<see langword="true"/>) or inherited by every node (<see langword="false"/>).</param>
public sealed record HostCapabilityContribution(Type CapabilityType, object Instance, bool IsHeld);
