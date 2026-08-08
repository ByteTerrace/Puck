// The repository's verification toolbox: one .NET 10 file-based app, subcommand-dispatched.
// Flags are case-insensitive (-NoBuild == --nobuild). Exit codes: 0 pass, 1 gate failure, 2
// usage/build/run failure. Capture runs open hands-off windows that self-terminate.
//
// parity [-NoBuild]
//   Cross-backend parity gate. Builds, then runs Puck.Post's Tier C battery (WorldStage, WorldChildStage,
//   ExportStage, ReverseShareStage): it renders the SDF scene on both the Vulkan and Direct3D 12 backends
//   offscreen, diffs them tolerance-aware, writes artifacts/post/ (the backend PNGs, an amplified diff heatmap,
//   post-report.txt), and exits 0 pass / 1 gate-fail / 2 infra-fail. -NoBuild skips the build.
//
// schema [<out=schema/run.schema.json>] [-NoBuild]
//   Emits the data-driven run-document JSON Schema by calling Puck.Scene.RunDocumentSchema.Export() directly. The schema is
//   exported from the live System.Text.Json source-gen model, so it cannot drift from the document types. -NoBuild
//   skips the build.
//
// compare-frames <a.png> <b.png>                       Pixel-diff two APNG captures frame-by-frame.
// extract-frame <capture.png> <frameIndex> <out.png>   Composite frames 0..index into one PNG.
// frame-count <capture.png>                            Count frames in an APNG capture.
#:project ../src/Puck.Scene/Puck.Scene.csproj
#nullable enable
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

return Puck.Tools.Toolbox.Run(args);

namespace Puck.Tools
{
    internal static class Toolbox
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            var commandArguments = args[1..];

            switch (args[0].ToLowerInvariant())
            {
                case "parity":
                    return Parity(commandArguments);
                case "schema":
                    return SchemaCommand(commandArguments);
                case "fuzz":
                    return Fuzz(commandArguments);
                case "compare-frames":
                    return CompareFrames(commandArguments);
                case "extract-frame":
                    return ExtractFrame(commandArguments);
                case "frame-count":
                    return FrameCount(commandArguments);
                default:
                    Console.Error.WriteLine($"ERROR: unknown command '{args[0]}'.");
                    PrintUsage();
                    return 2;
            }
        }

        // Cross-backend parity gate: builds the solution, runs Puck.Post's Tier C battery (the cross-backend world
        // parity stages: WorldStage, WorldChildStage, ExportStage, ReverseShareStage), and propagates its exit code
        // (0 pass / 1 gate-fail / 2 infra-fail). Puck.Post streams its own per-stage table live (console inherited),
        // so there is nothing left to re-print here. Release, matching Puck.Post's own documented invocation.
        private static int Parity(string[] arguments)
        {
            var noBuild = arguments.Any(static argument => string.Equals(argument, "-NoBuild", StringComparison.OrdinalIgnoreCase));

            if (!noBuild)
            {
                var buildExit = EngineRun.Build("Release");

                if (buildExit != 0)
                {
                    Console.Error.WriteLine("ERROR: build failed.");
                    return 2;
                }
            }

            return EngineRun.RunPostTier("C");
        }

        // Emits the data-driven run-document JSON Schema IN-PROCESS (relocated here by Arc 3 Beat A / OQ-16 from the
        // retired Demo composition root): RunDocumentSchema.Export() is the SAME source-gen model that reads documents,
        // so the schema cannot drift from the types. The first positional argument overrides the output path
        // (default schema/run.schema.json).
        private static int SchemaCommand(string[] arguments)
        {
            var output = (arguments.FirstOrDefault(static argument => !argument.StartsWith("-", StringComparison.Ordinal)) ?? Path.Combine(EngineRun.RepositoryRoot, "schema", "run.schema.json"));
            var schema = Puck.Scene.RunDocumentSchema.Export();
            var directory = Path.GetDirectoryName(output);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(output, schema);
            Console.WriteLine($"[emit-schema] wrote the run-document schema ({schema.Length} chars) to '{output}'.");
            return 0;
        }

        // Cross-backend DIFFERENTIAL FUZZER. Spawns one ISOLATED child per seed running Puck.Post's
        // `--stage fuzz --fuzz-seed <n>` (a fuzz-generated SDF program rendered identically on both backends and
        // diffed by the parity oracle), under a hard wall-clock timeout. Process isolation is mandatory: a malformed
        // program can device-loss / TDR / hang the GPU or native-crash, which would otherwise take down the harness.
        // A child that diverges (exit 1), throws (exit 2), native-crashes (other exit), or hangs (timeout -> killed)
        // is a finding: its seed, verdict line, captured output, and the parity PNGs are preserved to artifacts/fuzz/.
        // Exits 0 when no findings, 1 otherwise.
        private static int Fuzz(string[] arguments)
        {
            var count = FuzzIntArg(arguments, "-Count", 64);
            var start = FuzzIntArg(arguments, "-Start", 0);
            var timeoutSeconds = FuzzIntArg(arguments, "-Timeout", 30);
            var configuration = FuzzStringArg(arguments, "-Config", "Debug");
            var noBuild = arguments.Any(static argument => string.Equals(argument, "-NoBuild", StringComparison.OrdinalIgnoreCase));

            if (!noBuild)
            {
                var buildExit = EngineRun.Build(configuration);

                if (buildExit != 0)
                {
                    Console.Error.WriteLine("ERROR: build failed.");
                    return 2;
                }
            }

            var sweepExecutable = Path.Combine(EngineRun.RepositoryRoot, "src", "Puck.Post", "bin", configuration, "net10.0", "Puck.Post.exe");

            if (!File.Exists(sweepExecutable))
            {
                Console.Error.WriteLine($"ERROR: sweep executable not found at {sweepExecutable} (build first).");
                return 2;
            }

            var fuzzDirectory = Path.Combine(EngineRun.RepositoryRoot, "artifacts", "fuzz");

            Directory.CreateDirectory(fuzzDirectory);

            var timeoutMilliseconds = (timeoutSeconds * 1000);
            var passes = 0;
            var divergences = 0;
            var crashes = 0;
            var timeouts = 0;
            var infra = 0;
            var findings = new List<string>();

            Console.WriteLine($"FUZZ start | {count} seeds from {start} | timeout {timeoutSeconds}s/iter | {configuration}");

            for (var index = 0; (index < count); index++)
            {
                var seed = (start + index);
                var seedText = seed.ToString(CultureInfo.InvariantCulture);
                var sweepArguments = new[] { "--stage", "fuzz", "--fuzz-seed", seedText };

                var (exitCode, timedOut, output) = ToolProcess.RunWithTimeout(timeoutMilliseconds, sweepExecutable, sweepArguments);

                string classification;
                var finding = true;

                if (timedOut)
                {
                    classification = "TIMEOUT (hang/TDR)";
                    timeouts++;
                }
                else
                {
                    switch (exitCode)
                    {
                        case 0:
                            classification = "pass";
                            passes++;
                            finding = false;
                            break;
                        case 1:
                            classification = "DIVERGENCE";
                            divergences++;
                            break;
                        case 2:
                            classification = "infra/throw";
                            infra++;
                            break;
                        default:
                            classification = $"CRASH (exit {exitCode})";
                            crashes++;
                            break;
                    }
                }

                var verdictLine = FuzzVerdictLine(output);

                Console.WriteLine($"  seed {seed,7}: {classification,-18} {verdictLine}");

                if (finding)
                {
                    findings.Add($"seed {seed}: {classification} | {verdictLine}");
                    File.WriteAllText(Path.Combine(fuzzDirectory, $"seed-{seed}-output.txt"), output);
                    FuzzPreserveArtifacts(fuzzDirectory, seed);
                }
            }

            var summary =
                $"FUZZ summary | {count} runs from {start} | pass {passes} | divergence {divergences} | timeout {timeouts} | crash {crashes} | infra {infra} | findings {findings.Count}";

            Console.WriteLine(summary);
            File.WriteAllText(
                Path.Combine(fuzzDirectory, "findings.txt"),
                (((summary + Environment.NewLine) + Environment.NewLine) + string.Join(Environment.NewLine, findings))
            );
            return ((findings.Count == 0) ? 0 : 1);
        }
        private static int FuzzIntArg(string[] arguments, string name, int fallback)
        {
            for (var index = 0; (index < (arguments.Length - 1)); index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(arguments[(index + 1)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return fallback;
        }
        private static string FuzzStringArg(string[] arguments, string name, string fallback)
        {
            for (var index = 0; (index < (arguments.Length - 1)); index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[(index + 1)];
                }
            }

            return fallback;
        }
        // Picks the fuzz stage's own verdict line out of a Puck.Post battery run's console output — Puck.Post logs
        // each stage as "[<tier>] <name>: <verdict> | <detail>" (PostBattery.Run); the fuzz stage is Tier C, named
        // "fuzz", so "] fuzz:" identifies its line regardless of tier label or verdict.
        private static string FuzzVerdictLine(string output)
        {
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("] fuzz:", StringComparison.Ordinal))
                {
                    return line.Trim();
                }
            }

            return "(no verdict line)";
        }
        // Preserves the cross-backend artifact triple for a diverging seed: Puck.Post's FuzzStage writes
        // fuzz-{seed}-{vulkan,directx,diff}.png under its own artifacts directory (default artifacts/post).
        private static void FuzzPreserveArtifacts(string fuzzDirectory, int seed)
        {
            var sources = new[] {
                (Path.Combine("post", $"fuzz-{seed}-vulkan.png"), "vulkan"),
                (Path.Combine("post", $"fuzz-{seed}-directx.png"), "directx"),
                (Path.Combine("post", $"fuzz-{seed}-diff.png"), "diff"),
            };

            foreach (var (source, suffix) in sources)
            {
                var sourcePath = Path.Combine(EngineRun.RepositoryRoot, "artifacts", source);

                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, Path.Combine(fuzzDirectory, $"seed-{seed}-{suffix}.png"), overwrite: true);
                }
            }
        }
        private static int CompareFrames(string[] arguments)
        {
            if (arguments.Length != 2)
            {
                Console.Error.WriteLine("ERROR: usage: compare-frames <captureA.png> <captureB.png>");
                return 2;
            }

            var result = ApngFrameExtractor.CompareCaptures(Path.GetFullPath(arguments[0]), Path.GetFullPath(arguments[1]));

            foreach (var frame in result.Diffs)
            {
                Console.WriteLine($"frame {frame.FrameIndex}: diffPixels={frame.DiffPixelCount} maxChannelDelta={frame.MaxChannelDelta}");
            }

            var overallMaxDelta = ((result.Diffs.Count == 0) ? 0 : result.Diffs.Max(static frame => frame.MaxChannelDelta));

            Console.WriteLine(
                $"frames compared: {result.FrameCount} ({result.Width}x{result.Height}); frames with diffs: {result.Diffs.Count}; max channel delta: {overallMaxDelta}"
            );
            return 0;
        }
        private static int ExtractFrame(string[] arguments)
        {
            if ((arguments.Length != 3) || !int.TryParse(arguments[1], out var frameIndex))
            {
                Console.Error.WriteLine("ERROR: usage: extract-frame <capture.png> <frameIndex> <out.png>");
                return 2;
            }

            var (width, height) = ApngFrameExtractor.ExtractFrame(Path.GetFullPath(arguments[0]), frameIndex, Path.GetFullPath(arguments[2]));
            Console.WriteLine($"frame {frameIndex} -> {Path.GetFullPath(arguments[2])} ({width}x{height})");
            return 0;
        }
        private static int FrameCount(string[] arguments)
        {
            if (arguments.Length != 1)
            {
                Console.Error.WriteLine("ERROR: usage: frame-count <capture.png>");
                return 2;
            }

            Console.WriteLine(ApngFrameExtractor.GetFrameCount(Path.GetFullPath(arguments[0])));
            return 0;
        }
        private static void PrintUsage()
        {
            Console.Error.WriteLine("usage: dotnet run tools/Tools.cs -- <command> [options]");
            Console.Error.WriteLine("  parity          [-NoBuild]");
            Console.Error.WriteLine("  schema          [<out=schema/run.schema.json>] [-NoBuild]");
            Console.Error.WriteLine("  fuzz            [-Count N] [-Start S] [-Timeout secs] [-Config Debug] [-NoBuild]");
            Console.Error.WriteLine("  compare-frames  <captureA.png> <captureB.png>");
            Console.Error.WriteLine("  extract-frame   <capture.png> <frameIndex> <out.png>");
            Console.Error.WriteLine("  frame-count     <capture.png>");
        }
    }

    /// <summary>
    /// Extracts a composited frame from an AnimatedPngWriter capture as a standalone
    /// PNG. Reads exactly what the writer emits — filter byte 0 on every scanline, zlib
    /// streams, blend SOURCE delta regions, color type 6 (RGBA8) or 3 (indexed + PLTE)
    /// — and throws on anything else rather than guessing. CLI: the extract-frame and
    /// frame-count subcommands.
    /// </summary>
    internal static class ApngFrameExtractor
    {
        private sealed class FrameRecord
        {
            public List<byte[]> CompressedParts { get; } = [];

            public int Height;
            public int Left;
            public int Top;
            public int Width;
        }

        public readonly record struct FrameDiff(int FrameIndex, int DiffPixelCount, int MaxChannelDelta);
        public readonly record struct CaptureComparison(int Width, int Height, int FrameCount, List<FrameDiff> Diffs);

        /// <summary>Walks two captures in lockstep, compositing each frame and diffing
        /// the RGBA canvases — the quantitative companion to a hash-only verdict
        /// (how FAR did drifted frames move, not just whether).</summary>
        public static CaptureComparison CompareCaptures(string apngPathA, string apngPathB)
        {
            ParseApng(apngPathA, out var widthA, out var heightA, out var colorTypeA, out var paletteA, out var framesA);
            ParseApng(apngPathB, out var widthB, out var heightB, out var colorTypeB, out var paletteB, out var framesB);
            if ((widthA != widthB) || (heightA != heightB))
            {
                throw new InvalidDataException($"Capture canvases differ: {widthA}x{heightA} vs {widthB}x{heightB}.");
            }

            if (framesA.Count != framesB.Count)
            {
                throw new InvalidDataException($"Capture frame counts differ: {framesA.Count} vs {framesB.Count}.");
            }

            var bytesPerPixelA = ((colorTypeA == 6) ? 4 : 1);
            var bytesPerPixelB = ((colorTypeB == 6) ? 4 : 1);
            var canvasA = new byte[((widthA * heightA) * bytesPerPixelA)];
            var canvasB = new byte[((widthB * heightB) * bytesPerPixelB)];
            var diffs = new List<FrameDiff>();

            for (var index = 0; (index < framesA.Count); index++)
            {
                ApplyFrame(canvasA, framesA[index], index, widthA, bytesPerPixelA);
                ApplyFrame(canvasB, framesB[index], index, widthB, bytesPerPixelB);
                var rgbaA = ((colorTypeA == 6) ? canvasA : ExpandIndexed(canvasA, paletteA!));
                var rgbaB = ((colorTypeB == 6) ? canvasB : ExpandIndexed(canvasB, paletteB!));
                var diffPixelCount = 0;
                var maxChannelDelta = 0;

                for (var pixelOffset = 0; (pixelOffset < rgbaA.Length); pixelOffset += 4)
                {
                    var delta = 0;

                    for (var channel = 0; (channel < 4); channel++)
                    {
                        delta = Math.Max(delta, Math.Abs((rgbaA[(pixelOffset + channel)] - rgbaB[(pixelOffset + channel)])));
                    }

                    if (delta > 0)
                    {
                        diffPixelCount++;
                        maxChannelDelta = Math.Max(maxChannelDelta, delta);
                    }
                }

                if (diffPixelCount > 0)
                {
                    diffs.Add(new FrameDiff(index, diffPixelCount, maxChannelDelta));
                }
            }

            return new CaptureComparison(widthA, heightA, framesA.Count, diffs);
        }
        public static int GetFrameCount(string apngPath)
        {
            ParseApng(apngPath, out _, out _, out _, out _, out var frames);
            return frames.Count;
        }

        /// <summary>Composites frames 0..frameIndex and writes the result as a plain
        /// RGBA8 PNG. Returns the canvas dimensions.</summary>
        public static (int Width, int Height) ExtractFrame(string apngPath, int frameIndex, string outputPngPath)
        {
            ParseApng(apngPath, out var width, out var height, out var colorType, out var palette, out var frames);
            if ((frameIndex < 0) || (frameIndex >= frames.Count))
            {
                throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, $"APNG holds {frames.Count} frames.");
            }

            var bytesPerPixel = ((colorType == 6) ? 4 : 1);
            var canvas = new byte[((width * height) * bytesPerPixel)];

            for (var index = 0; (index <= frameIndex); index++)
            {
                ApplyFrame(canvas, frames[index], index, width, bytesPerPixel);
            }

            var rgba = ((colorType == 6) ? canvas : ExpandIndexed(canvas, palette!));

            WritePng(outputPngPath, width, height, rgba);
            return (width, height);
        }

        /// <summary>Inflates one frame record and blends (blend SOURCE: copies) its
        /// delta region onto the canvas.</summary>
        private static void ApplyFrame(byte[] canvas, FrameRecord frame, int index, int width, int bytesPerPixel)
        {
            var raw = Inflate(frame.CompressedParts);
            var rowByteLength = (frame.Width * bytesPerPixel);
            var expectedLength = ((rowByteLength + 1) * frame.Height);

            if (raw.Length != expectedLength)
            {
                throw new InvalidDataException($"Frame {index}: inflated {raw.Length} bytes, expected {expectedLength}.");
            }

            for (var row = 0; (row < frame.Height); row++)
            {
                var rowOffset = (row * (rowByteLength + 1));

                if (raw[rowOffset] != 0)
                {
                    throw new InvalidDataException($"Frame {index} row {row}: filter {raw[rowOffset]} unsupported (writer emits 0).");
                }

                Array.Copy(raw, (rowOffset + 1), canvas, ((((frame.Top + row) * width) + frame.Left) * bytesPerPixel), rowByteLength);
            }
        }
        private static byte[] ExpandIndexed(byte[] indexed, byte[] palette)
        {
            var rgba = new byte[(indexed.Length * 4)];

            for (var index = 0; (index < indexed.Length); index++)
            {
                var paletteOffset = (indexed[index] * 3);

                rgba[(index * 4)] = palette[paletteOffset];
                rgba[((index * 4) + 1)] = palette[(paletteOffset + 1)];
                rgba[((index * 4) + 2)] = palette[(paletteOffset + 2)];
                rgba[((index * 4) + 3)] = byte.MaxValue;
            }

            return rgba;
        }
        private static byte[] Inflate(List<byte[]> compressedParts)
        {
            using var concatenated = new MemoryStream();

            foreach (var part in compressedParts)
            {
                concatenated.Write(part, 0, part.Length);
            }

            concatenated.Position = 0;
            using var zlibStream = new ZLibStream(concatenated, CompressionMode.Decompress);
            using var inflated = new MemoryStream();

            zlibStream.CopyTo(inflated);
            return inflated.ToArray();
        }
        private static void ParseApng(
            string apngPath,
            out int width,
            out int height,
            out int colorType,
            out byte[]? palette,
            out List<FrameRecord> frames
        )
        {
            var bytes = File.ReadAllBytes(apngPath);

            width = 0;
            height = 0;
            colorType = 0;
            palette = null;
            frames = [];
            FrameRecord? currentFrame = null;
            var offset = 8;

            while ((offset + 8) <= bytes.Length)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                var type = Encoding.ASCII.GetString(bytes, (offset + 4), 4);
                var data = bytes.AsSpan((offset + 8), length);

                switch (type)
                {
                    case "IHDR":
                        width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                        height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                        colorType = data[9];
                        if ((data[8] != 8) || ((colorType != 6) && (colorType != 3)))
                        {
                            throw new InvalidDataException($"Unsupported PNG: bit depth {data[8]}, color type {colorType}.");
                        }

                        break;
                    case "PLTE":
                        palette = data.ToArray();
                        break;
                    case "fcTL":
                        currentFrame = new FrameRecord
                        {
                            Width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
                            Height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)),
                            Left = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4)),
                            Top = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4)),
                        };
                        frames.Add(currentFrame);
                        break;
                    case "IDAT":
                        currentFrame?.CompressedParts.Add(data.ToArray());
                        break;
                    case "fdAT":
                        currentFrame?.CompressedParts.Add(data[4..].ToArray());
                        break;
                }

                offset += (12 + length);
            }
        }
        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> lengthBytes = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
            stream.Write(lengthBytes);
            Span<byte> typeBytes = stackalloc byte[4];

            for (var index = 0; (index < 4); index++)
            {
                typeBytes[index] = (byte)type[index];
            }

            stream.Write(typeBytes);
            stream.Write(data);
            var crc = 0xFFFFFFFFu;

            crc = UpdateCrc(crc, typeBytes);
            crc = UpdateCrc(crc, data);
            BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, crc ^ 0xFFFFFFFFu);
            stream.Write(lengthBytes);
        }
        private static void WritePng(string path, int width, int height, byte[] rgba)
        {
            using var stream = File.Create(path);

            stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);

            Span<byte> ihdr = stackalloc byte[13];

            BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(stream, "IHDR", ihdr);

            using var compressed = new MemoryStream();

            using (var zlibStream = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var rowByteLength = (width * 4);

                for (var row = 0; (row < height); row++)
                {
                    zlibStream.WriteByte(0);
                    zlibStream.Write(rgba, (row * rowByteLength), rowByteLength);
                }
            }

            WriteChunk(stream, "IDAT", compressed.ToArray());
            WriteChunk(stream, "IEND", []);
        }
        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; (bit < 8); bit++)
                {
                    crc = (((crc & 1) == 1) ? 0xEDB88320u ^ (crc >> 1) : (crc >> 1));
                }
            }

            return crc;
        }
    }

    // The build + demo-run helpers shared by the parity and schema gates. Wraps
    // ToolProcess so each gate stops re-spelling the dotnet invocation.
    internal static class EngineRun
    {
        public static string RepositoryRoot => Path.GetDirectoryName(ToolProcess.GetToolsDirectory())!;

        public static int Build(string configuration) =>
            ToolProcess.RunStreamed(environment: null, "dotnet", "build", Path.Combine(RepositoryRoot, "Puck.slnx"), "-c", configuration);

        public static string PostProject =>
            Path.Combine(RepositoryRoot, "src", "Puck.Post", "Puck.Post.csproj");

        // Runs the Puck.Post battery filtered to a single tier (A/B/C/D); the battery writes artifacts/post/
        // (including post-report.txt) relative to the working directory and exits 0 pass / 1 gate-fail / 2 infra-fail.
        public static int RunPostTier(string tier) =>
            ToolProcess.RunStreamed(null, "dotnet", "run", "--no-build", "--project", PostProject, "-c", "Release", "--", "--tier", tier);
    }
    internal static class ToolProcess
    {
        // Child inherits this console (output streams live); the environment overlay
        // applies to the child only.
        public static int RunStreamed(IReadOnlyDictionary<string, string>? environment, string fileName, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    startInfo.Environment[key] = value;
                }
            }

            using var process = (Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}."));

            process.WaitForExit();
            return process.ExitCode;
        }

        // Spawns a child, captures stdout+stderr, and enforces a hard wall-clock timeout — the watchdog the fuzzer
        // needs (the engine has no GPU-wait timeout, so a hung/TDR'd child is killed here). Async event-based reads
        // avoid the full-buffer deadlock. Returns (exitCode, timedOut, combinedOutput); on timeout exitCode is -1 and
        // the entire process tree is killed.
        public static (int ExitCode, bool TimedOut, string Output) RunWithTimeout(int timeoutMilliseconds, string fileName, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var output = new StringBuilder();
            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, eventArguments) =>
            {
                if (eventArguments.Data is not null)
                {
                    lock (output)
                    {
                        output.AppendLine(eventArguments.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, eventArguments) =>
            {
                if (eventArguments.Data is not null)
                {
                    lock (output)
                    {
                        output.AppendLine(eventArguments.Data);
                    }
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start {fileName}.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may have exited between the timeout and the kill; ignore.
                }

                process.WaitForExit(5000);

                lock (output)
                {
                    return (-1, true, output.ToString());
                }
            }

            // Drain the async read handlers (overload with no timeout flushes them after exit).
            process.WaitForExit();

            lock (output)
            {
                return (process.ExitCode, false, output.ToString());
            }
        }

        // The app compiles to a temp directory, so the entry-point file path (a .NET 10
        // file-based-app property) — not AppContext.BaseDirectory — locates tools/.
        public static string GetToolsDirectory()
        {
            if (AppContext.GetData("EntryPointFilePath") is string entryPointFilePath)
            {
                return Path.GetDirectoryName(Path.GetFullPath(entryPointFilePath))!;
            }

            var directory = new DirectoryInfo(Environment.CurrentDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Puck.slnx")))
                {
                    return Path.Combine(directory.FullName, "tools");
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository root (no Puck.slnx above the current directory).");
        }
    }
}
