namespace Puck.GamingBricks;

using Puck.Abstractions.Machines;

/// <summary>Content-admission presets owned by the GamingBrick cartridge family.</summary>
public static class GamingBrickContentPolicies {
    /// <summary>The authored cartridge format emitted by the Puck cartridge forge.</summary>
    public const string PuckCartridgeFormat = "puck.cartridge.v1";

    /// <summary>Creates a policy admitting only verified <c>puck.cartridge.v1</c> source content.</summary>
    /// <param name="assetAdmission">The independent disposition of auxiliary firmware and other asset paths.</param>
    /// <returns>The immutable Puck cartridge policy.</returns>
    public static MachineContentAdmissionPolicy Puck(MachineAssetAdmission assetAdmission = MachineAssetAdmission.Deny) =>
        new(MachineContentAdmissionMode.AuthoredFormatsOnly, [PuckCartridgeFormat], assetAdmission: assetAdmission);
}
