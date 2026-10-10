using System.Reflection;

using Xunit;

using static Puck.Attestation.Tests.AttestationTestSupport;

namespace Puck.Attestation.Tests;

/// <summary>
/// Pins that a verification always runs under an explicit receiver-selected profile: the public verifier
/// cannot be called without one, and the attestation type exposes no public way to pair a projection with
/// independently supplied signed bytes.
/// </summary>
public sealed class VerifierProfileTests {
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

    // The compile-time shape: a required, non-nullable parameter with no default, so no call site can
    // leave the profile out or pass null without the compiler saying so.
    [Fact]
    public void VerifierProfile_IsARequiredNonNullableParameterWithNoDefault() {
        var parameter = Assert.Single(
            collection: typeof(AttestationVerifier)
                .GetMethod(name: nameof(AttestationVerifier.VerifyChain))!
                .GetParameters(),
            predicate: candidate => (candidate.Name == "profile")
        );
        var nullability = new NullabilityInfoContext().Create(parameterInfo: parameter);

        Assert.Equal(
            expected: typeof(AttestationProfile),
            actual: parameter.ParameterType
        );
        Assert.False(
            condition: parameter.HasDefaultValue,
            userMessage: "the verifier's profile parameter must have no default"
        );
        Assert.Equal(
            expected: NullabilityState.NotNull,
            actual: nullability.WriteState
        );
    }
    // The runtime backstop for a caller that suppresses the nullable warning.
    [Fact]
    public void Verification_WithoutAProfile_IsRefusedBeforeAnyVerdict() {
        var codec = new CborAttestationCodec();
        var keys = MintDomainKeys(subject: "user:jun");
        var trust = BuildDirectTrustList(
            keys: keys,
            reach: DefaultReach
        );
        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "jun's claim"
        );

        var exception = Assert.Throws<ArgumentNullException>(testCode: () => AttestationVerifier.VerifyChain(
            chain: null,
            claim: claim,
            codec: codec,
            expectedAudience: "world:home",
            expectedPurpose: "test.claim",
            now: Now,
            profile: null!,
            trustList: trust
        ));

        Assert.Equal(
            expected: "profile",
            actual: exception.ParamName
        );
    }
    // A projection paired with independently supplied signed bytes is the object-boundary forgery the
    // verifier re-checks; the type must not hand the pairing to callers in the first place.
    [Fact]
    public void SignedAttestation_ExposesNoPublicFactoryThatTakesIndependentSignedPortionBytes() {
        var offenders = typeof(SignedAttestation)
            .GetMembers(bindingAttr: BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .OfType<MethodBase>()
            .Where(predicate: method => method.GetParameters().Any(predicate: parameter => string.Equals(
                a: parameter.Name,
                b: "signedPortion",
                comparisonType: StringComparison.Ordinal
            )))
            .Select(selector: method => method.Name)
            .ToArray();

        Assert.Empty(collection: offenders);
    }
}
