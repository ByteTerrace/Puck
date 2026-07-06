using Puck.Abstractions.Gpu;
using Puck.Demo.Overworld;
using Puck.Demo.Tracker;
using Puck.Hosting;
using Puck.Input.Devices;

namespace Puck.Demo.Forge;

/// <summary>
/// The in-engine entry points that pull the forge, file IO, and GPU-resolution together on behalf of the live overworld
/// node — kept in one place so that node stays under its coupling budget. Two callers: the world-lens cabinet cart (a
/// forged room the brick renders) and the console <c>forge</c> verb (a player's creator scene → a playable overworld
/// cartridge).
/// </summary>
internal static class ForgeCommands {
    /// <summary>Builds the world-lens cabinet cart, SDF-forging its room background on the live GPU when available and
    /// falling back to the CPU-authored room otherwise (so the cabinet always has a cart).</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <returns>A genuine world-lens ROM.</returns>
    public static byte[] BuildWorldLensCart(in FrameContext context, IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        try {
            if (context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) &&
                (services.GetService(serviceType: typeof(IGpuComputeServices)) is IGpuComputeServices gpu)) {
                var room = SceneForge.ForgeRoom(device: device, gpu: gpu);

                return WorldLensRom.BuildFromForgedRoom(title: "PUCKLENS", room: room);
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[world-lens] SDF-forged background unavailable ({exception.Message}); using the CPU room.");
        }

        return WorldLensRom.Build(title: "PUCKLENS");
    }

    /// <summary>Builds the Brickfall cabinet cart, SDF-baking its title screen on the live GPU when available (see
    /// <see cref="BrickfallTitleBake"/>) and falling back to the hand-authored title otherwise — the same
    /// always-has-a-cart posture as the world-lens cart above. Narrates which title shipped.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <returns>A genuine Brickfall ROM.</returns>
    public static byte[] BuildBrickfallCart(in FrameContext context, IServiceProvider services) =>
        BuildTitleBakedCart(context: in context, services: services, label: "brickfall", tryInstallTitle: BrickfallTitleBake.TryInstall, build: static () => BrickfallRom.Build());

    /// <summary>Builds the Volley cabinet cart, SDF-baking its title screen on the live GPU when available (see
    /// <see cref="VolleyTitleBake"/>) and falling back to the hand-authored title otherwise. Narrates which title
    /// shipped.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <returns>A genuine Volley ROM.</returns>
    public static byte[] BuildVolleyCart(in FrameContext context, IServiceProvider services) =>
        BuildTitleBakedCart(context: in context, services: services, label: "volley", tryInstallTitle: VolleyTitleBake.TryInstall, build: static () => VolleyRom.Build());

    /// <summary>Builds the Chroma cabinet cart, SDF-baking its title screen on the live GPU when available (see
    /// <see cref="ChromaTitleBake"/>) and falling back to the hand-authored title otherwise. Narrates which title
    /// shipped.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <returns>A genuine Chroma ROM.</returns>
    public static byte[] BuildChromaCart(in FrameContext context, IServiceProvider services) =>
        BuildTitleBakedCart(context: in context, services: services, label: "chroma", tryInstallTitle: ChromaTitleBake.TryInstall, build: static () => ChromaRom.Build());

    /// <summary>Builds the Solitaire cabinet cart, SDF-baking its title emblem, felt table, and cursor on the live
    /// GPU when available (see <see cref="SolitaireBake"/>) and falling back to the hand-authored banner, flat felt,
    /// and pointer otherwise. Narrates which art shipped.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <returns>A genuine Solitaire ROM.</returns>
    public static byte[] BuildSolitaireCart(in FrameContext context, IServiceProvider services) =>
        BuildTitleBakedCart(context: in context, services: services, label: "solitaire", tryInstallTitle: SolitaireBake.TryInstall, build: static () => SolitaireRom.Build());

    /// <summary>Resolves the Solitaire cart's battery save path — forwarded here so the overworld node stays under
    /// its coupling budget (it already leans on this facade for the cart builds).</summary>
    /// <returns>The default Solitaire <c>.sav</c> path.</returns>
    public static string PrepareSolitaireSavePath() =>
        SolitaireRom.PrepareDefaultSavePath();

    /// <summary>Builds the Poker cabinet cart, SDF-baking its title emblem, table felt, and cursor on the live GPU
    /// when available (see <see cref="PokerBake"/>) and falling back to the hand-authored banner, flat felt, and
    /// pointer otherwise. Narrates which art shipped.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <returns>A genuine Poker ROM.</returns>
    public static byte[] BuildPokerCart(in FrameContext context, IServiceProvider services) =>
        BuildTitleBakedCart(context: in context, services: services, label: "poker", tryInstallTitle: PokerBake.TryInstall, build: static () => PokerRom.Build());

    /// <summary>Resolves the Poker cart's battery save path — forwarded here for the same coupling-budget reason as
    /// the Solitaire path above.</summary>
    /// <returns>The default Poker <c>.sav</c> path.</returns>
    public static string PreparePokerSavePath() =>
        PokerRom.PrepareDefaultSavePath();

    // The shared always-has-a-cart shape of the four framework games: try the live-GPU title bake (narrating a
    // decline or failure), then build the cartridge either way.
    private static byte[] BuildTitleBakedCart(in FrameContext context, IServiceProvider services, string label, Func<IGpuDeviceContext, IGpuComputeServices, bool> tryInstallTitle, Func<byte[]> build) {
        ArgumentNullException.ThrowIfNull(services);

        try {
            if (context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) &&
                (services.GetService(serviceType: typeof(IGpuComputeServices)) is IGpuComputeServices gpu)) {
                if (!tryInstallTitle(device, gpu)) {
                    Console.Error.WriteLine(value: $"[{label}] title bake declined; using the hand-authored title.");
                }
            } else {
                Console.Error.WriteLine(value: $"[{label}] no GPU device available; using the hand-authored title.");
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[{label}] title bake failed ({exception.Message}); using the hand-authored title.");
        }

        return build();
    }

    /// <summary>Forges the frame source's current creation into overworld ROM BYTES in memory (no disk, no boot check) —
    /// the hot path the in-game create→commit→play loop uses to swap a cabinet's cart live. Returns null (narrated) if
    /// the GPU device is unavailable or the bake throws, so a failed commit never takes the demo down.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <param name="frameSource">The live creator scene to export and forge.</param>
    /// <returns>The forged ROM bytes, or null on failure.</returns>
    public static byte[]? ForgeCreatorAvatarRom(in FrameContext context, IServiceProvider services, OverworldFrameSource frameSource) {
        ArgumentNullException.ThrowIfNull(frameSource);

        return ForgeAvatarRom(context: in context, services: services, avatar: frameSource.ExportAvatar());
    }

    /// <summary>Forges the built-in starter avatar into overworld ROM BYTES in memory — the cabinet's avatar cart before
    /// the player has committed anything of their own. Returns null (narrated) on failure.</summary>
    /// <param name="context">The current frame context.</param>
    /// <param name="services">The application services.</param>
    /// <returns>The forged ROM bytes, or null on failure.</returns>
    public static byte[]? ForgeDefaultAvatarRom(in FrameContext context, IServiceProvider services) =>
        ForgeAvatarRom(context: in context, services: services, avatar: AvatarDefinition.Default());

    private static byte[]? ForgeAvatarRom(in FrameContext context, IServiceProvider services, AvatarDefinition avatar) {
        ArgumentNullException.ThrowIfNull(services);

        try {
            if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) ||
                (services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices gpu)) {
                Console.Error.WriteLine(value: "[forge] no GPU device available; cannot forge the avatar cart.");

                return null;
            }

            return RomForge.ForgeAvatarRom(device: device, gpu: gpu, avatar: avatar, title: "PUCKAVTR");
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[forge] avatar cart forge failed: {exception.Message}");

            return null;
        }
    }

    /// <summary>Bakes the frame source's current creation into <c>./forged-avatars/creator-avatar.gbc</c> (with its
    /// <c>.avatar.json</c>, sprite-sheet preview, and boot proof), reusing the live GPU device. Never throws — any
    /// failure is narrated to stderr, so a bad bake can never take the demo down.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <param name="frameSource">The live creator scene to export and forge.</param>
    public static void Bake(in FrameContext context, IServiceProvider services, OverworldFrameSource frameSource) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        try {
            if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) ||
                (services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices gpu)) {
                Console.Error.WriteLine(value: "[forge] no GPU device available; cannot bake the avatar.");

                return;
            }

            var avatar = frameSource.ExportAvatar();
            var directory = Path.Combine(path1: Environment.CurrentDirectory, path2: "forged-avatars");
            _ = Directory.CreateDirectory(path: directory);
            var romPath = Path.Combine(path1: directory, path2: "creator-avatar.gbc");

            File.WriteAllText(path: Path.ChangeExtension(path: romPath, extension: ".avatar.json"), contents: avatar.ToJson());
            RomForge.ForgeAvatar(device: device, gpu: gpu, avatar: avatar, outputPath: romPath, title: "PUCKCREATOR");

            Console.Error.WriteLine(value: $"[forge] baked your creation → {romPath} (+ .avatar.json, .sheet.png, .bake.bin, .emulated.png). Boot it with: --rom {romPath}");
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[forge] failed: {exception.Message}");
        }
    }

    // The music tracker's ENTIRE host-side footprint (document model + pad controller + preview player) lives behind
    // this one lazily-built singleton, reached ONLY through the primitive-typed forwarders below — never as a typed
    // field/property/parameter on OverworldRenderNode, which sits AT its own analyzer coupling ceiling and cannot
    // take on a single additional referenced type. TrackerCommandModule (in Puck.Demo.Tracker, its own coupling
    // budget) reaches the SAME instance through TrackerModeInstance, so the pad and the console verbs edit one
    // shared working document.
    private static TrackerModeState? s_trackerMode;

    /// <summary>The tracker's mode-state singleton, building it on first touch. Internal (not <c>private</c>) so
    /// <see cref="TrackerCommandModule"/> can reach the SAME instance the forwarders below drive — it lives in a
    /// different project-relative folder but the same coupling-exempt path (it already depends on this class).</summary>
    /// <param name="services">The application services (resolves the on-screen console when registered).</param>
    /// <returns>The shared tracker mode state.</returns>
    internal static TrackerModeState TrackerModeInstance(IServiceProvider services) =>
        (s_trackerMode ??= new TrackerModeState(services: services));

    /// <summary>Toggles tracker mode and returns the new active state — the <c>tracker</c> console verb and the
    /// overworld node's mutual-exclusion guard with creator mode both route here.</summary>
    /// <param name="services">The application services.</param>
    /// <returns>Whether tracker mode is now active.</returns>
    public static bool TrackerToggle(IServiceProvider services) {
        var tracker = TrackerModeInstance(services: services);

        TrackerSetActive(services: services, active: !tracker.Active);

        return tracker.Active;
    }

    /// <summary>Whether tracker mode is currently active.</summary>
    /// <param name="services">The application services.</param>
    /// <returns>The active state.</returns>
    public static bool TrackerIsActive(IServiceProvider services) => TrackerModeInstance(services: services).Active;

    /// <summary>Enters or leaves tracker mode (see <see cref="TrackerModeState.SetActive"/>), narrating the entry
    /// banner/pattern dump exactly like the pad's own narration.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="active">The desired state.</param>
    public static void TrackerSetActive(IServiceProvider services, bool active) {
        var tracker = TrackerModeInstance(services: services);

        if (tracker.Active == active) {
            return;
        }

        tracker.SetActive(active: active);
        Console.Error.WriteLine(value: (active
            ? "[tracker] ENTER — d-pad up/down moves the row cursor, left/right switches pattern; bumpers nudge the note a semitone, stick clicks nudge an octave; South toggles hold/off, East plays/stops the preview, West saves, North exits; triggers nudge the tempo."
            : "[tracker] EXIT."));

        if (active) {
            tracker.NarrateRows();
        }
    }

    /// <summary>The tracker's per-frame pad takeover for the creating slot: advances pad input and consumes any exit
    /// request. A no-op when tracker mode is inactive.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="raw">The creating slot's raw pad state this frame.</param>
    /// <returns>Whether tracker mode was active (and therefore took the slot over) this frame.</returns>
    public static bool TrackerAdvanceInput(IServiceProvider services, in GamepadState raw) {
        var tracker = TrackerModeInstance(services: services);

        if (!tracker.Active) {
            return false;
        }

        tracker.AdvanceInput(raw: in raw);

        if (tracker.ConsumeExitRequest()) {
            TrackerSetActive(services: services, active: false);
        }

        return true;
    }

    /// <summary>Starts or stops the tracker's headless preview from the CURRENT working document — the
    /// <c>tracker.play</c>/<c>tracker.stop</c> console verbs and the pad's East button both route here (through
    /// <see cref="TrackerModeState"/>'s own forwarder), so both paths have the identical effect.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="play"><see langword="true"/> to (re)start the preview, <see langword="false"/> to stop it.</param>
    /// <returns>A status line for the console.</returns>
    public static string TrackerRequestPreview(IServiceProvider services, bool play) {
        var tracker = TrackerModeInstance(services: services);

        return (play ? tracker.StartPreview() : tracker.StopPreviewRequest());
    }

    /// <summary>Steps the tracker's headless preview by one rendered frame (a no-op when nothing is playing) — call
    /// once per produced frame.</summary>
    /// <param name="services">The application services.</param>
    public static void TrackerStepPreview(IServiceProvider services) {
        if (s_trackerMode is { } tracker) {
            tracker.StepPreview();
        }
    }

    /// <summary>Disposes the tracker's preview player/host audio stream, when one was ever built. Safe to call even
    /// if tracker mode was never entered this run.</summary>
    public static void TrackerDispose() {
        s_trackerMode?.Dispose();
    }
}
