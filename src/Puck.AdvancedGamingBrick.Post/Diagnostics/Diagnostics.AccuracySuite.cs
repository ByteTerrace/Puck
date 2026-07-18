using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick.Post;

// --accuracy-suite <rom>: the menu-driven accuracy suite, run headlessly.
internal static partial class Diagnostics {
    /// <summary>
    /// Runs the menu-driven accuracy suite head-lessly: a <see cref="SuiteDebugBus"/> emulates the debug-log register
    /// (so the suite prints each category's "BEGIN:"/"END: passes/total") and injects the controller input that drives
    /// the menu — press A to run each suite, read its result, press B then Down to advance. Returns the number of
    /// suites with at least one failing subtest.
    /// </summary>
    public static int RunAccuracySuite(string romPath, string name) {
        if (!File.Exists(path: romPath)) {
            Console.WriteLine(value: $"  [SKIP] {name}: not found at {romPath}");

            return 0;
        }

        const long frameCycles = 280_896; // one AGB frame
        var logs = new List<string>();

        using var instance = AgbMachineFactory.Create(
            configuration: new AgbMachineConfiguration(bios: BiosImage, rom: File.ReadAllBytes(path: romPath)),
            compose: services => {
                services.AddScoped<AgbBus>();
                services.AddScoped<IAgbBus>(implementationFactory: sp => new SuiteDebugBus(
                    inner: sp.GetRequiredService<AgbBus>(),
                    onLog: (level, text) => logs.Add(item: text)));
            });
        var machine = instance.Machine;
        var bus = instance.GetRequiredService<AgbBus>();
        var debug = (SuiteDebugBus)instance.GetRequiredService<IAgbBus>();

        machine.DirectBoot();

        void StepCycles(long cycles) {
            var target = (bus.Cycles + cycles);

            while (bus.Cycles < target) {
                machine.Step();
            }
        }

        void Press(ushort keyMask) {
            debug.Keys = (ushort)(0x3FFu & ~keyMask);
            StepCycles(cycles: (frameCycles * 3));
            debug.Keys = 0x3FF;
            StepCycles(cycles: (frameCycles * 5));
        }

        const ushort keyA = 0x1, keyB = 0x2, keyDown = 0x80;

        // Let the suite clear SRAM, set up, and reach its menu.
        StepCycles(cycles: (frameCycles * 40));

        const int suiteCount = AccuracySuiteCount;
        var passedSuites = 0;
        var failedSuites = 0;

        Console.WriteLine(value: $"  == {name} ==");

        for (var i = 0; (i < suiteCount); ++i) {
            var before = logs.Count;

            Press(keyMask: keyA); // run the selected suite

            // Wait for the "END: passes/total" line (slow suites take many frames).
            var endLine = (string?)null;

            for (var frame = 0; ((frame < 1200) && (endLine is null)); ++frame) {
                StepCycles(cycles: frameCycles);

                for (var l = before; (l < logs.Count); ++l) {
                    if (logs[l].StartsWith(value: "END:", comparisonType: StringComparison.Ordinal)) {
                        endLine = logs[l];

                        break;
                    }
                }
            }

            var beginLine = logs.Skip(count: before).FirstOrDefault(predicate: s => s.StartsWith(value: "BEGIN:", comparisonType: StringComparison.Ordinal));
            var suiteName = (beginLine?.Substring(startIndex: 6).Trim() ?? $"suite #{i}");

            if (endLine is null) {
                Console.WriteLine(value: $"    [????] {suiteName,-20} no result (timed out)");
                ++failedSuites;
            } else {
                // "END: passes/total"
                var slash = endLine.IndexOf(value: '/');
                var passes = int.Parse(endLine.AsSpan(start: 4, length: (slash - 4)).Trim());
                var total = int.Parse(endLine.AsSpan(start: (slash + 1)).Trim());
                var ok = ((passes == total) && (total > 0));

                Console.WriteLine(value: $"    [{(ok ? "PASS" : "FAIL")}] {suiteName,-20} {passes}/{total}");

                if (ok) {
                    ++passedSuites;
                } else {
                    ++failedSuites;

                    // Per-subtest detail: the suite logs each FAILING subtest between BEGIN: and END:. Dump them
                    // (gated/focusable via PUCK_AGB_SUITE_FOCUS=<substring>) so we can fix real mechanism failures rather
                    // than guess from the aggregate score. Capped to keep the output readable.
                    var focus = Environment.GetEnvironmentVariable(variable: "PUCK_AGB_SUITE_FOCUS");
                    var wantDetail = ((focus is null) || suiteName.Contains(value: focus, comparisonType: StringComparison.OrdinalIgnoreCase));

                    if (wantDetail) {
                        var shown = 0;

                        for (var l = before; ((l < logs.Count) && (shown < 80)); ++l) {
                            var text = logs[l];

                            // Show only failures: a suite's early sub-tests often pass, and the PASS debug lines would
                            // otherwise consume the cap before any FAIL detail (the "Got X vs Y" offset that tells us
                            // the actual cycle/value error) is reached.
                            if (text.StartsWith(value: "BEGIN:", comparisonType: StringComparison.Ordinal)
                                || text.StartsWith(value: "END:", comparisonType: StringComparison.Ordinal)
                                || (text.Length == 0)
                                || !text.Contains(value: "FAIL", comparisonType: StringComparison.Ordinal)) {
                                continue;
                            }

                            Console.WriteLine(value: $"        · {text}");
                            ++shown;
                        }
                    }
                }
            }

            Press(keyMask: keyB);    // back to the menu
            Press(keyMask: keyDown); // next suite
        }

        Console.WriteLine(value: $"  == {name}: {passedSuites}/{suiteCount} suites fully passed ==");

        return failedSuites;
    }
}
