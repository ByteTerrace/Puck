using System.CommandLine;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Request-level timings use the ordinary production entrypoints. Work profiles run afterwards, separately.
internal static class PrimeRequestSurvey {
    private const int DrawBudget = 2048;

    // Power-of-ten counts are independently tabulated in https://github.com/kimwalisch/primecount#benchmarks.
    private static readonly RequestCase[] Cases = [
        new("nth-table-10", "nth", 10, 29, Narrow: true),
        new("nth-table-1000", "nth", 1000, 7919, Narrow: true),
        new("nth-uint-1m", "nth", 1_000_000, 15_485_863, Narrow: true),
        new("nth-uint-100m", "nth", 100_000_000, 2_038_074_743, Narrow: true),
        new("nth-ulong-1b", "nth", 1_000_000_000, 22_801_763_489),
        new("nth-ulong-10b", "nth", 10_000_000_000, 252_097_800_623),
        new("nth-checkpoint-before", "nth", 17_094_432_576_650),
        new("nth-checkpoint-after", "nth", 17_094_432_576_906),
        new("nth-last", "nth", 425_656_284_035_217_743, 18_446_744_073_709_551_557),
        new("count-1e14", "count", 100_000_000_000_000, 3_204_941_750_802, Expensive: true),
        new("count-1e16", "count", 10_000_000_000_000_000, 279_238_341_033_925, Expensive: true),
        new("count-1e19", "count", 10_000_000_000_000_000_000, 234_057_667_276_344_607, Expensive: true),
        new("nth-value-1e14", "nth", 3_204_941_750_802, Expensive: true),
        new("nth-value-1e16", "nth", 279_238_341_033_925, Expensive: true),
        new("nth-value-1e19", "nth", 234_057_667_276_344_607, Expensive: true),
        new("nth-rank-1e14", "nth", 100_000_000_000_000, Expensive: true),
        new("nth-rank-1e16", "nth", 10_000_000_000_000_000, Expensive: true),
        new("random-small", "random", Low: 2, High: 65_535),
        new("random-uint", "random", Low: (1UL << 31), High: uint.MaxValue),
        new("random-ulong", "random", Low: (1UL << 63), High: ulong.MaxValue),
        new("random-high-window", "random", Low: 1_000_000_000_000_000_000, High: 1_000_000_000_000_999_999),
    ];

    public static Command Create(TimeProvider clock) {
        var cases = new Option<string[]>("--cases") {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            Description = "Case names (space or comma separated), default, or all. Expensive cases require explicit selection.",
        };
        var samples = new Option<int>("--samples") { DefaultValueFactory = _ => 3, Description = "Warm measured samples after the separately recorded first call, 1..100." };
        var warmups = new Option<int>("--warmups") { DefaultValueFactory = _ => 1, Description = "Additional untimed warmups after the first call, 0..100." };
        var batch = new Option<int?>("--batch") { Description = "Requests per sample, 1..1000000. Defaults: table 10000, random 128, other 1." };
        var cpu = new Option<int?>("--cpu") { Description = "Windows logical CPU in 0..63; restores original process affinity afterwards." };
        var seeds = new Option<ulong[]>("--seeds") {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => [42UL, 2026UL, 0xD00DFEEDUL],
            Description = "Identical PCG seeds for all four random implementations; stream 54.",
        };
        var timeout = new Option<double>("--timeout-seconds") { DefaultValueFactory = _ => 120D, Description = "Cooperative deadline for each case/implementation/seed including first, warm, and profile passes." };
        var profile = new Option<bool>("--work-profile") { DefaultValueFactory = _ => true, Description = "Run the separate untimed production work profile; false skips it for expensive cases." };
        var list = new Option<bool>("--list") { Description = "List selectable cases and their opt-in status without executing prime requests." };
        var output = new Option<string?>("--output") { Description = "Parent directory for a unique run directory; report.json is flushed after each completed phase." };
        var command = new Command(description: "Measure nth/count/random prime requests serially, with separate exact work profiles and matched random controls.", name: "prime-requests") {
            cases, samples, warmups, batch, cpu, seeds, timeout, profile, list, output,
        };

        command.SetAction(action: parse => Run(names: (parse.GetValue(option: cases) ?? []), samples: parse.GetValue(option: samples), warmups: parse.GetValue(option: warmups),
            batch: parse.GetValue(option: batch), cpu: parse.GetValue(option: cpu), seeds: parse.GetValue(option: seeds)!, timeoutSeconds: parse.GetValue(option: timeout), workProfile: parse.GetValue(option: profile),
            list: parse.GetValue(option: list), output: parse.GetValue(option: output), clock: clock));
        return command;
    }

    private static int Run(string[] names, int samples, int warmups, int? batch, int? cpu, ulong[] seeds, double timeoutSeconds, bool workProfile, bool list, string? output, TimeProvider clock) {
        int Survey() => RunSurvey(batch: batch, clock: clock, cpu: cpu, list: list, names: names, output: output,
            samples: samples, seeds: seeds, timeoutSeconds: timeoutSeconds, warmups: warmups, workProfile: workProfile);

        if (cpu is not { } index) { return Survey(); }
        if (!OperatingSystem.IsWindows() || (((uint)index) >= 64)) {
            Console.Error.WriteLine(value: "prime-requests: cpu must be an available Windows logical CPU in 0..63.");
            return 2;
        }
        using var process = Process.GetCurrentProcess();
        var original = process.ProcessorAffinity;
        var selected = ((nint)(1UL << index));

        if ((original & selected) == 0) {
            Console.Error.WriteLine(value: "prime-requests: selected CPU is outside the process affinity mask.");
            return 2;
        }
        process.ProcessorAffinity = selected;
        try { return Survey(); } finally { process.ProcessorAffinity = original; }
    }
    private static int RunSurvey(string[] names, int samples, int warmups, int? batch, int? cpu, ulong[] seeds, double timeoutSeconds, bool workProfile, bool list, string? output, TimeProvider clock) {
        if (list) {
            foreach (var item in Cases) { Console.WriteLine(value: $"{item.Name}: {item.Kind}; argument {item.Argument}; {(item.Expensive ? "opt-in" : "default")}"); }
            return 0;
        }
        var requested = names.SelectMany(selector: name => name.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ',')).ToArray();

        if ((samples is < 1 or > 100) || (warmups is < 0 or > 100) || (batch is < 1 or > 1_000_000) || (seeds.Length == 0) ||
            !double.IsFinite(d: timeoutSeconds) || (timeoutSeconds <= 0) || (timeoutSeconds > 86_400) ||
            requested.Any(predicate: name => ((name != "default") && (name != "all") && !Cases.Any(predicate: item => (item.Name == name))))) {
            Console.Error.WriteLine(value: "prime-requests: require known cases, samples 1..100, warmups 0..100, batch 1..1000000, seeds and finite timeout in (0,86400].");
            return 2;
        }
        var selected = Cases.Where(predicate: item => (requested.Contains(value: "all") || requested.Contains(value: item.Name) ||
            (((requested.Length == 0) || requested.Contains(value: "default")) && !item.Expensive))).ToArray();
        var directory = PuckPaths.Normalize(path: Path.GetFullPath(path: Path.Combine(path1: (output ?? "artifacts/prime-requests"), path2: Guid.NewGuid().ToString(format: "N"))));

        Directory.CreateDirectory(path: directory);
        var rows = new List<PrimeRequestSurveyRow>();
        var report = new PrimeRequestSurveyReport(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, cpu, Affinity(), Hash(path: typeof(PrimeExploration).Assembly.Location),
            Hash(path: typeof(PrimeRequestSurvey).Assembly.Location), samples, warmups, timeoutSeconds, DrawBudget, 256, "64-bit range-reduction words; each consumes two NextUInt32 draws",
            "First is one invocation of this case in this process, not guaranteed global cold initialization. Warm samples may batch requests and run ordinary uninstrumented production entrypoints. Per-request times include the shared batch loop, dispatch and cancellation polling; cheap rows are harness-inclusive, not isolated instruction latency. AllocationWindow.Total surrounds the same timed batch and adds no repeated long call; allocations are calling-thread managed bytes, excluding native memory and retained pools. Work is a separate untimed invocation of production kernels; units are algorithmic events, not CPU cycles. Random variants use identical bounds, seeds, stream 54, request counts and budgets of 2048 raw 64-bit words (at most 4096 NextUInt32 draws), overriding production's default 256-word budget. Wheel-mr and production share the sampler and filters through 163; only the final wide-candidate decision differs. Their result checksums and profiled draw counts must agree. Exact PCG state distance counts 32-bit draws; candidate decisions are only bounded above by half that count unless a small-table route proves zero. Unknown fine-grained marking/primality counts are not inferred. Instrumented results must match ordinary results. Cooperative cancellation preserves completed rows; timers may overrun until the next cancellation poll.",
            new Dictionary<string, string?> {
                ["DOTNET_TieredCompilation"] = Environment.GetEnvironmentVariable(variable: "DOTNET_TieredCompilation"),
                ["DOTNET_TieredPGO"] = Environment.GetEnvironmentVariable(variable: "DOTNET_TieredPGO"),
                ["DOTNET_ReadyToRun"] = Environment.GetEnvironmentVariable(variable: "DOTNET_ReadyToRun"),
                ["COMPlus_TieredCompilation"] = Environment.GetEnvironmentVariable(variable: "COMPlus_TieredCompilation"),
                ["COMPlus_TieredPGO"] = Environment.GetEnvironmentVariable(variable: "COMPlus_TieredPGO"),
            }, rows);
        var failed = false;
        var firstProcessRequest = true;

        Save();
        foreach (var item in selected) {
            var implementations = ((item.Kind == "random") ? new[] { "integer-mr", "integer-bpsw", "wheel-mr", "production" } : ["production"]);
            var caseSeeds = ((item.Kind == "random") ? seeds : [0UL]);
            var requests = (batch ?? ((item.Kind == "random") ? 128 : (item.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "nth-table") ? 10_000 : 1)));

            foreach (var seed in caseSeeds) {
                PrimeRequestSurveyRow? matchedWheel = null;

                foreach (var implementation in implementations) {
                    var row = new PrimeRequestSurveyRow(item.Name, item.Kind, implementation, item.Argument, item.Low, item.High, seed, requests, firstProcessRequest);

                    firstProcessRequest = false;
                    rows.Add(item: row);
                    Save();
                    Console.WriteLine(value: $"prime-requests: {item.Name}/{implementation}/seed{seed}, batch{requests}; {directory}");
                    using var deadline = new CancellationTokenSource(delay: TimeSpan.FromSeconds(value: timeoutSeconds), timeProvider: clock);

                    try {
                        var first = Measure(item: item, implementation: implementation, seed: seed, requests: 1, cancellationToken: deadline.Token);
                        ulong? batchResult = ((item.Kind == "random") ? null : first.Result);

                        row.Result = first.Result;
                        row.First = first;
                        row.Status = "first-complete";
                        Save();
                        for (var warmup = 0; (warmup < warmups); ++warmup) {
                            var result = Execute(item: item, implementation: implementation, seed: seed, requests: requests, cancellationToken: deadline.Token);

                            if (batchResult is { } expected) { RequireEqual(actual: result.Result, expected: expected); }
                            batchResult = result.Result;
                        }
                        for (var sample = 0; (sample < samples); ++sample) {
                            var measurement = Measure(item: item, implementation: implementation, seed: seed, requests: requests, cancellationToken: deadline.Token);

                            if (batchResult is { } expected) { RequireEqual(expected: expected, actual: measurement.Result); }
                            batchResult = measurement.Result;
                            row.Result = measurement.Result;
                            row.Samples.Add(item: measurement);
                            row.Status = "samples-in-progress";
                            Save();
                        }
                        var ordered = row.Samples.Select(selector: value => value.MillisecondsPerRequest).Order().ToArray();

                        row.MedianMilliseconds = (((ordered.Length & 1) == 0) ? ((ordered[((ordered.Length / 2) - 1)] + ordered[(ordered.Length / 2)]) / 2) : ordered[(ordered.Length / 2)]);
                        if (workProfile) {
                            row.Status = "profiling";
                            Save();
                            Console.WriteLine(value: $"prime-requests: profiling {item.Name}/{implementation}/seed{seed}");
                            if (item.Kind == "random") {
                                var result = ExecuteRandom(item: item, implementation: implementation, seed: seed, requests: requests, verify: true, cancellationToken: deadline.Token);

                                RequireEqual(expected: batchResult!.Value, actual: result.Result);
                                var smallTable = (((implementation is "production" or "wheel-mr") && (item.High <= 65535)));

                                row.RandomWork = new PrimeRandomWork(CandidateDecisionUpperBound: (smallTable ? 0 : (result.Draws / 2)), Coverage: (smallTable ? "prime-table; zero decisions" : "candidate decision upper bound; range-reduction rejections may consume draws"), UInt32Draws: result.Draws,
                                    UInt64Draws: (result.Draws / 2));
                            } else {
                                row.Work = ((item.Kind == "nth")
                                    ? PrimeExtensions.ProfileNthPrime(value: (item.Argument - 1), cancellationToken: deadline.Token)
                                    : PrimeExtensions.ProfilePrimeCountingFunction(value: item.Argument, cancellationToken: deadline.Token));
                                RequireEqual(expected: first.Result, actual: row.Work.Result);
                            }
                        }
                        if ((implementation == "production") && (matchedWheel is not null)) {
                            RequireEqual(expected: matchedWheel.First!.Result, actual: first.Result);
                            RequireEqual(expected: matchedWheel.Result!.Value, actual: row.Result!.Value);
                            if ((matchedWheel.RandomWork is { } wheelWork) && (row.RandomWork is { } productionWork)) {
                                RequireEqual(expected: wheelWork.UInt32Draws, actual: productionWork.UInt32Draws);
                            }
                        }
                        row.Status = "complete";
                        if (implementation == "wheel-mr") { matchedWheel = row; }
                        Console.WriteLine(value: $"prime-requests: {item.Name}/{implementation}/seed{seed}: {row.MedianMilliseconds:F6} ms/request; result{row.Result}");
                    } catch (OperationCanceledException) {
                        row.Status = "timed-out";
                        row.Error = "Cooperative per-case deadline exceeded; completed measurements are retained.";
                        failed = true;
                        Console.WriteLine(value: $"prime-requests: timed out {item.Name}/{implementation}/seed{seed}");
                    } catch (Exception exception) when ((exception is InvalidOperationException or ArgumentException or ArithmeticException)) {
                        row.Status = "failed";
                        row.Error = exception.Message;
                        failed = true;
                        Console.Error.WriteLine(value: $"prime-requests: {item.Name}: {exception.Message}");
                    }
                    Save();
                }
            }
        }
        Console.WriteLine(value: $"prime-requests: report {directory}/report.json");
        return (failed ? 1 : 0);

        void Save() => File.WriteAllText(path: $"{directory}/report.json", contents: JsonSerializer.Serialize(value: report, jsonTypeInfo: PrimeRequestSurveyJsonContext.Default.PrimeRequestSurveyReport));
    }
    private static PrimeRequestMeasurement Measure(RequestCase item, string implementation, ulong seed, int requests, CancellationToken cancellationToken) {
        var result = (Result: 0UL, Checksum: 0UL);
        var elapsed = 0D;
        var allocated = AllocationWindow.Total(window: () => {
            var start = Stopwatch.GetTimestamp();

            result = Execute(cancellationToken: cancellationToken, implementation: implementation, item: item, requests: requests, seed: seed);
            elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start).TotalMilliseconds;
        });

        if (item.Expected is { } expected) { RequireEqual(actual: result.Result, expected: expected); }
        return new PrimeRequestMeasurement(AllocatedBytes: allocated, AllocatedBytesPerRequest: (((double)allocated) / requests), Checksum: result.Checksum, Milliseconds: elapsed, MillisecondsPerRequest: (elapsed / requests), Requests: requests, Result: result.Result);
    }
    private static (ulong Result, ulong Checksum) Execute(RequestCase item, string implementation, ulong seed, int requests, CancellationToken cancellationToken) {
        if (item.Kind == "random") {
            var random = ExecuteRandom(cancellationToken: cancellationToken, implementation: implementation, item: item, requests: requests, seed: seed, verify: false);

            return (random.Result, random.Result);
        }
        var result = 0UL;
        var checksum = 0UL;

        for (var index = 0; (index < requests); ++index) {
            cancellationToken.ThrowIfCancellationRequested();
            result = ((item.Kind == "count") ? item.Argument.PrimeCountingFunction(cancellationToken: cancellationToken)
                : (item.Narrow ? ((uint)(item.Argument - 1)).NthPrime() : (item.Argument - 1).NthPrime(cancellationToken: cancellationToken)));
            checksum = BitOperations.RotateLeft(offset: 3, value: checksum) ^ result;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (result, checksum);
    }
    private static (ulong Result, ulong Draws) ExecuteRandom(RequestCase item, string implementation, ulong seed, int requests, bool verify, CancellationToken cancellationToken) {
        return implementation switch {
            "integer-mr" => ExecuteRandom<IntegerMillerRequest>(cancellationToken: cancellationToken, item: item, requests: requests, seed: seed, verify: verify),
            "integer-bpsw" => ExecuteRandom<IntegerBaillieRequest>(cancellationToken: cancellationToken, item: item, requests: requests, seed: seed, verify: verify),
            "wheel-mr" => ExecuteRandom<WheelMillerRequest>(cancellationToken: cancellationToken, item: item, requests: requests, seed: seed, verify: verify),
            _ => ExecuteRandom<ProductionRandomRequest>(cancellationToken: cancellationToken, item: item, requests: requests, seed: seed, verify: verify),
        };
    }
    private static (ulong Result, ulong Draws) ExecuteRandom<TRequest>(RequestCase item, ulong seed, int requests, bool verify, CancellationToken cancellationToken)
        where TRequest : struct, IRandomRequest {
        var generator = Pcg32XshRr.Create(state: seed, stream: 54);
        var initial = generator;
        var checksum = 0UL;

        for (var index = 0; (index < requests); ++index) {
            cancellationToken.ThrowIfCancellationRequested();
            var found = TRequest.Try(low: item.Low, high: item.High, generator: ref generator, prime: out var prime);

            if (!found) { throw new InvalidOperationException(message: "A random request exhausted the shared draw budget."); }
            if (verify && ((prime < item.Low) || (prime > item.High) || !PrimeExplorationBenchmarkReference.IsPrimeCandidate(value: prime))) {
                throw new InvalidOperationException(message: "A sampled prime failed its independent exact reference check.");
            }
            checksum = BitOperations.RotateLeft(offset: 3, value: checksum) ^ prime;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Every request consumes at most 4096 draws; even a million-request batch stays below 2^63.
        return (checksum, (verify ? checked((ulong)initial.Distance(other: in generator)) : 0));
    }
    private static void RequireEqual(ulong expected, ulong actual) {
        if (expected != actual) { throw new InvalidOperationException(message: $"Prime request returned {actual}; expected {expected}."); }
    }
    private static string Hash(string path) => Convert.ToHexString(inArray: SHA256.HashData(source: File.ReadAllBytes(path: path)));
    private static string? Affinity() {
        if (!OperatingSystem.IsWindows()) { return null; }
        using var process = Process.GetCurrentProcess();

        return $"0x{process.ProcessorAffinity.ToString(format: "X")}";
    }

    private interface IRandomRequest {
        static abstract bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime);
    }
    private readonly struct IntegerMillerRequest : IRandomRequest {
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) =>
            RandomPrimeRequests.TryIntegerMillerRabin(generator: ref generator, high: high, low: low, maxAttempts: DrawBudget, prime: out prime);
    }
    private readonly struct IntegerBaillieRequest : IRandomRequest {
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) =>
            RandomPrimeRequests.TryIntegerBailliePsw(generator: ref generator, high: high, low: low, maxAttempts: DrawBudget, prime: out prime);
    }
    private readonly struct ProductionRandomRequest : IRandomRequest {
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) =>
            PrimeExploration.TryRandomPrime(generator: ref generator, high: high, low: low, maxAttempts: DrawBudget, prime: out prime);
    }
    private readonly struct WheelMillerRequest : IRandomRequest {
        public static bool Try(ulong low, ulong high, ref Pcg32XshRr generator, out ulong prime) =>
            RandomPrimeRequests.TryWheelMillerRabin(generator: ref generator, high: high, low: low, maxAttempts: DrawBudget, prime: out prime);
    }
    private sealed record RequestCase(string Name, string Kind, ulong Argument = 0, ulong? Expected = null, bool Narrow = false, bool Expensive = false, ulong Low = 0, ulong High = 0);
}
internal sealed record PrimeRequestMeasurement(ulong Result, ulong Checksum, int Requests, double Milliseconds, double MillisecondsPerRequest, long AllocatedBytes, double AllocatedBytesPerRequest);
internal sealed record PrimeRandomWork(ulong UInt32Draws, ulong UInt64Draws, ulong CandidateDecisionUpperBound, string Coverage);
internal sealed record PrimeRequestSurveyRow(string Case, string Kind, string Implementation, ulong Argument, ulong Low, ulong High, ulong Seed, int RequestsPerSample, bool FirstPrimeRequestInProcess) {
    public string? Error { get; set; }
    public PrimeRequestMeasurement? First { get; set; }
    public double? MedianMilliseconds { get; set; }
    public PrimeRandomWork? RandomWork { get; set; }
    public ulong? Result { get; set; }
    public List<PrimeRequestMeasurement> Samples { get; } = [];
    public string Status { get; set; } = "pending";
    public PrimeRequestProfile? Work { get; set; }
}
internal sealed record PrimeRequestSurveyReport(string Runtime, string OperatingSystem, string Architecture, int LogicalProcessors, int? LogicalCpu, string? ProcessAffinity,
    string MathsAssemblySha256, string HarnessAssemblySha256, int Samples, int Warmups, double CaseTimeoutSeconds, int RawDrawBudget, int ProductionDefaultRawDrawBudget, string DrawBudgetUnit, string MeasurementContract,
    Dictionary<string, string?> RuntimeEnvironment, List<PrimeRequestSurveyRow> Rows);
[JsonSerializable(typeof(PrimeRequestSurveyReport))]
[JsonSourceGenerationOptions(NewLine = "\n", WriteIndented = true)]
internal sealed partial class PrimeRequestSurveyJsonContext : JsonSerializerContext;
