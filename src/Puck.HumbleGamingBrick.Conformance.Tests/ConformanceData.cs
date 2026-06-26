namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>Supplies xUnit theory rows from the test corpus, one row per (ROM, eligible model). When the corpus is
/// unavailable a single sentinel row (empty relative path) is emitted so the theory reports a skip rather than a
/// "no data found" failure.</summary>
public static class ConformanceData {
    /// <summary>Gets the mooneye theory rows.</summary>
    /// <returns>Rows of (suite, relativePath, model).</returns>
    public static IEnumerable<object[]> Mooneye() =>
        Rows(suite: "mooneye");

    /// <summary>Gets the same-suite theory rows.</summary>
    /// <returns>Rows of (suite, relativePath, model).</returns>
    public static IEnumerable<object[]> SameSuite() =>
        Rows(suite: "same-suite");

    /// <summary>Gets the blargg theory rows.</summary>
    /// <returns>Rows of (suite, relativePath, model).</returns>
    public static IEnumerable<object[]> Blargg() =>
        Rows(suite: "blargg");

    /// <summary>Gets the gbmicrotest theory rows.</summary>
    /// <returns>Rows of (suite, relativePath, model).</returns>
    public static IEnumerable<object[]> GbMicrotest() =>
        Rows(suite: "gbmicrotest");

    /// <summary>Gets the screenshot theory rows (mealybug, acid2, scribbltests) across all three suites.</summary>
    /// <returns>Rows of (suite, relativePath, model).</returns>
    public static IEnumerable<object[]> Screenshot() {
        if (!RomCatalog.IsAvailable) {
            yield return ["mealybug", string.Empty, ConsoleModel.Dmg];

            yield break;
        }

        var any = false;

        foreach (var romCase in RomCatalog.Enumerate().Where(predicate: static c => c.Protocol == ResultProtocol.Screenshot)) {
            any = true;

            yield return [romCase.Suite, romCase.RelativePath, romCase.Model];
        }

        if (!any) {
            yield return ["mealybug", string.Empty, ConsoleModel.Dmg];
        }
    }

    private static IEnumerable<object[]> Rows(string suite) {
        if (!RomCatalog.IsAvailable) {
            yield return [suite, string.Empty, ConsoleModel.Dmg];

            yield break;
        }

        var any = false;

        foreach (var romCase in RomCatalog.EnumerateSuite(suite: suite)) {
            any = true;

            yield return [suite, romCase.RelativePath, romCase.Model];
        }

        if (!any) {
            yield return [suite, string.Empty, ConsoleModel.Dmg];
        }
    }
}
