using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Puck.Cli.Test;
using Puck.Testing;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a <c>puck test</c> leg is bounded by its host's progress, never by how long its ticks take. A
/// host that keeps advancing, however slowly the machine lets it tick, runs to its export tick; it is never handed an
/// exit that ends the run first. The host here is a stand-in World compiled for the law: it answers each
/// <c>world.wait</c> as it starts it, spends a fixed wall time on every tick, writes its schedule manifest and state
/// export at the export tick, and, as the real host does, ends at its own <c>--exit-after-seconds</c>, exporting the tick
/// it reached. Its ticks are slow enough that the run outlasts twenty seconds plus the export tick at the document's
/// rate.
/// </summary>
public sealed class TestLegProgressLawTests {
    private const ulong ExportTick = 30;
    private const int RateHz = 30;
    private const int TickMilliseconds = 750;

    private static readonly MetadataReference[] References = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(separator: Path.PathSeparator)
        .Select(selector: static path => MetadataReference.CreateFromFile(path: path))
        .ToArray<MetadataReference>();

    // The stand-in host's program: the console answers and the schedule outputs a leg reads, at a fixed wall cost per
    // tick.
    private static string Program() => $$"""
        using System.Diagnostics;

        var schedule = string.Empty;
        var exitAfter = 0;

        for (var index = 0; (index < (args.Length - 1)); index++) {
            if (args[index] == "--schedule-dir") { schedule = args[index + 1]; }
            if (args[index] == "--exit-after-seconds") { exitAfter = int.Parse(args[index + 1]); }
        }

        var clock = Stopwatch.StartNew();
        var tick = 0UL;
        var exported = false;

        void Export() {
            if (exported) { return; }
            exported = true;
            Directory.CreateDirectory(schedule);
            File.WriteAllText(Path.Combine(schedule, "{{WorldScheduleSection.ManifestFileName}}"), "{\"exportTick\":" + tick + "}");
            File.WriteAllText(Path.Combine(schedule, "{{WorldScheduleSection.ExportFileName}}"), "{\"tick\":" + tick + "}");
        }

        string? line;

        while ((line = Console.ReadLine()) is not null) {
            if (line.StartsWith("world.wait ")) {
                var ticks = ulong.Parse(line.Substring(11));

                Console.WriteLine("[world.wait: " + ticks + " ticks from " + tick + " — releasing at tick " + (tick + ticks) + "]");
                for (var step = 0UL; (step < ticks); step++) {
                    if ((exitAfter > 0) && (clock.Elapsed.TotalSeconds >= exitAfter)) { Export(); return 0; }
                    Thread.Sleep({{TickMilliseconds.ToString(provider: CultureInfo.InvariantCulture)}});
                    tick++;
                    if (tick == {{ExportTick.ToString(provider: CultureInfo.InvariantCulture)}}UL) { Export(); }
                }
            } else if (line == "quit") {
                Export();
                Console.WriteLine("[quit: exiting]");
                return 0;
            } else {
                Console.WriteLine("[" + line + "]");
            }
        }

        Export();
        return 0;
        """;
    private static string StandInWorld(TemporaryDirectory scratch) {
        var compilation = CSharpCompilation.Create(
            assemblyName: "StandInWorld",
            options: new CSharpCompilationOptions(outputKind: OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable),
            references: References,
            syntaxTrees: [
                CSharpSyntaxTree.ParseText(text: Program()),
                CSharpSyntaxTree.ParseText(text: "global using System; global using System.IO; global using System.Threading;"),
            ]
        );
        var assembly = scratch.PathOf(name: "host/StandInWorld.dll");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: assembly)!);

        var result = compilation.Emit(outputPath: assembly);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        _ = scratch.WriteText(name: "host/StandInWorld.runtimeconfig.json", text: $$"""
            { "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "{{Environment.Version.ToString(fieldCount: 3)}}" } } }
            """);

        return assembly;
    }

    [Fact]
    public void AHostThatKeepsAdvancingIsNeverCutShortByWallTime() {
        using var scratch = new TemporaryDirectory(prefix: "puck-test-leg-");
        using var error = new StringWriter();
        var advanced = TestCommand.TryRunLeg(
            artifact: StandInWorld(scratch: scratch),
            error: error,
            exportTick: ExportTick,
            legDirectory: scratch.PathOf(name: "leg"),
            rateHz: RateHz,
            reading: out var reading,
            world: scratch.WriteText(name: "world.json", text: "{}")
        );

        Assert.True(condition: advanced, userMessage: error.ToString());
        Assert.Equal(expected: ExportTick, actual: reading!.ExportTick);
    }
}
