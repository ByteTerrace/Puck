using Xunit;

namespace Puck.World.Tests;

/// <summary>The deterministic core narrates through <see cref="Puck.World.Server.WorldOutputHub"/>; it never
/// writes to the console itself. The one console writer under <c>Puck.World.Server</c> is the sink a composition
/// root binds, so a browser or silo host can bind another. This holds the source tree to that, file by file, until
/// the architecture gate's console denial for the project is armed.</summary>
public sealed class ServerConsoleLawTests {
    private static readonly string[] s_writers = ["WorldNarration.cs"];

    [Fact]
    public void OnlyTheBoundSinkWritesToTheConsole() {
        var root = Path.Combine(AuthoredGameFixtures.Root, "src", "Puck.World.Server");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)) {
            if (s_writers.Contains(Path.GetFileName(path), StringComparer.Ordinal)) {
                continue;
            }

            var lines = File.ReadAllLines(path);

            for (var index = 0; index < lines.Length; index++) {
                var line = lines[index].TrimStart();

                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal)) {
                    continue;
                }
                if (line.Contains("Console.Error.Write", StringComparison.Ordinal) || line.Contains("Console.Out.Write", StringComparison.Ordinal) || line.Contains("Console.Write", StringComparison.Ordinal)) {
                    offenders.Add($"{Path.GetFileName(path)}:{index + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"the simulation core writes to the console at {string.Join(", ", offenders)}; narrate through WorldOutputHub instead");
    }
}
