namespace Puck.Scripting;

/// <summary>A neutral addon load request. Keeps the run-document type (<c>Puck.Scene.AddonDocument</c>) out of
/// Puck.Scripting's dependencies — the consumer bridges its document type to this.</summary>
/// <param name="Name">The addon's identifying name, unique within a host.</param>
/// <param name="ModulePath">The module file path resolved through the host's asset source.</param>
/// <param name="ModuleHash">The optional content-address integrity pin (canonical <c>sha256-64/{hex}</c>); <see langword="null"/> skips the check.</param>
/// <param name="Slot">The roster slot the addon drives exclusively; <see langword="null"/> means the first free non-human slot.</param>
/// <param name="FuelPerTick">The per-tick fuel budget; <see langword="null"/> uses the host default (<see cref="AddonAbi.DefaultFuelPerTick"/>).</param>
/// <param name="Enabled">Whether the addon starts enabled.</param>
public readonly record struct AddonDescriptor(string Name, string ModulePath, string? ModuleHash, int? Slot, long? FuelPerTick, bool Enabled);
