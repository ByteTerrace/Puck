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
        Description = "Headless tool: forge a CAMERA .gbc (a real ROM that drives the authentic M64282FP protocol — program registers, trigger, poll busy, blit the captured image) and self-verify it against the deterministic gradient sensor, writing an <out>.emulated.png. Boot it with --rom to run the webcam viewfinder.",
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

    /// <summary>The <c>--forge-town</c> option (a flag — the town writes to fixed locations, not a given path).</summary>
    public static Option<bool> TownOption { get; } = new(name: "--forge-town") {
        Description = "Headless tool: build + verify PUCKTON, the flagship town — regenerate every town creation (buildings + street props) byte-identically against docs/examples/creations/town-*.creation.json, assemble + walk-grid-bake the puck.world.v1 world, assert it is byte-identical to docs/examples/puckton.world.json plus determinism (build-twice) and round-trip (save→reload) proofs, and MATERIALIZE it into the runtime CAS store + ./worlds/puckton.world.json. Then walk it with --run docs/examples/overworld-town.json (whose overworld node names \"world\": \"puckton\"), or the live world.load puckton console verb. No GPU.",
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
        Description = "Headless tool: bake SDF stand-ins for the pipeline's hand-pixelled reference art (a paddle bar, a ball dot, a court net) at the reference tiles' native sizes on the DMG classic recipe, write a side-by-side comparison PNG to the given directory, and print a per-tile pixel-match report to stderr — a calibration REPORT for the sculpt→bake pipeline, never a gate (low match is a finding, not a failure).",
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
        if (parseResult.GetValue(option: ForgeOption) is { } forgePath) {
            return await RomForge.RunAsync(outputPath: forgePath, args: args);
        }
        if (parseResult.GetValue(option: CameraOption) is { } cameraPath) {
            return await RomForge.RunCameraAsync(outputPath: cameraPath);
        }
        if (parseResult.GetValue(option: TownOption)) {
            // Dispatched straight to the town forge (not via RomForge, which is at its class-coupling ceiling): the
            // town needs no GPU host, so Run is a synchronous CPU build+verify.
            return Puck.Demo.Town.TownForge.Run(args: args);
        }
        if (parseResult.GetValue(option: TuneOption) is { } tunePath) {
            return await RomForge.RunTuneAsync(documentPath: parseResult.GetValue(option: TuneFromOption), outputPath: tunePath);
        }
        if (parseResult.GetValue(option: BakeOption) is { } bakePath) {
            return await RomForge.RunBakeAsync(outputDirectory: bakePath, stress: false, args: args);
        }
        if (parseResult.GetValue(option: BakeStressOption) is { } bakeStressPath) {
            return await RomForge.RunBakeAsync(outputDirectory: bakeStressPath, stress: true, args: args);
        }
        if (parseResult.GetValue(option: BakeCalibrationOption) is { } bakeCalibrationPath) {
            return await RomForge.RunBakeCalibrationAsync(outputDirectory: bakeCalibrationPath, args: args);
        }
        if (parseResult.GetValue(option: AvatarOption) is { } avatarPath) {
            return await RomForge.RunAvatarAsync(args: args, creationPath: parseResult.GetValue(option: AvatarFromOption), movementModeText: parseResult.GetValue(option: AvatarMovementModeOption), outputPath: avatarPath);
        }
        if (parseResult.GetValue(option: FlagshipsOption) is { } flagshipsPath) {
            return await RomForge.RunFlagshipsAsync(outputPath: flagshipsPath, args: args);
        }

        return null;
    }
}
