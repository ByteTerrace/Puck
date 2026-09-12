using System.Security.Cryptography;
using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

/// <summary>Pins the neutral, file-free admission contract used before machine construction.</summary>
public sealed class MachineContentAdmissionPolicyTests {
    private static readonly byte[] Source = [1, 2, 3, 4];
    private static readonly byte[] Executable = [5, 6, 7, 8];

    [Fact]
    public void PuckPresetRefusesNativeContent() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(Request(format: null));

        Assert.False(decision.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.NativeRefusedCode, actual: decision.Code);
    }

    [Fact]
    public void PuckPresetAllowsTheExactAuthoredFormat() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(Request(format: GamingBrickContentPolicies.PuckCartridgeFormat));

        Assert.True(decision.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.AllowedFormatCode, actual: decision.Code);
    }

    [Fact]
    public void RenamingNativeContentDoesNotCreateAFormatProof() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(
            Request(format: null, fieldPath: "content.renamed.puck"));

        Assert.False(decision.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.NativeRefusedCode, actual: decision.Code);
    }

    [Fact]
    public void FormatComparisonIsOrdinalAndMixedFormatsAreIndependent() {
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            trustedSourceFormats: ["puck.cartridge.v1", "puck.audio.v1"]);

        Assert.True(policy.Evaluate(Request(format: "puck.cartridge.v1")).Allowed);
        Assert.True(policy.Evaluate(Request(format: "puck.audio.v1")).Allowed);
        Assert.False(policy.Evaluate(Request(format: "PUCK.CARTRIDGE.V1")).Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.UntrustedFormatCode,
            actual: policy.Evaluate(Request(format: "puck.unknown.v1")).Code);
    }

    [Fact]
    public void ExactExecutableHashExceptionAdmitsNativeContent() {
        var hash = Convert.ToHexString(SHA256.HashData(Executable));
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            trustedSourceFormats: ["puck.cartridge.v1"],
            executableSha256Exceptions: [hash.ToLowerInvariant()]);

        var decision = policy.Evaluate(Request(format: null));

        Assert.True(decision.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.AllowedHashCode, actual: decision.Code);
    }

    [Fact]
    public void HashOnlyRequiresAnExactExecutableMatch() {
        var hash = Convert.ToHexString(SHA256.HashData(Executable));
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.HashOnly,
            executableSha256Exceptions: [hash]);

        Assert.True(policy.Evaluate(Request(format: null)).Allowed);
        var changed = policy.Evaluate(Request(format: null, executable: [5, 6, 7, 9]));
        Assert.False(changed.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.HashRefusedCode, actual: changed.Code);
    }

    [Fact]
    public void AssetPathUsesItsExplicitDispositionAndNeverFormatAdmission() {
        var refused = GamingBrickContentPolicies.Puck().Evaluate(
            Request(format: GamingBrickContentPolicies.PuckCartridgeFormat, role: MachineFieldRole.AssetPath));
        var allowed = GamingBrickContentPolicies.Puck(MachineAssetAdmission.Allow).Evaluate(
            Request(format: null, role: MachineFieldRole.AssetPath));

        Assert.Equal(expected: MachineContentAdmissionPolicy.AssetRefusedCode, actual: refused.Code);
        Assert.Equal(expected: MachineContentAdmissionPolicy.AllowedAssetCode, actual: allowed.Code);
    }

    [Fact]
    public void OpenModeAdmitsAHostPinnedNativeImage() {
        var decision = MachineContentAdmissionPolicy.Open().Evaluate(Request(format: null));

        Assert.True(decision.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.AllowedOpenCode, actual: decision.Code);
    }

    [Fact]
    public void MalformedDefaultRequestIsRefused() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(
            new MachineContentAdmissionRequest("", "", default, null, default, default));

        Assert.False(decision.Allowed);
        Assert.Equal(expected: MachineContentAdmissionPolicy.InvalidRequestCode, actual: decision.Code);
    }

    [Fact]
    public void PolicyCollectionsAreCloned() {
        var formats = new List<string> { "puck.cartridge.v1" };
        var hashes = new List<string> { Convert.ToHexString(SHA256.HashData(Executable)) };
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly, formats, hashes);
        formats.Clear();
        hashes.Clear();

        Assert.True(policy.Evaluate(Request(format: "puck.cartridge.v1")).Allowed);
        Assert.True(policy.Evaluate(Request(format: null)).Allowed);
    }

    [Theory]
    [InlineData((MachineContentAdmissionMode)99)]
    public void InvalidPolicyModesAreRefusedAtConstruction(MachineContentAdmissionMode mode) =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new MachineContentAdmissionPolicy(mode));

    [Fact]
    public void MalformedPolicyInputsAreRefusedAtConstruction() {
        Assert.Throws<ArgumentException>(testCode: () => new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly, trustedSourceFormats: [" "]));
        Assert.Throws<ArgumentException>(testCode: () => new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.HashOnly, executableSha256Exceptions: ["not-a-hash"]));
        Assert.Throws<ArgumentException>(testCode: () => new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.HashOnly));
    }

    private static MachineContentAdmissionRequest Request(
        string? format,
        string fieldPath = "content.path",
        MachineFieldRole role = MachineFieldRole.ContentPath,
        byte[]? source = null,
        byte[]? executable = null) =>
        new("puck.agb", fieldPath, role, format, source ?? Source, executable ?? Executable);
}
