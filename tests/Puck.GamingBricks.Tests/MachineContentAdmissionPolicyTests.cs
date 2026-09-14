using System.Security.Cryptography;
using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Tests;

/// <summary>Pins the neutral, file-free admission contract used before machine construction.</summary>
public sealed class MachineContentAdmissionPolicyTests {
    private static readonly byte[] Executable = [5, 6, 7, 8];
    private static readonly byte[] Source = [1, 2, 3, 4];

    private static MachineContentAdmissionRequest Request(
        string? format,
        string fieldPath = "content.path",
        MachineFieldRole role = MachineFieldRole.ContentPath,
        byte[]? source = null,
        byte[]? executable = null) =>
        new(
            EngineId: "puck.agb",
            ExecutableBytes: (executable ?? Executable),
            FieldPath: fieldPath,
            Role: role,
            SourceBytes: (source ?? Source),
            VerifiedSourceFormat: format
        );

    [Fact]
    public void AssetPathUsesItsExplicitDispositionAndNeverFormatAdmission() {
        var refused = GamingBrickContentPolicies.Puck().Evaluate(request: Request(
            format: GamingBrickContentPolicies.PuckCartridgeFormat,
            role: MachineFieldRole.AssetPath
        ));
        var allowed = GamingBrickContentPolicies.Puck(assetAdmission: MachineAssetAdmission.Allow).Evaluate(request: Request(
            format: null,
            role: MachineFieldRole.AssetPath
        ));

        Assert.Equal(
            expected: MachineContentAdmissionPolicy.AssetRefusedCode,
            actual: refused.Code
        );
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.AllowedAssetCode,
            actual: allowed.Code
        );
    }
    [Fact]
    public void ExactExecutableHashExceptionAdmitsNativeContent() {
        var hash = Convert.ToHexString(inArray: SHA256.HashData(source: Executable));
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            trustedSourceFormats: ["puck.cartridge.v1"],
            executableSha256Exceptions: [hash.ToLowerInvariant()]
        );

        var decision = policy.Evaluate(request: Request(format: null));

        Assert.True(condition: decision.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.AllowedHashCode,
            actual: decision.Code
        );
    }
    [Fact]
    public void FormatComparisonIsOrdinalAndMixedFormatsAreIndependent() {
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            trustedSourceFormats: ["puck.cartridge.v1", "puck.tune.v1"]
        );

        Assert.True(condition: policy.Evaluate(request: Request(format: "puck.cartridge.v1")).Allowed);
        Assert.True(condition: policy.Evaluate(request: Request(format: "puck.tune.v1")).Allowed);
        Assert.False(condition: policy.Evaluate(request: Request(format: "PUCK.CARTRIDGE.V1")).Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.UntrustedFormatCode,
            actual: policy.Evaluate(request: Request(format: "puck.unknown.v1")).Code
        );
    }
    [Fact]
    public void HashOnlyRequiresAnExactExecutableMatch() {
        var hash = Convert.ToHexString(inArray: SHA256.HashData(source: Executable));
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.HashOnly,
            executableSha256Exceptions: [hash]
        );

        Assert.True(condition: policy.Evaluate(request: Request(format: null)).Allowed);
        var changed = policy.Evaluate(request: Request(
            format: null,
            executable: [5, 6, 7, 9]
        ));

        Assert.False(condition: changed.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.HashRefusedCode,
            actual: changed.Code
        );
    }
    [InlineData(((MachineContentAdmissionMode)99))]
    [Theory]
    public void InvalidPolicyModesAreRefusedAtConstruction(MachineContentAdmissionMode mode) =>
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new MachineContentAdmissionPolicy(mode));
    [Fact]
    public void MalformedDefaultRequestIsRefused() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(request: new MachineContentAdmissionRequest(
            EngineId: "",
            ExecutableBytes: default,
            FieldPath: "",
            Role: default,
            SourceBytes: default,
            VerifiedSourceFormat: null
        ));

        Assert.False(condition: decision.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.InvalidRequestCode,
            actual: decision.Code
        );
    }
    [Fact]
    public void MalformedPolicyInputsAreRefusedAtConstruction() {
        Assert.Throws<ArgumentException>(testCode: () => new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            trustedSourceFormats: [" "]
        ));
        Assert.Throws<ArgumentException>(testCode: () => new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.HashOnly,
            executableSha256Exceptions: ["not-a-hash"]
        ));
        Assert.Throws<ArgumentException>(testCode: () => new MachineContentAdmissionPolicy(MachineContentAdmissionMode.HashOnly));
    }
    [Fact]
    public void OpenModeAdmitsAHostPinnedNativeImage() {
        var decision = MachineContentAdmissionPolicy.Open().Evaluate(request: Request(format: null));

        Assert.True(condition: decision.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.AllowedOpenCode,
            actual: decision.Code
        );
    }
    [Fact]
    public void PolicyCollectionsAreCloned() {
        var formats = new List<string> { "puck.cartridge.v1" };
        var hashes = new List<string> { Convert.ToHexString(inArray: SHA256.HashData(source: Executable)) };
        var policy = new MachineContentAdmissionPolicy(
            MachineContentAdmissionMode.AuthoredFormatsOnly,
            formats,
            hashes
        );

        formats.Clear();
        hashes.Clear();

        Assert.True(condition: policy.Evaluate(request: Request(format: "puck.cartridge.v1")).Allowed);
        Assert.True(condition: policy.Evaluate(request: Request(format: null)).Allowed);
    }
    [Fact]
    public void PuckPresetAllowsTheExactAuthoredFormat() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(request: Request(format: GamingBrickContentPolicies.PuckCartridgeFormat));

        Assert.True(condition: decision.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.AllowedFormatCode,
            actual: decision.Code
        );
    }
    [Fact]
    public void PuckPresetRefusesNativeContent() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(request: Request(format: null));

        Assert.False(condition: decision.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.NativeRefusedCode,
            actual: decision.Code
        );
    }
    [Fact]
    public void RenamingNativeContentDoesNotCreateAFormatProof() {
        var decision = GamingBrickContentPolicies.Puck().Evaluate(request: Request(
            format: null,
            fieldPath: "content.renamed.puck"
        ));

        Assert.False(condition: decision.Allowed);
        Assert.Equal(
            expected: MachineContentAdmissionPolicy.NativeRefusedCode,
            actual: decision.Code
        );
    }
}
