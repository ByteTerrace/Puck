using Xunit;

namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>One xUnit case per test ROM. A clear pass passes; a clear fail fails (with the failing detail); an
/// inconclusive run (no result signal / harness gap) is skipped so it never masquerades as a pass. The Scorecard
/// fact captures the full picture including inconclusives.</summary>
public sealed class ConformanceTheories {
    [Theory]
    [MemberData(memberName: nameof(ConformanceData.Mooneye), MemberType = typeof(ConformanceData))]
    public void Mooneye(string suite, string relativePath, ConsoleModel model) =>
        RunCase(suite: suite, relativePath: relativePath, model: model);

    [Theory]
    [MemberData(memberName: nameof(ConformanceData.SameSuite), MemberType = typeof(ConformanceData))]
    public void SameSuite(string suite, string relativePath, ConsoleModel model) =>
        RunCase(suite: suite, relativePath: relativePath, model: model);

    [Theory]
    [MemberData(memberName: nameof(ConformanceData.Blargg), MemberType = typeof(ConformanceData))]
    public void Blargg(string suite, string relativePath, ConsoleModel model) =>
        RunCase(suite: suite, relativePath: relativePath, model: model);

    [Theory]
    [MemberData(memberName: nameof(ConformanceData.GbMicrotest), MemberType = typeof(ConformanceData))]
    public void GbMicrotest(string suite, string relativePath, ConsoleModel model) =>
        RunCase(suite: suite, relativePath: relativePath, model: model);

    [Theory]
    [MemberData(memberName: nameof(ConformanceData.Screenshot), MemberType = typeof(ConformanceData))]
    public void Screenshot(string suite, string relativePath, ConsoleModel model) =>
        RunCase(suite: suite, relativePath: relativePath, model: model);

    private static void RunCase(string suite, string relativePath, ConsoleModel model) {
        Assert.SkipWhen(condition: string.IsNullOrEmpty(value: relativePath), reason: "PUCK_GB_TESTROMS not set; GB test corpus unavailable.");

        var romCase = RomCatalog.Find(suite: suite, relativePath: relativePath, model: model);

        Assert.SkipWhen(condition: romCase is null, reason: "ROM no longer present: " + relativePath);

        var outcome = ConformanceEngine.Execute(romCase: romCase!);

        Assert.SkipWhen(condition: outcome.Status == TestStatus.Inconclusive, reason: "inconclusive: " + outcome.Detail);

        Assert.True(condition: outcome.Status == TestStatus.Pass, userMessage: relativePath + " [" + model + "] -> " + outcome.Status + ": " + outcome.Detail);
    }
}
