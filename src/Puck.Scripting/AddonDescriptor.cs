namespace Puck.Scripting;

/// <summary>A neutral addon load request. Keeps the document type that describes an addon (today,
/// <c>Puck.World.WorldAddonRow</c>) out of Puck.Scripting's dependencies — the consumer bridges its document type
/// to this.</summary>
/// <param name="Name">The addon's identifying name, unique within a host.</param>
/// <param name="ModulePath">The module file path resolved through the host's asset source.</param>
/// <param name="ModuleHash">The content-address integrity pin (canonical <c>sha256-64/{hex}</c>). Required by the
/// document gate: a guest whose module is not pinned makes the state it touches depend on a file on disk, which is a
/// determinism hole before it is a security one. <see langword="null"/> still skips the check here, because this
/// neutral type is reachable from hosts that have no document gate in front of them.</param>
/// <param name="FuelPerTick">The per-tick fuel budget; <see langword="null"/> uses the host default (<see cref="AddonAbi.DefaultFuelPerTick"/>).</param>
/// <param name="Enabled">Whether the addon starts enabled.</param>
public readonly record struct AddonDescriptor(string Name, string ModulePath, string? ModuleHash, long? FuelPerTick, bool Enabled);
