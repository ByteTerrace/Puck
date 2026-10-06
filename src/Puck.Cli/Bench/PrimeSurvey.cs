using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.Cli.Bench;

// A serial survey; round-robin samples compare equal output operations, rather than callback and native count.
internal static class PrimeSurvey {
    public static Command Create(TimeProvider clock) {
        var low = new Option<ulong>("--low") { Description = "Inclusive lower endpoint of the unsigned-64-bit interval." };
        var upper = new Option<ulong>("--upper") { DefaultValueFactory = _ => 100_000_000UL, Description = "Inclusive upper endpoint, through ulong.MaxValue." };
        var modes = new Option<PrimeSieveMode[]>("--mode") {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => [PrimeSieveMode.Eratosthenes],
            Description = "Distinct sieve modes to compare in the same balanced rounds.",
        };
        var layouts = new Option<PrimeByteLayout[]>("--layouts") {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => Enum.GetValues<PrimeByteLayout>(),
            Description = "Distinct bitmap layouts to compare.",
        };
        var patterns = new Option<PrimeSurveyPatterns>("--patterns") { DefaultValueFactory = _ => PrimeSurveyPatterns.Both, Description = "Both, Enabled, or Disabled periodic pre-sieve patterns." };
        var batch = new Option<int>("--batch") { DefaultValueFactory = _ => 1, Description = "Managed calls per sample, 1..100000; times and allocations are reported per call." };
        var samples = new Option<int>("--samples") { DefaultValueFactory = _ => 5, Description = "Measured rounds after all variants warm up, 1..100." };
        var warmups = new Option<int>("--warmups") { DefaultValueFactory = _ => 3, Description = "Untimed rounds before measuring, 1..100." };
        var segments = new Option<int[]>("--segments") {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => [16_384, 32_768, 1_048_576, 8_388_608],
            Description = "Requested bitmap bytes: distinct KiB multiples in 16 KiB..8 MiB.",
        };
        var countOnly = new Option<bool>("--count-only") { Description = "Measure only population counts; omit callback enumeration." };
        var cpu = new Option<int?>("--cpu") { Description = "Windows logical CPU shared by managed counts and inherited native processes; defaults to scheduler placement." };
        var strategies = new Option<PrimeSieveStrategy[]>("--strategies") {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => Enum.GetValues<PrimeSieveStrategy>(),
            Description = "Distinct managed marking strategies; defaults to all. Layout and pre-sieve controls remain paired.",
        };
        var native = new Option<string?>("--primesieve") { Description = "Optional existing primesieve executable; one thread and count output." };
        var output = new Option<string?>("--output") { Description = "Parent for a unique report directory and native transcripts." };
        var command = new Command(description: "Compare matched prime operations serially with balanced ordering and explicit bitmap footprints.", name: "primes") { low, upper, modes, layouts, patterns, batch, samples, warmups, segments, strategies, countOnly, cpu, native, output };

        command.SetAction(action: parse => Run(parse.GetValue(option: low), parse.GetValue(option: upper), parse.GetValue(option: modes)!, parse.GetValue(option: layouts)!, parse.GetValue(option: patterns), parse.GetValue(option: batch), parse.GetValue(option: samples), parse.GetValue(option: warmups),
            parse.GetValue(option: segments)!, parse.GetValue(option: strategies)!, parse.GetValue(option: countOnly), parse.GetValue(option: cpu), parse.GetValue(option: native), parse.GetValue(option: output), clock));
        return command;
    }

    private static int Run(ulong low, ulong upper, PrimeSieveMode[] modes, PrimeByteLayout[] layouts, PrimeSurveyPatterns patterns, int batch, int samples, int warmups, int[] sizes, PrimeSieveStrategy[] strategies, bool countOnly, int? cpu, string? native, string? output, TimeProvider clock) {
        int Survey() => RunSurvey(batch: batch, clock: clock, countOnly: countOnly, cpu: cpu, layouts: layouts, low: low, modes: modes, native: native, output: output, patterns: patterns, samples: samples, sizes: sizes, strategies: strategies, upper: upper, warmups: warmups);
        if (cpu is not { } index) { return Survey(); }
        if (!OperatingSystem.IsWindows() || (((uint)index) >= 64)) {
            Console.Error.WriteLine(value: "primes: cpu must be an available Windows logical CPU in 0..63.");
            return 2;
        }
        using var process = Process.GetCurrentProcess();
        var original = process.ProcessorAffinity;
        var selected = ((nint)(1UL << index));

        if ((original & selected) == 0) {
            Console.Error.WriteLine(value: "primes: selected CPU is outside the current process affinity mask.");
            return 2;
        }
        process.ProcessorAffinity = selected;
        try { return Survey(); } finally { process.ProcessorAffinity = original; }
    }
    private static int RunSurvey(ulong low, ulong upper, PrimeSieveMode[] modes, PrimeByteLayout[] layouts, PrimeSurveyPatterns patterns, int batch, int samples, int warmups, int[] sizes, PrimeSieveStrategy[] strategies, bool countOnly, int? cpu, string? native, string? output, TimeProvider clock) {
        if ((strategies.Length == 0) || (strategies.Distinct().Count() != strategies.Length) || strategies.Any(predicate: strategy => !Enum.IsDefined(value: strategy))) {
            Console.Error.WriteLine(value: "primes: strategies must be distinct defined marking strategies.");
            return 2;
        }
        if ((low > upper) || (batch is < 1 or > 100000) || !Enum.IsDefined(value: patterns) ||
            (modes.Length == 0) || (modes.Distinct().Count() != modes.Length) || modes.Any(predicate: mode => !Enum.IsDefined(value: mode)) ||
            (layouts.Length == 0) || (layouts.Distinct().Count() != layouts.Length) || layouts.Any(predicate: layout => !Enum.IsDefined(value: layout)) ||
            (samples is < 1 or > 100) || (warmups is < 1 or > 100) ||
            (sizes.Length == 0) || (sizes.Distinct().Count() != sizes.Length) || sizes.Any(predicate: size => ((size is < 16384 or > 8388608) || ((size % 1024) != 0)))) {
            Console.Error.WriteLine(value: "primes: require low <= upper; defined distinct modes/layouts; defined patterns; batch 1..100000; samples/warmups 1..100; distinct KiB segments in 16 KiB..8 MiB.");
            return 2;
        }
        if (native is not null) {
            native = PuckPaths.Normalize(path: Path.GetFullPath(path: native));
            if (!File.Exists(path: native)) {
                Console.Error.WriteLine(value: $"primes: missing executable {native}.");
                return 2;
            }
        }
        var run = PuckPaths.Normalize(path: Path.GetFullPath(path: Path.Combine(path1: (output ?? "artifacts/primes"), path2: Guid.NewGuid().ToString(format: "N"))));

        Directory.CreateDirectory(path: run);
        var rows = new List<PrimeSurveyRow>();
        var nativeVersion = ((native is null) ? null : Capture(arguments: ["--version"], clock: clock, executable: native));
        var nativeCpu = ((native is null) ? null : Capture(arguments: ["--cpu-info"], clock: clock, executable: native));
        var variants = new List<Variant>();
        var bases = new List<ulong>();

        // The original exact marking counters cover zero-origin complete uint sieves. Other intervals retain
        // exact bitmap metadata and measured allocations, with unsupported marking counters explicitly null.
        if ((low == 0) && (upper <= uint.MaxValue)) { PrimeExploration.Enumerate(7, upper.SquareRoot(), bases.Add); }
        var updates = 0UL;
        var smallUpdates = 0UL;

        foreach (var prime in bases) {
            var marked = (WheelCount(high: (upper / prime)) - WheelCount(high: (prime - 1)));

            updates += marked;
            if (prime <= 163) { smallUpdates += marked; }
        }
        foreach (var size in sizes) {
            foreach (var strategy in strategies) {
                foreach (var layout in layouts) {
                    foreach (var mode in modes) {
                        foreach (var usePreSieve in ((ReadOnlySpan<bool>)[false, true])) {
                            if (((patterns == PrimeSurveyPatterns.Enabled) && !usePreSieve) || ((patterns == PrimeSurveyPatterns.Disabled) && usePreSieve)) { continue; }
                            variants.Add(item: new(Enumerate: false, Layout: layout, Mode: mode, Native: false, Size: size, Strategy: strategy, UsePreSieve: usePreSieve));
                            if (!countOnly) { variants.Add(item: new(Enumerate: true, Layout: layout, Mode: mode, Native: false, Size: size, Strategy: strategy, UsePreSieve: usePreSieve)); }
                        }
                    }
                }
            }
            if (native is not null) { variants.Add(item: new(Enumerate: false, Layout: default, Mode: PrimeSieveMode.Eratosthenes, Native: true, Size: size, Strategy: default, UsePreSieve: true)); }
        }
        var counter = new Counter();
        ulong? expected = null;
        var ordinal = 0;

        Console.WriteLine(value: $"primes: [{low},{upper}], {warmups} warmup rounds, {samples} measured rounds, batch {batch}; {run}");
        for (var round = 0; (round < (warmups + samples)); ++round) {
            // Rotate the starting variant, alternating traversal direction. Every variant runs once per round.
            for (var offset = 0; (offset < variants.Count); ++offset) {
                var index = ((round + (((round & 1) == 0) ? offset : (variants.Count - offset))) % variants.Count);
                var variant = variants[index];
                var sample = ((round < warmups) ? 0 : ((round - warmups) + 1));
                var count = 0UL;
                double? internalMilliseconds = null;
                long? allocated = null;

                counter.Value = 0;
                var timer = Stopwatch.StartNew();

                if (variant.Native) {
                    var transcript = Capture(native!, [low.ToString(provider: CultureInfo.InvariantCulture), upper.ToString(provider: CultureInfo.InvariantCulture), "--count=1", "--threads=1",
                        $"--size={(variant.Size / 1024)}", "--no-status", "--time"], clock);

                    timer.Stop();
                    File.WriteAllText($"{run}/native-{variant.Size}-round-{round}.log", transcript);
                    var lines = transcript.Split(options: StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries, separator: '\n');

                    count = ulong.Parse(lines.Single(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "Primes:")).AsSpan(start: 7), CultureInfo.InvariantCulture);
                    internalMilliseconds = (double.Parse(lines.Single(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "Seconds:")).AsSpan(start: 8), CultureInfo.InvariantCulture) * 1000);
                } else {
                    Action operation = () => {
                        for (var invocation = 0; (invocation < batch); ++invocation) {
                            counter.Value = 0;
                            count = (variant.Enumerate
                                ? Enumerate(counter: counter, low: low, upper: upper, variant: variant)
                                : PrimeExploration.Count(low, upper, variant.Size, variant.Strategy, variant.Layout, variant.Mode, variant.UsePreSieve));
                            expected ??= count;
                            if (count != expected) { throw new InvalidOperationException(message: $"{variant.Name}: count {count} differs from {expected}."); }
                        }
                    };

                    timer.Restart();
                    allocated = AllocationWindow.Total(window: operation);
                    timer.Stop();
                }
                expected ??= count;
                if (count != expected) { throw new InvalidOperationException(message: $"{variant.Name}: count {count} differs from {expected}."); }
                ++ordinal;
                if (sample == 0) { continue; }
                var totalBytes = ((upper < 7) ? 0UL : (((upper / 30) - (Math.Max(val1: low, val2: 7UL) / 30)) + 1));
                var actualSize = variant.Size;

                if (!variant.Native && (variant.Strategy == PrimeSieveStrategy.BucketPackets)) {
                    var cacheBytes = Math.Min(val1: actualSize, val2: 32768);
                    var adaptiveBytes = Math.Clamp((2UL * upper.SquareRoot()), ((ulong)cacheBytes), ((ulong)actualSize));

                    actualSize = ((int)(adaptiveBytes - (adaptiveBytes % ((ulong)cacheBytes))));
                }
                var fullSegments = (totalBytes / ((ulong)actualSize));
                var tailBytes = ((int)(totalBytes % ((ulong)actualSize)));
                var segmentCount = (fullSegments + ((tailBytes == 0) ? 0UL : 1UL));
                var visits = 0UL;
                var initializations = 0UL;
                var phaseSortCandidates = 0UL;
                var bucketTransfers = 0UL;
                var chunkSize = ((variant.Strategy == PrimeSieveStrategy.CacheBlockedPackets) ? Math.Min(val1: 16384, val2: actualSize)
                    : ((variant.Strategy == PrimeSieveStrategy.BucketPackets) ? Math.Min(val1: 32768, val2: actualSize) : actualSize));
                var chunksPerSegment = (((((ulong)actualSize) + ((ulong)chunkSize)) - 1UL) / ((ulong)chunkSize));
                var markingChunks = ((fullSegments * chunksPerSegment) + (((((ulong)tailBytes) + ((ulong)chunkSize)) - 1UL) / ((ulong)chunkSize)));

                foreach (var prime in bases) {
                    if (variant.UsePreSieve && (prime <= 163)) { continue; }
                    var squareBlock = ((prime * prime) / 30);
                    var before = (((squareBlock / ((ulong)actualSize)) * chunksPerSegment) + ((squareBlock % ((ulong)actualSize)) / ((ulong)chunkSize)));

                    if ((variant.Strategy == PrimeSieveStrategy.BucketPackets) && (prime > ((ulong)(chunkSize / 8)))) {
                        var mediumVisits = (segmentCount - (squareBlock / ((ulong)actualSize)));

                        visits += mediumVisits;
                        bucketTransfers += mediumVisits;
                    } else { visits += (markingChunks - before); }
                    initializations += ((variant.Strategy >= PrimeSieveStrategy.CarriedPackets) ? 1UL
                        : (segmentCount - (squareBlock / ((ulong)actualSize))));
                    if (variant.Strategy == PrimeSieveStrategy.PhaseSortedPackets) {
                        var firstSegment = (squareBlock / ((ulong)variant.Size));

                        if (prime > ((ulong)(Math.Min(val1: variant.Size, val2: 32768) / 5))) {
                            phaseSortCandidates += ((fullSegments > firstSegment) ? (fullSegments - firstSegment) : 0UL);
                        }
                        if ((tailBytes != 0) && (prime > ((ulong)(Math.Min(val1: tailBytes, val2: 32768) / 5)))) { ++phaseSortCandidates; }
                    }
                }
                // Source-established full-bitmap size: not a runtime measurement or a total-workspace claim.
                int? nativeFullBytes = ((variant.Native && (low == 0) && (variant.Size == 16384) && (upper > 491526) &&
                    (nativeVersion?.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "primesieve 12.15") == true)) ? 16384 : null);

                var knownMarks = (!variant.Native && (low == 0) && (upper <= uint.MaxValue) && (variant.Mode != PrimeSieveMode.Presieve));

                rows.Add(item: new(variant.Name, variant.Size, sample, ordinal, count, (timer.Elapsed.TotalMilliseconds / (variant.Native ? 1 : batch)), internalMilliseconds,
                    (variant.Native ? null : ((int)Math.Min(val1: totalBytes, val2: ((ulong)actualSize)))), (variant.Native ? null : fullSegments),
                    (variant.Native ? null : tailBytes), (knownMarks ? visits : null),
                    (knownMarks ? (updates - (variant.UsePreSieve ? smallUpdates : 0UL)) : null), nativeFullBytes,
                    (variant.Native ? null : (variant.UsePreSieve ? (16UL * totalBytes) : 0UL)),
                    (variant.Native ? null : (variant.UsePreSieve ? (4UL * segmentCount) : 0UL)),
                    (knownMarks ? initializations : null), (variant.Native ? null : chunkSize), (variant.Native ? null : markingChunks),
                    (knownMarks ? phaseSortCandidates : null), (knownMarks ? bucketTransfers : null),
                    (allocated.HasValue ? (((double)allocated.Value) / batch) : null), (variant.Native ? 1 : batch), variant.Mode.ToString()));
            }
            Save();
            Console.WriteLine(value: $"primes: completed {((round < warmups) ? "warmup" : "measured")} round {((round < warmups) ? (round + 1) : ((round - warmups) + 1))}; {expected} primes.");
        }
        foreach (var variant in variants) { Print(rows, variant.Name, variant.Size); }
        return 0;

        void Save() {
            // The runtime reads these before this verb starts; record them as evidence rather than Puck switches.
            var environment = new Dictionary<string, string?> {
                ["DOTNET_TieredCompilation"] = Environment.GetEnvironmentVariable(variable: "DOTNET_TieredCompilation"),
                ["DOTNET_TieredPGO"] = Environment.GetEnvironmentVariable(variable: "DOTNET_TieredPGO"),
                ["DOTNET_ReadyToRun"] = Environment.GetEnvironmentVariable(variable: "DOTNET_ReadyToRun"),
                ["DOTNET_TC_QuickJitForLoops"] = Environment.GetEnvironmentVariable(variable: "DOTNET_TC_QuickJitForLoops"),
                ["DOTNET_gcServer"] = Environment.GetEnvironmentVariable(variable: "DOTNET_gcServer"),
                ["COMPlus_TieredCompilation"] = Environment.GetEnvironmentVariable(variable: "COMPlus_TieredCompilation"),
                ["COMPlus_TieredPGO"] = Environment.GetEnvironmentVariable(variable: "COMPlus_TieredPGO"),
                ["COMPlus_ReadyToRun"] = Environment.GetEnvironmentVariable(variable: "COMPlus_ReadyToRun"),
                ["COMPlus_TC_QuickJitForLoops"] = Environment.GetEnvironmentVariable(variable: "COMPlus_TC_QuickJitForLoops"),
                ["COMPlus_gcServer"] = Environment.GetEnvironmentVariable(variable: "COMPlus_gcServer"),
            };
            var managedAssembly = typeof(PrimeExploration).Assembly.Location;
            var harnessAssembly = typeof(PrimeSurvey).Assembly.Location;
            var report = new PrimeSurveyReport(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, cpu, low, upper, samples, warmups,
                "Same closed unsigned-64-bit interval, one thread, verified counts. Mode is explicit per row: Automatic completely sieves through uint.MaxValue and otherwise presieves through 65535 before exact survivor decisions. Eratosthenes includes upper-base generation on every call; Presieve always tests survivors. All variants warm before round-robin samples; start rotates and direction alternates. Scalar and periodic-pattern variants share all remaining code; patterns filter primes 7..163 using sixteen tables in four bitmap passes. Packet variants share one marking body: UnrolledPackets initializes per segment; CarriedPackets initializes once; CacheBlockedPackets also marks in at most 16384-byte chunks; SpecializedPackets groups primes by residue and instantiates the same source body with constant masks and lifts; PhaseSortedPackets additionally groups medium states by their saved phase. Phase-sort candidates count active states above min(segment length,32768)/5, including singleton groups whose copy is skipped. BucketPackets uses adaptive segments, at most 32768-byte small-prime chunks with cutoff chunkSize/8, eight-byte quotient states and direct medium-prime phase transfers with one static starting-phase dispatch per occupied bucket. All managed bitmaps use 64-byte alignment where padding fits. Residue grouping uses a stable counting partition with an immediately returned pooled temporary buffer. LogicalCpu records optional Windows affinity shared with inherited native children; the original process mask is restored on exit. Bucket transfer counts describe active medium states visited once per actual segment; its chunk-size/count fields describe the small-prime partition, and its visit count combines both partitions. Managed /count and native count match output; /enumerate is separate with a reused counted callback. Bitmap sizes, segment counts, pattern input bytes and passes describe the requested interval. Fine-grained marking counters are exact only for zero-origin complete uint sieves; unsupported counters are null for other intervals or Presieve. Times and ManagedAllocatedBytesPerCall are divided by CallsPerSample; every batched call checks its count. Allocations use the shared calling-thread AllocationWindow and do not measure retained pools or peak workspace. Per-call state initialization and upper-base generation are included; shared first-use initialization is warmed. End-byte padding and overlapping final pattern vectors may receive extra AND stores; logical exclusions are not a count of hardware stores or memory traffic. Alignment padding, pooled capacity, carried-state buffers, shared base tables and pattern tables are additional workspace; pattern construction is outside warm measurements. Native requested size is an adaptive upper limit. NativeFullBitmapBytes records the guaranteed pinned 12.15 --size=16 multi-segment 16384-byte case; other requests leave that field null, which does not establish their actual size. Native endpoint alignment/tail and additional state differ. Native internal time excludes process launch and has printed precision; whole-process time is separate. Managed warm-process and native fresh-process measurements remain integration comparisons.",
                environment, (File.Exists(path: managedAssembly) ? Hash(path: managedAssembly) : null),
                (File.Exists(path: harnessAssembly) ? Hash(path: harnessAssembly) : null), native, ((native is null) ? null : Hash(path: native)), nativeVersion, nativeCpu, rows.ToArray());

            File.WriteAllText($"{run}/report.json", JsonSerializer.Serialize(report, PrimeSurveyJsonContext.Default.PrimeSurveyReport));
        }
    }
    private static ulong WheelCount(ulong high) {
        var count = ((high / 30) * 8);
        var remainder = (high % 30);

        foreach (var residue in PrimeWheel30.NumericResidues) { if (residue <= remainder) { ++count; } }
        return count;
    }
    private static ulong Enumerate(Variant variant, ulong low, ulong upper, Counter counter) {
        PrimeExploration.Enumerate(low, upper, counter.Callback, variant.Size, variant.Strategy, variant.Layout, variant.Mode, variant.UsePreSieve);
        return counter.Value;
    }
    private static string Hash(string path) => Convert.ToHexString(inArray: SHA256.HashData(source: File.ReadAllBytes(path: path)));
    private static string Capture(string executable, string[] arguments, TimeProvider clock) {
        var result = CliProcess.RunCaptured(executable, arguments, "", TimeSpan.FromMinutes(minutes: 10), clock: clock);

        if (result.TimedOut || (result.ExitCode != 0)) { throw new InvalidOperationException(message: $"primesieve failed: {result.ExitCode}; {result.Stderr}"); }
        return result.Stdout;
    }
    private static void Print(List<PrimeSurveyRow> rows, string name, int size) {
        var selected = rows.Where(predicate: row => ((row.Implementation == name) && (row.SegmentBytes == size))).ToArray();
        var values = selected.Select(selector: row => row.Milliseconds).Order().ToArray();
        var middle = (values.Length / 2);
        var median = (((values.Length % 2) == 0) ? ((values[(middle - 1)] + values[middle]) / 2) : values[middle]);
        var nativeTimes = selected.Where(predicate: row => row.NativeInternalMilliseconds.HasValue).Select(selector: row => row.NativeInternalMilliseconds!.Value).Order().ToArray();
        var nativeMedian = ((((nativeTimes.Length % 2) == 0) && (nativeTimes.Length > 0))
            ? ((nativeTimes[(middle - 1)] + nativeTimes[middle]) / 2) : ((nativeTimes.Length > 0) ? nativeTimes[middle] : 0));
        var detail = ((nativeTimes.Length == 0) ? "" : $"; {nativeMedian:F3} ms native internal median");

        Console.WriteLine(value: $"{name}, {size} requested bytes: {median:F3} ms median{detail}; {selected[0].Count} primes.");
    }

    private sealed class Counter {
        public Counter() { Callback = _ => ++Value; }

        public Action<ulong> Callback { get; }
        public ulong Value { get; set; }
    }
    private sealed record Variant(int Size, PrimeSieveStrategy Strategy, PrimeByteLayout Layout, PrimeSieveMode Mode, bool Enumerate, bool Native, bool UsePreSieve) {
        public string Name => (Native ? "primesieve/count" : $"{Strategy}/{Layout}/{(UsePreSieve ? "patterns" : "scalar")}/{Mode}/{(Enumerate ? "enumerate" : "count")}");
    }
}
internal sealed record PrimeSurveyRow(string Implementation, int SegmentBytes, int Sample, int ExecutionOrdinal, ulong Count,
    double Milliseconds, double? NativeInternalMilliseconds, int? ManagedActiveBitmapBytes, ulong? ManagedFullSegments,
    int? ManagedTailBytes, ulong? ManagedBasePrimeSegmentVisits, ulong? ManagedPrimeMultipleUpdates, int? NativeFullBitmapBytes,
    ulong? ManagedPatternInputBytes, ulong? ManagedPatternBitmapPasses, ulong? ManagedPrimeStartInitializations,
    int? ManagedMarkingChunkBytes, ulong? ManagedMarkingChunks, ulong? ManagedPhaseSortCandidates, ulong? ManagedBucketTransfers,
    double? ManagedAllocatedBytesPerCall, int CallsPerSample, string Mode);
internal sealed record PrimeSurveyReport(string Runtime, string OperatingSystem, string Architecture, int LogicalProcessors, int? LogicalCpu, ulong Low, ulong Upper,
    int SamplesPerRow, int WarmupRounds, string MeasurementContract, Dictionary<string, string?> RuntimeEnvironment,
    string? ManagedAssemblySha256, string? HarnessAssemblySha256, string? NativeExecutable, string? NativeExecutableSha256, string? NativeVersion, string? NativeCpuInfo, PrimeSurveyRow[] Rows);
[JsonSerializable(typeof(PrimeSurveyReport))]
[JsonSourceGenerationOptions(NewLine = "\n", WriteIndented = true)]
internal sealed partial class PrimeSurveyJsonContext : JsonSerializerContext;
internal enum PrimeSurveyPatterns { Both, Enabled, Disabled }
