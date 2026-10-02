namespace Puck.GamingBricks.Forge;

using Puck.Abstractions.Machines;

/// <summary>Content-admission presets owned by the GamingBrick cartridge family.</summary>
public static class GamingBrickContentPolicies {
    /// <summary>Creates a policy admitting only verified <see cref="CartridgeDocument.SchemaId"/> source content, the
    /// authored cartridge format the forge emits.</summary>
    /// <param name="assetAdmission">The independent disposition of auxiliary firmware and other asset paths.</param>
    /// <returns>The immutable Puck cartridge policy.</returns>
    public static MachineContentAdmissionPolicy Puck(MachineAssetAdmission assetAdmission = MachineAssetAdmission.Deny) =>
        new(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            [CartridgeDocument.SchemaId],
            assetAdmission: assetAdmission
        );
}
