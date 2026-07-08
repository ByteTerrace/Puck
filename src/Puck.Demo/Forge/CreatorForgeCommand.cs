using Puck.Abstractions.Gpu;
using Puck.Demo.Creator;
using Puck.Demo.Overworld;
using Puck.Demo.Tracker;
using Puck.Hosting;
using Puck.Input.Devices;

namespace Puck.Demo.Forge;

/// <summary>
/// The in-engine entry points that pull the forge, file IO, and GPU-resolution together on behalf of the live overworld
/// node — kept in one place so that node stays under its coupling budget. Its callers: the framework-game/world-lens
/// cabinet carts (SDF-baked title art the brick renders) and the two in-game AVATAR forge paths — the console
/// <c>forge</c> verb (writes <c>./forged-avatars/creator-avatar.gbc</c>) and the creator Start-commit (in-memory
/// hot-swap of the running avatar cart). Both avatar paths are LOSSLESS: they route the live scene's FULL
/// <c>puck.creation.v1</c> document (<see cref="Puck.Demo.Creator.CreatorScene.ToDocument"/> — the same one
/// <c>creator.save</c> persists) through the same rich bake (<see cref="AvatarForge.FromCreation"/> + the document's
/// bake style) the headless <c>--forge-avatar-from</c> uses, so an in-game forge and a CLI forge of the same saved
/// creation produce a BYTE-IDENTICAL cart — the animation frames and bake style reach the ROM, not just the rest-pose
/// geometry.
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

    // ── The subject-neutral author→forge→hot-swap registry (contract rule 6, "generalizes beyond avatars") ──
    //
    // ONE mechanism forges all three in-session subjects — the AVATAR walker, the TUNE jukebox, and the SDF-ART SCENE
    // creature — each pulling its OWN live document and producing 32 KiB cart bytes. The render node reaches this only
    // through the primitive-typed forwarders below (ForgeSubjectRom / IsForgedCartType / ForgedCartTypes), never as a
    // typed ForgeSubject/ForgeRegistry field — it is at its analyzer coupling ceiling.

    /// <summary>The AVATAR walker cart type (the in-engine create→commit→play loop; the original forged subject).</summary>
    public const int AvatarCartType = 3;

    /// <summary>The JUKEBOX (tune) cart type — the tracker's live puck.audio.v1 document compiled to a music cart (no GPU).</summary>
    public const int JukeboxCartType = 9;

    /// <summary>The SDF-ART SCENE cart type — the creator's creation baked as a centred creature sprite (needs the GPU).</summary>
    public const int SceneCartType = 10;

    // The three subjects, declared once. Each Forge pulls its own live document (the creator scene, or the tracker's
    // working tune) and returns the cart bytes; the tune is NeedsGpu:false so its compile is NEVER gated behind device
    // resolution (a room with no GPU can still forge + play a tune). Never mutated after construction.
    private static readonly ForgeRegistry s_forgeRegistry = new(subjects: [
        new ForgeSubject(CartType: AvatarCartType, Kind: "avatar", NeedsGpu: true, Forge: static context => (
            // EMPTY creator scene → the built-in STARTER figure (the lazy-default cabinet cart before the player has
            // committed anything of their own — byte-identical to the old ForgeDefaultAvatarRom). A NON-empty scene →
            // LOSSLESS: the SAME rich AvatarForge.FromCreation route --forge-avatar-from uses (frames + bake style reach
            // the ROM), so an in-game commit and a headless forge of the same saved creation are byte-identical.
            (context.FrameSource.Creator.PlacedCount == 0)
                ? RomForge.ForgeAvatarRom(device: context.Device!, gpu: context.Gpu!, avatar: AvatarDefinition.Default(), title: "PUCKAVTR")
                : RomForge.ForgeAvatarRomFromCreation(device: context.Device!, gpu: context.Gpu!, document: context.FrameSource.Creator.ToDocument(), title: "PUCKAVTR"))),
        new ForgeSubject(CartType: JukeboxCartType, Kind: "tune", NeedsGpu: false, Forge: static context =>
            // The tracker's live working document, compiled through the GPU-FREE Tune.TuneRom.Build.
            Tune.TuneRom.Build(document: TrackerModeInstance(services: context.Services).Scene.Document)),
        new ForgeSubject(CartType: SceneCartType, Kind: "scene", NeedsGpu: true, Forge: static context =>
            // The SAME creator creation as the avatar, but baked through the SDF-art/creature path (a centred sprite),
            // not the walker — so authoring one creation forges two DISTINCT carts (walker avatar vs. creature scene).
            RomForge.ForgeSceneRomFromCreation(device: context.Device!, gpu: context.Gpu!, document: context.FrameSource.Creator.ToDocument(), title: "PUCKSCEN")),
    ]);

    /// <summary>Whether <paramref name="cartType"/> is a FORGED (lazily-baked) subject cart — the registry-driven test the
    /// render node's lazy-forge / reload paths iterate against, replacing the old avatar-only <c>== AvatarCartType</c>
    /// branch. A forged type is Cycle-reachable / lazy-forged, never a cabinet boot default.</summary>
    /// <param name="cartType">The cart type to test.</param>
    /// <returns>Whether a subject forges this type.</returns>
    public static bool IsForgedCartType(int cartType) => s_forgeRegistry.IsForgedType(cartType: cartType);

    /// <summary>The forged (lazily-baked) subject cart types, for the node's "reload any cabinet running a forged cart"
    /// pass. Primitive-typed (an int sequence) so the node names no ForgeSubject type.</summary>
    public static IEnumerable<int> ForgedCartTypes => [AvatarCartType, JukeboxCartType, SceneCartType];

    /// <summary>Forges the registered subject for <paramref name="cartType"/> into overworld ROM BYTES in memory — the ONE
    /// generalized path the render node's commit / lazy-forge routes call for ANY forged subject (avatar/tune/scene),
    /// replacing the avatar-hardcoded forge. The GPU-vs-no-GPU dispatch is EXPLICIT: a GPU-needing subject (avatar/scene)
    /// resolves the live device first and narrates a decline when it is unavailable; a pure-CPU subject (the tune) is
    /// invoked WITHOUT touching device resolution, so its compile is never gated behind a device the room may lack.
    /// Returns null (narrated) on an unknown type, a missing GPU, or a bake throw, so a failed forge never takes the demo
    /// down.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services + the tracker document).</param>
    /// <param name="frameSource">The live overworld frame source (the document composition point).</param>
    /// <param name="cartType">The forged subject's cart type.</param>
    /// <returns>The forged ROM bytes, or null on failure.</returns>
    public static byte[]? ForgeSubjectRom(in FrameContext context, IServiceProvider services, OverworldFrameSource frameSource, int cartType) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (s_forgeRegistry.Find(cartType: cartType) is not { } subject) {
            Console.Error.WriteLine(value: $"[forge] cart type {cartType} is not a forged subject.");

            return null;
        }

        IGpuDeviceContext? device = null;
        IGpuComputeServices? gpu = null;

        // EXPLICIT GPU dispatch: resolve the device ONLY for a GPU-needing subject — the tune's compile never waits on a
        // device. A GPU-needing subject with no device narrates and declines rather than throwing.
        if (subject.NeedsGpu) {
            if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out device) ||
                (services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices resolved)) {
                Console.Error.WriteLine(value: $"[forge] no GPU device available; cannot forge the {subject.Kind} cart.");

                return null;
            }

            gpu = resolved;
        }

        try {
            return subject.Forge(arg: new ForgeContext(frame: in context, services: services, frameSource: frameSource, device: device, gpu: gpu));
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[forge] {subject.Kind} cart forge failed: {exception.Message}");

            return null;
        }
    }

    /// <summary>Bakes the frame source's current creation into <c>./forged-avatars/creator-avatar.gbc</c> (with its
    /// <c>.creation.json</c>, sprite-sheet preview, bake blob, and boot proof), reusing the live GPU device. LOSSLESS:
    /// it routes the live scene's FULL <c>puck.creation.v1</c> document through the same rich bake
    /// <c>--forge-avatar-from</c> uses (the animation frames become the walk poses and the document's bake style
    /// applies), so the <c>forge</c> verb and a headless forge of the same saved creation produce byte-identical carts.
    /// Never throws — any failure is narrated to stderr, so a bad bake can never take the demo down.</summary>
    /// <param name="context">The current frame context (its host resolves the live GPU device).</param>
    /// <param name="services">The application services (for the compute services).</param>
    /// <param name="frameSource">The live creator scene to forge.</param>
    public static void Bake(in FrameContext context, IServiceProvider services, OverworldFrameSource frameSource) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        try {
            if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) ||
                (services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices gpu)) {
                Console.Error.WriteLine(value: "[forge] no GPU device available; cannot bake the avatar.");

                return;
            }

            var document = frameSource.Creator.ToDocument();
            var directory = Path.Combine(path1: Environment.CurrentDirectory, path2: "forged-avatars");
            _ = Directory.CreateDirectory(path: directory);
            var romPath = Path.Combine(path1: directory, path2: "creator-avatar.gbc");

            File.WriteAllText(path: Path.ChangeExtension(path: romPath, extension: ".creation.json"), contents: CreationStore.ToJson(document: document));
            // Same header title the CLI --forge-avatar-from uses (RomForge.RunAvatarAsync) so the in-game forge and the
            // headless forge of the same saved creation produce byte-identical carts — the lossless-equivalence proof.
            RomForge.ForgeAvatarFromCreation(device: device, gpu: gpu, document: document, outputPath: romPath, title: "PUCKAVTR");

            Console.Error.WriteLine(value: $"[forge] baked your creation → {romPath} (+ .creation.json, .sheet.png, .bake.bin, .emulated.png). Boot it with: --rom {romPath}");
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
