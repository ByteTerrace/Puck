using System.Text.Json;
using Puck.Cli.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CompileBatchTests {
    [Fact]
    public async Task BatchPreservesOrderAndPerSourceLoweringAsync() {
        var directory = Directory.CreateTempSubdirectory("puck compile batch ");
        try {
            var first = Path.Combine(directory.FullName, "first.puck");
            var second = Path.Combine(directory.FullName, "second.puck");
            File.WriteAllText(first, "let marker = 1\nstate { ints [{ name: \"counter\", value: marker }] }");
            File.WriteAllText(second, "let marker = 2\nimports [\"first.world.json\"]\nstate { ints [{ name: \"second\", value: marker }] }");
            Assert.Equal(0, await PuckRootCommand.InvokeAsync(["compile", first, second]));
            var firstOutput = Path.ChangeExtension(first, ".world.json");
            var secondOutput = Path.ChangeExtension(second, ".world.json");
            using var one = JsonDocument.Parse(File.ReadAllBytes(firstOutput));
            using var two = JsonDocument.Parse(File.ReadAllBytes(secondOutput));
            Assert.Equal(1, one.RootElement.GetProperty("state").GetProperty("ints")[0].GetProperty("value").GetInt32());
            Assert.Equal(2, two.RootElement.GetProperty("state").GetProperty("ints")[0].GetProperty("value").GetInt32());
            var single = Path.Combine(directory.FullName, "single.json");
            Assert.Equal(0, await PuckRootCommand.InvokeAsync(["compile", second, "--output", single]));
            Assert.Equal(File.ReadAllBytes(single), File.ReadAllBytes(secondOutput));
        } finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task BatchStopsBeforeWritingSourcesAfterTheFirstFailureAsync() {
        var directory = Directory.CreateTempSubdirectory("puck compile failure ");
        try {
            var first = Path.Combine(directory.FullName, "first.puck");
            var missing = Path.Combine(directory.FullName, "missing.puck");
            var last = Path.Combine(directory.FullName, "last.puck");
            File.WriteAllText(first, "state { ints [] }");
            File.WriteAllText(last, "state { ints [] }");
            Assert.Equal(2, await PuckRootCommand.InvokeAsync(["compile", first, missing, last]));
            Assert.True(File.Exists(Path.ChangeExtension(first, ".world.json")));
            Assert.False(File.Exists(Path.ChangeExtension(last, ".world.json")));
        } finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void BatchRefusesAmbiguousOutputAndWatchBeforeExecution() {
        Assert.NotEmpty(CompileCommand.Create().Parse(["a.puck", "b.puck", "--output", "shared.json"]).Errors);
        Assert.NotEmpty(CompileCommand.Create().Parse(["a.puck", "b.puck", "--watch"]).Errors);
    }
}
