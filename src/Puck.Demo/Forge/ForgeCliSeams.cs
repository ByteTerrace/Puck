using System.CommandLine;

namespace Puck.Demo.Forge;

/// <summary>
/// The forge tool-mode CLI surface, housed OUTSIDE <c>Program</c>: <c>Main</c> sits at both its class-coupling
/// (CA1506) and maintainability-index (CA1505) ceilings, so every forge option declares here and the whole
/// dispatch chain runs through one nullable call — <c>Program</c> pays one property reference per option in its
/// root-command list and a single await, and never names <see cref="RomForge"/> or a forge enum at all.
/// </summary>
internal static class ForgeCliSeams {
    /// <summary>The <c>--forge</c> option (the original SDF-art forge).</summary>
    public static Option<string?> ForgeOption { get; } = new(name: "--forge") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: FORGE a Humble GamingBrick ROM from SDF-authored art — render a mini overworld scene, crush it to GBC tiles + CGB palettes, and write a real .gbc (plus a preview PNG) to the given path, then exit. Boot the result with --rom.",
    };

    /// <summary>The <c>--forge-camera</c> option.</summary>
    public static Option<string?> CameraOption { get; } = new(name: "--forge-camera") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: forge a POCKET CAMERA .gbc (a real ROM that drives the authentic M64282FP protocol — program registers, trigger, poll busy, blit the captured image) and self-verify it against the deterministic gradient sensor, writing an <out>.emulated.png. Boot it with --rom to run the webcam viewfinder.",
    };

    /// <summary>The <c>--forge-avatar</c> option.</summary>
    public static Option<string?> AvatarOption { get; } = new(name: "--forge-avatar") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: forge a playable OVERWORLD .gbc from a player's avatar — a forged room the avatar's own sprite sheet (4 facings × a walk cycle, snapshotted from the 3D SDF creation) walks around, in the classic top-down RPG style. With --forge-avatar-from, bakes a saved avatar JSON; without it, a built-in demo avatar. Boot the result with --rom.",
    };

    /// <summary>The <c>--forge-avatar-from</c> option.</summary>
    public static Option<string?> AvatarFromOption { get; } = new(name: "--forge-avatar-from") {
        DefaultValueFactory = static _ => null,
        Description = "Path to an avatar JSON (as saved from creator mode) to bake with --forge-avatar. Omit to forge the built-in demo avatar.",
    };

    /// <summary>The <c>--forge-avatar-movement-mode</c> option (rides <c>--forge-avatar</c>).</summary>
    public static Option<string?> AvatarMovementModeOption { get; } = new(name: "--forge-avatar-movement-mode") {
        DefaultValueFactory = static _ => null,
        Description = "With --forge-avatar: the walker's d-pad direction lock — four (default, the classic brick walker), eight (diagonals move both axes), or hex (pointy-top: W/E pure, four 60-degree diagonals, vertical-alone is a no-op).",
    };

    /// <summary>The <c>--forge-flagships</c> option.</summary>
    public static Option<string?> FlagshipsOption { get; } = new(name: "--forge-flagships") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: regenerate the three flagship avatars (lantern-fish, crt-robot, adventurer) from their recipes, assert byte-identical content determinism against docs/examples/creations/*.creation.json plus per-flagship rig assertions, then forge the adventurer through the avatar-forge path to the given .gbc path (proving the bake inherits the IK'd stride).",
    };

    /// <summary>The <c>--forge-volley</c> option.</summary>
    public static Option<string?> VolleyOption { get; } = new(name: "--forge-volley") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: build the five-star VOLLEY .gbc (genuine SM83 on the game framework — player vs. a tracking AI, title/attract/pause/battery scores; the title SDF-bakes on the GPU with a hand-authored fallback), self-verify it on a real machine, and write it (plus an <out>.emulated.png). Boot it with --rom, or cycle to it at a cabinet in the overworld.",
    };

    /// <summary>The <c>--forge-brickfall</c> option.</summary>
    public static Option<string?> BrickfallOption { get; } = new(name: "--forge-brickfall") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: build the five-star BRICKFALL .gbc (genuine SM83, 7-piece falling blocks with rotation + line clears; the title SDF-bakes on the GPU with a hand-authored fallback), self-verify it, and write it (plus an <out>.emulated.png). Boot it with --rom, or cycle to it at a cabinet.",
    };

    /// <summary>The <c>--forge-chroma</c> option.</summary>
    public static Option<string?> ChromaOption { get; } = new(name: "--forge-chroma") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: build the five-star CHROMA .gbc (a column-drop colour-match game with title/attract/pause/battery scores; the title SDF-bakes on the GPU with a hand-authored fallback), self-verify it, and write it (plus an <out>.emulated.png). Boot it with --rom, or cycle to it at a cabinet.",
    };

    /// <summary>The <c>--forge-solitaire</c> option.</summary>
    public static Option<string?> SolitaireOption { get; } = new(name: "--forge-solitaire") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: build the SOLITAIRE .gbc (framework Klondike on the shared card layer — deterministic deal-from-seed, undo, battery streaks/scores, SDF-baked title/felt/cursor with hand-authored fallbacks), self-verify it, and write it (plus an <out>.emulated.png). Boot it with --rom.",
    };

    /// <summary>The <c>--forge-poker</c> option.</summary>
    public static Option<string?> PokerOption { get; } = new(name: "--forge-poker") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: build the POKER .gbc (framework five-card draw on the shared card layer — a four-seat table with three data-table AI personalities, deterministic deal-from-seed, fixed-limit betting, showdown evaluation, battery bankrolls/records, SDF-baked title/felt/cursor with hand-authored fallbacks), self-verify it, and write it (plus an <out>.emulated.png and a dealt-table <out>.play.png). Boot it with --rom.",
    };

    /// <summary>The <c>--forge-tune</c> option.</summary>
    public static Option<string?> TuneOption { get; } = new(name: "--forge-tune") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: build the minimal framework JUKEBOX .gbc from an authored puck.audio.v1 document (--forge-tune-from selects it; omit for docs/examples/tunes/tune.audio.json) — the ENTIRE music loop comes from AudioDocumentCompiler, never a hand array. Boots straight into the loop; START toggles play/stop. Self-verifies (state graph + audio WAV-spread proof) and writes it (plus an <out>.emulated.png and <out>.audio.wav). Boot it with --rom.",
    };

    /// <summary>The <c>--forge-tune-from</c> option.</summary>
    public static Option<string?> TuneFromOption { get; } = new(name: "--forge-tune-from") {
        DefaultValueFactory = static _ => null,
        Description = "Path to a puck.audio.v1 document (JSON) to compile with --forge-tune. Omit to use docs/examples/tunes/tune.audio.json.",
    };

    /// <summary>The <c>--forge-bake</c> option.</summary>
    public static Option<string?> BakeOption { get; } = new(name: "--forge-bake") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: run the SDF→brick BAKE pipeline over two subjects (the default avatar as a sprite, an authored scene as a background) in both styles (classic/bold) × both targets (dmg/cgb), writing 8 preview PNGs to the given directory plus one diagnostics line each, then exit. Deterministic: two runs write byte-identical PNGs.",
    };

    /// <summary>The <c>--forge-bake-stress</c> option.</summary>
    public static Option<string?> BakeStressOption { get; } = new(name: "--forge-bake-stress") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: bake the rainbow-striped palette-pressure scene — more distinct per-tile palettes than the 8-palette budget — proving the greedy merge path and its report-only warning, writing the preview PNGs to the given directory.",
    };

    /// <summary>The <c>--forge-bake-calibration</c> option.</summary>
    public static Option<string?> BakeCalibrationOption { get; } = new(name: "--forge-bake-calibration") {
        DefaultValueFactory = static _ => null,
        Description = "Headless tool: bake SDF stand-ins for Volley's hand-pixelled art (the paddle bar, the ball dot, the court net) at the hand art's native sizes on the DMG classic recipe, write a side-by-side comparison PNG to the given directory, and print a per-tile pixel-match report to stderr — a calibration REPORT for the sculpt→bake pipeline, never a gate (low match is a finding, not a failure).",
    };

    /// <summary>Dispatches the forge tool modes in their settled order, returning the exit code when one ran
    /// (null = no forge option was given; the caller falls through to the run modes). The avatar dispatch consumes
    /// the movement-mode token here, so <c>Main</c> never names the enum.</summary>
    /// <param name="args">The host args (backend selection etc.).</param>
    /// <param name="parseResult">The parsed command line.</param>
    /// <returns>The exit code, or null when no forge mode matched.</returns>
    public static async Task<int?> TryRunAsync(string[] args, System.CommandLine.ParseResult parseResult) {
        // The SDF-art forge builds its own trimmed GPU host and forges on the first frame; the camera/tune forges
        // need no GPU and run synchronously; the framework games bake their SDF titles on the one-shot host first.
        if (parseResult.GetValue(ForgeOption) is { } forgePath) {
            return await RomForge.RunAsync(outputPath: forgePath, args: args);
        }
        if (parseResult.GetValue(CameraOption) is { } cameraPath) {
            return await RomForge.RunCameraAsync(outputPath: cameraPath);
        }
        if (parseResult.GetValue(VolleyOption) is { } volleyPath) {
            return await RomForge.RunVolleyAsync(outputPath: volleyPath, args: args);
        }
        if (parseResult.GetValue(BrickfallOption) is { } brickfallPath) {
            return await RomForge.RunBrickfallAsync(outputPath: brickfallPath, args: args);
        }
        if (parseResult.GetValue(ChromaOption) is { } chromaPath) {
            return await RomForge.RunChromaAsync(outputPath: chromaPath, args: args);
        }
        if (parseResult.GetValue(SolitaireOption) is { } solitairePath) {
            return await RomForge.RunSolitaireAsync(outputPath: solitairePath, args: args);
        }
        if (parseResult.GetValue(PokerOption) is { } pokerPath) {
            return await RomForge.RunPokerAsync(outputPath: pokerPath, args: args);
        }
        if (parseResult.GetValue(TuneOption) is { } tunePath) {
            return await RomForge.RunTuneAsync(documentPath: parseResult.GetValue(TuneFromOption), outputPath: tunePath);
        }
        if (parseResult.GetValue(BakeOption) is { } bakePath) {
            return await RomForge.RunBakeAsync(outputDirectory: bakePath, stress: false, args: args);
        }
        if (parseResult.GetValue(BakeStressOption) is { } bakeStressPath) {
            return await RomForge.RunBakeAsync(outputDirectory: bakeStressPath, stress: true, args: args);
        }
        if (parseResult.GetValue(BakeCalibrationOption) is { } bakeCalibrationPath) {
            return await RomForge.RunBakeCalibrationAsync(outputDirectory: bakeCalibrationPath, args: args);
        }
        if (parseResult.GetValue(AvatarOption) is { } avatarPath) {
            return await RomForge.RunAvatarAsync(args: args, avatarJsonPath: parseResult.GetValue(AvatarFromOption), movementModeText: parseResult.GetValue(AvatarMovementModeOption), outputPath: avatarPath);
        }
        if (parseResult.GetValue(FlagshipsOption) is { } flagshipsPath) {
            return await RomForge.RunFlagshipsAsync(outputPath: flagshipsPath, args: args);
        }

        return null;
    }
}
