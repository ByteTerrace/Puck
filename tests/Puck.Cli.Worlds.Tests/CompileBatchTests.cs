using System.Text.Json;
using Puck.Cli.Transpiler;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

public sealed class CompileBatchTests {
    [Fact]
    public async Task BatchPreservesOrderAndPerSourceLoweringAsync() {
        using var directory = new TemporaryDirectory(prefix: "puck-compile batch ");

        var first = Path.Combine(path1: directory.RootPath, path2: "first.puck");
        var second = Path.Combine(path1: directory.RootPath, path2: "second.puck");

        File.WriteAllText(contents: "let marker = 1\nstate { ints [{ name: \"counter\", value: marker }] }", path: first);
        File.WriteAllText(contents: "let marker = 2\nimports [\"first.world.json\"]\nstate { ints [{ name: \"second\", value: marker }] }", path: second);
        Assert.Equal(0, await SuiteRoot.InvokeAsync(args: ["compile", first, second]));
        var firstOutput = Path.ChangeExtension(extension: ".world.json", path: first);
        var secondOutput = Path.ChangeExtension(extension: ".world.json", path: second);
        using var one = JsonDocument.Parse(File.ReadAllBytes(path: firstOutput));
        using var two = JsonDocument.Parse(File.ReadAllBytes(path: secondOutput));

        Assert.Equal(1, one.RootElement.GetProperty(propertyName: "state").GetProperty(propertyName: "ints")[0].GetProperty(propertyName: "value").GetInt32());
        Assert.Equal(2, two.RootElement.GetProperty(propertyName: "state").GetProperty(propertyName: "ints")[0].GetProperty(propertyName: "value").GetInt32());
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

        File.WriteAllText(contents: "state { ints [] }", path: first);
        File.WriteAllText(contents: "state { ints [] }", path: last);
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
