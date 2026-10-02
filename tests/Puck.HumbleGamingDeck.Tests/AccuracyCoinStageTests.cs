using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Puck.Testing;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Checks the AccuracyCoin runner's ratchet and infrastructure reports through its executable.</summary>
public sealed class AccuracyCoinStageTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(prefix: "puck-hgd-accuracy-");

    public void Dispose() => m_directory.Dispose();
    /// <summary>Checks unchanged outcomes, improvements, regressions, and changed failure or success codes.</summary>
    /// <param name="result">The fixture's result byte.</param>
    /// <param name="recorded">The ledger outcome.</param>
    /// <param name="exitCode">The required process exit code.</param>
    /// <param name="caseVerdict">The required case classification.</param>
    /// <returns>The asynchronous check.</returns>
    [InlineData(0x01, "PASS", 0, "Pass")]
    [InlineData(0x06, "FAIL 1", 0, "ExpectedFail")]
    [InlineData(0x41, "PASS variant 16", 0, "Pass")]
    [InlineData(0x56, "FAIL 21", 0, "ExpectedFail")]
    [InlineData(0xFF, "SKIP", 0, "ExpectedFail")]
    [InlineData(0x00, "PASS", 1, "Mismatch")]
    [InlineData(0x01, "FAIL 1", 1, "Mismatch")]
    [InlineData(0x06, "PASS", 1, "Mismatch")]
    [InlineData(0x0A, "FAIL 1", 1, "Mismatch")]
    [InlineData(0x09, "PASS variant 1", 1, "Mismatch")]
    [Theory]
    public async Task RatchetRequiresEveryOutcomeToMatch(byte result, string recorded, int exitCode, string caseVerdict) {
        var root = CreateCheckout(recorded: recorded, result: result);
        var stage = await Run(root: root, exitCode: exitCode);

        Assert.Equal(expected: ((exitCode == 0) ? "Pass" : "Fail"), actual: stage.GetProperty(propertyName: "Verdict").GetString());
        Assert.Equal(expected: 1, actual: stage.GetProperty(propertyName: "Cases").GetProperty(propertyName: caseVerdict).GetInt32());
        using var candidate = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: root, path2: "artifacts/accuracy-coin.candidate.json")));

        Assert.True(condition: candidate.RootElement.TryGetProperty(propertyName: "Probe", value: out _));
    }
    /// <summary>A candidate becomes authoritative immediately when copied to the checkout ledger.</summary>
    /// <returns>The asynchronous check.</returns>
    [Fact]
    public async Task RerecordingUsesTheCheckoutWithoutRebuilding() {
        var root = CreateCheckout(recorded: "FAIL 1", result: 1);

        _ = await Run(root: root, exitCode: 1);
        File.Copy(sourceFileName: Path.Combine(path1: root, path2: "artifacts/accuracy-coin.candidate.json"),
            destFileName: Path.Combine(path1: root, path2: "src/Puck.HumbleGamingDeck.Post/AccuracyCoinExpectations.json"), overwrite: true);
        _ = await Run(root: root, exitCode: 0);
    }
    /// <summary>A recorded test absent from the ROM must fail JUnit as well as the process.</summary>
    /// <returns>The asynchronous check.</returns>
    [Fact]
    public async Task AbsentRecordedTestProducesAJUnitFailure() {
        var root = CreateCheckout(recorded: "PASS", result: 1);

        File.WriteAllText(path: Path.Combine(path1: root, path2: "src/Puck.HumbleGamingDeck.Post/AccuracyCoinExpectations.json"),
            contents: """{ "Probe": "PASS", "Absent": "PASS" }""");
        var stage = await Run(root: root, exitCode: 1);

        Assert.Equal(expected: 1, actual: stage.GetProperty(propertyName: "Cases").GetProperty(propertyName: "Mismatch").GetInt32());
        var report = XDocument.Load(uri: Path.Combine(path1: root, path2: "artifacts/results.junit.xml"));
        var failure = Assert.Single(collection: report.Descendants(name: "failure"));

        Assert.Equal(expected: "Absent", actual: failure.Parent!.Attribute(name: "name")!.Value);
    }
    /// <summary>Missing assets, rejected hashes and unreadable ledgers produce reports with exit code two.</summary>
    /// <param name="fault">The missing or invalid input.</param>
    /// <returns>The asynchronous check.</returns>
    [InlineData("missing-cache")]
    [InlineData("missing-explicit")]
    [InlineData("wrong-hash")]
    [InlineData("missing-ledger")]
    [InlineData("null-ledger")]
    [Theory]
    public async Task InfrastructureFailuresReachTheReports(string fault) {
        var root = CreateCheckout(recorded: "PASS", result: 1);
        var corpus = Path.Combine(path1: root, path2: "cache/accuracy-coin/fixture");
        var ledger = Path.Combine(path1: root, path2: "src/Puck.HumbleGamingDeck.Post/AccuracyCoinExpectations.json");

        switch (fault) {
            case "missing-cache":
            case "missing-explicit":
                File.Delete(path: Path.Combine(path1: corpus, path2: "AccuracyCoin.nes"));
                break;
            case "wrong-hash":
                File.WriteAllBytes(path: Path.Combine(path1: corpus, path2: "AccuracyCoin.nes"), bytes: [0]);
                break;
            case "missing-ledger":
                File.Delete(path: ledger);
                break;
            case "null-ledger":
                File.WriteAllText(contents: "null", path: ledger);
                break;
        }
        var stage = await Run(arguments: ((fault == "missing-explicit") ? ["--accuracy-coin", corpus] : []), exitCode: 2,
            root: root);

        Assert.Equal(expected: "Infra", actual: stage.GetProperty(propertyName: "Verdict").GetString());
        Assert.False(condition: File.Exists(path: Path.Combine(path1: root, path2: "artifacts/accuracy-coin.candidate.json")));
    }

    private string CreateCheckout(byte result, string recorded) {
        var root = m_directory.RootPath;
        var project = Path.Combine(path1: root, path2: "src/Puck.HumbleGamingDeck.Post");
        var corpus = Path.Combine(path1: root, path2: "cache/accuracy-coin/fixture");

        _ = Directory.CreateDirectory(path: project);
        _ = Directory.CreateDirectory(path: corpus);
        File.WriteAllText(path: Path.Combine(path1: root, path2: "Puck.slnx"), contents: "<Solution />");
        var image = new byte[(16 + 32768)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 2;
        // Wait for Start through $4016, then publish one result and the pinned ROM's completed-menu NMI vector.
        byte[] program = [
            0x78, 0xA9, 0x01, 0x8D, 0x16, 0x40, 0xA9, 0x00, 0x8D, 0x16, 0x40, 0xA2, 0x04,
            0xAD, 0x16, 0x40, 0xCA, 0xD0, 0xFA, 0x29, 0x01, 0xF0, 0xEA,
            0xA9, result, 0x8D, 0x10, 0x04, 0xA9, 0x01, 0x85, 0x37,
            0xA9, 0x4C, 0x8D, 0x00, 0x07, 0xA9, 0x08, 0x8D, 0x01, 0x07,
            0xA9, 0x93, 0x8D, 0x02, 0x07, 0x4C, 0x2F, 0x80,
        ];

        program.CopyTo(array: image, index: 16);
        "Probe"u8.CopyTo(destination: image.AsSpan(start: 0x110));
        byte[] entry = [0xFF, 0x10, 0x04, 0x00, 0x80];

        entry.CopyTo(array: image, index: 0x115);
        image[(16 + 0x7FFD)] = 0x80;
        File.WriteAllBytes(path: Path.Combine(path1: corpus, path2: "AccuracyCoin.nes"), bytes: image);
        File.WriteAllText(path: Path.Combine(path1: project, path2: "AccuracyCoinExpectations.json"), contents: $$"""{ "Probe": "{{recorded}}" }""");
        File.WriteAllText(path: Path.Combine(path1: project, path2: "corpora.json"), contents: $$"""
            [
              { "Name": "nes6502-sst", "Version": "fixture", "Archive": "https://example.invalid/sst.zip", "Sha256": "{{new string(c: '0', count: 64)}}" },
              { "Name": "nes-test-roms", "Version": "fixture", "Archive": "https://example.invalid/tests.zip", "Sha256": "{{new string(c: '0', count: 64)}}" },
              { "Name": "accuracy-coin", "Version": "fixture", "UrlPrefix": "https://example.invalid/",
                "Files": [{ "Path": "AccuracyCoin.nes", "Sha256": "{{Convert.ToHexString(inArray: SHA256.HashData(source: image))}}" }] }
            ]
            """);

        return root;
    }
    private static async Task<JsonElement> Run(string root, int exitCode, string[]? arguments = null) {
        var artifacts = Path.Combine(path1: root, path2: "artifacts");
        var start = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };

        foreach (var argument in new[] {
            Path.Combine(path1: AppContext.BaseDirectory, path2: "Puck.HumbleGamingDeck.Post.dll"),
            "--filter", "accuracy-coin", "--corpus-cache", Path.Combine(path1: root, path2: "cache"), "--artifacts", artifacts,
        }.Concat(second: (arguments ?? []))) {
            start.ArgumentList.Add(item: argument);
        }
        using var process = Process.Start(startInfo: start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);

        try {
            await process.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);
        } finally {
            if (!process.HasExited) {
                try {
                    process.Kill(entireProcessTree: true);
                } catch (InvalidOperationException) when (process.HasExited) {
                }
            }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None);
        }
        var stdout = await output;
        var stderr = await error;

        Assert.True(condition: (process.ExitCode == exitCode), userMessage: $"Expected exit {exitCode}, got {process.ExitCode}\n{stdout}\n{stderr}");
        Assert.True(condition: File.Exists(path: Path.Combine(path1: artifacts, path2: "results.junit.xml")));
        using var summary = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: artifacts, path2: "summary.json")));

        return summary.RootElement.GetProperty(propertyName: "Stages")[0].Clone();
    }
}
