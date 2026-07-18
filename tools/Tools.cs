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
//   Emits the data-driven run-document JSON Schema by running the demo's headless --emit-schema. The schema is
//   exported from the live System.Text.Json source-gen model, so it cannot drift from the document types. -NoBuild
//   skips the build.
//
// compare-frames <a.png> <b.png>                       Pixel-diff two APNG captures frame-by-frame.
// extract-frame <capture.png> <frameIndex> <out.png>   Composite frames 0..index into one PNG.
// frame-count <capture.png>                            Count frames in an APNG capture.
//
// scan [<root=src>] [-Only comments,comment-smells,locks,clones]
//      [-OutDir dir] [-Grouped] [-MaxPerChunk N] [-MinTokens N] [-MinStatements N] [-NoBlocks]
//   Roslyn source sweep: parses every .cs under <root> (obj/bin pruned) once and runs the
//   selected analyzers over the shared corpus (default: all). Emits one JSONL record per finding
//   to <OutDir>/<name>.jsonl (default artifacts/scan; a lone -Only with no -OutDir/-Grouped
//   streams to stdout); -Grouped also writes a <name>.grouped.json fan-out work-list.
//     comments       — every non-XML inline comment (// and /* */).
//     comment-smells — those comments bucketed by weakness (sync-coupling / debt-marker /
//                      banner-divider / commented-out-code / unclassified), plus unresolved
//                      shader-file or UPPER_SNAKE-define references.
//     locks          — synchronization sites, kind-tagged (lock statements, lock-primitive
//                      declarations, Monitor.*/Interlocked.* calls, [MethodImpl(Synchronized)]).
//     clones         — structurally identical callable bodies and nested blocks, Type-1/Type-2
//                      fingerprinted; gated by -MinTokens/-MinStatements, -NoBlocks drops blocks.
//
// format [<root=src>] [-WhatIf] [-Verify]
//        [-Only attr-order,member-spacing,member-order,null-pattern,paren-clarity,logical-lines,
//               arg-lines,ternary-lines,init-order,trailing-comma,decl-spacing,literal-var,named-args]
//   Source rewriters for conventions .editorconfig cannot express. Phase 0 always runs
//   `dotnet format whitespace` per project first (the .editorconfig baseline this layers onto),
//   then the bare command runs the semantics-preserving normalizers (attr-order, member-spacing,
//   member-order, null-pattern, paren-clarity, init-order, trailing-comma, decl-spacing, literal-var,
//   named-args); the
//   vertical line-wrappers (arg-lines, logical-lines, ternary-lines) stay opt-in via -Only. -WhatIf reports
//   drift and exits 1 instead of rewriting. -Verify audits without writing: it also fails if a
//   pass would introduce syntax errors, or if a rewrite is not a fixed point (running the pipeline
//   twice differs from once — a non-idempotent pass). A pass that would corrupt a file is always
//   skipped, never written, even in a plain rewrite run.
//     attr-order     — one attribute per list/line, alphabetized.
//     member-spacing — blank-line grouping between type members.
//     member-order   — a const block or property block (same kind + scope, comment-free) is
//                      sorted alphabetically by name; non-const fields are never reordered.
//     null-pattern   — `== null` / `!= null` -> `is null` / `is not null`.
//     paren-clarity  — explicit precedence parens (`((0 == a) || (0 == b))`, `var x = (a + b)`).
//     logical-lines  — multi-operand &&/|| in an if/while condition or parenthesized return:
//                      one operand per line, operator trailing, hanging parens.
//     arg-lines      — a call with >1 argument: one argument per line, hanging close paren.
//     ternary-lines  — a conditional `c ? t : f`: condition then `? t` / `: f`, operators
//                      leading, each branch indented one level beyond the condition.
//     init-order     — object-initializer members alphabetized.
//     trailing-comma — trailing comma on a multi-line initializer's last element.
//     decl-spacing   — one blank line between a local-declaration run and the next statement.
//     literal-var    — `uint x = 0;` -> `var x = 0U;` for suffix-bearing primitives.
//     named-args     — call arguments named and alphabetized (semantic; resolves symbols).
//   Required braces live in .editorconfig (IDE0011, csharp_prefer_braces) and are applied by
//   `dotnet format style`, not by a pass here.
#:package Microsoft.CodeAnalysis.CSharp@4.*
#nullable enable
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
                case "scan":
                    return ScanCommand.Run(commandArguments);
                case "format":
                    return FormatCommand.Run(commandArguments);
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

        // Emits the data-driven run-document JSON Schema by running the demo's headless --emit-schema (so the schema
        // is exported from the live System.Text.Json source-gen model and can never drift from the types). The first
        // positional argument overrides the output path (default schema/run.schema.json); -NoBuild skips the build.
        private static int SchemaCommand(string[] arguments)
        {
            var output = (arguments.FirstOrDefault(static argument => !argument.StartsWith("-", StringComparison.Ordinal)) ?? Path.Combine(EngineRun.RepositoryRoot, "schema", "run.schema.json"));

            if (!ToolsNoBuild(arguments) && (EngineRun.Build("Debug") != 0))
            {
                Console.Error.WriteLine("ERROR: build failed.");
                return 2;
            }

            return EngineRun.RunDemo("--emit-schema", output);
        }
        private static bool ToolsNoBuild(string[] arguments) =>
            arguments.Any(static argument => string.Equals(argument, "-NoBuild", StringComparison.OrdinalIgnoreCase));

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
            Console.Error.WriteLine(
                "  scan            [<root=src>] [-Only comments,comment-smells,locks,clones] [-OutDir <dir>] [-Grouped] [-MaxPerChunk N] [-MinTokens N] [-MinStatements N] [-NoBlocks]"
            );
            Console.Error.WriteLine(
                "  format          [<root=src>] [-WhatIf] [-Only attr-order,member-spacing,member-order,null-pattern,paren-clarity,logical-lines,arg-lines,ternary-lines,init-order,trailing-comma,decl-spacing,literal-var,named-args]"
            );
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
    internal sealed class CommentAnalyzer : ISourceAnalyzer
    {
        // One JSONL record per non-XML comment (the SingleLine // and MultiLine /* */
        // trivia kinds; the /// and /** */ doc-comment kinds are skipped, which is why
        // "//" inside a string never registers). -Grouped buckets the comments into
        // per-file chunks of line numbers — the work-list a fan-out staleness audit fans
        // across, big files split so no reviewer judges too many comments at once. Hand-
        // writes its json: the file-based app's trimming/AOT analyzers reject the
        // reflection-based JsonSerializer.Serialize.
        public string Name => "comments";

        public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options)
        {
            var jsonl = new StringBuilder();
            var perFile = new Dictionary<string, int>();
            var byFile = new Dictionary<string, List<(int Line, string Text)>>();
            var single = 0;
            var multi = 0;

            foreach (var parsed in corpus.Files)
            {
                var relative = parsed.Relative;

                foreach (var trivia in parsed.Root.DescendantTrivia())
                {
                    var kind = trivia.Kind();
                    var isSingle = (kind == SyntaxKind.SingleLineCommentTrivia);

                    if (!isSingle && (kind != SyntaxKind.MultiLineCommentTrivia))
                    {
                        continue;
                    }

                    var span = trivia.GetLocation().GetLineSpan();
                    var startLine = (span.StartLinePosition.Line + 1);
                    var endLine = (span.EndLinePosition.Line + 1);
                    var text = trivia.ToString().Trim();

                    jsonl.Append('{')
                        .Append("\"file\":").Append(ScanJsonl.JsonString(relative)).Append(',')
                        .Append("\"line\":").Append(startLine).Append(',')
                        .Append("\"endLine\":").Append(endLine).Append(',')
                        .Append("\"kind\":").Append((isSingle ? "\"single\"" : "\"multi\"")).Append(',')
                        .Append("\"text\":").Append(ScanJsonl.JsonString(text))
                        .Append("}\n");

                    if (isSingle)
                    {
                        single++;
                    }
                    else
                    {
                        multi++;
                    }

                    perFile[relative] = (perFile.GetValueOrDefault(relative) + 1);
                    if (!byFile.TryGetValue(relative, out var lines))
                    {
                        lines = [];
                        byFile[relative] = lines;
                    }

                    lines.Add((startLine, text));
                }
            }

            var total = (single + multi);

            Console.Error.WriteLine(
                $"scan[comments]: {total} inline comments ({single} single-line, {multi} block) across {perFile.Count} files (of {corpus.FileCount} scanned)."
            );
            foreach (var line in ScanJsonl.TopFiles(perFile))
            {
                Console.Error.WriteLine(line);
            }

            return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile, options.MaxPerChunk));
        }
    }

    // The comment-WEAKNESS classifier — the same non-XML inline-comment corpus as
    // CommentAnalyzer, but each comment is bucketed by the kind of weakness it represents
    // (premise: self-documenting code needs no inline comment, so every one is a smell to
    // triage). Buckets, first match wins:
    //   sync-coupling      — a prose guardrail over a constraint the compiler can't see
    //                        (source order: "load-bearing; do not alphabetize") or can't
    //                        span (a C#<->GLSL contract: "KEEP IN SYNC with <file>"). The
    //                        design-smell bucket: these mark a missing single source of
    //                        truth, not a documentation gap — question the design, don't
    //                        just reword the comment.
    //   debt-marker        — TODO/FIXME/HACK/XXX/REVISIT/"for now": tracked deferred work.
    //   banner-divider     — a run of >=4 rule glyphs: structure, not information.
    //   commented-out-code — the body parses as a C# statement (with a real terminator/
    //                        operator/keyword): dead code left in a comment.
    //   unclassified       — everything else: the work-list a fan-out audit judges by hand
    //                        (does it lie? does it earn its keep?). Purely syntactic
    //                        detection can bucket the first four; whether a comment LIES is
    //                        a semantic claim only an agent reading the code can settle.
    // Orthogonally, any comment that NAMES a cross-artifact referent (a shader file or an
    // UPPER_SNAKE define) is resolved against the shader + C# corpus; an unresolved referent
    // is the one slice of "the comment lies" a tool can prove. Substring resolution biases
    // toward "exists" (it cries wolf rarely, by design). Hand-writes its json like siblings.
    internal sealed class CommentSmellAnalyzer : ISourceAnalyzer
    {
        // The design smell: prose standing in for a constraint the compiler can't enforce —
        // intra-source order ("load-bearing; do not alphabetize") or a cross-language
        // contract ("keep in sync with <shader>"). `must match/mirror` is gated by a nearby
        // structure word so a plain API fact ("must match the framebuffer") stays out.
        private static readonly Regex SyncPattern = new(
            @"keep[\s\w]{0,16}in sync|kept in sync|stay(?:s|ing)? in sync|in lockstep|do not (?:alphabetize|reorder|re-order|sort)|load-bearing|same order as|must (?:match|mirror)\b[^.\n]*\b(?:layout|order|struct|block|offset|enum|field|kernel|glsl|shader|push-constant)\b|mirror of the",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex DebtPattern = new(@"\b(?:TODO|FIXME|HACK|XXX|KLUDGE|REVISIT)\b|\bfor now\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BannerPattern = new(@"[-=*#_~]{4,}", RegexOptions.Compiled);
        // A C# code signal: a trailing terminator, a leading statement keyword, or an
        // operator prose rarely carries. The gate keeps an English sentence that happens to
        // parse as a bare expression out of the commented-out-code bucket.
        private static readonly Regex CodeSignalPattern = new(
            @";\s*$|^\s*(?:return|if|for|foreach|while|var|using|throw|await|public|private|internal|protected)\b|=>|==|!=|&&|\|\|",
            RegexOptions.Compiled
        );
        // Cross-artifact referents a stale comment would dangle: a shader file name, or an
        // UPPER_SNAKE define (>= one underscore, so single words like NOTE/RED and acronyms
        // like RGBA never register).
        private static readonly Regex ShaderFilePattern = new(@"\b[\w-]+\.(?:glsl|comp|frag|vert|hlsl)\b", RegexOptions.Compiled);
        private static readonly Regex SymbolPattern = new(@"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b", RegexOptions.Compiled);

        public string Name => "comment-smells";

        public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options)
        {
            var haystack = BuildReferentHaystack(corpus, out var shaderFileNames);
            var jsonl = new StringBuilder();
            var byFile = new Dictionary<string, List<(int Line, string Text)>>();
            var bucketCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var unresolved = 0;

            foreach (var parsed in corpus.Files)
            {
                foreach (var trivia in parsed.Root.DescendantTrivia())
                {
                    var kind = trivia.Kind();
                    var isSingle = (kind == SyntaxKind.SingleLineCommentTrivia);

                    if (!isSingle && (kind != SyntaxKind.MultiLineCommentTrivia))
                    {
                        continue;
                    }

                    var text = trivia.ToString().Trim();
                    var body = StripMarkers(text);
                    var bucket = Classify(body);

                    bucketCounts[bucket] = (bucketCounts.GetValueOrDefault(bucket) + 1);
                    var references = ResolveReferences(body, haystack, shaderFileNames, out var anyUnresolved);

                    if (anyUnresolved)
                    {
                        unresolved++;
                    }

                    var span = trivia.GetLocation().GetLineSpan();
                    var startLine = (span.StartLinePosition.Line + 1);

                    jsonl.Append('{')
                        .Append("\"file\":").Append(ScanJsonl.JsonString(parsed.Relative)).Append(',')
                        .Append("\"line\":").Append(startLine).Append(',')
                        .Append("\"endLine\":").Append((span.EndLinePosition.Line + 1)).Append(',')
                        .Append("\"kind\":").Append((isSingle ? "\"single\"" : "\"multi\"")).Append(',')
                        .Append("\"bucket\":").Append(ScanJsonl.JsonString(bucket)).Append(',')
                        .Append("\"text\":").Append(ScanJsonl.JsonString(text));
                    if (references.Length > 0)
                    {
                        jsonl.Append(",\"references\":[").Append(references).Append(']');
                    }

                    jsonl.Append("}\n");

                    if (!byFile.TryGetValue(parsed.Relative, out var lines))
                    {
                        lines = [];
                        byFile[parsed.Relative] = lines;
                    }

                    lines.Add((startLine, text));
                }
            }

            var total = bucketCounts.Values.Sum();

            Console.Error.WriteLine(
                $"scan[comment-smells]: {total} inline comments classified across {byFile.Count} files (of {corpus.FileCount} scanned)."
            );
            foreach (var (bucket, count) in bucketCounts.OrderByDescending(static pair => pair.Value))
            {
                Console.Error.WriteLine($"{count,5}  {bucket}");
            }

            Console.Error.WriteLine($"{unresolved,5}  (comments with an UNRESOLVED cross-artifact referent — provable staleness)");
            return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile, options.MaxPerChunk));
        }

        private static string Classify(string body)
        {
            if (SyncPattern.IsMatch(body))
            {
                return "sync-coupling";
            }

            if (DebtPattern.IsMatch(body))
            {
                return "debt-marker";
            }

            if (BannerPattern.IsMatch(body))
            {
                return "banner-divider";
            }

            if (LooksLikeCode(body))
            {
                return "commented-out-code";
            }

            return "unclassified";
        }

        // True when the body parses as a C# statement with no errors AND carries a real code
        // signal — so dead code registers but an English sentence that happens to parse as a
        // bare expression statement does not.
        private static bool LooksLikeCode(string body)
        {
            if ((body.Length == 0) || !CodeSignalPattern.IsMatch(body))
            {
                return false;
            }

            return !SyntaxFactory.ParseStatement(body).GetDiagnostics().Any(static d => (d.Severity == DiagnosticSeverity.Error));
        }

        // The comment text without its // or /* */ delimiters, so the classifiers see only
        // the prose/code.
        private static string StripMarkers(string text)
        {
            var trimmed = text;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }
            else if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
                if (trimmed.EndsWith("*/", StringComparison.Ordinal))
                {
                    trimmed = trimmed[..^2];
                }
            }

            return trimmed.Trim();
        }

        // One text blob of every scanned .cs file plus every shader source, and the set of
        // shader file names — what a "does this referent still exist?" check resolves
        // against (substring existence, not exact symbol binding).
        private static string BuildReferentHaystack(SourceCorpus corpus, out HashSet<string> shaderFileNames)
        {
            var builder = new StringBuilder();

            shaderFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parsed in corpus.Files)
            {
                builder.Append(parsed.Text).Append('\n');
            }

            var sourceRoot = Path.Combine(EngineRun.RepositoryRoot, "src");

            if (Directory.Exists(sourceRoot))
            {
                foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (Path.GetExtension(path).ToLowerInvariant() is ".glsl" or ".comp" or ".frag" or ".vert" or ".hlsl")
                    {
                        shaderFileNames.Add(Path.GetFileName(path));
                        builder.Append(File.ReadAllText(path)).Append('\n');
                    }
                }
            }

            return builder.ToString();
        }

        // The cross-artifact referents named in the body, each tagged resolved/dangling, as
        // the JSON array body (no brackets); sets anyUnresolved when one dangles.
        private static string ResolveReferences(string body, string haystack, HashSet<string> shaderFileNames, out bool anyUnresolved)
        {
            anyUnresolved = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var parts = new List<string>();

            foreach (Match match in ShaderFilePattern.Matches(body))
            {
                if (!seen.Add(match.Value))
                {
                    continue;
                }

                var resolved = shaderFileNames.Contains(match.Value);

                anyUnresolved |= !resolved;
                parts.Add(Reference(match.Value, "file", resolved));
            }

            foreach (Match match in SymbolPattern.Matches(body))
            {
                if (!seen.Add(match.Value))
                {
                    continue;
                }

                var resolved = haystack.Contains(match.Value, StringComparison.Ordinal);

                anyUnresolved |= !resolved;
                parts.Add(Reference(match.Value, "symbol", resolved));
            }

            return string.Join(",", parts);
        }
        private static string Reference(string token, string kind, bool resolved) =>
            $"{{\"token\":{ScanJsonl.JsonString(token)},\"kind\":\"{kind}\",\"resolved\":{(resolved ? "true" : "false")}}}";
    }

    // Shared output helpers for the Roslyn scan commands (comment-scan, lock-scan): the
    // hand-written JSONL string escaper and the per-file chunked work-list a fan-out
    // audit consumes. Both commands emit the same byFile shape, so the grouping is
    // identical — only what populates each line differs.
    internal static class ScanJsonl
    {
        public static string BuildGroupedChunks(Dictionary<string, List<(int Line, string Text)>> byFile, int maxPerChunk)
        {
            var builder = new StringBuilder("[");
            var firstChunk = true;

            foreach (var (file, sites) in byFile.OrderByDescending(static pair => pair.Value.Count))
            {
                var chunkCount = (((sites.Count + maxPerChunk) - 1) / maxPerChunk);

                for (var offset = 0; (offset < sites.Count); offset += maxPerChunk)
                {
                    if (!firstChunk)
                    {
                        builder.Append(',');
                    }

                    firstChunk = false;
                    builder.Append('{')
                        .Append("\"file\":").Append(JsonString(file)).Append(',')
                        .Append("\"chunk\":").Append((offset / maxPerChunk)).Append(',')
                        .Append("\"chunks\":").Append(chunkCount).Append(',')
                        .Append("\"lines\":[");
                    var end = Math.Min((offset + maxPerChunk), sites.Count);

                    for (var lineIndex = offset; (lineIndex < end); lineIndex++)
                    {
                        if (lineIndex > offset)
                        {
                            builder.Append(',');
                        }

                        builder.Append(sites[lineIndex].Line);
                    }

                    builder.Append("]}");
                }
            }

            return builder.Append(']').ToString();
        }

        // The densest files first, formatted for the stderr digest every record analyzer
        // prints — `<count>  <file>`, top 30 by default.
        public static IEnumerable<string> TopFiles(Dictionary<string, int> perFile, int take = 30) =>
            perFile.OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Take(take)
                .Select(static pair => $"{pair.Value,5}  {pair.Key}");

        // Minimal JSON string escaper. The toolbox only ever reads json back through
        // JsonDocument, so this needs to round-trip, not to be a general serializer.
        public static string JsonString(string value)
        {
            var builder = new StringBuilder("\"");

            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
    internal sealed class LockAnalyzer : ISourceAnalyzer
    {
        // Roslyn-backed synchronization-primitive inventory — the concurrency-audit
        // sibling of comment-scan. Parses every .cs file under a root and emits one JSONL
        // record per lock site, each tagged with a `kind`:
        //   lock                — a `lock (expr) {...}` statement; text is the lock-object
        //                         EXPRESSION (the field that drives lock ordering and the
        //                         lock-on-this/typeof/public/string smells), NOT the body.
        //   lock-type/semaphore/mutex/rwlock/rwlock-slim/spinlock
        //                       — a field/local/using/for/property DECLARATION of an
        //                         instance primitive (Lock is System.Threading.Lock, net9+).
        //   monitor/interlocked — a Monitor.* / Interlocked.* member call (a static-class
        //                         call IS the lock site; there is no instance to anchor on).
        //   synchronized-method — a [MethodImpl(MethodImplOptions.Synchronized)] attribute.
        // Detection is purely syntactic (no semantic model): a field typed `object` only
        // surfaces through the `lock (_gate)` that uses it — which is the audit signal
        // anyway — and a fully-aliased/using-static primitive may slip through. The axes
        // are disjoint, so no node is counted twice (a Lock field and the lock() that uses
        // it are two distinct sites, by design). Same -Grouped work-list, -Jsonl sink, and
        // stderr summary as comment-scan; like the other analyzers it hand-writes its json.

        // Static synchronization classes whose member calls are themselves the lock site.
        private static readonly Dictionary<string, string> StaticLockClasses = new(StringComparer.Ordinal)
        {
            ["Monitor"] = "monitor",
            ["Interlocked"] = "interlocked",
        };

        // Instance synchronization types, recorded where they are DECLARED.
        private static readonly Dictionary<string, string> InstanceLockTypes = new(StringComparer.Ordinal)
        {
            ["Lock"] = "lock-type",
            ["SemaphoreSlim"] = "semaphore",
            ["Mutex"] = "mutex",
            ["ReaderWriterLock"] = "rwlock",
            ["ReaderWriterLockSlim"] = "rwlock-slim",
            ["SpinLock"] = "spinlock",
        };
        private static readonly Regex WhitespaceRun = new("\\s+");

        public string Name => "locks";

        public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options)
        {
            var jsonl = new StringBuilder();
            var perFile = new Dictionary<string, int>();
            var byFile = new Dictionary<string, List<(int Line, string Text)>>();
            var kindCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

            foreach (var parsed in corpus.Files)
            {
                var relative = parsed.Relative;

                foreach (var node in parsed.Root.DescendantNodes())
                {
                    if (!Classify(node, out var kind, out var text, out var startLine, out var endLine))
                    {
                        continue;
                    }

                    jsonl.Append('{')
                        .Append("\"file\":").Append(ScanJsonl.JsonString(relative)).Append(',')
                        .Append("\"line\":").Append(startLine).Append(',')
                        .Append("\"endLine\":").Append(endLine).Append(',')
                        .Append("\"kind\":").Append(ScanJsonl.JsonString(kind)).Append(',')
                        .Append("\"text\":").Append(ScanJsonl.JsonString(text))
                        .Append("}\n");

                    kindCounts[kind] = (kindCounts.GetValueOrDefault(kind) + 1);
                    perFile[relative] = (perFile.GetValueOrDefault(relative) + 1);
                    if (!byFile.TryGetValue(relative, out var sites))
                    {
                        sites = [];
                        byFile[relative] = sites;
                    }

                    sites.Add((startLine, text));
                }
            }

            var total = kindCounts.Values.Sum();
            var breakdown = ((total == 0) ? "none" : string.Join(", ", kindCounts.Select(static pair => $"{pair.Value} {pair.Key}")));

            Console.Error.WriteLine($"scan[locks]: {total} lock sites ({breakdown}) across {perFile.Count} files (of {corpus.FileCount} scanned).");
            foreach (var line in ScanJsonl.TopFiles(perFile))
            {
                Console.Error.WriteLine(line);
            }

            return (jsonl.ToString(), ScanJsonl.BuildGroupedChunks(byFile, options.MaxPerChunk));
        }

        // Pure-syntax classification of one node into a lock site, or false for anything
        // that is not one. The cases are mutually exclusive node shapes, so a node is
        // recorded at most once.
        private static bool Classify(SyntaxNode node, out string kind, out string text, out int startLine, out int endLine)
        {
            kind = string.Empty;
            text = string.Empty;
            startLine = 0;
            endLine = 0;

            switch (node)
            {
                // `lock (expr) {...}` — record only the header span and the lock-object
                // expression; the body would bloat the record and the target is the signal.
                case LockStatementSyntax lockStatement:
                    kind = "lock";
                    text = Condense(lockStatement.Expression.ToString());
                    SetSpan(lockStatement.LockKeyword.GetLocation(), lockStatement.CloseParenToken.GetLocation(), out startLine, out endLine);
                    return true;

                // Monitor.Enter(...) / Interlocked.Increment(...), including a qualified
                // receiver like System.Threading.Monitor.Enter (rightmost name is matched).
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } invocation
                    when ((ReceiverTypeName(memberAccess.Expression) is { } receiver)
                        && StaticLockClasses.TryGetValue(receiver, out var staticKind)):
                    kind = staticKind;
                    text = Condense(invocation.ToString());
                    SetSpan(invocation.GetLocation(), out startLine, out endLine);
                    return true;

                // Field / local / using / for declaration of an instance primitive.
                case VariableDeclarationSyntax variableDeclaration
                    when InstanceLockTypes.TryGetValue(SimpleTypeName(variableDeclaration.Type), out var declaredKind):
                    kind = declaredKind;
                    text = Condense(variableDeclaration.ToString());
                    SetSpan(variableDeclaration.GetLocation(), out startLine, out endLine);
                    return true;

                // Property of an instance primitive (rare; PropertyDeclaration is not a
                // VariableDeclaration, so it needs its own case).
                case PropertyDeclarationSyntax propertyDeclaration
                    when InstanceLockTypes.TryGetValue(SimpleTypeName(propertyDeclaration.Type), out var propertyKind):
                    kind = propertyKind;
                    text = Condense($"{propertyDeclaration.Type} {propertyDeclaration.Identifier.ValueText}");
                    SetSpan(propertyDeclaration.GetLocation(), out startLine, out endLine);
                    return true;

                // [MethodImpl(MethodImplOptions.Synchronized)] — a whole-method monitor lock.
                case AttributeSyntax attribute
                    when ((SimpleTypeName(attribute.Name) is "MethodImpl" or "MethodImplAttribute")
                        && attribute.ToString().Contains("Synchronized", StringComparison.Ordinal)):
                    kind = "synchronized-method";
                    text = Condense(attribute.ToString());
                    SetSpan(attribute.GetLocation(), out startLine, out endLine);
                    return true;

                default:
                    return false;
            }
        }

        // Rightmost identifier of an invocation receiver: Monitor -> Monitor,
        // System.Threading.Monitor -> Monitor. Anything else (a call result, an indexer)
        // yields null and is not a static lock class.
        private static string? ReceiverTypeName(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => null
        };

        // Rightmost identifier of a possibly-qualified/nullable type name:
        // System.Threading.Lock -> Lock, SemaphoreSlim? -> SemaphoreSlim. Generics and
        // arrays keep their suffix and simply fail to match the lock-type set, as intended.
        private static string SimpleTypeName(TypeSyntax type)
        {
            var name = type.ToString();
            var lastDot = name.LastIndexOf('.');

            if (lastDot >= 0)
            {
                name = name[(lastDot + 1)..];
            }

            return name.TrimEnd('?', ' ');
        }

        // Collapses a node's source text to a trimmed single line and caps the length, so
        // every record is a readable one-liner rather than a wrapped, indented fragment.
        private static string Condense(string text)
        {
            var condensed = WhitespaceRun.Replace(text.Trim(), " ");

            return ((condensed.Length <= 200) ? condensed : string.Concat(condensed.AsSpan(0, 197), "..."));
        }
        private static void SetSpan(Location location, out int startLine, out int endLine)
        {
            var span = location.GetLineSpan();

            startLine = (span.StartLinePosition.Line + 1);
            endLine = (span.EndLinePosition.Line + 1);
        }
        private static void SetSpan(Location start, Location end, out int startLine, out int endLine)
        {
            startLine = (start.GetLineSpan().StartLinePosition.Line + 1);
            endLine = (end.GetLineSpan().EndLinePosition.Line + 1);
        }
    }
    internal sealed class CloneAnalyzer : ISourceAnalyzer
    {
        // Roslyn-backed duplicate-code detector — the refactor-target sibling of the
        // other *-scan commands. Parses every .cs file under a root and clusters
        // structurally identical code, two passes deep:
        //   unit  — a whole callable body (method / ctor / dtor / operator / conversion /
        //           property-or-indexer-or-event accessor / local function), block- or
        //           expression-bodied.
        //   block — a nested `{...}` (an if/else/loop/using/lock/try/switch-section body)
        //           of >= -MinStatements statements, reported ONLY when its enclosing unit
        //           is NOT itself a clone (partial copy-paste) and only at the OUTERMOST
        //           duplicated nesting level, so a method's inner blocks never double-count.
        // Each body is fingerprinted twice over its token stream (trivia-, whitespace- and
        // comment-insensitive): a STRUCTURAL hash that abstracts every identifier and
        // literal to a placeholder (so renamed-variable / changed-constant copies — Type-2
        // clones — still collide) and an EXACT hash over the raw token text (verbatim
        // Type-1 copies). Clustering is on the structural hash; `exactCount` reports how
        // many of a cluster's members are byte-identical, so triage sees verbatim vs
        // renamed at a glance. Clusters are gated by token weight (-MinTokens) — two
        // one-line getters share a fingerprint but are not a refactor target.
        //
        // Members are `abiTainted`-tagged when they live in a marshalling/ABI context
        // (a VulkanNative* file, the Vulkan/Bindings tree, or a Vk*-named type), because
        // CLAUDE.md's NEVER-list makes that "duplication" a deliberate ABI mirror, not a
        // cleanup target — the tag lets a triage pass separate contract from copy-paste.
        // Detection is purely syntactic (no semantic model). One JSONL record per cluster;
        // -Grouped emits a per-cluster work-list (one chunk = one cluster) for a fan-out
        // triage; like its siblings it hand-writes its json. Output is fully deterministic
        // (files, members, and clusters are all ordered) so re-runs diff cleanly.
        public string Name => "clones";

        public (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options)
        {
            var trees = corpus.Files.Select(static file => (file.Relative, file.Root)).ToList();

            var unitBodies = new HashSet<SyntaxNode>();
            var units = CollectUnits(trees, unitBodies);
            var unitClusters = units
                .Where(member => (member.Weight >= options.MinTokens))
                .GroupBy(static member => member.Structural, StringComparer.Ordinal)
                .Where(static group => (group.Count() >= 2))
                .Select(static group => group.ToList())
                .ToList();

            var clusteredBodies = BodySpansByFile(unitClusters);
            var blockClusters = (options.IncludeBlocks
                ? CollectBlockClusters(trees, unitBodies, clusteredBodies, options.MinStatements, options.MinTokens)
                : []);

            var clusters = new List<Cluster>();

            foreach (var group in unitClusters)
            {
                clusters.Add(BuildCluster("unit", group));
            }

            foreach (var group in blockClusters)
            {
                clusters.Add(BuildCluster("block", group));
            }

            clusters = OrderAndNumber(clusters);

            PrintSummary(clusters, options.MinTokens, options.MinStatements, options.IncludeBlocks, corpus.FileCount);
            return (BuildJsonl(clusters), BuildCloneGroups(clusters, options.MaxPerChunk));
        }

        // Pass 1: every callable body, fingerprinted. unitBodies records the block bodies
        // so the block pass can skip them (a unit body is the unit pass's job).
        private static List<Member> CollectUnits(List<(string Relative, SyntaxNode Root)> trees, HashSet<SyntaxNode> unitBodies)
        {
            var units = new List<Member>();

            foreach (var (relative, treeRoot) in trees)
            {
                foreach (var node in treeRoot.DescendantNodes())
                {
                    var body = UnitBody(node);

                    if (body is null)
                    {
                        continue;
                    }

                    if (body is BlockSyntax)
                    {
                        unitBodies.Add(body);
                    }

                    var (structural, exact, weight) = Fingerprint(body);
                    units.Add(CreateMember(relative, node, body, structural, exact, weight));
                }
            }

            return units;
        }

        // The body spans of clustered units, per file — a block inside one of these is
        // redundant with the unit clone, so the block pass skips it.
        private static Dictionary<string, List<(int Start, int End)>> BodySpansByFile(List<List<Member>> clusters)
        {
            var spansByFile = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);

            foreach (var cluster in clusters)
            {
                foreach (var member in cluster)
                {
                    if (!spansByFile.TryGetValue(member.File, out var spans))
                    {
                        spans = [];
                        spansByFile[member.File] = spans;
                    }

                    spans.Add((member.BodyStart, member.BodyEnd));
                }
            }

            return spansByFile;
        }

        // Pass 2: nested blocks of >= minStatements statements that are neither a unit body
        // nor inside a clustered unit, kept only at the OUTERMOST duplicated level so a
        // duplicated outer block never re-reports its inner blocks.
        private static List<List<Member>> CollectBlockClusters(
            List<(string Relative, SyntaxNode Root)> trees,
            HashSet<SyntaxNode> unitBodies,
            Dictionary<string, List<(int Start, int End)>> clusteredBodies,
            int minStatements,
            int minTokens
        )
        {
            var candidates = new List<(SyntaxNode Node, Member Member)>();

            foreach (var (relative, treeRoot) in trees)
            {
                var bodies = clusteredBodies.GetValueOrDefault(relative);

                foreach (var block in treeRoot.DescendantNodes().OfType<BlockSyntax>())
                {
                    if (unitBodies.Contains(block) || (block.Statements.Count < minStatements))
                    {
                        continue;
                    }

                    var start = block.Span.Start;
                    var end = block.Span.End;

                    if ((bodies is not null) && bodies.Any(span => ((span.Start <= start) && (end <= span.End))))
                    {
                        continue;
                    }

                    var (structural, exact, weight) = Fingerprint(block);
                    if (weight < minTokens)
                    {
                        continue;
                    }

                    candidates.Add((block, CreateMember(relative, block, block, structural, exact, weight)));
                }
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                counts[candidate.Member.Structural] = (counts.GetValueOrDefault(candidate.Member.Structural) + 1);
            }

            var clusteredNodes = new HashSet<SyntaxNode>();

            foreach (var candidate in candidates)
            {
                if (counts[candidate.Member.Structural] >= 2)
                {
                    clusteredNodes.Add(candidate.Node);
                }
            }

            var survivors = new List<Member>();

            foreach (var candidate in candidates)
            {
                if ((counts[candidate.Member.Structural] >= 2) && !candidate.Node.Ancestors().Any(clusteredNodes.Contains))
                {
                    survivors.Add(candidate.Member);
                }
            }

            return survivors
                .GroupBy(static member => member.Structural, StringComparer.Ordinal)
                .Where(static group => (group.Count() >= 2))
                .Select(static group => group.ToList())
                .ToList();
        }

        // Heaviest clones first (redundant mass = extra copies x weight), deterministically
        // tie-broken, then numbered.
        private static List<Cluster> OrderAndNumber(List<Cluster> clusters)
        {
            var ordered = clusters
                .OrderByDescending(static cluster => ((cluster.Members.Count - 1) * cluster.Weight))
                .ThenByDescending(static cluster => cluster.Members.Count)
                .ThenBy(static cluster => cluster.Fingerprint, StringComparer.Ordinal)
                .ToList();

            for (var clusterIndex = 0; (clusterIndex < ordered.Count); clusterIndex++)
            {
                ordered[clusterIndex] = ordered[clusterIndex] with { Id = clusterIndex };
            }

            return ordered;
        }
        private static string BuildJsonl(List<Cluster> clusters)
        {
            var jsonl = new StringBuilder();

            foreach (var cluster in clusters)
            {
                var exactByHash = cluster.Members
                    .GroupBy(static member => member.Exact, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

                jsonl.Append('{')
                    .Append("\"id\":").Append(cluster.Id).Append(',')
                    .Append("\"kind\":").Append(ScanJsonl.JsonString(cluster.Kind)).Append(',')
                    .Append("\"fingerprint\":").Append(ScanJsonl.JsonString(cluster.Fingerprint)).Append(',')
                    .Append("\"memberCount\":").Append(cluster.Members.Count).Append(',')
                    .Append("\"exactCount\":").Append(cluster.ExactCount).Append(',')
                    .Append("\"tokenWeight\":").Append(cluster.Weight).Append(',')
                    .Append("\"redundantMass\":").Append(((cluster.Members.Count - 1) * cluster.Weight)).Append(',')
                    .Append("\"abiTainted\":").Append((cluster.Abi ? "true" : "false")).Append(',')
                    .Append("\"label\":").Append(ScanJsonl.JsonString(cluster.Label)).Append(',')
                    .Append("\"members\":[");
                for (var memberIndex = 0; (memberIndex < cluster.Members.Count); memberIndex++)
                {
                    if (memberIndex > 0)
                    {
                        jsonl.Append(',');
                    }

                    var member = cluster.Members[memberIndex];

                    jsonl.Append('{')
                        .Append("\"file\":").Append(ScanJsonl.JsonString(member.File)).Append(',')
                        .Append("\"line\":").Append(member.StartLine).Append(',')
                        .Append("\"endLine\":").Append(member.EndLine).Append(',')
                        .Append("\"unit\":").Append(ScanJsonl.JsonString(member.Unit)).Append(',')
                        .Append("\"exact\":").Append(((exactByHash[member.Exact] >= 2) ? "true" : "false"))
                        .Append('}');
                }

                jsonl.Append("]}\n");
            }

            return jsonl.ToString();
        }
        private static void PrintSummary(List<Cluster> clusters, int minTokens, int minStatements, bool includeBlocks, int filesScanned)
        {
            var unitClusterCount = clusters.Count(static cluster => (cluster.Kind == "unit"));
            var blockClusterCount = clusters.Count(static cluster => (cluster.Kind == "block"));
            var siteCount = clusters.Sum(static cluster => cluster.Members.Count);
            var redundantMass = clusters.Sum(static cluster => ((cluster.Members.Count - 1) * cluster.Weight));
            var abiClusters = clusters.Count(static cluster => cluster.Abi);

            Console.Error.WriteLine(
                $"scan[clones]: {clusters.Count} clone clusters ({unitClusterCount} unit, {blockClusterCount} block; {abiClusters} abi-tainted) over {siteCount} sites; redundant token mass ~{redundantMass} (minTokens={minTokens}, minStatements={minStatements}, blocks={(includeBlocks ? "on" : "off")}, files scanned={filesScanned})."
            );
            foreach (var cluster in clusters.Take(30))
            {
                var fileCount = cluster.Members.Select(static member => member.File).Distinct(StringComparer.Ordinal).Count();
                var abiTag = (cluster.Abi ? "[abi] " : "");
                var exactTag = ((cluster.ExactCount == cluster.Members.Count) ? "exact" : $"{cluster.ExactCount}/{cluster.Members.Count} exact");

                Console.Error.WriteLine(
                    $"{((cluster.Members.Count - 1) * cluster.Weight),7}  x{cluster.Members.Count} w{cluster.Weight} {abiTag}{cluster.Kind} ({exactTag}) {cluster.Label} [{fileCount} file{((fileCount == 1) ? "" : "s")}]"
                );
            }
        }

        // One chunk per cluster (split if a cluster has more than maxPerChunk members), so
        // a fan-out triage spends one agent per duplication cluster.
        private static string BuildCloneGroups(List<Cluster> clusters, int maxPerChunk)
        {
            var builder = new StringBuilder("[");
            var firstChunk = true;

            foreach (var cluster in clusters)
            {
                var chunkCount = (((cluster.Members.Count + maxPerChunk) - 1) / maxPerChunk);

                for (var offset = 0; (offset < cluster.Members.Count); offset += maxPerChunk)
                {
                    if (!firstChunk)
                    {
                        builder.Append(',');
                    }

                    firstChunk = false;
                    builder.Append('{')
                        .Append("\"cluster\":").Append(cluster.Id).Append(',')
                        .Append("\"kind\":").Append(ScanJsonl.JsonString(cluster.Kind)).Append(',')
                        .Append("\"chunk\":").Append((offset / maxPerChunk)).Append(',')
                        .Append("\"chunks\":").Append(chunkCount).Append(',')
                        .Append("\"abiTainted\":").Append((cluster.Abi ? "true" : "false")).Append(',')
                        .Append("\"label\":").Append(ScanJsonl.JsonString(cluster.Label)).Append(',')
                        .Append("\"members\":[");
                    var end = Math.Min((offset + maxPerChunk), cluster.Members.Count);

                    for (var memberIndex = offset; (memberIndex < end); memberIndex++)
                    {
                        if (memberIndex > offset)
                        {
                            builder.Append(',');
                        }

                        var member = cluster.Members[memberIndex];

                        builder.Append('{')
                            .Append("\"file\":").Append(ScanJsonl.JsonString(member.File)).Append(',')
                            .Append("\"line\":").Append(member.StartLine).Append(',')
                            .Append("\"endLine\":").Append(member.EndLine).Append(',')
                            .Append("\"unit\":").Append(ScanJsonl.JsonString(member.Unit))
                            .Append('}');
                    }

                    builder.Append("]}");
                }
            }

            return builder.Append(']').ToString();
        }
        private static Cluster BuildCluster(string kind, List<Member> members)
        {
            members.Sort(
                static (left, right) =>
            {
                var byFile = string.CompareOrdinal(left.File, right.File);

                return ((byFile != 0) ? byFile : left.StartLine.CompareTo(right.StartLine));
            }
            );
            var exactCount = members
                .GroupBy(static member => member.Exact, StringComparer.Ordinal)
                .Max(static group => group.Count());
            var abi = members.Any(static member => member.Abi);

            return new Cluster(-1, kind, members[0].Structural, members[0].Weight, exactCount, abi, members[0].Unit, members);
        }
        private static Member CreateMember(string relative, SyntaxNode node, SyntaxNode body, string structural, string exact, int weight)
        {
            var lineSpan = node.GetLocation().GetLineSpan();

            var (typeName, memberName) = Describe(node);
            var unit = ((typeName.Length == 0) ? memberName : $"{typeName}.{memberName}");

            return new Member(
                relative,
                (lineSpan.StartLinePosition.Line + 1),
                (lineSpan.EndLinePosition.Line + 1),
                body.Span.Start,
                body.Span.End,
                structural,
                exact,
                weight,
                unit,
                IsAbi(relative, typeName)
            );
        }

        // The body to fingerprint for a callable node — its block body, else its
        // expression body, else null (an abstract/partial/extern/interface declaration or
        // an auto-property accessor has no body and is not a clone candidate).
        private static SyntaxNode? UnitBody(SyntaxNode node) => node switch
        {
            MethodDeclarationSyntax method => ((SyntaxNode?)method.Body ?? method.ExpressionBody),
            ConstructorDeclarationSyntax constructor => ((SyntaxNode?)constructor.Body ?? constructor.ExpressionBody),
            DestructorDeclarationSyntax destructor => ((SyntaxNode?)destructor.Body ?? destructor.ExpressionBody),
            OperatorDeclarationSyntax op => ((SyntaxNode?)op.Body ?? op.ExpressionBody),
            ConversionOperatorDeclarationSyntax conversion => ((SyntaxNode?)conversion.Body ?? conversion.ExpressionBody),
            AccessorDeclarationSyntax accessor => ((SyntaxNode?)accessor.Body ?? accessor.ExpressionBody),
            LocalFunctionStatementSyntax local => ((SyntaxNode?)local.Body ?? local.ExpressionBody),
            _ => null
        };
        private static bool IsUnitNode(SyntaxNode node) => (node
            is MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax);

        // A human label for a node: (enclosing type, member). For a block it is the
        // enclosing callable's member name plus the block's line, so two block clones are
        // distinguishable in the report.
        private static (string Type, string Member) Describe(SyntaxNode node)
        {
            var typeName = (node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "");

            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return (typeName, method.Identifier.ValueText);
                case ConstructorDeclarationSyntax:
                    return (typeName, ".ctor");
                case DestructorDeclarationSyntax:
                    return (typeName, "~ctor");
                case OperatorDeclarationSyntax op:
                    return (typeName, $"operator{op.OperatorToken.ValueText}");
                case ConversionOperatorDeclarationSyntax:
                    return (typeName, "operator");
                case AccessorDeclarationSyntax accessor:
                    return (typeName, AccessorName(accessor));
                case LocalFunctionStatementSyntax local:
                    return (typeName, $"local:{local.Identifier.ValueText}");
                case BlockSyntax block:
                    var owner = block.Ancestors().FirstOrDefault(IsUnitNode);
                    var ownerName = ((owner is null) ? "" : Describe(owner).Member);
                    var line = (block.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    return (typeName, $"{ownerName} block@{line}");
                default:
                    return (typeName, "");
            }
        }
        private static string AccessorName(AccessorDeclarationSyntax accessor)
        {
            var owner = accessor.Ancestors().FirstOrDefault(static node => (node is BasePropertyDeclarationSyntax));
            var ownerName = owner switch
            {
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                IndexerDeclarationSyntax => "this[]",
                EventDeclarationSyntax @event => @event.Identifier.ValueText,
                _ => ""
            };

            return $"{ownerName}.{accessor.Keyword.ValueText}";
        }

        // True when a member lives in a marshalling/ABI context the NEVER-list guards, so
        // triage can separate deliberate Vulkan mirrors from real copy-paste.
        private static bool IsAbi(string relative, string typeName) =>
            (relative.Contains("VulkanNative", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("Vulkan/Bindings", StringComparison.OrdinalIgnoreCase)
            || typeName.StartsWith("Vk", StringComparison.Ordinal));

        // Two hashes over the body's token stream (trivia excluded, so whitespace and
        // comments never matter): STRUCTURAL abstracts identifiers and literals to a
        // placeholder (Type-2 clones collide), EXACT keeps the raw token text (Type-1).
        private static (string Structural, string Exact, int Weight) Fingerprint(SyntaxNode body)
        {
            var structural = new StringBuilder();
            var exact = new StringBuilder();
            var weight = 0;

            foreach (var token in body.DescendantTokens())
            {
                structural.Append(CanonicalToken(token)).Append('\u0001');
                exact.Append(token.Text).Append('\u0001');
                weight++;
            }

            return (Hash(structural.ToString()), Hash(exact.ToString()), weight);
        }

        // Structural canonicalization: identifiers and literals collapse to a kind marker
        // (the \u0002 prefix keeps a placeholder from ever colliding with literal token
        // text); keywords, punctuation and operators keep their text, as they ARE the
        // structure that must match.
        private static string CanonicalToken(SyntaxToken token) => token.Kind() switch
        {
            SyntaxKind.IdentifierToken => "\u0002I",
            SyntaxKind.NumericLiteralToken => "\u0002N",
            SyntaxKind.StringLiteralToken => "\u0002S",
            SyntaxKind.Utf8StringLiteralToken => "\u0002S",
            SyntaxKind.SingleLineRawStringLiteralToken => "\u0002S",
            SyntaxKind.MultiLineRawStringLiteralToken => "\u0002S",
            SyntaxKind.Utf8SingleLineRawStringLiteralToken => "\u0002S",
            SyntaxKind.Utf8MultiLineRawStringLiteralToken => "\u0002S",
            SyntaxKind.CharacterLiteralToken => "\u0002C",
            SyntaxKind.InterpolatedStringTextToken => "\u0002T",
            _ => token.Text
        };
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private sealed record Cluster(int Id, string Kind, string Fingerprint, int Weight, int ExactCount, bool Abi, string Label, List<Member> Members);
        private sealed record Member(
            string File,
            int StartLine,
            int EndLine,
            int BodyStart,
            int BodyEnd,
            string Structural,
            string Exact,
            int Weight,
            string Unit,
            bool Abi
        );
    }

    // The blank-line-between-members normalizer (a `format` pass): ZERO blank lines
    // between members that share BOTH kind and accessibility scope (so a run of `public`
    // methods packs tight whether each body is one line or many, and attributed one-liners
    // like the [LibraryImport] P/Invokes or [DllImport] groups stay tight too), and exactly
    // ONE blank line at every subject shift (a member-kind change OR an accessibility-scope
    // change, e.g. a `protected override` method followed by a `public` one). Comment-/
    // #directive-led members are left alone. This is the one spacing rule .editorconfig/the
    // formatter cannot express.
    internal sealed class MemberSpacingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitClassDeclaration(node)!);
        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitStructDeclaration(node)!);
        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitInterfaceDeclaration(node)!);
        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitRecordDeclaration(node)!);
        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;

            return visited.WithMembers(Normalize(visited.Members));
        }
        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;

            return visited.WithMembers(Normalize(visited.Members));
        }
        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;

            return visited.WithMembers(Normalize(visited.Members));
        }

        private static TypeDeclarationSyntax Fix(TypeDeclarationSyntax node) => node.WithMembers(Normalize(node.Members));

        // The accessibility-scope key two adjacent members are grouped by. Only the access
        // modifiers count (public/private/protected/internal); ordering is normalized so
        // `protected internal` and `internal protected` compare equal. A member with no
        // explicit accessibility (interface members, implicit-private) keys as the empty
        // string, so such siblings group together.
        private static string Scope(MemberDeclarationSyntax member) => string.Join(
            ' ',
            member.Modifiers
                .Where(
                    static m =>
                    (m.IsKind(SyntaxKind.PublicKeyword)
                    || m.IsKind(SyntaxKind.PrivateKeyword)
                    || m.IsKind(SyntaxKind.ProtectedKeyword)
                    || m.IsKind(SyntaxKind.InternalKeyword))
                )
                .Select(static m => m.ValueText)
                .OrderBy(static text => text, StringComparer.Ordinal)
        );
        private static SyntaxList<MemberDeclarationSyntax> Normalize(SyntaxList<MemberDeclarationSyntax> members)
        {
            if (members.Count < 2)
            {
                return members;
            }

            var result = new List<MemberDeclarationSyntax> { members[0] };

            for (var i = 1; (i < members.Count); i++)
            {
                var previous = members[(i - 1)];
                var current = members[i];
                var lead = current.GetLeadingTrivia();

                if (lead.Any(
                    static t =>
                    (t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                    || t.IsDirective)
                ))
                {
                    result.Add(current);
                    continue;
                }

                var sameSubject = ((Bucket(previous) == Bucket(current)) && (Scope(previous) == Scope(current)));

                result.Add(current.WithLeadingTrivia(SetBlankLines(lead, (sameSubject ? 0 : 1))));
            }

            return SyntaxFactory.List(result);
        }
        private static SyntaxTriviaList SetBlankLines(SyntaxTriviaList lead, int desired)
        {
            var trivia = lead.ToList();
            var start = 0;

            while ((start < trivia.Count) && trivia[start].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                start++;
            }

            var rebuilt = new List<SyntaxTrivia>();

            for (var k = 0; (k < desired); k++)
            {
                rebuilt.Add(SyntaxFactory.CarriageReturnLineFeed);
            }

            rebuilt.AddRange(trivia.Skip(start));
            return SyntaxFactory.TriviaList(rebuilt);
        }
        private static string Bucket(MemberDeclarationSyntax member) => member switch
        {
            FieldDeclarationSyntax field => (field.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword)) ? "const" : "field"),
            EventFieldDeclarationSyntax => "event",
            PropertyDeclarationSyntax => "property",
            IndexerDeclarationSyntax => "indexer",
            ConstructorDeclarationSyntax => "ctor",
            DestructorDeclarationSyntax => "dtor",
            MethodDeclarationSyntax => "method",
            OperatorDeclarationSyntax => "operator",
            ConversionOperatorDeclarationSyntax => "operator",
            DelegateDeclarationSyntax => "delegate",
            BaseTypeDeclarationSyntax => "type",
            _ => member.Kind().ToString()
        };
    }

    // The member-ordering normalizer (the `member-order` format pass): a contiguous block of
    // const fields, or a contiguous block of properties, sharing the same accessibility scope
    // is sorted alphabetically by declared name — the same convention `named-args`/`init-order`
    // apply to arguments and object-initializer members. The block boundary is exactly the run
    // `member-spacing` packs tight (same kind AND same scope), so each blank-line-delimited
    // group sorts independently. Regular (non-const) fields are NEVER reordered — their order is
    // a [StructLayout]/ABI contract. A block is left untouched if ANY of its members carries a
    // leading/trailing comment or #directive (a human arranged it; reordering would scramble the
    // annotation). Per-slot trivia is reassigned positionally, so the tight one-per-line layout
    // is preserved and a second run is a no-op.
    internal sealed class MemberOrderRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitClassDeclaration(node)!);
        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitStructDeclaration(node)!);
        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitInterfaceDeclaration(node)!);
        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Fix((TypeDeclarationSyntax)base.VisitRecordDeclaration(node)!);

        private static TypeDeclarationSyntax Fix(TypeDeclarationSyntax node) => node.WithMembers(Reorder(node.Members));
        private static SyntaxList<MemberDeclarationSyntax> Reorder(SyntaxList<MemberDeclarationSyntax> members)
        {
            if (members.Count < 2)
            {
                return members;
            }

            var result = new List<MemberDeclarationSyntax>(members.Count);
            var run = new List<MemberDeclarationSyntax>();
            string? runKey = null;

            foreach (var member in members)
            {
                var key = (HasCommentOrDirective(member) ? null : GroupKey(member));

                if ((key is not null) && (key == runKey))
                {
                    run.Add(member);
                    continue;
                }

                FlushRun(result, run);
                runKey = key;
                if (key is null)
                {
                    result.Add(member);
                }
                else
                {
                    run.Add(member);
                }
            }

            FlushRun(result, run);
            return SyntaxFactory.List(result);
        }
        private static void FlushRun(List<MemberDeclarationSyntax> result, List<MemberDeclarationSyntax> run)
        {
            if (run.Count == 1)
            {
                result.Add(run[0]);
            }
            else if (run.Count > 1)
            {
                // A property initializer is evaluated in declaration order (in the constructor);
                // if any in the run has a side effect, reordering would change that order, so the
                // run is left as written. (const initializers are compile-time — always safe.)
                if (run.Any(static member => ((member is PropertyDeclarationSyntax { Initializer.Value: { } value }) && ExpressionSafety.HasSideEffect(value))))
                {
                    result.AddRange(run);
                }
                else
                {
                    // The inter-member whitespace is positional (slot i always carries the same
                    // surrounding trivia); reassigning it by slot preserves the layout while the
                    // declarations move.
                    var slots = run.Select(static member => (member.GetLeadingTrivia(), member.GetTrailingTrivia())).ToArray();
                    var sorted = run.OrderBy(SortKey, StringComparer.Ordinal).ToArray();

                    for (var slot = 0; (slot < sorted.Length); slot++)
                    {
                        result.Add(sorted[slot].WithLeadingTrivia(slots[slot].Item1).WithTrailingTrivia(slots[slot].Item2));
                    }
                }
            }

            run.Clear();
        }

        // The block key: kind (const / property) plus accessibility scope. A null key marks a
        // member that cannot join a sortable run (any other member kind).
        private static string? GroupKey(MemberDeclarationSyntax member)
        {
            var kind = member switch
            {
                FieldDeclarationSyntax field when field.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ConstKeyword)) => "const",
                PropertyDeclarationSyntax => "property",
                _ => null
            };

            return ((kind is null) ? null : ((kind + " ") + Scope(member)));
        }
        private static string SortKey(MemberDeclarationSyntax member) => member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            _ => ""
        };
        private static string Scope(MemberDeclarationSyntax member) => string.Join(
            ' ',
            member.Modifiers
                .Where(
                    static m =>
                    (m.IsKind(SyntaxKind.PublicKeyword)
                    || m.IsKind(SyntaxKind.PrivateKeyword)
                    || m.IsKind(SyntaxKind.ProtectedKeyword)
                    || m.IsKind(SyntaxKind.InternalKeyword))
                )
                .Select(static m => m.ValueText)
                .OrderBy(static text => text, StringComparer.Ordinal)
        );
        private static bool HasCommentOrDirective(MemberDeclarationSyntax member) =>
            member.GetLeadingTrivia().Concat(member.GetTrailingTrivia()).Any(
                static t =>
                (t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                || t.IsDirective)
            );
    }

    // The attribute-ordering normalizer (a `format` pass): a declaration's multiple
    // attributes each get their OWN attribute list on their own line, sorted alphabetically
    // by attribute name. Single-attribute declarations and the rare combined [A, B] list
    // are left alone. Only the attribute-list ORDER changes (each list keeps its own
    // trivia, so the multi-line layout is preserved).
    internal sealed class AttrRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => Reorder((MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!);
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => Reorder((ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!);
        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) => Reorder((DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!);
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) => Reorder((OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!);
        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) => Reorder((ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!);
        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) => Reorder((PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!);
        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node) => Reorder((IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!);
        public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node) => Reorder((EventDeclarationSyntax)base.VisitEventDeclaration(node)!);
        public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) => Reorder((EventFieldDeclarationSyntax)base.VisitEventFieldDeclaration(node)!);
        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) => Reorder((FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!);
        public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node) => Reorder((DelegateDeclarationSyntax)base.VisitDelegateDeclaration(node)!);
        public override SyntaxNode? VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) => Reorder((EnumMemberDeclarationSyntax)base.VisitEnumMemberDeclaration(node)!);
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Reorder((ClassDeclarationSyntax)base.VisitClassDeclaration(node)!);
        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => Reorder((StructDeclarationSyntax)base.VisitStructDeclaration(node)!);
        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Reorder((InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!);
        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Reorder((RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!);
        public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node) => Reorder((EnumDeclarationSyntax)base.VisitEnumDeclaration(node)!);

        // Moves each attribute (with its own trailing newline) into its alphabetical slot
        // and reassigns each slot's leading trivia, preserving the one-per-line layout.
        private static T Reorder<T>(T node) where T : MemberDeclarationSyntax
        {
            var lists = node.AttributeLists;

            if ((lists.Count <= 1) || lists.Any(static l => (l.Attributes.Count != 1)))
            {
                return node;
            }

            var order = Enumerable.Range(0, lists.Count)
                .OrderBy(i => SimpleName(lists[i].Attributes[0]), StringComparer.Ordinal)
                .ToList();

            if (order.SequenceEqual(Enumerable.Range(0, lists.Count)))
            {
                return node;
            }

            var newLists = new List<AttributeListSyntax>();

            for (var slot = 0; (slot < lists.Count); slot++)
            {
                newLists.Add(lists[order[slot]].WithLeadingTrivia(lists[slot].GetLeadingTrivia()));
            }

            return (T)node.WithAttributeLists(SyntaxFactory.List(newLists));
        }
        private static string SimpleName(AttributeSyntax attribute) => attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => attribute.Name.ToString()
        };
    }

    // The null-pattern normalizer (a `format` pass): rewrites equality/inequality against
    // the `null` literal into the pattern form the house style requires — `x is null` /
    // `x is not null` — whichever side the literal sits on (`x == null`, `null != x`). The
    // non-null operand becomes the pattern subject (its inner trivia preserved); the node's
    // own outer trivia is carried across. Comparisons with no `null` literal are untouched,
    // and an existing is-pattern is not a binary expression, so the pass is idempotent.
    internal sealed class NullPatternRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var kind = visited.Kind();

            if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression))
            {
                return visited;
            }

            var leftIsNull = visited.Left.IsKind(SyntaxKind.NullLiteralExpression);
            var rightIsNull = visited.Right.IsKind(SyntaxKind.NullLiteralExpression);

            if (leftIsNull == rightIsNull)
            {
                return visited;
            }

            var subject = (rightIsNull ? visited.Left : visited.Right);
            PatternSyntax pattern = SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

            if (kind is SyntaxKind.NotEqualsExpression)
            {
                pattern = SyntaxFactory.UnaryPattern(SyntaxFactory.Token(SyntaxKind.NotKeyword).WithTrailingTrivia(SyntaxFactory.Space), pattern);
            }

            var isToken = SyntaxFactory.Token(SyntaxKind.IsKeyword)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space);

            return SyntaxFactory.IsPatternExpression(subject.WithoutTrivia(), isToken, pattern)
                .WithLeadingTrivia(visited.GetLeadingTrivia())
                .WithTrailingTrivia(visited.GetTrailingTrivia());
        }
    }

    // The clarity-parens normalizer (a `format` pass): wraps comparison / arithmetic /
    // shift / bitwise / is-pattern expressions and ternaries in their own parentheses for
    // explicit precedence — the house style the Interop/Factories tree follows by hand
    // (e.g. `((0 == a) || (0 == b))`, `var x = (a + b);`, `return (cond ? p : q);`). An
    // expression is left bare only where it is ALREADY delimited: inside existing parens,
    // or as the sole condition of if/while/do/switch/lock (the statement keyword's own
    // parentheses). Same-operator logical chains (`a || b || c`) are not re-nested — only
    // their leaf operands get wrapped — and unary operators (`!x`, `-1`) are left alone.
    // Purely syntactic and idempotent: a second run sees the delimiting parens and stops.
    internal sealed class ParenClarityRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
            => MaybeWrap(node, (ExpressionSyntax)base.VisitBinaryExpression(node)!);
        public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
            => MaybeWrap(node, (ExpressionSyntax)base.VisitConditionalExpression(node)!);
        public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
            => MaybeWrap(node, (ExpressionSyntax)base.VisitIsPatternExpression(node)!);

        // Flag-combining bitwise (`a | b`) is idiomatic bare in value position — the gold
        // standard wraps it only inside a comparison (precedence vs ==/!=). Drop a redundant
        // bitwise paren wherever the surrounding construct binds looser than the operator
        // (arguments, initializers, returns, assignments, ternary arms), so the pass settles
        // on the gold shape regardless of any extra parens already present.
        public override SyntaxNode? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
        {
            var visited = (ParenthesizedExpressionSyntax)base.VisitParenthesizedExpression(node)!;

            if ((visited.Expression is BinaryExpressionSyntax inner)
                && IsBitwise(inner.Kind())
                && (IsLooseContext(node.Parent) || IsSameOperatorChain(inner.Kind(), node.Parent)))
            {
                return inner.WithLeadingTrivia(visited.GetLeadingTrivia()).WithTrailingTrivia(visited.GetTrailingTrivia());
            }

            return visited;
        }

        private static ExpressionSyntax MaybeWrap(ExpressionSyntax original, ExpressionSyntax visited)
        {
            if (!NeedsParens(original))
            {
                return visited;
            }

            var inner = visited.WithoutLeadingTrivia().WithoutTrailingTrivia();

            return SyntaxFactory.ParenthesizedExpression(inner)
                .WithLeadingTrivia(visited.GetLeadingTrivia())
                .WithTrailingTrivia(visited.GetTrailingTrivia());
        }

        // Decided on the ORIGINAL node, so the parent is the real (pre-rewrite) context.
        private static bool NeedsParens(ExpressionSyntax node)
        {
            var parent = node.Parent;

            if (parent is null or ParenthesizedExpressionSyntax)
            {
                return false;
            }

            // Statement/expression slots whose own keyword parentheses already delimit.
            if (((parent is IfStatementSyntax ifStatement) && (ifStatement.Condition == node))
                || ((parent is WhileStatementSyntax whileStatement) && (whileStatement.Condition == node))
                || ((parent is DoStatementSyntax doStatement) && (doStatement.Condition == node))
                || ((parent is SwitchStatementSyntax switchStatement) && (switchStatement.Expression == node))
                || ((parent is LockStatementSyntax lockStatement) && (lockStatement.Expression == node)))
            {
                return false;
            }

            if (node is BinaryExpressionSyntax binary)
            {
                var kind = binary.Kind();

                // A logical operand of the SAME operator is left bare so `a || b || c` keeps
                // a single flat group instead of re-nesting (its leaf operands still wrap).
                if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
                {
                    return ((parent is not BinaryExpressionSyntax parentBinary) || (parentBinary.Kind() != kind));
                }

                // Flag-combining bitwise gets clarity parens only where precedence against a
                // comparison is genuinely confusing (an operand of ==/!=/</> ...), matching
                // the gold standard's bare `a | b` in plain value position.
                if (IsBitwise(kind))
                {
                    return ((parent is BinaryExpressionSyntax comparison) && IsComparison(comparison.Kind()));
                }
            }

            return true;
        }
        private static bool IsBitwise(SyntaxKind kind) => (kind
            is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression);

        // A bitwise paren whose parent is the SAME bitwise operator is redundant (the op is
        // associative): `(a | b) | c` == `a | b | c`. Mixed operators keep their parens.
        private static bool IsSameOperatorChain(SyntaxKind innerKind, SyntaxNode? parent)
            => ((parent is BinaryExpressionSyntax parentBinary) && (parentBinary.Kind() == innerKind));
        private static bool IsComparison(SyntaxKind kind) => (kind is SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression);

        // Constructs that bind looser than a bitwise operator, so wrapping its result adds
        // nothing: a redundant bitwise paren in any of these can be dropped safely.
        private static bool IsLooseContext(SyntaxNode? parent) => (parent is ArgumentSyntax
            or AttributeArgumentSyntax or EqualsValueClauseSyntax or ReturnStatementSyntax
            or ArrowExpressionClauseSyntax or AssignmentExpressionSyntax or ConditionalExpressionSyntax
            or InitializerExpressionSyntax or ExpressionStatementSyntax);
    }

    // Reorder-safety for the passes that alphabetize EVALUATED expressions (named-args,
    // init-order). C# evaluates call arguments and object-initializer values in written order,
    // so moving an expression that has a side effect — or that reads state another element
    // mutates — changes behavior. An element is treated as side-effecting if its subtree holds a
    // call, object creation, await, assignment, indexer access, or ++/--; those cover the
    // observable-order hazards without a semantic model. Pure reads commute, so a group with none
    // of these is safe to reorder; otherwise the pass leaves it in source order.
    internal static class ExpressionSafety
    {
        public static bool HasSideEffect(SyntaxNode expression) => expression.DescendantNodesAndSelf().Any(static node =>
            ((node is InvocationExpressionSyntax
                or ObjectCreationExpressionSyntax
                or ImplicitObjectCreationExpressionSyntax
                or AwaitExpressionSyntax
                or AssignmentExpressionSyntax
                or ElementAccessExpressionSyntax)
            || node.IsKind(SyntaxKind.PreIncrementExpression)
            || node.IsKind(SyntaxKind.PreDecrementExpression)
            || node.IsKind(SyntaxKind.PostIncrementExpression)
            || node.IsKind(SyntaxKind.PostDecrementExpression)));
    }

    // The named-argument normalizer (the `named-args` format pass). SEMANTIC: it resolves
    // each call's method symbol to read parameter names, so it runs against a Compilation
    // (NamedArgsPhase) rather than the syntactic Func pipeline. Every real method/ctor call
    // gets its arguments named (`name: value`) and sorted alphabetically by parameter name —
    // the Interop/Factories house convention. Left positional (skipped) when there is no
    // resolvable method symbol (function-pointer / delegate invokes have none), an out/ref/in
    // or already-named argument, a `params` parameter, an omitted optional argument, or when the
    // reorder would move a side-effecting argument (see ExpressionSafety) — the cases where
    // naming-and-reordering is unsafe or ambiguous.
    internal sealed class NamedArgsRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel m_model;

        public NamedArgsRewriter(SemanticModel model)
        {
            m_model = model;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            return ((Rebuild(node, visited.ArgumentList) is { } rebuilt) ? visited.WithArgumentList(rebuilt) : visited);
        }
        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;

            return (((visited.ArgumentList is { } list) && (Rebuild(node, list) is { } rebuilt)) ? visited.WithArgumentList(rebuilt) : visited);
        }
        public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node)!;

            return ((Rebuild(node, visited.ArgumentList) is { } rebuilt) ? visited.WithArgumentList(rebuilt) : visited);
        }

        // The ORIGINAL node carries the symbol (the rewritten copy is detached from the
        // model); the VISITED list supplies the already-child-rewritten argument expressions.
        // Returns the reordered+named list, or null to leave the call alone.
        private ArgumentListSyntax? Rebuild(SyntaxNode originalCall, ArgumentListSyntax? visitedList)
        {
            if ((visitedList is null) || (visitedList.Arguments.Count == 0))
            {
                return null;
            }

            if ((m_model.GetSymbolInfo(originalCall).Symbol is not IMethodSymbol method)
                || (method.MethodKind is MethodKind.FunctionPointerSignature or MethodKind.DelegateInvoke))
            {
                return null;
            }

            var arguments = visitedList.Arguments;
            var parameters = method.Parameters;

            if ((arguments.Count != parameters.Length)
                || parameters.Any(static parameter => parameter.IsParams)
                || arguments.Any(static argument => (argument.NameColon is not null)))
            {
                return null;
            }

            // Naming preserves written positions, but the alphabetical SORT moves them. When that
            // move is real AND any argument is side-effecting, leave the call positional — C#
            // evaluates arguments left-to-right, so reordering would change evaluation order.
            var parameterNames = parameters.Select(static parameter => parameter.Name);

            if (!parameterNames.SequenceEqual(parameterNames.OrderBy(static name => name, StringComparer.Ordinal))
                && arguments.Any(static argument => ExpressionSafety.HasSideEffect(argument.Expression)))
            {
                return null;
            }

            // Content (name + expression) is built per ORIGINAL position, then reordered; the
            // per-slot leading/trivia is reassigned afterwards so the call's existing
            // single-line or one-argument-per-line layout survives the reorder unchanged. An
            // out/ref/in keyword is carried with its argument (named args allow it: `value: out x`).
            var entries = new (string Name, ArgumentSyntax Argument)[arguments.Count];

            for (var index = 0; (index < arguments.Count); index++)
            {
                var argument = arguments[index];
                var nameColon = SyntaxFactory
                    .NameColon(SyntaxFactory.IdentifierName(parameters[index].Name))
                    .WithColonToken(SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space));
                var refKind = (argument.RefKindKeyword.IsKind(SyntaxKind.None)
                    ? default
                    : argument.RefKindKeyword.WithLeadingTrivia().WithTrailingTrivia(SyntaxFactory.Space));
                var bareExpression = argument.Expression.WithoutLeadingTrivia().WithoutTrailingTrivia();

                entries[index] = (parameters[index].Name, SyntaxFactory.Argument(nameColon, refKind, bareExpression));
            }

            var ordered = entries.OrderBy(static entry => entry.Name, StringComparer.Ordinal).Select(static entry => entry.Argument).ToArray();
            var separators = arguments.GetSeparators().ToArray();
            var nodesAndTokens = new List<SyntaxNodeOrToken>((arguments.Count * 2));

            for (var slot = 0; (slot < ordered.Length); slot++)
            {
                nodesAndTokens.Add(
                    ordered[slot]
                    .WithLeadingTrivia(arguments[slot].GetLeadingTrivia())
                    .WithTrailingTrivia(arguments[slot].GetTrailingTrivia())
                );
                if (slot < separators.Length)
                {
                    nodesAndTokens.Add(separators[slot]);
                }
            }

            return visitedList.WithArguments(SyntaxFactory.SeparatedList<ArgumentSyntax>(nodesAndTokens));
        }
    }

    // The argument-layout normalizer (a `format` pass): a call with MORE THAN ONE argument
    // gets every argument on its own line, indented one level past the line the call opens
    // on, with the closing parenthesis hanging on its own line left-justified to that line —
    //   foo(
    //       a,
    //       b
    //   );
    // Zero- and single-argument calls stay inline (`foo()`, `bar(a)`); a single-argument call
    // that was previously split is collapsed back onto one line (trivia INSIDE the argument —
    // e.g. a nested multi-argument call — is preserved). Positional order is preserved (the
    // `named-args` pass owns alphabetical ordering, and only it knows which calls can be safely
    // reordered), so this is a pure layout edit. Indentation is taken from the call's current
    // line, so nested calls settle over successive runs (the outer split moves the inner call to
    // its own line, and the next run re-indents the inner from there).
    internal sealed class ArgLinesRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitArgumentList(ArgumentListSyntax node)
        {
            var visited = (ArgumentListSyntax)base.VisitArgumentList(node)!;

            if (visited.Arguments.Count < 1)
            {
                return visited;
            }

            // A single-argument call stays on one line: clear the open paren's trailing trivia,
            // the argument's surrounding trivia, and the close paren's leading trivia so a
            // previously split `foo(\n    a\n)` collapses back to `foo(a)`.
            if (visited.Arguments.Count == 1)
            {
                return visited
                    .WithOpenParenToken(visited.OpenParenToken.WithTrailingTrivia())
                    .WithArguments(
                        SyntaxFactory.SingletonSeparatedList<ArgumentSyntax>(visited.Arguments[0].WithLeadingTrivia().WithTrailingTrivia())
                    )
                    .WithCloseParenToken(visited.CloseParenToken.WithLeadingTrivia());
            }

            var lineIndent = WrappedIndent(node);
            var argumentTrivia = SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(new string(' ', (lineIndent + 4))));

            var nodesAndTokens = new List<SyntaxNodeOrToken>((visited.Arguments.Count * 2));

            for (var index = 0; (index < visited.Arguments.Count); index++)
            {
                nodesAndTokens.Add(visited.Arguments[index].WithLeadingTrivia(argumentTrivia).WithTrailingTrivia());
                if (index < (visited.Arguments.Count - 1))
                {
                    nodesAndTokens.Add(SyntaxFactory.Token(SyntaxKind.CommaToken));
                }
            }

            return visited
                .WithOpenParenToken(visited.OpenParenToken.WithTrailingTrivia())
                .WithArguments(SyntaxFactory.SeparatedList<ArgumentSyntax>(nodesAndTokens))
                .WithCloseParenToken(
                    visited.CloseParenToken.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(new string(' ', lineIndent)))
                );
        }

        // The indent this call's wrapped body hangs from, computed STRUCTURALLY so it is stable
        // across passes (idempotency). Anchoring to the input's current line would compound: once
        // an enclosing call wraps, this call moves to a deeper line, and reading that line's indent
        // would add 4 more spaces every run. Instead: take the enclosing statement's indent (which
        // arg-lines never moves — it only wraps the argument lists inside a statement) and add one
        // level per ENCLOSING multi-argument call, since every such call is itself wrapped and
        // pushes this one deeper. The count is driven by argument counts, not the current layout,
        // so a second pass reproduces the same value exactly.
        private static int WrappedIndent(ArgumentListSyntax node)
        {
            var anchor = ((node.FirstAncestorOrSelf<StatementSyntax>() as SyntaxNode)
                ?? ((node.FirstAncestorOrSelf<MemberDeclarationSyntax>() as SyntaxNode)
                ?? node));
            var anchorLine = node.SyntaxTree.GetText().Lines.GetLineFromPosition(anchor.GetFirstToken().SpanStart).ToString();
            var baseIndent = (anchorLine.Length - anchorLine.TrimStart().Length);

            var depth = 0;

            for (var ancestor = node.Parent; ((ancestor is not null) && (ancestor != anchor)); ancestor = ancestor.Parent)
            {
                if (ancestor is ArgumentListSyntax { Arguments.Count: > 1 })
                {
                    depth++;
                }
            }

            return (baseIndent + (4 * depth));
        }
    }

    // The object-initializer ordering normalizer (a `format` pass): the member assignments in
    // an `new T { A = ..., B = ... }` initializer are sorted alphabetically by member name —
    // the same alphabetical convention `named-args` applies to call arguments. Only pure
    // `identifier = value` object initializers are touched (collection/array initializers and
    // any with a non-identifier element are left alone, since their order is significant). The
    // per-slot trivia is reassigned so the one-per-line layout is preserved across the sort.
    internal sealed class InitOrderRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
        {
            var visited = (InitializerExpressionSyntax)base.VisitInitializerExpression(node)!;

            if (!visited.IsKind(SyntaxKind.ObjectInitializerExpression)
                || (visited.Expressions.Count < 2)
                || !visited.Expressions.All(static expression => (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax })))
            {
                return visited;
            }

            // Initializer values are evaluated in written order. Leave the initializer as-is when
            // it is already sorted (nothing to do) or when any value is side-effecting (reordering
            // would change evaluation order).
            var memberNames = visited.Expressions.Select(static expression =>
                ((IdentifierNameSyntax)((AssignmentExpressionSyntax)expression).Left).Identifier.ValueText);

            if (memberNames.SequenceEqual(memberNames.OrderBy(static name => name, StringComparer.Ordinal))
                || visited.Expressions.Any(static expression => ExpressionSafety.HasSideEffect(((AssignmentExpressionSyntax)expression).Right)))
            {
                return visited;
            }

            var ordered = visited.Expressions
                .OrderBy(
                    static expression => ((IdentifierNameSyntax)((AssignmentExpressionSyntax)expression).Left).Identifier.ValueText,
                    StringComparer.Ordinal
                )
                .ToArray();
            var separators = visited.Expressions.GetSeparators().ToArray();
            var nodesAndTokens = new List<SyntaxNodeOrToken>((visited.Expressions.Count * 2));

            for (var slot = 0; (slot < ordered.Length); slot++)
            {
                nodesAndTokens.Add(
                    ordered[slot]
                    .WithLeadingTrivia(visited.Expressions[slot].GetLeadingTrivia())
                    .WithTrailingTrivia(visited.Expressions[slot].GetTrailingTrivia())
                );
                if (slot < separators.Length)
                {
                    nodesAndTokens.Add(separators[slot]);
                }
            }

            return visited.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>(nodesAndTokens));
        }
    }

    // The logical-line normalizer (the `logical-lines` format pass): a multi-operand
    // `&&`/`||` chain that delimits an `if`/`while` condition or a parenthesized `return`
    // value is laid out one operand per line, the operator TRAILING each line, with the
    // enclosing `(` and `)` hanging on their own lines —
    //   if (
    //       (0 == deviceHandle) ||
    //       (0 == pipelineHandle)
    //   ) {
    //   return (
    //       (a is not null) &&
    //       (b is not null)
    //   );
    // For if/while the keyword's own parentheses are the delimiter (the condition is left
    // bare by paren-clarity); for return the value is an explicit ParenthesizedExpression
    // (paren-clarity wraps a logical return). Operand inner trivia is preserved and only
    // the outer layout trivia is reset, so a second run reproduces the same shape — the
    // pass is idempotent. A same-operator chain (`a || b || c`) flattens to a single group;
    // mixed precedence (`a && b || c`) keeps the tighter `&&` inline on its operand line.
    internal sealed class LogicalLinesRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;

            if ((visited.Condition is not BinaryExpressionSyntax binary) || !IsLogical(binary))
            {
                return visited;
            }

            var indent = LineIndentAt(node, node.IfKeyword.SpanStart);

            return visited
                .WithOpenParenToken(visited.OpenParenToken.WithTrailingTrivia())
                .WithCondition(Layout(binary, (indent + "    ")))
                .WithCloseParenToken(visited.CloseParenToken.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(indent)));
        }
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;

            if ((visited.Condition is not BinaryExpressionSyntax binary) || !IsLogical(binary))
            {
                return visited;
            }

            var indent = LineIndentAt(node, node.WhileKeyword.SpanStart);

            return visited
                .WithOpenParenToken(visited.OpenParenToken.WithTrailingTrivia())
                .WithCondition(Layout(binary, (indent + "    ")))
                .WithCloseParenToken(visited.CloseParenToken.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(indent)));
        }
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;

            if ((visited.Expression is not ParenthesizedExpressionSyntax paren)
                || (paren.Expression is not BinaryExpressionSyntax binary)
                || !IsLogical(binary))
            {
                return visited;
            }

            var indent = LineIndentAt(node, node.ReturnKeyword.SpanStart);
            var laidOut = paren
                .WithOpenParenToken(paren.OpenParenToken.WithTrailingTrivia())
                .WithExpression(Layout(binary, (indent + "    ")))
                .WithCloseParenToken(paren.CloseParenToken.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(indent)));

            return visited.WithExpression(laidOut);
        }

        private static bool IsLogical(BinaryExpressionSyntax binary)
            => (binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression);

        // Rebuilds a same-operator logical chain with each operand on its own indented line
        // and the operator hugging the end of the previous operand's line.
        private static ExpressionSyntax Layout(BinaryExpressionSyntax binary, string innerIndent)
        {
            var kind = binary.Kind();
            var operands = new List<ExpressionSyntax>();
            var operators = new List<SyntaxToken>();

            Flatten(binary, kind, operands, operators);

            var operandLead = new[] { SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(innerIndent) };
            var result = operands[0].WithLeadingTrivia(operandLead).WithTrailingTrivia();

            for (var index = 0; (index < operators.Count); index++)
            {
                var operatorToken = operators[index].WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia();
                var right = operands[(index + 1)].WithLeadingTrivia(operandLead).WithTrailingTrivia();

                result = SyntaxFactory.BinaryExpression(kind, result, operatorToken, right);
            }

            return result;
        }

        // Collects the operands and operators of a left-associative same-operator chain in
        // source order (the right operand is never the same operator unless re-parenthesized,
        // in which case it is a ParenthesizedExpression and stays a single operand).
        private static void Flatten(BinaryExpressionSyntax binary, SyntaxKind kind, List<ExpressionSyntax> operands, List<SyntaxToken> operators)
        {
            if ((binary.Left is BinaryExpressionSyntax leftBinary) && (leftBinary.Kind() == kind))
            {
                Flatten(leftBinary, kind, operands, operators);
            }
            else
            {
                operands.Add(binary.Left);
            }

            operators.Add(binary.OperatorToken);
            operands.Add(binary.Right);
        }
        private static string LineIndentAt(SyntaxNode node, int position)
        {
            var line = node.SyntaxTree!.GetText().Lines.GetLineFromPosition(position).ToString();

            return line[..(line.Length - line.TrimStart().Length)];
        }
    }

    // The ternary-layout normalizer (the `ternary-lines` format pass): a conditional
    // expression `cond ? whenTrue : whenFalse` is laid out across three lines with the `?`
    // and `:` operators LEADING their branch, each indented one level beyond the line the
    // condition opens on —
    //   return someTest
    //       ? "A"
    //       : "B";
    // A `? : ? :` chain (the whenFalse — or whenTrue — is itself a conditional) nests one
    // level deeper per link; the chain's root drives the layout so the nested links indent
    // off the rebuilt shape rather than their original source position. Condition and branch
    // inner trivia are preserved and only the outer layout trivia is reset, so a second run
    // reproduces the same shape — the pass is idempotent. Indentation is computed structurally
    // (the enclosing statement's indent plus one level per enclosing ternary branch), never read
    // from the condition's own line, so sibling ternaries in one expression cannot shift each
    // other across runs.
    internal sealed class TernaryLinesRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node)!;

            // A conditional that is a branch of another conditional is a link in a `? : ? :`
            // chain — its root lays it out (one level deeper), so leave it alone here.
            if ((node.Parent is ConditionalExpressionSyntax parent)
                && ((parent.WhenTrue == node) || (parent.WhenFalse == node)))
            {
                return visited;
            }

            return Layout(visited, ConditionIndent(node));
        }

        private static ConditionalExpressionSyntax Layout(ConditionalExpressionSyntax conditional, string conditionIndent)
        {
            var branchIndent = (conditionIndent + "    ");
            var branchLead = new[] { SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(branchIndent) };

            var whenTrue = ((conditional.WhenTrue is ConditionalExpressionSyntax trueChain)
                ? Layout(trueChain, branchIndent).WithLeadingTrivia()
                : conditional.WhenTrue.WithLeadingTrivia().WithTrailingTrivia());
            var whenFalse = ((conditional.WhenFalse is ConditionalExpressionSyntax falseChain)
                ? Layout(falseChain, branchIndent).WithLeadingTrivia()
                : conditional.WhenFalse.WithLeadingTrivia().WithTrailingTrivia());

            return conditional
                .WithCondition(conditional.Condition.WithTrailingTrivia())
                .WithQuestionToken(conditional.QuestionToken.WithLeadingTrivia(branchLead).WithTrailingTrivia(SyntaxFactory.Space))
                .WithWhenTrue(whenTrue)
                .WithColonToken(conditional.ColonToken.WithLeadingTrivia(branchLead).WithTrailingTrivia(SyntaxFactory.Space))
                .WithWhenFalse(whenFalse);
        }

        // The indent this ternary's `? t` / `: f` branches hang from, computed STRUCTURALLY for
        // idempotency. Reading this ternary's own line would compound: a sibling ternary in the
        // same expression (`(a ? 1 : 0) + (b ? 1 : 0)`) gets pushed onto a line the first ternary
        // just indented, so the next run would read that deeper indent and shift again. Instead:
        // anchor to the enclosing statement/member indent (which this pass never moves) and add
        // one level per enclosing ternary whose BRANCH holds this node — a structural count that
        // is the same on every run.
        private static string ConditionIndent(ConditionalExpressionSyntax node)
        {
            var anchor = ((node.FirstAncestorOrSelf<StatementSyntax>() as SyntaxNode)
                ?? ((node.FirstAncestorOrSelf<MemberDeclarationSyntax>() as SyntaxNode)
                ?? node));
            var anchorLine = node.SyntaxTree!.GetText().Lines.GetLineFromPosition(anchor.GetFirstToken().SpanStart).ToString();
            var baseIndent = (anchorLine.Length - anchorLine.TrimStart().Length);

            var depth = 0;
            var child = (SyntaxNode)node;

            for (var ancestor = node.Parent; ((ancestor is not null) && (ancestor != anchor)); child = ancestor, ancestor = ancestor.Parent)
            {
                if ((ancestor is ConditionalExpressionSyntax cond) && ((cond.WhenTrue == child) || (cond.WhenFalse == child)))
                {
                    depth++;
                }
            }

            return new string(' ', (baseIndent + (4 * depth)));
        }
    }

    // The trailing-comma normalizer (the `trailing-comma` format pass): a MULTI-LINE
    // initializer (object / collection / array, where the last element and the closing brace
    // sit on different source lines) gets a trailing comma after its last element, so adding
    // or reordering a member later is a one-line diff —
    //   new VkPipelineInfoKhr {
    //       Pipeline = pipelineHandle,
    //       SType = StructureTypePipelineInfoKhr,
    //   }
    // Single-line initializers (`{ A = 1, B = 2 }`) are left alone — a trailing comma there
    // reads oddly — and an initializer that already ends with a comma is untouched, so the
    // pass is idempotent.
    internal sealed class TrailingCommaRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
        {
            var visited = (InitializerExpressionSyntax)base.VisitInitializerExpression(node)!;
            var expressions = visited.Expressions;
            var separators = expressions.GetSeparators().ToList();

            if ((expressions.Count == 0) || (separators.Count >= expressions.Count))
            {
                return visited;
            }

            var text = node.SyntaxTree!.GetText();
            var lastExpressionLine = text.Lines.IndexOf(node.Expressions[^1].Span.End);
            var closeBraceLine = text.Lines.IndexOf(node.CloseBraceToken.SpanStart);

            if (lastExpressionLine == closeBraceLine)
            {
                return visited;
            }

            var last = expressions[^1];
            var trailingComma = SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(last.GetTrailingTrivia());
            var nodesAndTokens = new List<SyntaxNodeOrToken>((expressions.Count * 2));

            for (var index = 0; (index < expressions.Count); index++)
            {
                nodesAndTokens.Add(((index == (expressions.Count - 1)) ? last.WithTrailingTrivia() : expressions[index]));
                nodesAndTokens.Add(((index < separators.Count) ? separators[index] : trailingComma));
            }

            return visited.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>(nodesAndTokens));
        }
    }

    // The declaration-spacing normalizer (the `decl-spacing` format pass): inside a block, a
    // run of local-variable declarations is visually separated from the first NON-declaration
    // statement that follows it by exactly one blank line (the house "variables apart from the
    // body" rule) —
    //   var pointers = GetPointers(deviceHandle: deviceHandle);
    //
    //   if (pointers.GetPipelineExecutableProperties is null) {
    // Consecutive declarations stay grouped (no blank between them) and a statement whose own
    // leading trivia is a comment/directive is left untouched (its placement is the comment's
    // to own). Only the decl-to-statement boundary is normalized — blank lines elsewhere are
    // preserved — so the pass is idempotent.
    internal sealed class DeclSpacingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            var statements = visited.Statements;

            if (statements.Count < 2)
            {
                return visited;
            }

            var rebuilt = new List<StatementSyntax> { statements[0] };

            for (var index = 1; (index < statements.Count); index++)
            {
                var previous = statements[(index - 1)];
                var current = statements[index];

                rebuilt.Add(
                    (((previous is LocalDeclarationStatementSyntax)
                    && (current is not LocalDeclarationStatementSyntax)
                    && OnSeparateLines(previous, current)
                    && !HasCommentOrDirective(current.GetLeadingTrivia()))
                    ? WithLeadingBlankLine(current)
                    : current)
                );
            }

            return visited.WithStatements(SyntaxFactory.List(rebuilt));
        }

        // Only space declarations that already sit on their own lines: a single-line body
        // (`{ int n = f(); return n; }`) must not be blown open — and splitting it was also the
        // source of a non-idempotent run, since the inserted newline retriggered the rule.
        private static bool OnSeparateLines(StatementSyntax previous, StatementSyntax current) =>
            (previous.GetTrailingTrivia().Any(static trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            || current.GetLeadingTrivia().Any(static trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
        private static bool HasCommentOrDirective(SyntaxTriviaList lead) => lead.Any(
            static trivia =>
            (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            || trivia.IsDirective)
        );

        // Forces exactly one blank line: the indentation whitespace (and any non-newline lead)
        // is kept, the leading end-of-lines are collapsed, and a single newline is prepended.
        private static StatementSyntax WithLeadingBlankLine(StatementSyntax statement)
        {
            var lead = statement.GetLeadingTrivia().ToList();
            var start = 0;

            while ((start < lead.Count) && lead[start].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                start++;
            }

            var rebuilt = new List<SyntaxTrivia> { SyntaxFactory.CarriageReturnLineFeed };

            rebuilt.AddRange(lead.Skip(start));
            return statement.WithLeadingTrivia(SyntaxFactory.TriviaList(rebuilt));
        }
    }

    // The literal-var normalizer (the `literal-var` format pass): an explicitly-typed local
    // initialized from a bare numeric literal becomes `var` with the literal carrying the
    // matching type suffix, so the type is stated ONCE and inferred — `uint executableCount =
    // 0;` -> `var executableCount = 0U;`. Only the suffix-bearing primitives are converted
    // (uint -> U, long -> L, ulong -> UL, float -> F, double -> D, decimal -> M); the suffix
    // makes `var` infer the original type exactly. Left alone: `const`/`using` declarations,
    // multi-declarator statements, non-literal or already-suffixed initializers, and hex/binary
    // literals (whose trailing letters are digits, not a suffix). After conversion the type is
    // `var`, so the pass is idempotent.
    internal sealed class LiteralVarRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            var visited = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;

            if (!visited.UsingKeyword.IsKind(SyntaxKind.None)
                || visited.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ConstKeyword)))
            {
                return visited;
            }

            var declaration = visited.Declaration;

            if ((declaration.Type is not PredefinedTypeSyntax predefined)
                || (SuffixFor(predefined.Keyword.Kind()) is not { } suffix)
                || (declaration.Variables.Count != 1))
            {
                return visited;
            }

            var variable = declaration.Variables[0];

            if ((variable.Initializer?.Value is not LiteralExpressionSyntax literal)
                || !literal.Token.IsKind(SyntaxKind.NumericLiteralToken))
            {
                return visited;
            }

            var literalText = literal.Token.Text;

            if (literalText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || literalText.StartsWith("0b", StringComparison.OrdinalIgnoreCase)
                || HasTypeSuffix(literalText)
                || (SyntaxFactory.ParseExpression((literalText + suffix)) is not LiteralExpressionSyntax suffixed))
            {
                return visited;
            }

            var newLiteral = suffixed
                .WithLeadingTrivia(literal.GetLeadingTrivia())
                .WithTrailingTrivia(literal.GetTrailingTrivia());
            var newVariable = variable.WithInitializer(variable.Initializer!.WithValue(newLiteral));
            var newType = SyntaxFactory.IdentifierName("var")
                .WithLeadingTrivia(predefined.GetLeadingTrivia())
                .WithTrailingTrivia(predefined.GetTrailingTrivia());

            return visited.WithDeclaration(
                declaration
                .WithType(newType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(newVariable))
            );
        }

        private static string? SuffixFor(SyntaxKind keyword) => keyword switch
        {
            SyntaxKind.UIntKeyword => "U",
            SyntaxKind.LongKeyword => "L",
            SyntaxKind.ULongKeyword => "UL",
            SyntaxKind.FloatKeyword => "F",
            SyntaxKind.DoubleKeyword => "D",
            SyntaxKind.DecimalKeyword => "M",
            _ => null
        };
        private static bool HasTypeSuffix(string text)
            => ((text.Length > 0) && (text[^1] is 'u' or 'U' or 'l' or 'L' or 'f' or 'F' or 'd' or 'D' or 'm' or 'M'));
    }

    // format umbrella: the source rewriters applied in one parse/write per file.
    internal static class FormatCommand
    {
        // Canonical order: attribute lists first, then member blank-line spacing. SourceRewrite
        // re-parses between passes, so running both here is identical to running them back to
        // back — but each file is written ONCE.
        private static readonly (string Name, Func<SyntaxNode, SyntaxNode> Apply)[] Passes =
        [
            ("attr-order", static node => new AttrRewriter().Visit(node)!),
            ("member-spacing", static node => new MemberSpacingRewriter().Visit(node)!),
            ("member-order", static node => new MemberOrderRewriter().Visit(node)!),
            ("null-pattern", static node => new NullPatternRewriter().Visit(node)!),
            ("paren-clarity", static node => new ParenClarityRewriter().Visit(node)!),
            ("logical-lines", static node => new LogicalLinesRewriter().Visit(node)!),
            ("arg-lines", static node => new ArgLinesRewriter().Visit(node)!),
            ("ternary-lines", static node => new TernaryLinesRewriter().Visit(node)!),
            ("init-order", static node => new InitOrderRewriter().Visit(node)!),
            ("trailing-comma", static node => new TrailingCommaRewriter().Visit(node)!),
            ("decl-spacing", static node => new DeclSpacingRewriter().Visit(node)!),
            ("literal-var", static node => new LiteralVarRewriter().Visit(node)!)
        ];

        // named-args is a SEMANTIC pass (it needs parameter names, so it resolves symbols
        // against a Compilation instead of bare syntax) — it runs as its own disk phase
        // after the syntactic passes above, not through SourceRewrite's Func pipeline.
        private const string NamedArgsPassName = "named-args";

        // The bare-`format` set: the semantics-preserving normalizers. The vertical line-wrappers
        // (arg-lines, logical-lines, ternary-lines) stay opt-in via -Only — their one-per-line
        // layout is a deliberate choice, not a baseline. named-args is in despite its semantic
        // cost. NOTE: the tree is not yet swept to these, so a bare `format`/`-WhatIf` reports (and
        // fixes) drift until a deliberate tree-wide run converts it.
        private static readonly string[] DefaultPassNames =
        [
            "attr-order", "member-spacing", "member-order", "null-pattern", "paren-clarity",
            "init-order", "trailing-comma", "decl-spacing", "literal-var", "named-args"
        ];

        public static int Run(string[] args)
        {
            var scanner = new ArgScanner().Flag("WhatIf").Flag("Verify").Value("Only");

            if (!scanner.Parse(args))
            {
                Console.Error.WriteLine($"ERROR: {scanner.Error}");
                return 2;
            }

            var known = Passes.Select(static pass => pass.Name).Append(NamedArgsPassName).ToHashSet(StringComparer.Ordinal);
            var selected = DefaultPassNames.ToHashSet(StringComparer.Ordinal);

            if (scanner.Get("Only") is { } only)
            {
                selected = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static name => name.ToLowerInvariant())
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var name in selected)
                {
                    if (!known.Contains(name))
                    {
                        var knownNames = string.Join(", ", Passes.Select(static pass => pass.Name).Append(NamedArgsPassName));

                        Console.Error.WriteLine($"ERROR: unknown format pass '{name}' (known: {knownNames}).");
                        return 2;
                    }
                }
            }

            var root = ((scanner.Positionals.Count > 0) ? scanner.Positionals[0] : "src");
            var whatIf = scanner.Has("WhatIf");
            var verify = scanner.Has("Verify");

            // Phase 0: `dotnet format whitespace` establishes the .editorconfig baseline (spacing,
            // alignment, newlines) the custom passes then layer bespoke conventions onto. Disjoint
            // concerns — the result is a fixed point of both — so running it first is safe.
            var result = DotnetFormatPhase.Run(root, (whatIf || verify));
            var syntacticPasses = Passes.Where(pass => selected.Contains(pass.Name)).ToList();

            if (syntacticPasses.Count > 0)
            {
                result = Math.Max(result, SourceRewrite.Run("format", root, whatIf, verify, syntacticPasses));
            }

            if (selected.Contains(NamedArgsPassName))
            {
                result = Math.Max(result, NamedArgsPhase.Run(root, whatIf, verify));
            }

            return result;
        }
    }

    // Phase 0 of `format`: `dotnet format whitespace` applies the .editorconfig whitespace baseline
    // the custom passes build on. Run per owning project — experimental/* live out of the solution,
    // so one solution-wide invocation would miss them — with --no-restore (the project must already
    // be restored/built, same as named-args). In verify mode dotnet format's "changes needed"
    // (nonzero) maps to a drift/gate failure (1); a genuine tool error in write mode maps to infra (2).
    internal static class DotnetFormatPhase
    {
        public static int Run(string rootArgument, bool verifyOnly)
        {
            if (!SourceFiles.TryEnumerate(rootArgument, out var scanRoot, out _))
            {
                return 2;
            }

            var projects = Directory.EnumerateFiles(scanRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(static path => (!path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
                .OrderBy(static path => path, StringComparer.Ordinal);
            var result = 0;

            foreach (var project in projects)
            {
                Console.Error.WriteLine($"dotnet format whitespace: {Path.GetRelativePath(EngineRun.RepositoryRoot, project).Replace('\\', '/')}");
                var code = (verifyOnly
                    ? ToolProcess.RunStreamed(null, "dotnet", "format", "whitespace", project, "--no-restore", "--verify-no-changes")
                    : ToolProcess.RunStreamed(null, "dotnet", "format", "whitespace", project, "--no-restore"));

                if (code != 0)
                {
                    result = Math.Max(result, (verifyOnly ? 1 : 2));
                }
            }

            return result;
        }
    }

    // scan umbrella: parses every .cs file under a root ONCE and runs the selected record
    // analyzers over that single shared corpus.
    internal static class ScanCommand
    {
        private static readonly string[] AllAnalyzers = ["comments", "comment-smells", "locks", "clones"];

        public static int Run(string[] args)
        {
            var scanner = new ArgScanner()
                .Value("Only").Value("OutDir").Flag("Grouped")
                .Value("MaxPerChunk").Value("MinTokens").Value("MinStatements").Flag("NoBlocks");

            if (!scanner.Parse(args))
            {
                Console.Error.WriteLine($"ERROR: {scanner.Error}");
                return 2;
            }

            var selected = ResolveSelection(scanner.Get("Only"));

            if (selected is null)
            {
                return 2;
            }

            var options = new ScanOptions
            {
                OutDirectory = ((scanner.Get("OutDir") is { } outDir)
                    ? Path.GetFullPath(outDir)
                    : Path.Combine(EngineRun.RepositoryRoot, "artifacts", "scan")),
                Grouped = scanner.Has("Grouped"),
                MaxPerChunk = ((scanner.TryGetInt("MaxPerChunk", out var maxPerChunk) && (maxPerChunk > 0)) ? maxPerChunk : 40),
                MinTokens = ((scanner.TryGetInt("MinTokens", out var minTokens) && (minTokens > 0)) ? minTokens : 30),
                MinStatements = ((scanner.TryGetInt("MinStatements", out var minStatements) && (minStatements > 0)) ? minStatements : 4),
                IncludeBlocks = !scanner.Has("NoBlocks"),
                SingleStdout = ((selected.Count == 1) && !scanner.Has("OutDir") && !scanner.Has("Grouped")),
            };

            var root = ((scanner.Positionals.Count > 0) ? scanner.Positionals[0] : "src");
            var corpus = SourceCorpus.TryLoad(root);

            if (corpus is null)
            {
                return 2;
            }

            foreach (var name in selected)
            {
                ISourceAnalyzer analyzer = name switch
                {
                    "comments" => new CommentAnalyzer(),
                    "comment-smells" => new CommentSmellAnalyzer(),
                    "locks" => new LockAnalyzer(),
                    "clones" => new CloneAnalyzer(),
                    _ => throw new InvalidOperationException(name)
                };

                var (jsonl, grouped) = analyzer.Analyze(corpus, options);
                ScanSink.Emit(name, jsonl, grouped, options);
            }

            return 0;
        }

        // The requested analyzers in canonical order, all of them when -Only is absent, or
        // null (with an error already written) on an unknown name.
        private static List<string>? ResolveSelection(string? only)
        {
            if (only is null)
            {
                return [.. AllAnalyzers];
            }

            var requested = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static name => name.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var name in requested)
            {
                if (!AllAnalyzers.Contains(name))
                {
                    Console.Error.WriteLine($"ERROR: unknown scan analyzer '{name}' (known: {string.Join(", ", AllAnalyzers)}).");
                    return null;
                }
            }

            return AllAnalyzers.Where(requested.Contains).ToList();
        }
    }

    // ----- Shared scan / format / engine infrastructure -------------------------------
    // One copy each of the file-enumeration, corpus-parse, arg-parsing, record-sink and
    // engine-run plumbing the commands used to duplicate. The scan analyzers and the
    // formatters are thin layers over these.

    internal readonly record struct ParsedFile(string Absolute, string Relative, SyntaxNode Root, string Text);

    // Resolves a scan root and enumerates its *.cs files (obj/bin pruned, ordinal-sorted) —
    // the single source of the file list every scan and format command walks.
    internal static class SourceFiles
    {
        public static bool TryEnumerate(string rootArgument, out string scanRoot, out string[] files)
        {
            scanRoot = (Path.IsPathRooted(rootArgument)
                ? rootArgument
                : Path.Combine(EngineRun.RepositoryRoot, rootArgument));
            files = [];
            if (!Directory.Exists(scanRoot))
            {
                Console.Error.WriteLine($"ERROR: scan root not found: {scanRoot}");
                return false;
            }

            files = Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories)
                .Where(
                    static path =>
                {
                    var normalized = path.Replace('\\', '/');

                    return (!normalized.Contains("/obj/") && !normalized.Contains("/bin/"));
                }
                )
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
    }

    // Every .cs file under a root, parsed exactly ONCE. The scan analyzers all share one
    // instance, so a full sweep parses the tree a single time instead of once per command.
    internal sealed class SourceCorpus
    {
        private SourceCorpus(string scanRoot, IReadOnlyList<ParsedFile> files)
        {
            ScanRoot = scanRoot;
            Files = files;
        }

        public int FileCount => Files.Count;
        public IReadOnlyList<ParsedFile> Files { get; }
        public string ScanRoot { get; }

        public static SourceCorpus? TryLoad(string rootArgument)
        {
            if (!SourceFiles.TryEnumerate(rootArgument, out var scanRoot, out var files))
            {
                return null;
            }

            var parsed = new List<ParsedFile>(files.Length);

            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                var root = CSharpSyntaxTree.ParseText(text).GetRoot();
                var relative = Path.GetRelativePath(scanRoot, file).Replace('\\', '/');

                parsed.Add(new ParsedFile(file, relative, root, text));
            }

            return new SourceCorpus(scanRoot, parsed);
        }
    }

    // One case-insensitive argument parser for every subcommand: bool flags, valued flags
    // (-Name value) and bare positionals. Names are canonicalized the way the old gates did
    // it (leading dashes trimmed, inner dashes dropped, lowercased), so -NoBuild == --nobuild
    // and -Out-Dir == -OutDir.
    internal sealed class ArgScanner
    {
        private readonly Dictionary<string, bool> m_spec = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_values = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_present = new(StringComparer.Ordinal);
        private readonly List<string> m_positionals = [];

        public string? Error { get; private set; }
        public IReadOnlyList<string> Positionals => m_positionals;

        public ArgScanner Flag(string name)
        {
            m_spec[Canonical(name)] = false;
            return this;
        }
        public ArgScanner Value(string name)
        {
            m_spec[Canonical(name)] = true;
            return this;
        }
        public bool Parse(string[] args)
        {
            for (var index = 0; (index < args.Length); index++)
            {
                var argument = args[index];

                if (!argument.StartsWith('-'))
                {
                    m_positionals.Add(argument);
                    continue;
                }

                var name = Canonical(argument);

                if (!m_spec.TryGetValue(name, out var takesValue))
                {
                    Error = $"unknown argument '{argument}'.";
                    return false;
                }

                if (!takesValue)
                {
                    m_present.Add(name);
                    continue;
                }

                if ((index + 1) >= args.Length)
                {
                    Error = $"argument '{argument}' requires a value.";
                    return false;
                }

                m_values[name] = args[++index];
                m_present.Add(name);
            }

            return true;
        }
        public bool Has(string name) => m_present.Contains(Canonical(name));
        public string? Get(string name) => (m_values.TryGetValue(Canonical(name), out var value) ? value : null);
        public string Get(string name, string fallback) => (Get(name) ?? fallback);
        public bool TryGetInt(string name, out int value) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        public bool TryGetDouble(string name, out double value) => double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        public bool TryGetUInt(string name, out uint value) => uint.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private static string Canonical(string name) => name.TrimStart('-').Replace("-", string.Empty).ToLowerInvariant();
    }

    // The knobs a scan analyzer may read; only the ones an analyzer uses matter to it.
    internal sealed class ScanOptions
    {
        public bool Grouped { get; init; }
        public bool IncludeBlocks { get; init; } = true;
        public int MaxPerChunk { get; init; } = 40;
        public int MinStatements { get; init; } = 4;
        public int MinTokens { get; init; } = 30;
        public string OutDirectory { get; init; } = "";
        public bool SingleStdout { get; init; }
    }
    internal interface ISourceAnalyzer
    {
        string Name { get; }

        // Builds the analyzer's JSONL records and its grouped work-list over a shared
        // corpus, and writes its own one-line stderr digest. No file IO (the sink owns
        // that), so the same corpus feeds every analyzer in one pass.
        (string Jsonl, string Grouped) Analyze(SourceCorpus corpus, ScanOptions options);
    }

    // The record-analyzer output contract: stream a lone analyzer's JSONL to stdout, or
    // write <OutDir>/<name>.jsonl (+ <name>.grouped.json) for a batch — the same bytes the
    // old per-command -Jsonl/-Grouped sinks produced.
    internal static class ScanSink
    {
        public static void Emit(string name, string jsonl, string grouped, ScanOptions options)
        {
            if (options.SingleStdout)
            {
                Console.Out.Write(jsonl);
                return;
            }

            Directory.CreateDirectory(options.OutDirectory);
            var jsonlPath = Path.Combine(options.OutDirectory, $"{name}.jsonl");

            File.WriteAllText(jsonlPath, jsonl);
            Console.Error.WriteLine($"scan: wrote {jsonlPath}");
            if (options.Grouped)
            {
                var groupedPath = Path.Combine(options.OutDirectory, $"{name}.grouped.json");

                File.WriteAllText(groupedPath, grouped);
                Console.Error.WriteLine($"scan: wrote {groupedPath}");
            }
        }
    }

    // The build + demo-run helpers shared by the parity and schema gates. Wraps
    // ToolProcess so each gate stops re-spelling the dotnet invocation.
    internal static class EngineRun
    {
        public static string RepositoryRoot => Path.GetDirectoryName(ToolProcess.GetToolsDirectory())!;

        public static int Build(string configuration) =>
            ToolProcess.RunStreamed(environment: null, "dotnet", "build", Path.Combine(RepositoryRoot, "Puck.slnx"), "-c", configuration);

        public static string DemoProject =>
            Path.Combine(RepositoryRoot, "src", "Puck.Demo", "Puck.Demo.csproj");
        public static string PostProject =>
            Path.Combine(RepositoryRoot, "src", "Puck.Post", "Puck.Post.csproj");

        // Runs the Puck.Post battery filtered to a single tier (A/B/C/D); the battery writes artifacts/post/
        // (including post-report.txt) relative to the working directory and exits 0 pass / 1 gate-fail / 2 infra-fail.
        public static int RunPostTier(string tier) =>
            ToolProcess.RunStreamed(null, "dotnet", "run", "--no-build", "--project", PostProject, "-c", "Release", "--", "--tier", tier);

        // Runs the Puck.Post battery filtered to a single named stage (case-insensitive substring match); used by
        // the fuzz sweep to isolate FuzzStage per seed. Exits 0 pass / 1 gate-fail / 2 infra-fail.
        public static int RunPostStage(string configuration, string stage, params string[] extraArguments)
        {
            var arguments = new List<string>
            {
                "run",
                "--no-build",
                "--project",
                PostProject,
                "-c",
                configuration,
                "--",
                "--stage",
                stage,
            };

            arguments.AddRange(extraArguments);

            return ToolProcess.RunStreamed(null, "dotnet", arguments.ToArray());
        }

        // Runs the (already-built) demo with arbitrary arguments — used by the headless data-driven utilities
        // (--emit-schema), which short-circuit before any window is created.
        public static int RunDemo(params string[] demoArguments)
        {
            var arguments = new List<string>
            {
                "run",
                "--no-build",
                "--project",
                DemoProject,
                "--",
            };

            arguments.AddRange(demoArguments);

            return ToolProcess.RunStreamed(null, "dotnet", arguments.ToArray());
        }
    }

    // The enumerate / parse / rewrite / check-or-write / drift-summary skeleton the two
    // formatters used to each own a copy of. Passes run in sequence with a re-parse between
    // them, so chaining is identical to running the old commands back to back — except the
    // file is written once.
    // Shared IO + safety for the disk rewrite phases (SourceRewrite and NamedArgsPhase),
    // so the drift-tracking, the write guard, the CRLF write, and the summary live once.
    internal static class RewriteIo
    {
        // Newline-insensitive equality, so a pass that only reflows whitespace reads as a no-op
        // regardless of the working tree's line endings.
        public static bool ContentEquals(string a, string b) =>
            (a.ReplaceLineEndings("\n") == b.ReplaceLineEndings("\n"));

        // The write guard: a pass must never leave a file with MORE syntax errors than it had.
        // If it would, the rewrite is dropped and the file reported as corrupt, so a misfiring
        // pass fails the run loudly instead of silently overwriting source with broken code.
        public static bool IntroducesErrors(string original, string rewritten) =>
            (ErrorCount(rewritten) > ErrorCount(original));

        // Source is committed CRLF; ReplaceLineEndings normalizes any mix (incl. lone \r) first.
        public static void WriteCrlf(string file, string text) =>
            File.WriteAllText(file, text.ReplaceLineEndings("\r\n"));

        // The shared drift/normalize summary plus any number of labelled problem buckets
        // (corruption, non-convergence, ...). Exit code is 1 on any problem or on drift in
        // check mode, else 0.
        public static int Report(string label, int fileCount, IReadOnlyList<string> drifted, bool whatIf, params ReadOnlySpan<(string Reason, IReadOnlyList<string> Files)> problems)
        {
            Console.Error.WriteLine(
                (whatIf
                ? ((drifted.Count == 0) ? $"{label}: consistent across {fileCount} files." : $"{label}: {drifted.Count} file(s) drifted from the convention:")
                : $"{label}: normalized {drifted.Count} of {fileCount} files.")
            );
            foreach (var path in drifted)
            {
                Console.Error.WriteLine($"  {path}");
            }

            var hadProblem = false;

            foreach (var (reason, files) in problems)
            {
                if (files.Count == 0)
                {
                    continue;
                }

                hadProblem = true;
                Console.Error.WriteLine($"{label}: {files.Count} file(s) {reason}:");
                foreach (var path in files)
                {
                    Console.Error.WriteLine($"  {path}");
                }
            }

            return ((hadProblem || (whatIf && (drifted.Count > 0))) ? 1 : 0);
        }

        private static int ErrorCount(string text) =>
            CSharpSyntaxTree.ParseText(text).GetDiagnostics().Count(static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));
    }
    internal static class SourceRewrite
    {
        public static int Run(string label, string rootArgument, bool whatIf, bool verify, IReadOnlyList<(string Name, Func<SyntaxNode, SyntaxNode> Apply)> passes)
        {
            if (!SourceFiles.TryEnumerate(rootArgument, out _, out var files))
            {
                return 2;
            }

            // -Verify audits the passes without touching the tree: it asserts every rewrite is
            // a fixed point (a formatter run twice must equal running it once), never writes.
            var writing = (!whatIf && !verify);
            var repositoryRoot = EngineRun.RepositoryRoot;
            var drifted = new List<string>();
            var corrupted = new List<string>();
            var nonConvergent = new List<string>();

            foreach (var file in files)
            {
                var original = File.ReadAllText(file);
                var current = ApplyAll(original, passes);

                if (RewriteIo.ContentEquals(current, original))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');

                if (RewriteIo.IntroducesErrors(original, current))
                {
                    corrupted.Add(relative);
                    continue;
                }

                if (verify && !RewriteIo.ContentEquals(ApplyAll(current, passes), current))
                {
                    nonConvergent.Add(relative);
                    continue;
                }

                drifted.Add(relative);
                if (writing)
                {
                    RewriteIo.WriteCrlf(file, current);
                }
            }

            return RewriteIo.Report(
                label,
                files.Length,
                drifted,
                (whatIf || verify),
                ("would introduce syntax errors — SKIPPED", corrupted),
                ("do not converge (a pass is not idempotent) — SKIPPED", nonConvergent)
            );
        }

        // Applies the pass pipeline once, re-parsing between passes so each sees the prior's output.
        private static string ApplyAll(string text, IReadOnlyList<(string Name, Func<SyntaxNode, SyntaxNode> Apply)> passes)
        {
            foreach (var pass in passes)
            {
                var node = CSharpSyntaxTree.ParseText(text).GetRoot();

                text = pass.Apply(node).ToFullString();
            }

            return text;
        }
    }

    // The disk phase behind the semantic `named-args` pass: parses the whole src tree into
    // one Compilation (so a call's method symbol — BCL or in-repo — resolves) referencing the
    // runtime's assemblies, then runs NamedArgsRewriter against each target file's semantic
    // model. Symbol binding tolerates unrelated errors elsewhere, so a call is named whenever
    // ITS own method resolves. Writes CRLF like SourceRewrite; -WhatIf reports drift only.
    internal static class NamedArgsPhase
    {
        // Fallback when a project has not been built (no obj/*.GlobalUsings.g.cs to read): the
        // SDK's default ImplicitUsings set. A built project supplies its real global usings.
        private const string DefaultGlobalUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """;

        // Some assemblies in a build output are native (mimalloc, vulkan-1, ...) and are not valid
        // managed metadata references. CreateFromFile is lazy (it wouldn't throw until the
        // compilation reads the file), so probe the PE eagerly: PEReader.HasMetadata is true only
        // for managed assemblies, and — unlike GetAssemblyName — reads no culture, so it is safe
        // under globalization-invariant mode. Native / unreadable files are dropped.
        private static MetadataReference? TryReference(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);

                return (peReader.HasMetadata ? MetadataReference.CreateFromFile(path) : null);
            }
            catch (Exception exception) when ((exception is BadImageFormatException or IOException))
            {
                return null;
            }
        }

        // The project's NuGet package compile assemblies, read from the restore output
        // (obj/project.assets.json). A LIBRARY project's transitive package DLLs are not copied to
        // its own bin (they land in the consuming app's output), so bin alone misses them; the
        // assets file lists every package's compile asset by path into the global packages folder.
        private static IEnumerable<string> PackageReferences(string projectRoot)
        {
            var assetsPath = Path.Combine(projectRoot, "obj", "project.assets.json");

            if (!File.Exists(assetsPath))
            {
                return [];
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(assetsPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("packageFolders", out var folders)
                || !root.TryGetProperty("targets", out var targets)
                || !root.TryGetProperty("libraries", out var libraries))
            {
                return [];
            }

            var packageRoots = folders.EnumerateObject().Select(static folder => folder.Name).ToList();
            var results = new List<string>();

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("compile", out var compile)
                        || !libraries.TryGetProperty(library.Name, out var entry)
                        || !entry.TryGetProperty("path", out var libraryPath))
                    {
                        continue;
                    }

                    foreach (var asset in compile.EnumerateObject())
                    {
                        if (asset.Name.EndsWith("_._", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var relative = asset.Name.Replace('/', Path.DirectorySeparatorChar);
                        var resolved = packageRoots
                            .Select(packageRoot => Path.Combine(packageRoot, libraryPath.GetString()!, relative))
                            .FirstOrDefault(File.Exists);

                        if (resolved is not null)
                        {
                            results.Add(resolved);
                        }
                    }
                }
            }

            return results;
        }

        // Builds a compilation over the project's trees against its REAL build closure, so calls
        // into package / sibling-project types bind and get named instead of being silently
        // skipped. Two inputs match the actual build: the SDK-generated global-usings file
        // (obj/**/*.GlobalUsings.g.cs — the project's true implicit + explicit global usings) and
        // every dependency assembly in the built output (bin/**/*.dll, minus the project's own
        // output and native DLLs), unioned with the shared framework assemblies. Both need a prior
        // build; without one, `degraded` is set and only the framework set + a default usings list
        // are used, so coverage is reduced.
        private static CSharpCompilation BuildProjectCompilation(string projectRoot, IEnumerable<SyntaxTree> trees, CSharpParseOptions parseOptions, out bool degraded)
        {
            var objDirectory = Path.Combine(projectRoot, "obj");
            var globalUsingsFile = (Directory.Exists(objDirectory)
                ? Directory.EnumerateFiles(objDirectory, "*.GlobalUsings.g.cs", SearchOption.AllDirectories).FirstOrDefault()
                : null);
            var globalUsings = CSharpSyntaxTree.ParseText(
                ((globalUsingsFile is not null) ? File.ReadAllText(globalUsingsFile) : DefaultGlobalUsings),
                parseOptions
            );

            // Source-generator output (CsWin32 interop, etc.) is produced in-memory during build
            // and is absent from disk unless the project sets EmitCompilerGeneratedFiles. When it
            // IS emitted (obj/**/generated/**/*.cs), include it so calls into generated types
            // resolve too; otherwise those calls are reported as unresolved and left positional.
            var generatedTrees = (Directory.Exists(objDirectory)
                ? Directory.EnumerateFiles(objDirectory, "*.cs", SearchOption.AllDirectories)
                    .Where(static path => path.Contains($"{Path.DirectorySeparatorChar}generated{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path: path))
                : Enumerable.Empty<SyntaxTree>());

            var frameworkDlls = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var projectName = Path.GetFileNameWithoutExtension((Directory.EnumerateFiles(projectRoot, "*.csproj").FirstOrDefault() ?? ""));
            var binDirectory = Path.Combine(projectRoot, "bin");
            var outputDlls = (Directory.Exists(binDirectory)
                ? Directory.EnumerateFiles(binDirectory, "*.dll", SearchOption.AllDirectories)
                    .Where(path => (!string.Equals(Path.GetFileNameWithoutExtension(path), projectName, StringComparison.OrdinalIgnoreCase)
                        && !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
                : Enumerable.Empty<string>());
            // Built dependency assemblies and NuGet package assemblies win over the framework on a
            // name clash — they are the exact versions the project compiles against.
            var references = outputDlls.Concat(PackageReferences(projectRoot)).Concat(frameworkDlls)
                .GroupBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(static group => TryReference(group.First()))
                .Where(static reference => (reference is not null))
                .Select(static reference => reference!);

            degraded = ((globalUsingsFile is null) || !Directory.Exists(binDirectory));
            return CSharpCompilation.Create(
                "named-args",
                trees.Append(globalUsings).Concat(generatedTrees),
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
            );
        }

        // Coverage probe: a call whose symbol binds to nothing (no symbol, no candidate) is one
        // named-args must leave positional. Driving this to zero is the point of the real build
        // closure; a nonzero count means the references are still incomplete. `nameof(...)` is
        // syntactically an invocation but a contextual operator with no method symbol, so it is
        // excluded — counting it would be a false positive.
        private static int CountUnresolvedCalls(SyntaxNode root, SemanticModel model) =>
            root.DescendantNodes().Count(node =>
                ((node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
                && (node is not InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
                && (model.GetSymbolInfo(node) is { Symbol: null, CandidateSymbols.IsEmpty: true })));

        public static int Run(string rootArgument, bool whatIf, bool verify)
        {
            if (!SourceFiles.TryEnumerate(rootArgument, out _, out var targetFiles))
            {
                return 2;
            }

            // Resolve symbols against each file's OWNING project (its own trees + build closure),
            // never one merged compilation — so a tree-wide root spanning many projects still binds
            // every project's calls correctly. Files are grouped by owning project and processed
            // against that project's compilation; each real method call then gets named.
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var byProject = targetFiles.GroupBy(
                static file => (FindProjectDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!) ?? ""),
                StringComparer.OrdinalIgnoreCase
            );

            var drifted = new List<string>();
            var corrupted = new List<string>();
            var degradedProjects = new List<string>();
            var unresolved = 0;

            foreach (var projectGroup in byProject)
            {
                if ((projectGroup.Key.Length == 0) || !SourceFiles.TryEnumerate(projectGroup.Key, out _, out var compilationFiles))
                {
                    continue;
                }

                unresolved += ProcessProject(projectGroup.Key, projectGroup, compilationFiles, parseOptions, whatIf, verify, drifted, corrupted, degradedProjects);
            }

            if (degradedProjects.Count > 0)
            {
                Console.Error.WriteLine($"named-args: {degradedProjects.Count} project(s) not built ({string.Join(", ", degradedProjects)}) — resolved against the framework only; some calls stay positional. Build for full coverage.");
            }

            if (unresolved > 0)
            {
                Console.Error.WriteLine($"named-args: {unresolved} call(s) could not be resolved and were left positional (see the not-built note above, or check references).");
            }

            return RewriteIo.Report(
                "named-args",
                targetFiles.Length,
                drifted,
                (whatIf || verify),
                ("would introduce syntax errors — SKIPPED", corrupted)
            );
        }

        // Names the target files of ONE project against a compilation of that project's trees and
        // its real build closure; accumulates drift / corruption / not-built into the shared lists
        // and returns the count of calls that could not be resolved (left positional).
        private static int ProcessProject(string projectRoot, IEnumerable<string> targets, string[] compilationFiles, CSharpParseOptions parseOptions, bool whatIf, bool verify, List<string> drifted, List<string> corrupted, List<string> degradedProjects)
        {
            var treesByPath = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in compilationFiles)
            {
                treesByPath[Path.GetFullPath(file)] = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, path: file);
            }

            var compilation = BuildProjectCompilation(projectRoot, treesByPath.Values, parseOptions, out var degraded);

            if (degraded)
            {
                degradedProjects.Add(Path.GetFileName(projectRoot));
            }

            var unresolved = 0;

            foreach (var file in targets)
            {
                if (!treesByPath.TryGetValue(Path.GetFullPath(file), out var tree))
                {
                    continue;
                }

                var model = compilation.GetSemanticModel(tree);

                unresolved += CountUnresolvedCalls(tree.GetRoot(), model);
                var rewritten = new NamedArgsRewriter(model).Visit(tree.GetRoot())!.ToFullString();
                var original = File.ReadAllText(file);

                if (RewriteIo.ContentEquals(rewritten, original))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(EngineRun.RepositoryRoot, file).Replace('\\', '/');

                if (RewriteIo.IntroducesErrors(original, rewritten))
                {
                    corrupted.Add(relative);
                    continue;
                }

                drifted.Add(relative);
                // named-args only ADDS names where absent, so a second run is a no-op — idempotent
                // by construction. -Verify still audits (report drift, never write) + the guard.
                if (!whatIf && !verify)
                {
                    RewriteIo.WriteCrlf(file, rewritten);
                }
            }

            return unresolved;
        }

        // The nearest ancestor directory (from the scan root up) that holds a .csproj — the
        // owning project whose trees form the compilation.
        private static string? FindProjectDirectory(string scanRoot)
        {
            for (var directory = new DirectoryInfo(scanRoot); (directory is not null); directory = directory.Parent)
            {
                if (directory.EnumerateFiles("*.csproj").Any())
                {
                    return directory.FullName;
                }
            }

            return null;
        }
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
        public static string RunCaptured(string fileName, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, UseShellExecute = false };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = (Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}."));
            var output = process.StandardOutput.ReadToEnd();

            process.WaitForExit();
            return output;
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
