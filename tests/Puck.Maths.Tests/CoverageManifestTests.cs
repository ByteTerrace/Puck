using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// The coverage ratchet gate. Enumerates the public Puck.Maths surface and reconciles it against the committed
/// <c>coverage-manifest.json</c>. It fails only when a public member is classified nowhere — no manifest state, no
/// covering law, no waiver (new API appeared silently) — or when a member moved covered→uncovered. The large initial
/// uncovered backlog never fails it; coverage only grows. The manifest is the artifact of THIS gate: the assembly
/// ledger regenerates it only when this test ran, and never classifies a member itself, so an unclassified member keeps
/// failing here on every run until a law covers it or a waiver names it.
/// </summary>
public sealed class CoverageManifestTests {
    [Fact]
    [Trait(name: "tier", value: "Default")]
    public void ManifestRatchetHolds() {
        // Claims the manifest for this run: the ledger persists it only because this gate executed.
        LedgerState.RecordRatchet();

        var committed = ArtifactJson.ReadOrDefault<Manifest>(path: TestPaths.Artifact(fileName: "coverage-manifest.json"));

        Assert.SkipWhen(condition: (committed is null), reason: "Bootstrapping: the assembly ledger generates the manifest at run end; commit it, then this gate enforces.");

        // Every declared CoverRef must resolve to at least one real member — a typo would silently under-count coverage.
        foreach (var lawCase in LawRegistry.All) {
            foreach (var reference in lawCase.Members) {
                Assert.True(condition: MemberSurface.Resolve(reference: reference).Any(), userMessage: $"CoverRef {reference.Type.Name}.{reference.Name} (case {lawCase.Id}) resolved to no public member.");
            }
        }

        // Every waived member must carry a reason.
        foreach (var entry in committed!.Members.Where(predicate: entry => (entry.State == "waived"))) {
            Assert.False(condition: string.IsNullOrWhiteSpace(value: entry.Reason), userMessage: $"Waived member {entry.Id} has no reason.");
        }

        var (newMembers, regressions) = Coverage.Ratchet(committed: committed);

        Assert.True(condition: (newMembers.Count == 0), userMessage: $"{newMembers.Count} unclassified public member(s) — no manifest state, no covering law, no waiver: {string.Join(separator: ", ", values: newMembers.Take(count: 20))}");
        Assert.True(condition: (regressions.Count == 0), userMessage: $"{regressions.Count} member(s) moved covered→uncovered: {string.Join(separator: ", ", values: regressions.Take(count: 20))}");
    }
}
