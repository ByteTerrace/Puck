using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Demo.Creator;
using Puck.Demo.Forge.Bake;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// The <c>--forge</c> tool mode: the first artifact of the SDF-authored-cartridge pipeline. It authors a mini overworld
/// scene as SDF programs (<see cref="SceneForge"/>), crushes it to the Humble GamingBrick's indexed-tile world, assembles a
/// genuine CGB ROM around the baked assets (<see cref="HgbCartridge.Build"/>), writes the <c>.gbc</c> (plus a preview
/// PNG), and self-verifies by booting the result on a real Humble machine. Boot it separately with <c>--rom</c>.
/// </summary>
internal static class RomForge {
    private const int CreatureSupersample = 128;
    private const int CreatureReduceFactor = 8; // 128 -> 16, a 2x2-tile metasprite.
    private const int CreatureSize = 16;
    private const int MaxTiles = 256; // Single-byte tile ids under 0x8000 unsigned addressing.

    /// <summary>Runs the forge inside the shared GPU host; returns 0 on success.</summary>
    public static Task<int> RunAsync(string outputPath, string[] args) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        return ForgeHost.RunAsync(args: args, work: (device, gpu) => {
            Forge(device: device, gpu: gpu, outputPath: outputPath);

            return 0;
        });
    }

    /// <summary>Forges the Pocket Camera viewfinder cartridge and self-verifies it. Unlike the SDF forge this needs no
    /// GPU — the ROM's pixels come from the M64282FP sensor at run time — so it builds, writes, and boots synchronously:
    /// the boot runs against the emulator's default (deterministic gradient) sensor, so <c>&lt;out&gt;.emulated.png</c>
    /// shows the forged ROM driving the real camera protocol and dithering the gradient onto the brick screen.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunCameraAsync(string outputPath) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var rom = CameraRom.Build(title: "PUCKCAM");

        WriteRom(outputPath: outputPath, rom: rom);
        VerifyBoot(rom: rom, outputPath: outputPath);

        Console.WriteLine(value: $"camera forge | wrote {outputPath} ({rom.Length} bytes) | Pocket Camera (0xFC) viewfinder | boot it with: --rom {outputPath}");

        return Task.FromResult(result: 0);
    }

    /// <summary>The default audio document the tune forge compiles when <c>--forge-tune-from</c> is omitted.</summary>
    private const string DefaultTuneDocumentPath = "docs/examples/tunes/tune.audio.json";

    // A single channel's full 0..15 envelope swing at maxed master volume tops out at 960 counts (gatedSum × 64) —
    // this floor proves real modulation happened (a dead driver's spread is 0) without demanding the multi-channel
    // games' floor a solo voice can never reach.
    private const int TuneMinimumAudioSpread = 256;

    /// <summary>The <c>--forge-tune</c> tool mode: build the minimal framework JUKEBOX .gbc from an authored
    /// <c>puck.audio.v1</c> document (see <see cref="AudioDocument"/>) — its music loop comes ENTIRELY from
    /// <see cref="AudioDocumentCompiler"/>, never a hand array. No GPU is needed (the cart is one flat tile behind
    /// the song name + "PUSH START"), so — like <see cref="RunCameraAsync"/> — it builds, self-verifies (state-graph
    /// battery + the shared <see cref="VerifyGameAudio"/> WAV-spread proof), and writes synchronously.</summary>
    /// <param name="documentPath">The audio document JSON path, or <see langword="null"/> for the checked-in
    /// <see cref="DefaultTuneDocumentPath"/> example.</param>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunTuneAsync(string? documentPath, string outputPath) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var resolvedDocumentPath = (documentPath ?? DefaultTuneDocumentPath);
        var document = AudioDocumentStore.Load(path: resolvedDocumentPath);
        var rom = Tune.TuneRom.Build(document: document);

        WriteRom(outputPath: outputPath, rom: rom);
        Tune.TuneRom.Verify(rom: rom);
        VerifyBoot(rom: rom, outputPath: outputPath);
        // The jukebox only ever sounds ONE channel (pulse-2, the music voice) — see DefaultMinimumAudioSpread's
        // remarks — so its spread floor is scaled to what a single channel can physically produce, not the
        // multi-channel games' floor.
        VerifyGameAudio(label: "tune", outputPath: outputPath, rom: rom, startsPlaying: true, minimumSpread: TuneMinimumAudioSpread);

        Console.WriteLine(value: $"tune forge | wrote {outputPath} ({rom.Length} bytes) | \"{document.Name}\" from {resolvedDocumentPath} | boot it with: --rom {outputPath}");

        return Task.FromResult(result: 0);
    }

    /// <summary>The <c>--forge-volley</c> tool mode: bake the SDF-authored title screen on the one-shot GPU host
    /// (<see cref="VolleyTitleBake"/>) — the DEFAULT title, with the hand-authored banner as the fallback when the
    /// GPU is unavailable — then build + self-verify + write the Volley cartridge. The verify battery runs OUTSIDE
    /// the GPU host, so a verification failure is never masked by a title-bake fallback.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunVolleyAsync(string outputPath, string[] args) =>
        RunFrameworkGameAsync(args: args, build: static () => VolleyRom.Build(), label: "volley", outputPath: outputPath, tryInstallTitle: VolleyTitleBake.TryInstall, verify: VolleyRom.Verify);

    /// <summary>The <c>--forge-brickfall</c> tool mode: bake the SDF-authored title screen on the one-shot GPU host
    /// (<see cref="BrickfallTitleBake"/>) — the DEFAULT title, with the hand-authored banner as the fallback when the
    /// GPU is unavailable — then build + self-verify + write the Brickfall cartridge. The verify battery runs OUTSIDE
    /// the GPU host, so a verification failure is never masked by a title-bake fallback.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunBrickfallAsync(string outputPath, string[] args) =>
        RunFrameworkGameAsync(args: args, build: static () => BrickfallRom.Build(), label: "brickfall", outputPath: outputPath, tryInstallTitle: BrickfallTitleBake.TryInstall, verify: BrickfallRom.Verify);

    // The default spread floor: proven for the five framework games, whose capture windows always overlap at least
    // two channels (the title chirp/loop kick-in on pulse-1 plus the loop on pulse-2, gameplay effects on top). A
    // cart that only ever sounds ONE channel — the jukebox, whose whole capture window is pulse-2 alone — physically
    // cannot reach it: the mix law's (volume+1)/8 scale caps a single channel's full 0..15 envelope swing at 960
    // counts (gatedSum × 64, master volume maxed), an order of magnitude under this floor.
    private const int DefaultMinimumAudioSpread = 4_096;

    // The audio proof (shared by every framework game): boot the forged cartridge with the host-facing audio sink
    // enabled, drive it into its loop, drain the sink after every frame, and write the whole capture as
    // <out>.audio.wav — the listenable artifact. Asserts that APU frames actually reach the sink AND that the
    // capture carries real modulation (a wide min→max spread), so a dead sound path fails the forge instead of
    // shipping a mute cartridge. (A "nonzero" check would be vacuous: the emulator's documented mix law recenters
    // powered-but-silent output to a constant DC level, so silence is nonzero by design.)
    private static void VerifyGameAudio(string label, string outputPath, byte[] rom, bool startsPlaying = false, int minimumSpread = DefaultMinimumAudioSpread) {
        const int sampleRate = 32_768;

        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        var joypad = machine.GetRequiredService<IJoypad>();
        var samples = new List<short>(capacity: (sampleRate * 12));
        var sink = machine.GetRequiredService<IAudioSink>();
        var staging = new short[sampleRate];
        var maximum = short.MinValue;
        var minimum = short.MaxValue;

        sink.Configure(sampleRate: sampleRate);

        void Run(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                joypad.SetButtons(pressed: buttons);
                machine.Machine.Run(tCycles: 70224UL);

                var read = sink.ReadSamples(destination: staging);

                for (var index = 0; (index < read); index++) {
                    maximum = Math.Max(val1: maximum, val2: staging[index]);
                    minimum = Math.Min(val1: minimum, val2: staging[index]);
                    samples.Add(item: staging[index]);
                }
            }
        }

        // Most framework games boot to a silent title and need a START edge to kick the loop in; a cart whose loop
        // is already running at boot (the jukebox) skips straight to capturing play.
        if (!startsPlaying) {
            Run(buttons: JoypadButtons.None, frames: 20);   // Boot → title (quiet).
            Run(buttons: JoypadButtons.Start, frames: 2);   // START: the chirp + the loop kick in.
        }

        Run(buttons: JoypadButtons.None, frames: 320);  // ~5 s of play under the loop (per-event effects included).

        if (samples.Count == 0) {
            throw new InvalidOperationException(message: $"{label} audio verification failed: no samples reached the audio sink (is the output stage ticking?).");
        }

        if ((maximum - minimum) < minimumSpread) {
            throw new InvalidOperationException(message: $"{label} audio verification failed: the capture is flat (spread {maximum - minimum}, range {minimum}..{maximum}) — the ROM sound driver never modulated the APU.");
        }

        var wavPath = Path.ChangeExtension(path: outputPath, extension: ".audio.wav");

        WriteWav(path: wavPath, sampleRate: sampleRate, samples: samples);
        Console.WriteLine(value: $"{label} audio | {samples.Count / 2} stereo frames ({(samples.Count / 2) / (double)sampleRate:F1} s at {sampleRate} Hz, sample range {minimum}..{maximum}) → {wavPath}");
    }

    // A minimal PCM16 stereo RIFF writer for the audio proof (no external audio library, on purpose — mirrors the
    // PngEncoder posture).
    private static void WriteWav(string path, int sampleRate, List<short> samples) {
        using var stream = File.Create(path: path);
        using var writer = new BinaryWriter(output: stream);

        var dataByteCount = (samples.Count * sizeof(short));

        writer.Write(chars: "RIFF".ToCharArray());
        writer.Write(value: (36 + dataByteCount));
        writer.Write(chars: "WAVE".ToCharArray());
        writer.Write(chars: "fmt ".ToCharArray());
        writer.Write(value: 16);
        writer.Write(value: (short)1);              // PCM.
        writer.Write(value: (short)2);              // Stereo.
        writer.Write(value: sampleRate);
        writer.Write(value: (sampleRate * 4));      // Bytes per second.
        writer.Write(value: (short)4);              // Block align.
        writer.Write(value: (short)16);             // Bits per sample.
        writer.Write(chars: "data".ToCharArray());
        writer.Write(value: dataByteCount);

        foreach (var sample in samples) {
            writer.Write(value: sample);
        }
    }

    /// <summary>The <c>--forge-solitaire</c> tool mode: bake the SDF-authored title emblem, felt table, and cursor
    /// on the one-shot GPU host (<see cref="SolitaireBake"/>) — the DEFAULT art, with the hand-authored banner,
    /// flat felt, and pointer as the fallbacks when the GPU is unavailable — then build + self-verify + write the
    /// Solitaire cartridge. The verify battery runs OUTSIDE the GPU host, so a verification failure is never
    /// masked by an art-bake fallback.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunSolitaireAsync(string outputPath, string[] args) {
        byte[]? rom = null;

        var exitCode = await RunFrameworkGameAsync(
            args: args,
            build: () => (rom = SolitaireRom.Build()),
            label: "solitaire",
            outputPath: outputPath,
            tryInstallTitle: SolitaireBake.TryInstall,
            verify: SolitaireRom.Verify
        );

        if (rom is { } bytes) {
            // The second boot proof: the dealt board as pixels, beside the title's .emulated.png.
            SolitaireRom.WritePlayProof(path: Path.ChangeExtension(path: outputPath, extension: ".play.png"), rom: bytes);
        }

        return exitCode;
    }

    /// <summary>The <c>--forge-poker</c> tool mode: bake the SDF-authored title emblem, felt table, and cursor on
    /// the one-shot GPU host (<see cref="PokerBake"/>) — the DEFAULT art, with the hand-authored banner, flat felt,
    /// and pointer as the fallbacks when the GPU is unavailable — then build + self-verify + write the five-card
    /// draw Poker cartridge. The verify battery runs OUTSIDE the GPU host, so a verification failure is never
    /// masked by an art-bake fallback.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunPokerAsync(string outputPath, string[] args) {
        byte[]? rom = null;

        var exitCode = await RunFrameworkGameAsync(
            args: args,
            build: () => (rom = PokerRom.Build()),
            label: "poker",
            outputPath: outputPath,
            tryInstallTitle: PokerBake.TryInstall,
            verify: PokerRom.Verify
        );

        if (rom is { } bytes) {
            // The second boot proof: the dealt table as pixels, beside the title's .emulated.png.
            PokerRom.WritePlayProof(path: Path.ChangeExtension(path: outputPath, extension: ".play.png"), rom: bytes);
        }

        return exitCode;
    }

    /// <summary>The <c>--forge-bake-calibration</c> tool mode: bake SDF stand-ins for Volley's hand art at the hand
    /// tiles' native sizes and report the per-tile pixel match — a calibration REPORT, never a gate (see
    /// <see cref="Bake.BakeCalibration"/>) — forwarded so the composition root keeps one forge dispatcher.</summary>
    /// <param name="outputDirectory">Where the comparison PNG lands.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunBakeCalibrationAsync(string outputDirectory, string[] args) =>
        Bake.BakeCalibration.RunAsync(args: args, outputDirectory: outputDirectory);

    /// <summary>The <c>--forge-chroma</c> tool mode: bake the SDF-authored title screen on the one-shot GPU host
    /// (<see cref="ChromaTitleBake"/>) — the DEFAULT title, with the hand-authored banner as the fallback when the
    /// GPU is unavailable — then build + self-verify + write the Chroma colour-match cartridge. The verify battery
    /// runs OUTSIDE the GPU host, so a verification failure is never masked by a title-bake fallback.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunChromaAsync(string outputPath, string[] args) =>
        RunFrameworkGameAsync(args: args, build: static () => ChromaRom.Build(), label: "chroma", outputPath: outputPath, tryInstallTitle: ChromaTitleBake.TryInstall, verify: ChromaRom.Verify);

    /// <summary>The <c>--forge-bake</c>/<c>--forge-bake-stress</c> tool modes: the SDF→brick BAKE pipeline's headless
    /// proof (see <see cref="Bake.BakeForge"/>) — a thin forward so the composition root keeps one forge dispatcher.</summary>
    /// <param name="outputDirectory">Where the preview PNGs land.</param>
    /// <param name="stress">Whether to bake the palette-pressure stress scene instead of the standard 8 results.</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunBakeAsync(string outputDirectory, bool stress, string[] args) =>
        Bake.BakeForge.RunAsync(args: args, outputDirectory: outputDirectory, stress: stress);

    // The shared shape of the three framework games' forges: SDF-bake the title on the one-shot GPU host (best-effort
    // — the hand-authored banner is the narrated fallback), then OUTSIDE the host build the cartridge, write the .gbc,
    // run its behavioural self-verify (drives the joypad and reads work RAM), boot it for an <out>.emulated.png
    // proof, and capture its sound path as an <out>.audio.wav proof — so a verification failure is never masked by a
    // title-bake fallback.
    private static async Task<int> RunFrameworkGameAsync(string outputPath, string[] args, string label, Func<IGpuDeviceContext, IGpuComputeServices, bool> tryInstallTitle, Func<byte[]> build, Action<byte[]> verify) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var baked = false;

        try {
            _ = await ForgeHost.RunAsync(args: args, work: (device, gpu) => {
                baked = tryInstallTitle(device, gpu);

                return 0;
            });
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"{label} forge | GPU host unavailable ({exception.Message})");
        }

        if (!baked) {
            Console.Error.WriteLine(value: $"{label} forge | using the hand-authored title");
        }

        var rom = build();

        WriteRom(outputPath: outputPath, rom: rom);
        verify(rom);
        VerifyBoot(rom: rom, outputPath: outputPath);
        VerifyGameAudio(label: label, outputPath: outputPath, rom: rom);

        Console.WriteLine(value: $"{label} forge | wrote {outputPath} ({rom.Length} bytes) | boot it with: --rom {outputPath}");

        return 0;
    }

    /// <summary>The <c>--forge-avatar</c> tool mode: forge a playable OVERWORLD ROM from a player's avatar (a saved
    /// creation document or legacy avatar JSON, or a built-in demo avatar when no path is given). A
    /// <c>puck.creation.v1</c> document contributes its bake style AND its timeline FRAMES as the authored walk
    /// poses; anything else walks the procedural cycle. Runs inside the shared GPU host; returns 0 on success.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="avatarJsonPath">An optional creation/avatar JSON file (as saved by creator mode).</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunAvatarAsync(string outputPath, string? avatarJsonPath, string[] args) =>
        RunAvatarAsync(outputPath: outputPath, avatarJsonPath: avatarJsonPath, args: args, movementMode: MovementMode.FourWay);

    /// <summary>String-typed movement-mode overload — the CLI seam. <c>Main</c> sits at its class-coupling ceiling
    /// and may not name <see cref="MovementMode"/>, so the <c>--forge-avatar-movement-mode</c> option passes its raw
    /// token here and the mapping lives with the forge: <c>four</c> (default), <c>eight</c>, or <c>hex</c>.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="avatarJsonPath">An optional creation/avatar JSON file (as saved by creator mode).</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <param name="movementModeText">The direction-lock token (<c>four</c>/<c>eight</c>/<c>hex</c>; null = four).</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunAvatarAsync(string outputPath, string? avatarJsonPath, string[] args, string? movementModeText) =>
        RunAvatarAsync(outputPath: outputPath, avatarJsonPath: avatarJsonPath, args: args, movementMode: (movementModeText?.ToLowerInvariant() switch {
            "eight" => MovementMode.EightWay,
            "hex" => MovementMode.Hex,
            _ => MovementMode.FourWay,
        }));

    /// <summary>Overload taking the walker's direction-lock mode explicitly — see the <c>--forge-avatar-movement-mode</c>
    /// patch block in this workstream's report for the CLI option that will call this once wired up. Kept as a SEPARATE
    /// overload (rather than a new parameter on the three-argument overload) so <c>Program.cs</c>'s existing call site
    /// keeps resolving against the untouched overload, off <c>Main</c>'s class-coupling ceiling.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <param name="avatarJsonPath">An optional creation/avatar JSON file (as saved by creator mode).</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <param name="movementMode">The forged walker's d-pad direction lock.</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunAvatarAsync(string outputPath, string? avatarJsonPath, string[] args, MovementMode movementMode) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses = null;
        BakeStyle? style = null;
        var avatar = BuildDemoAvatar();

        if (avatarJsonPath is { } path) {
            // CreationStore.Load accepts both the native puck.creation.v1 document and the legacy avatar JSON.
            var document = CreationStore.Load(nameOrPath: path)
                ?? throw new ArgumentException(message: $"No creation or avatar JSON readable at '{path}'.", paramName: nameof(avatarJsonPath));

            avatar = AvatarForge.FromCreation(document: document, framePoses: out framePoses);
            style = BakeStyles.Resolve(diagnostic: out _, name: document.BakeStyle);
        }

        return ForgeHost.RunAsync(args: args, work: (device, gpu) => {
            ForgeAvatar(avatar: avatar, device: device, framePoses: framePoses, gpu: gpu, movementMode: movementMode, outputPath: outputPath, style: style, title: "PUCKAVTR");

            return 0;
        });
    }

    /// <summary>Forges a playable overworld ROM: a forged room the avatar's own sprite sheet walks around. Shared by the
    /// <c>--forge-avatar</c> tool and the in-engine creator forge, so both bake the identical cartridge from an avatar.
    /// Beside the cartridge it writes the sheet preview and the bake pipeline's <c>&lt;out&gt;.bake.bin</c> asset blob
    /// (size + chunk list narrated to stderr).</summary>
    /// <param name="device">The one-shot GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="avatar">The player's creation.</param>
    /// <param name="outputPath">Where to write the <c>.gbc</c> (and its <c>.emulated.png</c> boot proof).</param>
    /// <param name="title">The cartridge header title (≤ 15 chars, upper-cased).</param>
    /// <param name="framePoses">The authored walk poses from a creation document's timeline, or null (procedural).</param>
    /// <param name="style">The bake style from the creation document, or null (classic).</param>
    /// <param name="movementMode">The forged walker's d-pad direction lock (default <see cref="MovementMode.FourWay"/>
    /// — today's behaviour, byte-identical to every ROM forged before this parameter existed).</param>
    internal static void ForgeAvatar(IGpuDeviceContext device, IGpuComputeServices gpu, AvatarDefinition avatar, string outputPath, string title, IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses = null, BakeStyle? style = null, MovementMode movementMode = MovementMode.FourWay) {
        ArgumentNullException.ThrowIfNull(avatar);

        var rom = ForgeAvatarRom(avatar: avatar, bundle: out var bundle, device: device, framePoses: framePoses, gpu: gpu, movementMode: movementMode, roomTileCount: out var roomTileCount, sheet: out var sheet, style: style, title: title);

        WriteRom(outputPath: outputPath, rom: rom);
        PngEncoder.Write(height: sheet.PreviewHeight, path: Path.ChangeExtension(path: outputPath, extension: ".sheet.png"), rgba: sheet.PreviewRgba, width: sheet.PreviewWidth);
        WriteBakeBlob(bundle: bundle, outputPath: outputPath);
        VerifyOverworld(movementMode: movementMode, rom: rom);
        VerifyBoot(rom: rom, outputPath: outputPath);

        Console.WriteLine(value: $"avatar forge | wrote {outputPath} ({rom.Length} bytes) | {roomTileCount} room tiles + {sheet.TileCount} avatar tiles ({AvatarForge.PoseCount} poses) | boot it with: --rom {outputPath}");
    }

    /// <summary>Forges the overworld ROM BYTES from an avatar, in memory — no disk, no boot self-check. This is the
    /// hot path the live in-engine loop uses: the player commits an edited avatar and the cabinet re-forges + swaps to
    /// these bytes without ever leaving the game. The disk <see cref="ForgeAvatar"/> is this plus write + verify.</summary>
    /// <param name="device">The GPU device (the live overworld's, or a one-shot host's).</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="avatar">The avatar to bake.</param>
    /// <param name="title">The cartridge header title.</param>
    /// <param name="movementMode">The forged walker's d-pad direction lock (default <see cref="MovementMode.FourWay"/>
    /// — today's behaviour, byte-identical to every ROM forged before this parameter existed).</param>
    /// <returns>A genuine 32 KiB overworld ROM.</returns>
    internal static byte[] ForgeAvatarRom(IGpuDeviceContext device, IGpuComputeServices gpu, AvatarDefinition avatar, string title, MovementMode movementMode = MovementMode.FourWay) =>
        ForgeAvatarRom(avatar: avatar, bundle: out _, device: device, framePoses: null, gpu: gpu, movementMode: movementMode, roomTileCount: out _, sheet: out _, style: null, title: title);

    /// <summary>Forges the overworld ROM BYTES from a full creation document, in memory — the LOSSLESS in-game hot
    /// path. Lifts the document exactly as <c>--forge-avatar-from</c> does (<see cref="AvatarForge.FromCreation"/> for
    /// the recentered avatar + the timeline's authored walk poses, and the document's <c>bakeStyle</c>), so an in-game
    /// commit bakes the SAME cartridge the CLI would from the same saved creation — the animation frames and bake style
    /// reach the ROM, not just the rest-pose geometry. The disk <see cref="ForgeAvatar"/> is this plus write + verify.</summary>
    /// <param name="device">The GPU device (the live overworld's, or a one-shot host's).</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="document">The full creation document (the live scene's <c>ToDocument()</c>).</param>
    /// <param name="title">The cartridge header title.</param>
    /// <param name="movementMode">The forged walker's d-pad direction lock (default <see cref="MovementMode.FourWay"/>).</param>
    /// <returns>A genuine 32 KiB overworld ROM.</returns>
    internal static byte[] ForgeAvatarRomFromCreation(IGpuDeviceContext device, IGpuComputeServices gpu, CreationDocument document, string title, MovementMode movementMode = MovementMode.FourWay) {
        ArgumentNullException.ThrowIfNull(document);

        var avatar = AvatarForge.FromCreation(document: document, framePoses: out var framePoses);
        var style = BakeStyles.Resolve(diagnostic: out _, name: document.BakeStyle);

        return ForgeAvatarRom(avatar: avatar, bundle: out _, device: device, framePoses: framePoses, gpu: gpu, movementMode: movementMode, roomTileCount: out _, sheet: out _, style: style, title: title);
    }

    /// <summary>Forges a creation document to disk exactly as <c>--forge-avatar-from</c> does: the recentered avatar +
    /// the timeline's authored walk poses (<see cref="AvatarForge.FromCreation"/>) baked with the document's
    /// <c>bakeStyle</c>, then written + boot-verified (<see cref="ForgeAvatar"/>). The LOSSLESS in-game disk bake — the
    /// <c>forge</c> verb's rich twin.</summary>
    /// <param name="device">The GPU device (the live overworld's, or a one-shot host's).</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="document">The full creation document (the live scene's <c>ToDocument()</c>).</param>
    /// <param name="outputPath">Where to write the <c>.gbc</c> (and its <c>.sheet.png</c>/<c>.bake.bin</c>/<c>.emulated.png</c>).</param>
    /// <param name="title">The cartridge header title.</param>
    internal static void ForgeAvatarFromCreation(IGpuDeviceContext device, IGpuComputeServices gpu, CreationDocument document, string outputPath, string title) {
        ArgumentNullException.ThrowIfNull(document);

        var avatar = AvatarForge.FromCreation(document: document, framePoses: out var framePoses);
        var style = BakeStyles.Resolve(diagnostic: out _, name: document.BakeStyle);

        ForgeAvatar(avatar: avatar, device: device, framePoses: framePoses, gpu: gpu, outputPath: outputPath, style: style, title: title);
    }

    // The three committed flagship documents, in the order the report enumerates them. Each character's recipe
    // migrates from the original shared FlagshipCreations into its own per-character class as its quality
    // workstream lands (the fish first); the shared entries retire one by one.
    private static readonly (string Name, Func<CreationDocument> Build)[] FlagshipRecipes = [
        ("lantern-fish", FlagshipLanternFish.BuildLanternFish),
        ("crt-robot", FlagshipCrtRobot.BuildCrtRobot),
        ("adventurer", FlagshipAdventurer.BuildAdventurer),
    ];

    /// <summary>The <c>--forge-flagships</c> tool mode: Arc 3's three flagship avatars, verified as CONTENT. For each
    /// of <see cref="FlagshipCreations"/>'s recipes this regenerates the document and asserts it is BYTE-IDENTICAL to
    /// the committed <c>docs/examples/creations/&lt;name&gt;.creation.json</c> (content determinism — the recipe is
    /// the source of truth, the committed file is its checked-in output), runs the recipe's own rig assertions (see
    /// <see cref="AssertLanternFish"/>/<see cref="AssertCrtRobot"/>/<see cref="AssertAdventurer"/>), then forges the
    /// ADVENTURER through the same <see cref="ForgeAvatarRom"/> path <c>--forge-avatar-from</c> uses — proving the
    /// bake inherits the IK'd stride end to end. Runs inside the shared GPU host (the adventurer bake needs one);
    /// the document round-trip and rig assertions run first, outside it, so a failure there is never masked by the
    /// GPU host's own try/catch.</summary>
    /// <param name="outputPath">Where to write the adventurer's forged <c>.gbc</c> (the other two flagships are
    /// documents only — no ROM).</param>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <returns>0 on success.</returns>
    public static Task<int> RunFlagshipsAsync(string outputPath, string[] args) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var documents = new Dictionary<string, CreationDocument>(capacity: FlagshipRecipes.Length);

        foreach (var (name, build) in FlagshipRecipes) {
            var document = build();

            VerifyByteIdenticalRegeneration(document: document, name: name);
            documents[name] = document;

            Console.WriteLine(value: $"flagship forge | {name} | {document.Shapes?.Count ?? 0} shape(s), {document.Chains?.Count ?? 0} chain(s), {document.Frames?.Count ?? 0} frame(s) | regenerated byte-identical to docs/examples/creations/{name}.creation.json");
        }

        AssertLanternFish(document: documents["lantern-fish"]);
        AssertCrtRobot(document: documents["crt-robot"]);
        AssertAdventurer(document: documents["adventurer"]);

        Console.WriteLine(value: "flagship forge | lantern-fish | spine solves clean (no NaN) and visibly curves across its 4 swim frames");
        Console.WriteLine(value: "flagship forge | crt-robot | 4 limb chains all valid + a shape named 'face' exists");
        Console.WriteLine(value: "flagship forge | adventurer | frames 1-2 exist and differ (the walk-pair stride)");

        return ForgeHost.RunAsync(args: args, work: (device, gpu) => {
            var adventurer = documents["adventurer"];
            var avatar = AvatarForge.FromCreation(document: adventurer, framePoses: out var framePoses);

            if (framePoses is null) {
                throw new InvalidOperationException(message: "flagship forge: adventurer AvatarForge.FromCreation produced no frame poses — the IK'd stride did not reach the bake.");
            }

            ForgeAvatar(avatar: avatar, device: device, framePoses: framePoses, gpu: gpu, outputPath: outputPath, style: BakeStyles.Resolve(diagnostic: out _, name: adventurer.BakeStyle), title: "ADVNTURER");

            Console.WriteLine(value: $"flagship forge | adventurer | bake inherits the IK'd stride | {framePoses.Count} pose slot(s) fed to the sprite sheet bake");

            return 0;
        });
    }

    // Content determinism: the recipe regenerates the SAME serialized bytes CreationStore.Save would have written —
    // proven by re-running the recipe against the checked-in file's exact bytes (the store's own JsonOptions, via
    // ToJson), never a semantic/structural comparison that could paper over an accidental drift.
    private static void VerifyByteIdenticalRegeneration(CreationDocument document, string name) {
        var path = Path.Combine(path1: "docs", path2: "examples", path3: "creations", path4: $"{name}.creation.json");
        var regenerated = CreationStore.ToJson(document: document);

        // The add-a-field ritual for creations: a schema evolution (a new nullable document member) legitimately
        // changes every recipe's canonical bytes — PUCK_FLAGSHIPS_REGENERATE=1 rewrites the committed outputs from
        // the recipes (narrated, then committed by a human/orchestrator), while the default path stays the loud
        // byte-identical assertion.
        if (Environment.GetEnvironmentVariable(variable: "PUCK_FLAGSHIPS_REGENERATE") == "1") {
            File.WriteAllText(contents: regenerated, path: path);
            Console.WriteLine(value: $"flagship forge | {name} | committed document REGENERATED from the recipe ({path})");

            return;
        }

        if (!File.Exists(path: path)) {
            throw new InvalidOperationException(message: $"flagship forge: no committed document at '{path}' to compare '{name}' against.");
        }

        var committed = File.ReadAllText(path: path);

        if (!string.Equals(a: committed, b: regenerated, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidOperationException(message: $"flagship forge: '{name}' regenerated DIFFERENT bytes than the committed '{path}' — the recipe drifted from its checked-in content (a deliberate schema evolution regenerates via PUCK_FLAGSHIPS_REGENERATE=1).");
        }
    }

    // The fish's rig proof: the spine chain solves to finite (non-NaN, non-infinite) joint positions on every
    // recorded frame, and at least one joint's position actually DIFFERS across frames (the swim sweep visibly
    // curves the body rather than freezing it) — the two things a spine-chain flagship must demonstrate.
    private static void AssertLanternFish(CreationDocument document) {
        var chains = (document.Chains ?? throw new InvalidOperationException(message: "flagship forge: lantern-fish declared no chains."));
        var spine = chains.FirstOrDefault(predicate: static chain => string.Equals(a: chain.Name, b: "spine", comparisonType: StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(message: "flagship forge: lantern-fish declared no 'spine' chain.");

        if (!string.Equals(a: spine.Kind, b: CreatorChainState.KindSpine, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidOperationException(message: $"flagship forge: lantern-fish 'spine' chain is kind '{spine.Kind}', expected 'spine'.");
        }

        var frames = (document.Frames ?? throw new InvalidOperationException(message: "flagship forge: lantern-fish recorded no swim frames."));

        if (frames.Count < 2) {
            throw new InvalidOperationException(message: $"flagship forge: lantern-fish recorded {frames.Count} frame(s), need at least 2 to show a swim cycle.");
        }

        var spineShapeIds = new HashSet<int>(spine.Shapes);
        var firstFramePositions = new Dictionary<int, Vector3>();
        var anyDifference = false;

        foreach (var frame in frames) {
            foreach (var transform in frame.Transforms) {
                if (!spineShapeIds.Contains(item: transform.Id)) {
                    continue;
                }

                if (!float.IsFinite(f: transform.Position.X) || !float.IsFinite(f: transform.Position.Y) || !float.IsFinite(f: transform.Position.Z)) {
                    throw new InvalidOperationException(message: $"flagship forge: lantern-fish spine shape {transform.Id} has a non-finite position in frame '{frame.Name}'.");
                }

                if (firstFramePositions.TryGetValue(key: transform.Id, value: out var earlier)) {
                    anyDifference |= (Vector3.DistanceSquared(value1: earlier, value2: transform.Position) > 1e-8f);
                } else {
                    firstFramePositions[transform.Id] = transform.Position;
                }
            }
        }

        if (!anyDifference) {
            throw new InvalidOperationException(message: "flagship forge: lantern-fish spine joints did not move across the recorded swim frames (the sweep is degenerate).");
        }
    }

    // The robot's rig proof: exactly the 4 named limb chains, each structurally "limb"-valid (3 shapes/2 bones), plus
    // a shape named "face" for the host's screen-slab ledger to find.
    private static void AssertCrtRobot(CreationDocument document) {
        var chains = (document.Chains ?? throw new InvalidOperationException(message: "flagship forge: crt-robot declared no chains."));
        string[] limbNames = ["armLeft", "armRight", "legLeft", "legRight"];

        foreach (var limbName in limbNames) {
            var chain = chains.FirstOrDefault(predicate: chain => string.Equals(a: chain.Name, b: limbName, comparisonType: StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(message: $"flagship forge: crt-robot declared no '{limbName}' chain.");

            if (!string.Equals(a: chain.Kind, b: CreatorChainState.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) || (chain.Shapes.Count != 3)) {
                throw new InvalidOperationException(message: $"flagship forge: crt-robot '{limbName}' is not a valid limb (kind '{chain.Kind}', {chain.Shapes.Count} shape(s)).");
            }
        }

        var shapes = (document.Shapes ?? throw new InvalidOperationException(message: "flagship forge: crt-robot declared no shapes."));

        if (!shapes.Any(predicate: static shape => string.Equals(a: shape.Name, b: "face", comparisonType: StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidOperationException(message: "flagship forge: crt-robot declared no shape named 'face'.");
        }
    }

    // The adventurer's rig proof: frames 1-2 exist (the walk-pair convention) and differ from each other (a real
    // stride, not a frozen pose), and AvatarForge.FromCreation lifts non-null frame poses from the document — the
    // seam the bake actually consumes.
    private static void AssertAdventurer(CreationDocument document) {
        var frames = (document.Frames ?? throw new InvalidOperationException(message: "flagship forge: adventurer recorded no walk frames."));

        if (frames.Count < 2) {
            throw new InvalidOperationException(message: $"flagship forge: adventurer recorded {frames.Count} frame(s), need the walk-pair (frames 1-2).");
        }

        var frame1 = frames[0];
        var frame2 = frames[1];
        var anyDifference = false;

        foreach (var transform in frame1.Transforms) {
            var counterpart = frame2.Transforms.FirstOrDefault(predicate: entry => (entry.Id == transform.Id));

            if ((counterpart is not null) && (Vector3.DistanceSquared(value1: transform.Position, value2: counterpart.Position) > 1e-8f)) {
                anyDifference = true;

                break;
            }
        }

        if (!anyDifference) {
            throw new InvalidOperationException(message: "flagship forge: adventurer's walk-pair frames 1-2 are identical (no stride).");
        }

        var avatar = AvatarForge.FromCreation(document: document, framePoses: out var framePoses);

        if (avatar.Shapes.Count == 0) {
            throw new InvalidOperationException(message: "flagship forge: adventurer AvatarForge.FromCreation produced an empty avatar.");
        }

        if (framePoses is null) {
            throw new InvalidOperationException(message: "flagship forge: adventurer AvatarForge.FromCreation produced null frame poses (the IK'd stride would not reach the bake).");
        }
    }

    // The bake blob beside the cartridge: the bundle's PBAK wire form, chunk list narrated so a forge log shows what
    // an external assembler would receive — then proved consumable by the framework's own reader/linker.
    private static void WriteBakeBlob(BakedAssetBundle bundle, string outputPath) {
        var path = Path.ChangeExtension(path: outputPath, extension: ".bake.bin");
        var blob = bundle.ToBlob(chunks: out var chunks);

        File.WriteAllBytes(bytes: blob, path: path);
        Console.Error.WriteLine(value: $"avatar bake | wrote {path} ({blob.Length} bytes) | chunks: {string.Join(separator: ", ", values: chunks.Select(selector: static chunk => $"{chunk.FourCc}[{chunk.ByteLength}]"))}");
        VerifyBlobLinks(blob: blob, bundle: bundle);
    }

    // The wire-form round trip: parse the blob back through the framework's PBAK reader and LINK every section into a
    // scratch data window, throwing if the reader disagrees with the writer (section shape, frame counts) or any
    // relocation lands out of bounds — so the .bake.bin beside the cartridge is PROVEN consumable, not just written.
    private static void VerifyBlobLinks(byte[] blob, BakedAssetBundle bundle) {
        var parsed = Framework.PbakBundle.Parse(blob: blob);

        if ((parsed.Background is not null) != (bundle.Background is not null)) {
            throw new InvalidOperationException(message: "PBAK round-trip failed: the parsed background presence disagrees with the bundle.");
        }

        if (parsed.Sprites.Count != bundle.Sprites.Count) {
            throw new InvalidOperationException(message: $"PBAK round-trip failed: {parsed.Sprites.Count} parsed sprite sets vs {bundle.Sprites.Count} baked.");
        }

        var emitter = new Sm83Emitter();
        var bg = new Framework.BgModule(emitter: emitter);
        var linker = new Framework.AssetLinker(data: new Framework.RomDataBuilder(text: new Framework.TextModule(emitter: emitter, bg: bg, fontTileBase: 0)));

        if (parsed.Background is { } background) {
            _ = linker.LinkBackground(name: "background", background: background);
        }

        var frameTotal = 0;

        for (var index = 0; (index < parsed.Sprites.Count); index++) {
            var linked = linker.LinkSpriteSet(name: $"sprites-{index}", sprites: parsed.Sprites[index]);

            if (linked.FrameEntryCounts.Count != bundle.Sprites[index].Frames.Count) {
                throw new InvalidOperationException(message: $"PBAK round-trip failed: sprite set {index} linked {linked.FrameEntryCounts.Count} frames vs {bundle.Sprites[index].Frames.Count} baked.");
            }

            frameTotal += linked.FrameEntryCounts.Count;
        }

        Console.Error.WriteLine(value: $"avatar bake | PBAK round-trip linked | {linker.TileCount} tiles | {parsed.Sprites.Count} sprite sets | {frameTotal} frames");
    }

    private static byte[] ForgeAvatarRom(IGpuDeviceContext device, IGpuComputeServices gpu, AvatarDefinition avatar, string title, IReadOnlyList<IReadOnlyList<AvatarShape>>? framePoses, BakeStyle? style, out AvatarForge.AvatarSheet sheet, out BakedAssetBundle bundle, out int roomTileCount, MovementMode movementMode = MovementMode.FourWay) {
        ArgumentNullException.ThrowIfNull(avatar);

        var room = SceneForge.ForgeRoom(device: device, gpu: gpu);

        sheet = AvatarForge.Forge(avatar: avatar, bundle: out bundle, device: device, framePoses: framePoses, gpu: gpu, style: style);
        roomTileCount = room.TileCount;

        if ((roomTileCount + sheet.TileCount) > MaxTiles) {
            throw new InvalidOperationException(message: $"The forged overworld needs {roomTileCount + sheet.TileCount} tiles ({roomTileCount} room + {sheet.TileCount} avatar), over the {MaxTiles}-tile VRAM budget.");
        }

        var tileData = new byte[room.TileData.Length + sheet.SpriteTiles.Length];

        room.TileData.CopyTo(array: tileData, index: 0);
        sheet.SpriteTiles.CopyTo(array: tileData, index: room.TileData.Length);

        return HgbCartridge.BuildOverworld(
            title: title,
            backgroundPalette: HgbImage.EncodePalette(palette: room.Palette),
            objectPalette: sheet.ObjectPalette,
            spriteTileBase: roomTileCount,
            tileData: tileData,
            tileMap: room.TileMap,
            movementMode: movementMode
        );
    }

    // A built-in demo avatar (a little standing figure) so --forge-avatar works with no input file and the Post battery
    // has a deterministic subject. Uses only the creator's own primitive set, in the creator's local frame.
    private static AvatarDefinition BuildDemoAvatar() {
        var shapes = new List<AvatarShape> {
            new(Type: AvatarPrimitive.Ellipsoid, Position: new Vector3(0f, 0.62f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(1.05f, 1.25f, 0.85f)),
            new(Type: AvatarPrimitive.Sphere, Position: new Vector3(0f, 1.18f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(0.72f)),
            new(Type: AvatarPrimitive.Capsule, Position: new Vector3(-0.2f, 0.05f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(0.5f)),
            new(Type: AvatarPrimitive.Capsule, Position: new Vector3(0.2f, 0.05f, 0f), Rotation: Quaternion.Identity, Scale: new Vector3(0.5f)),
        };

        return AvatarDefinition.FromPlacedShapes(shapes: shapes);
    }

    /// <summary>Authors the sprite: a blobby, smooth-unioned creature — SDF's home turf, legible once crushed to 16×16.</summary>
    private static SdfProgram BuildCreatureScene() {
        var builder = new SdfProgramBuilder();
        var body = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.88f, 0.44f, 0.20f)));
        var belly = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.96f, 0.82f, 0.38f)));

        _ = builder.ResetPoint().Sphere(radius: 1.0f, material: body);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0.95f, 0f)).Sphere(radius: 0.66f, material: body, blend: SdfBlendOp.SmoothUnion, smooth: 0.55f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(-0.55f, -0.8f, 0.25f)).Sphere(radius: 0.34f, material: body, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0.55f, -0.8f, 0.25f)).Sphere(radius: 0.34f, material: body, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0.05f, 0.8f)).Sphere(radius: 0.42f, material: belly, blend: SdfBlendOp.SmoothUnion, smooth: 0.25f);

        return builder.Build();
    }

    private static void Forge(IGpuDeviceContext device, IGpuComputeServices gpu, string outputPath) {
        var rom = ForgeSceneCart(device: device, gpu: gpu, program: BuildCreatureScene(), title: "PUCKFORGE", room: out var room, creature: out var creature);

        WriteRom(outputPath: outputPath, rom: rom);
        WritePreview(outputPath: outputPath, backgroundPalette: room.Palette, backgroundIndices: room.Indices, objectPalette: creature.Palette, creatureIndices: creature.Indices);
        VerifyBoot(rom: rom, outputPath: outputPath);

        Console.WriteLine(value: $"forge | wrote {outputPath} ({rom.Length} bytes) | {room.TileCount} BG tiles + {creature.TileCount} OBJ tiles | boot it with: --rom {outputPath}");
    }

    // The shared SDF-art SCENE cart: a forged room with an SDF program crushed to a centred 16x16 creature metasprite
    // over it. Both the headless --forge tool (a fixed creature) and the in-session SCENE subject (the live creator
    // document) go through here, so the two paths are one bake. The camera + budget are the creature-cart contract.
    private static byte[] ForgeSceneCart(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program, string title, out SceneForge.RoomAssets room, out SceneForge.SpriteAssets creature) {
        ArgumentNullException.ThrowIfNull(program);

        room = SceneForge.ForgeRoom(device: device, gpu: gpu);

        var creatureCamera = CameraSnapshot.LookAt(
            position: new Vector3(0f, 0.15f, 4.4f),
            target: new Vector3(0f, 0.05f, 0f),
            fieldOfViewRadians: (42f * (MathF.PI / 180f)),
            viewportWidth: CreatureSupersample,
            viewportHeight: CreatureSupersample
        );

        creature = SceneForge.ForgeSprite(device: device, gpu: gpu, program: program, camera: creatureCamera, supersampleWidth: CreatureSupersample, supersampleHeight: CreatureSupersample, reduceFactor: CreatureReduceFactor);

        if ((room.TileCount + creature.TileCount) > MaxTiles) {
            throw new InvalidOperationException(message: $"The forged scene needs {room.TileCount + creature.TileCount} tiles, over the {MaxTiles}-tile VRAM budget.");
        }

        var objectTileBase = room.TileCount;
        var tileData = new byte[room.TileData.Length + creature.TileData.Length];

        room.TileData.CopyTo(array: tileData, index: 0);
        creature.TileData.CopyTo(array: tileData, index: room.TileData.Length);

        return HgbCartridge.Build(
            title: title,
            backgroundPalette: HgbImage.EncodePalette(palette: room.Palette),
            objectPalette: HgbImage.EncodePalette(palette: creature.Palette),
            tileData: tileData,
            tileMap: room.TileMap,
            objectAttributes: BuildCreatureOam(objectTileBase: objectTileBase)
        );
    }

    /// <summary>Forges a creation document into an SDF-ART SCENE cart: the creation's rest pose is baked (through the
    /// SAME <see cref="AvatarForge.FromCreation"/> lift the avatar walker uses) into a centred 16×16 creature metasprite
    /// over the forged room — the sculpt-a-creature-and-see-it half of the in-session forge, DISTINCT from the walker
    /// avatar cart the same document also forges. Needs the live GPU device (the SDF rasterize). The in-session
    /// <c>forge scene</c> path calls this; it is the creature-cart twin of <see cref="ForgeAvatarRomFromCreation"/>.</summary>
    /// <param name="device">The GPU device (the live overworld's, or a one-shot host's).</param>
    /// <param name="gpu">The compute services.</param>
    /// <param name="document">The full creation document (the live scene's <c>ToDocument()</c>).</param>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>A genuine 32 KiB SDF-art scene ROM.</returns>
    internal static byte[] ForgeSceneRomFromCreation(IGpuDeviceContext device, IGpuComputeServices gpu, CreationDocument document, string title) {
        ArgumentNullException.ThrowIfNull(document);

        var avatar = AvatarForge.FromCreation(document: document, framePoses: out _);

        return ForgeSceneCart(device: device, gpu: gpu, program: avatar.BuildProgram(), title: title, room: out _, creature: out _);
    }

    // Four 8x8 sprites forming the 16x16 metasprite, centred on the 160x144 screen.
    private static byte[] BuildCreatureOam(int objectTileBase) {
        const int screenLeft = ((SceneForge.ScreenWidth - CreatureSize) / 2);
        const int screenTop = ((SceneForge.ScreenHeight - CreatureSize) / 2);

        (int Dx, int Dy, int Tile)[] sprites = [
            (0, 0, objectTileBase + 0),
            (8, 0, objectTileBase + 1),
            (0, 8, objectTileBase + 2),
            (8, 8, objectTileBase + 3),
        ];

        var oam = new byte[sprites.Length * 4];

        for (var index = 0; (index < sprites.Length); index++) {
            oam[(index * 4) + 0] = (byte)(screenTop + sprites[index].Dy + 16);
            oam[(index * 4) + 1] = (byte)(screenLeft + sprites[index].Dx + 8);
            oam[(index * 4) + 2] = (byte)sprites[index].Tile;
            oam[(index * 4) + 3] = 0x00;
        }

        return oam;
    }

    internal static void WriteRom(string outputPath, byte[] rom) {
        var directory = Path.GetDirectoryName(path: Path.GetFullPath(path: outputPath));

        if (!string.IsNullOrEmpty(value: directory)) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllBytes(path: outputPath, bytes: rom);
    }

    /// <summary>Resolves (and creates the directory for) a cartridge's default battery-save path, following the
    /// demo's local-state convention (the bindings profile store's <c>%LOCALAPPDATA%\Puck\Demo</c>). Shared by every
    /// framework game facade; each passes its own <c>.sav</c> filename.</summary>
    /// <param name="saveFileName">The save file's name (e.g. <c>"brickfall.sav"</c>).</param>
    /// <returns>The save-file path.</returns>
    internal static string PrepareDefaultSavePath(string saveFileName) {
        var directory = Path.Combine(Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData), "Puck", "Demo");

        _ = Directory.CreateDirectory(path: directory);

        return Path.Combine(directory, saveFileName);
    }

    // A 160x144 RGBA preview of what the ROM should display: the quantized background with the creature composited.
    private static void WritePreview(string outputPath, HgbImage.Rgb[] backgroundPalette, byte[] backgroundIndices, HgbImage.Rgb[] objectPalette, byte[] creatureIndices) {
        var preview = new byte[SceneForge.ScreenWidth * SceneForge.ScreenHeight * 4];

        for (var pixel = 0; (pixel < backgroundIndices.Length); pixel++) {
            var colour = backgroundPalette[backgroundIndices[pixel]];
            var offset = (pixel * 4);

            preview[offset] = colour.R;
            preview[offset + 1] = colour.G;
            preview[offset + 2] = colour.B;
            preview[offset + 3] = 0xFF;
        }

        const int screenLeft = ((SceneForge.ScreenWidth - CreatureSize) / 2);
        const int screenTop = ((SceneForge.ScreenHeight - CreatureSize) / 2);

        for (var y = 0; (y < CreatureSize); y++) {
            for (var x = 0; (x < CreatureSize); x++) {
                var index = creatureIndices[(y * CreatureSize) + x];

                if (index == 0) {
                    continue;
                }

                var colour = objectPalette[index];
                var offset = ((((screenTop + y) * SceneForge.ScreenWidth) + (screenLeft + x)) * 4);

                preview[offset] = colour.R;
                preview[offset + 1] = colour.G;
                preview[offset + 2] = colour.B;
                preview[offset + 3] = 0xFF;
            }
        }

        PngEncoder.Write(height: SceneForge.ScreenHeight, path: Path.ChangeExtension(path: outputPath, extension: ".preview.png"), rgba: preview, width: SceneForge.ScreenWidth);
    }

    // The overworld ROM's BEHAVIOUR proof: boot the forged cartridge on a real Humble CGB machine and drive its joypad,
    // reading the routine's work-RAM state back each frame (the ROM writes its player position, facing, and computed
    // pose tile every frame — see OverworldProtocol). Asserts the four things the walk routine must do: the d-pad moves
    // the sprite and sets its facing; the position clamps to the reachable window instead of wrapping; the walk
    // animation cycles the pose tile while moving; and it stays still when idle. MODE-AWARE on the vertical axis:
    // a HEX walker's pure Up/Down is a deliberate NO-OP (a pointy-top hex has no vertical neighbor), so hex proves
    // vertical movement through the Up+Left diagonal instead. Throws (failing the forge) on any violation — this is
    // the forge's honest "verify by running" gate for the SM83 walker.
    private static void VerifyOverworld(byte[] rom, MovementMode movementMode = MovementMode.FourWay) {
        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        var bus = machine.GetRequiredService<ISystemBus>();
        var cpu = machine.GetRequiredService<ICpu>();
        var joypad = machine.GetRequiredService<IJoypad>();

        byte Read(ushort address) => bus.ReadByte(address: address);

        void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                joypad.SetButtons(pressed: buttons);
                machine.Machine.Run(tCycles: 70224UL);
            }

            VerifyMachineSettle.SettleOutOfOamDma(machine: machine.Machine, cpu: cpu, label: "overworld");
        }

        void Assert(bool condition, string message) {
            if (!condition) {
                throw new InvalidOperationException(message: $"overworld ROM verification failed: {message}");
            }
        }

        // Settle past boot setup, then confirm the seeded spawn state.
        RunFrames(buttons: JoypadButtons.None, frames: 16);
        var startX = Read(OverworldProtocol.PlayerXAddress);
        var startY = Read(OverworldProtocol.PlayerYAddress);

        Assert(condition: (startX == OverworldProtocol.StartX), message: $"spawn X {startX} != {OverworldProtocol.StartX}");
        Assert(condition: (startY == OverworldProtocol.StartY), message: $"spawn Y {startY} != {OverworldProtocol.StartY}");
        Assert(condition: (Read(OverworldProtocol.FacingAddress) == OverworldProtocol.FacingDown), message: "spawn facing is not down");

        // Idle is static: the pose tile and position hold while no direction is held.
        var idleTile = Read(OverworldProtocol.TileScratchAddress);

        RunFrames(buttons: JoypadButtons.None, frames: 24);
        Assert(condition: (Read(OverworldProtocol.TileScratchAddress) == idleTile), message: "idle pose tile changed with no input (spurious animation)");
        Assert(condition: (Read(OverworldProtocol.PlayerXAddress) == startX), message: "idle position drifted with no input");
        Assert(condition: (Read(OverworldProtocol.MovingAddress) == 0), message: "moving flag set with no input");

        // Each direction moves the sprite the right way AND sets its facing.
        RunFrames(buttons: JoypadButtons.Right, frames: 16);
        var afterRight = Read(OverworldProtocol.PlayerXAddress);
        Assert(condition: (afterRight > startX), message: $"Right did not increase X ({startX} → {afterRight})");
        Assert(condition: (Read(OverworldProtocol.FacingAddress) == OverworldProtocol.FacingRight), message: "Right did not set facing right");

        RunFrames(buttons: JoypadButtons.Left, frames: 16);
        Assert(condition: (Read(OverworldProtocol.PlayerXAddress) < afterRight), message: "Left did not decrease X");
        Assert(condition: (Read(OverworldProtocol.FacingAddress) == OverworldProtocol.FacingLeft), message: "Left did not set facing left");

        var beforeUp = Read(OverworldProtocol.PlayerYAddress);

        if (movementMode == MovementMode.Hex) {
            // Pure vertical is a NO-OP in hex (no vertical neighbor); the 60° diagonals carry the vertical axis.
            RunFrames(buttons: JoypadButtons.Up, frames: 16);
            Assert(condition: (Read(OverworldProtocol.PlayerYAddress) == beforeUp), message: $"hex: Up alone moved Y ({beforeUp} → {Read(OverworldProtocol.PlayerYAddress)})");
            RunFrames(buttons: JoypadButtons.Down, frames: 16);
            Assert(condition: (Read(OverworldProtocol.PlayerYAddress) == beforeUp), message: "hex: Down alone moved Y");

            var beforeDiagonalX = Read(OverworldProtocol.PlayerXAddress);

            RunFrames(buttons: (JoypadButtons.Up | JoypadButtons.Left), frames: 16);
            Assert(condition: (Read(OverworldProtocol.PlayerYAddress) < beforeUp), message: "hex: Up+Left did not decrease Y (the NW neighbor)");
            Assert(condition: (Read(OverworldProtocol.PlayerXAddress) < beforeDiagonalX), message: "hex: Up+Left did not decrease X");
            // Hex diagonals step (±1, ±2): the vertical magnitude dominates, so the |dx| ≥ |dy| facing rule
            // resolves the diagonal to the VERTICAL pose (the sprite walks "up" while drifting west — correct
            // for a 4-facing sheet).
            Assert(condition: (Read(OverworldProtocol.FacingAddress) == OverworldProtocol.FacingUp), message: "hex: Up+Left did not face up (vertical magnitude dominates the 1:2 step)");

            var beforeReturnY = Read(OverworldProtocol.PlayerYAddress);

            RunFrames(buttons: (JoypadButtons.Down | JoypadButtons.Right), frames: 16);
            Assert(condition: (Read(OverworldProtocol.PlayerYAddress) > beforeReturnY), message: "hex: Down+Right did not increase Y (the SE neighbor)");
        }
        else {
            RunFrames(buttons: JoypadButtons.Up, frames: 16);
            var afterUp = Read(OverworldProtocol.PlayerYAddress);
            Assert(condition: (afterUp < beforeUp), message: $"Up did not decrease Y ({beforeUp} → {afterUp})");
            Assert(condition: (Read(OverworldProtocol.FacingAddress) == OverworldProtocol.FacingUp), message: "Up did not set facing up");

            RunFrames(buttons: JoypadButtons.Down, frames: 16);
            Assert(condition: (Read(OverworldProtocol.PlayerYAddress) > afterUp), message: "Down did not increase Y");
            Assert(condition: (Read(OverworldProtocol.FacingAddress) == OverworldProtocol.FacingDown), message: "Down did not set facing down");
        }

        // Boundary clamp: hold Right well past the edge — X must PIN at the max, never wrap the byte past it.
        RunFrames(buttons: JoypadButtons.Right, frames: 220);
        var clampedX = Read(OverworldProtocol.PlayerXAddress);
        Assert(condition: (clampedX == OverworldProtocol.MaxX), message: $"X did not clamp to {OverworldProtocol.MaxX} (got {clampedX} — wrapped or overran)");

        // Walk animation: at the clamp the position is pinned, so a changing pose tile isolates the animation from
        // movement — over two toggle periods it must take at least two distinct pose tiles.
        var poseTiles = new HashSet<byte>();

        for (var frame = 0; (frame < 24); frame++) {
            joypad.SetButtons(pressed: JoypadButtons.Right);
            machine.Machine.Run(tCycles: 70224UL);
            poseTiles.Add(item: Read(OverworldProtocol.TileScratchAddress));
        }

        Assert(condition: (poseTiles.Count >= 2), message: $"walk animation did not cycle the pose tile while moving (saw {poseTiles.Count} distinct tiles)");
        Assert(condition: (Read(OverworldProtocol.PlayerXAddress) == OverworldProtocol.MaxX), message: "position drifted off the clamp while animating");

        Console.WriteLine(value: $"overworld verify | spawn ({startX},{startY}) | {(movementMode == MovementMode.Hex ? "hex moves (W/E pure, diagonals carry Y, vertical-alone no-op)" : "move+facing all four ways")} | clamp X→{OverworldProtocol.MaxX} | walk cycled {poseTiles.Count} pose tiles");
    }

    // Self-verification (the "two worlds meet at a file" round-trip): boot the freshly-forged .gbc on a real Humble CGB
    // machine — the SAME core the demo's --rom path uses, seeded post-boot with A = 0x11 — advance it a few frames, and
    // dump its native 160×144 framebuffer so <out>.emulated.png confirms the ROM boots and renders in colour.
    private static void VerifyBoot(byte[] rom, string outputPath) {
        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        machine.Machine.Run(tCycles: (70224UL * 60UL));

        PngEncoder.Write(height: Framebuffer.ScreenHeight, path: Path.ChangeExtension(path: outputPath, extension: ".emulated.png"), rgba: FramebufferToRgba(machine: machine), width: Framebuffer.ScreenWidth);
    }

    /// <summary>The second boot proof beside a card game's title <c>.emulated.png</c>: boots a real machine, drives
    /// it through a caller-supplied button sequence to reach and settle a dealt table/board, and dumps the
    /// framebuffer. Shared by <see cref="PokerRom.WritePlayProof"/> and <see cref="SolitaireRom.WritePlayProof"/>,
    /// each of which passes its own settle timing (Poker settles 150 frames after START, Solitaire 120).</summary>
    /// <param name="rom">The ROM image.</param>
    /// <param name="path">Where to write the PNG.</param>
    /// <param name="sequence">The button/frame-count steps to run, in order, before capturing the framebuffer.</param>
    internal static void WriteCardGamePlayProof(byte[] rom, string path, params ReadOnlySpan<(JoypadButtons Buttons, int Frames)> sequence) {
        ArgumentNullException.ThrowIfNull(rom);

        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        var joypad = machine.GetRequiredService<IJoypad>();

        foreach (var (buttons, frames) in sequence) {
            for (var frame = 0; (frame < frames); frame++) {
                joypad.SetButtons(pressed: buttons);
                machine.Machine.Run(tCycles: 70224UL);
            }
        }

        PngEncoder.Write(height: Framebuffer.ScreenHeight, path: path, rgba: FramebufferToRgba(machine: machine), width: Framebuffer.ScreenWidth);
    }

    /// <summary>Packs a machine's native ARGB framebuffer into the RGBA8 bytes the PNG encoder / strip builder expects.</summary>
    internal static byte[] FramebufferToRgba(MachineInstance machine) {
        var pixels = machine.GetRequiredService<IFramebuffer>().Pixels;
        var rgba = new byte[Framebuffer.ScreenWidth * Framebuffer.ScreenHeight * 4];

        for (var index = 0; (index < pixels.Length); index++) {
            var pixel = pixels[index];
            var offset = (index * 4);

            rgba[offset] = (byte)(pixel >> 16);
            rgba[offset + 1] = (byte)(pixel >> 8);
            rgba[offset + 2] = (byte)pixel;
            rgba[offset + 3] = 0xFF;
        }

        return rgba;
    }
}
