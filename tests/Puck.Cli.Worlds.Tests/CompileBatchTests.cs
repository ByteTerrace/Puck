using System.Text.Json;
using Puck.Cli.Transpiler;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

public sealed class CompileBatchTests {
    [InlineData("sql { k = \"v\" }", "sql")]
    [InlineData("sql { k: \"v\" }", "sql")]
    [InlineData("host { mystery: 1 }", "mystery")]
    [InlineData("let bad = { mystery: 1 }\nhost: bad", "mystery")]
    [InlineData("template bad() { sql { k: 1 } }\nbad()", "sql")]
    [Theory]
    public void UnknownMembersRefuseBeforeOutputWithEitherValidationSetting(string body, string member) {
        using var directory = new TemporaryDirectory(prefix: "puck-compile-unknown-");
        var path = directory.WriteText(name: "unknown.puck", text: body);

        foreach (var validate in new[] { false, true }) {
            var (exit, output) = ConsoleCapture.Run(run: () => CompileCommand.Run(
                path: path, output: null, watch: false, strict: false, validate: validate));
            Assert.Equal(actual: exit, expected: 1);
            Assert.Contains(actualString: output, expectedSubstring: "PUCK030");
            Assert.Contains(actualString: output, expectedSubstring: $"'{member}'");
            Assert.False(condition: File.Exists(path: Path.ChangeExtension(extension: ".world.json", path: path)));
        }
    }
    [Fact]
    public async Task BatchPreservesOrderAndPerSourceLoweringAsync() {
        using var directory = new TemporaryDirectory(prefix: "puck-compile batch ");

        var first = Path.Combine(path1: directory.RootPath, path2: "first.puck");
        var second = Path.Combine(path1: directory.RootPath, path2: "second.puck");

        File.WriteAllText(contents: "let marker = 1\nhost { height: marker }", path: first);
        File.WriteAllText(contents: "let marker = 2\nimports [\"first.world.json\"]\nhost { height: marker }", path: second);
        Assert.Equal(0, await SuiteRoot.InvokeAsync(args: ["compile", first, second]));
        var firstOutput = Path.ChangeExtension(extension: ".world.json", path: first);
        var secondOutput = Path.ChangeExtension(extension: ".world.json", path: second);
        using var one = JsonDocument.Parse(File.ReadAllBytes(path: firstOutput));
        using var two = JsonDocument.Parse(File.ReadAllBytes(path: secondOutput));

        Assert.Equal(1, one.RootElement.GetProperty(propertyName: "host").GetProperty(propertyName: "height").GetInt32());
        Assert.Equal(2, two.RootElement.GetProperty(propertyName: "host").GetProperty(propertyName: "height").GetInt32());
        var single = Path.Combine(path1: directory.RootPath, path2: "single.json");

        Assert.Equal(0, await SuiteRoot.InvokeAsync(args: ["compile", second, "--output", single]));
        Assert.Equal(File.ReadAllBytes(path: single), File.ReadAllBytes(path: secondOutput));
    }
    [Fact]
    public async Task BatchStopsBeforeWritingSourcesAfterTheFirstFailureAsync() {
        using var directory = new TemporaryDirectory(prefix: "puck-compile failure ");

        var first = Path.Combine(path1: directory.RootPath, path2: "first.puck");
        var missing = Path.Combine(path1: directory.RootPath, path2: "missing.puck");
        var last = Path.Combine(path1: directory.RootPath, path2: "last.puck");

        File.WriteAllText(contents: "host { height: 720 }", path: first);
        File.WriteAllText(contents: "host { height: 720 }", path: last);
        Assert.Equal(2, await SuiteRoot.InvokeAsync(args: ["compile", first, missing, last]));
        Assert.True(condition: File.Exists(path: Path.ChangeExtension(extension: ".world.json", path: first)));
        Assert.False(condition: File.Exists(path: Path.ChangeExtension(extension: ".world.json", path: last)));
    }
    [Fact]
    public void BatchRefusesAmbiguousOutputAndWatchBeforeExecution() {
        Assert.NotEmpty(collection: CompileCommand.Create().Parse(["a.puck", "b.puck", "--output", "shared.json"]).Errors);
        Assert.NotEmpty(collection: CompileCommand.Create().Parse(["a.puck", "b.puck", "--watch"]).Errors);
    }
}
