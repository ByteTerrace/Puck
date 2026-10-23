using Xunit;

namespace Puck.Maths.Tests;

/// <summary>Authored law members and waivers resolve against the current public Maths surface.</summary>
public sealed class LawCompletenessTests {
    [Fact]
    [Trait(name: "tier", value: "Default")]
    public void EveryDeclaredMemberAndWaiverResolves() {
        foreach (var law in LawRegistry.All) {
            foreach (var reference in law.Members) {
                Assert.True(condition: MemberSurface.Resolve(reference: reference).Any(), userMessage: $"{law.Id}: {reference} resolves to no public member.");
            }
        }
        foreach (var (reference, reason) in Coverage.WaiverDeclarations) {
            Assert.True(condition: MemberSurface.Resolve(reference: reference).Any(), userMessage: $"Waiver {reference} resolves to no public member.");
            Assert.False(condition: string.IsNullOrWhiteSpace(value: reason), userMessage: $"{reference} has no waiver reason.");
        }
    }
}
