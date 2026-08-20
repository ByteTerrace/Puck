using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

using Xunit;

using Puck.Assets.Documents;
using Puck.Forge.Authoring;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>
/// Fixture construction — the Domains-module equivalent for this suite: where a law's raw material (a base
/// document, a fresh in-process <see cref="WorldServer"/>) comes from, kept out of the law bodies themselves.
/// The base document is COMPILER-MAINTAINED: <see cref="BuildDocument"/> constructs a minimal, valid
/// <see cref="WorldDefinition"/> directly in code (never read from <c>src/Puck.World/Assets/worlds</c> — Puck.World,
/// the composition root, is out of scope; see README.md, and CLAUDE.md's greenfield/scope rules). A change to
/// <see cref="WorldDefinition"/>'s required member set breaks this file at COMPILE time rather than at a runtime
/// parse of a JSON fixture nobody is watching — the whole point of the shape.
/// </summary>
internal static class Fixtures {
    /// <summary>The repository's fixed simulation rate (CLAUDE.md, the puck-world skill: "the fixed simulation rate
    /// is 240 Hz").</summary>
    private const uint SimulationRateHz = 240U;

    /// <summary>The engine screen-surface index the code-built test-pattern screen occupies — the ENGAGE target
    /// <see cref="EngageAuthorityLawTests"/> routes against. The GPU-side <c>Puck.SdfVm.SdfWorldEngine</c> that
    /// actually enforces <see cref="SdfProgramBuilder.MaxScreenSurfaces"/> is out of reach here (Puck.SdfVm is not
    /// referenced by this project), so this simply names index 0, comfortably below any reserved derived-face
    /// band.</summary>
    public const int TestPatternScreenIndex = 0;

    /// <summary>Builds the <c>admission</c> row that authorizes travelers from any authenticated federation
    /// authority, minting the same three rows a driven arrival needs: <c>Control</c>/<c>all</c>, and exclusive
    /// <c>Drive</c> plus <c>Observe</c> over the body admission assigns (an absent subject).</summary>
    public static WorldAdmissionEntry AnyAuthorityArrivals() => new(
        Domain: WorldAdmissionEntry.AnyAuthority,
        Subject: null,
        Mode: WorldAdmissionTrustMode.FederatedAuthority,
        Algorithm: string.Empty,
        PublicKey: string.Empty,
        Grants: [
            new WorldAdmissionGrant(Capability: WorldCapability.Control, Subject: GrantSubject.All),
            new WorldAdmissionGrant(Capability: WorldCapability.Drive, Exclusive: true, Budget: 64),
            new WorldAdmissionGrant(Capability: WorldCapability.Observe, Budget: 64),
        ]);
    /// <summary>Builds a minimal, valid <see cref="WorldDefinition"/> entirely in code — one row per REQUIRED
    /// section, each populated with the smallest value shape
    /// its own validation pass accepts. Carries exactly the extra furniture the laws in this suite need beyond the
    /// bare-minimum skeleton:
    /// <list type="bullet">
    /// <item><description>an <c>addons</c> row (<see cref="StrictParseLawTests"/> injects an unknown member into
    /// its serialized form);</description></item>
    /// <item><description>an empty <c>state</c> section — <see cref="MutationAllOrNothingLawTests"/> targets it by
    /// ADDING a row through <c>UpsertStateRow</c>, so no row needs to pre-exist;</description></item>
    /// <item><description>the default 4-seat population (<see cref="AuthorityAdministrationLawTests"/> needs
    /// Seat(1)/Seat(2), body indices 0 and 1 — bodies 1 and 2 in this 0-based scheme);</description></item>
    /// <item><description>a <see cref="WorldScreen"/> carrying a <see cref="WorldScreenSource.TestPattern"/> source
    /// (the simplest engageable-shaped source that needs no booted machine) at
    /// <see cref="TestPatternScreenIndex"/> — <see cref="EngageAuthorityLawTests"/>'s target.</description></item>
    /// </list>
    /// Every other section is the smallest legal value <c>WorldDefinitionValidator</c> accepts: one locomotion kit
    /// ("traveler", a bare-bones grounded model with an empty response table — "the empty table snaps planar
    /// velocity instantly"), the three channels its body motion program's selected operations require
    /// (<c>MoveAdvance</c>/<c>MoveStrafe</c>/<c>Turn</c>), and <see cref="IntentSource.Idle"/> as the population's
    /// default peer source so no producer program (wander/attend/designated) is needed at all. This kit carries NO
    /// collider — no law here needs one — so <see cref="BuildDocumentCore"/> is the shared shape
    /// <see cref="BuildGradientUpDocument"/> extends with the ONE collider-bearing arm
    /// <see cref="GradientUpContactLawTests"/> needs, without duplicating this whole literal.
    /// </summary>
    public static WorldDefinition BuildDocument() => BuildDocumentCore(
        spawnPoints: BuildSpawnPoints(),
        collision: new WorldCollision(ContactSkin: 0.02f, GradientProbe: 0f, MaxIterations: 4, MaxSlopeDegrees: 60f, Requirements: []),
        seatCollider: null,
        creations: [],
        placements: []
    );

    /// <summary>The default 4-corner spawn layout every <see cref="BuildDocumentCore"/> call starts from — a fresh
    /// array per call, since <see cref="BuildGradientUpDocument"/> overwrites one entry's position without disturbing
    /// <see cref="BuildDocument"/>'s own copy.</summary>
    private static WorldSpawnPoint[] BuildSpawnPoints() => [
        new(Id: "seat-1", Position: new Vector3(x: 0f, y: 0f, z: 0f)),
        new(Id: "seat-2", Position: new Vector3(x: 2f, y: 0f, z: 0f)),
        new(Id: "seat-3", Position: new Vector3(x: 0f, y: 0f, z: 2f)),
        new(Id: "seat-4", Position: new Vector3(x: 2f, y: 0f, z: 2f)),
    ];

    /// <summary>The full parameter set <c>ValidateProducerParameters</c> requires for a kit naming the "wander"
    /// producer — shared by every fixture kit that declares one (<see cref="BuildKits"/>'s own "traveler" row, and
    /// any other suite file's own custom kit — see <see cref="TransferAbortKitWideningLawTests"/>'s vehicle/swim
    /// kits). Values mirror the shipped worlds' own "traveler"-style kit; none of them is exercised by any of these
    /// suites' laws — WorldPopulation.SeedSeatWander unconditionally resolves this producer on the assigned kit's
    /// row regardless of whether anything actually wanders (see <see cref="BuildKits"/>'s own remarks on why the
    /// row must exist at all).</summary>
    public static BodyProgramParameters TravelerWanderParameters { get; } = new(
        Scalars: new Dictionary<string, float> {
            ["forward"] = 0.375f,
            ["softRadius"] = 45f,
            ["weaveAmplitude"] = 0.5f,
            ["inwardGain"] = 1.6f,
            ["turnScale"] = 2.5f,
            ["weaveFrequencyBase"] = 0.3f,
            ["weaveFrequencyRange"] = 0.2f,
            ["altitudeGain"] = 0.32f,
            ["activityRateBase"] = 2.2f,
            ["activityRateRange"] = 1.3f,
            ["strafeWave"] = 0f,
            ["turnWave"] = 0f,
            ["upWave"] = 0f,
            ["pitchWave"] = 0f,
            ["rollTurn"] = 0f,
            ["pressThreshold"] = 0f,
            ["altitudeBase"] = 0f,
            ["altitudeRange"] = 0f,
        },
        Channels: new Dictionary<string, string>()
    );

    /// <summary>The one locomotion kit name every fixture document declares.</summary>
    public const string SeatKitName = "traveler";

    /// <summary>The one locomotion kit every fixture document declares — <see cref="BuildDocument"/> passes
    /// <see langword="null"/> (no law here needs a body collider); <see cref="BuildGradientUpDocument"/> is the
    /// ONE caller that supplies one, so every other law's compiled kit table is untouched byte-for-byte.</summary>
    /// <param name="seatCollider">The seat kit's body collider, or <see langword="null"/> for none.</param>
    private static WorldKit[] BuildKits(WorldCollider? seatCollider) => [
        new(
            Name: SeatKitName,
            BodyMotionProgram: "grounded",
            Motion: new WorldMotionModel.Grounded(
                MoveSpeed: 4f,
                TurnSpeed: 2.5f,
                RiseGravity: 14f,
                FallGravity: 23f,
                MaxFallSpeed: 20f,
                // The empty response table snaps planar velocity instantly — a legitimate, minimal table
                // (WorldDefinitionValidator.ValidateResponse loops zero times over it).
                Response: [],
                SprintMultiplier: 1f
            ),
            // The full parameter set ValidateProducerParameters requires for a kit naming the "wander"
            // producer — see the bodyMotionPrograms remark above for why this exists at all. Values mirror the
            // shipped worlds' own "traveler"-style kit; none of them is exercised by this suite's laws.
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["wander"] = TravelerWanderParameters,
            },
            ActionsRaw: new Dictionary<string, ActionSpec>(),
            Collider: seatCollider
        ),
    ];
    /// <summary>The parameterized core every <see cref="WorldDefinition"/> fixture in this suite builds from — the
    /// pieces that vary across the two callers (<see cref="BuildDocument"/>, <see cref="BuildGradientUpDocument"/>)
    /// are parameters; everything else is the one shared literal, never forked.</summary>
    /// <param name="spawnPoints">The seat spawn layout.</param>
    /// <param name="collision">The contact tuning and requirements.</param>
    /// <param name="seatCollider">The seat kit's body collider, or <see langword="null"/> for none.</param>
    /// <param name="creations">The document's creation rows.</param>
    /// <param name="placements">The document's placement rows.</param>
    private static WorldDefinition BuildDocumentCore(WorldSpawnPoint[] spawnPoints, WorldCollision collision, WorldCollider? seatCollider, WorldCreation[] creations, WorldPlacement[] placements) {
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
        };

        var bodyMotionPrograms = new BodyMotionProgram[] {
            new(
                Name: "grounded",
                Version: "puck.body-motion.v1",
                Kind: BodyProgramKind.Motion,
                Operations: [
                    BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                    BodyMotionOp.ComputePlanarTargetVelocity,
                    BodyMotionOp.ShapePlanarVelocity,
                    BodyMotionOp.SnapYawToPlanarIntent,
                    BodyMotionOp.RunActionTriggers,
                    BodyMotionOp.ApplyVerticalGravity,
                    BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                    BodyMotionOp.CommitPose,
                ]
            ),
            // WorldPopulation.SeedSeatWander/SeedSimulated unconditionally look up a "ProduceWanderIntent" producer
            // on the assigned kit's row (WorldPopulation.SeedProducer) to seed per-index wander phase/altitude —
            // even for a body whose IntentSource is Idle and that never actually wanders. EngageAuthorityLawTests
            // joins a seat (Server.WorldPopulation.ActivateSeat, reached through the ordinary session door) to get
            // a LIVE body to route, which walks this exact path, so the producer must exist even though nothing in
            // this suite drives it.
            new(
                Name: "wander",
                Version: "puck.body-motion.v1",
                Kind: BodyProgramKind.Producer,
                Operations: [BodyMotionOp.ProduceWanderIntent]
            ),
        };

        var population = new WorldPopulationDefaults(
            SeatActivationRaw: [SeatActivationPolicy.Eager, SeatActivationPolicy.Eager, SeatActivationPolicy.Eager, SeatActivationPolicy.Eager],
            NetworkPlayers: 0,
            // Idle, not Producer("wander") — this fixture declares no producer program at all; nothing simulated
            // ever needs to resolve a producer name.
            DefaultPeerSourceRaw: IntentSource.Idle,
            SeatSpawnsRaw: ["seat-1", "seat-2", "seat-3", "seat-4"],
            DistributionRaw: new WorldDistribution(
                Region: new WorldDistributionRegion.Disc(Radius: 40f, SampleCount: 124),
                Fill: new WorldSequence(Name: WorldSequence.Additive, Offset: 0, Step: 0.38196602f)
            ),
            PeerVariationRaw: new WorldPopulationVariation(
                Phase: new WorldSequence(Name: WorldSequence.Additive, Offset: -4, Step: 0.38196602f),
                Weave: new WorldSequence(Name: WorldSequence.Additive, Offset: -4, Step: 0.618034f),
                Activity: new WorldSequence(Name: WorldSequence.R2, Offset: 1, Step: 0f)
            ),
            SeatVariationRaw: new WorldPopulationVariation(
                Phase: new WorldSequence(Name: WorldSequence.Additive, Offset: 0, Step: 0.38196602f),
                Weave: new WorldSequence(Name: WorldSequence.Additive, Offset: 0, Step: 0.618034f),
                Activity: new WorldSequence(Name: WorldSequence.R2, Offset: 1, Step: 0f)
            ),
            PeerColorsRaw: new WorldSequence(Name: WorldSequence.Additive, Offset: -4, Step: 0.618034f),
            // Capacity == LocalSeatCount (the validator's own floor) deliberately: WorldPopulation seeds every
            // entry from LocalSeatCount..Capacity as a "simulated" crowd stand-in (WorldPopulation.SeedSimulated),
            // which unconditionally requires a "wander" producer program on the assigned kit REGARDLESS of
            // DefaultPeerSource — a requirement this minimal fixture has no reason to carry, since none of this
            // suite's laws exercise simulated/peer bodies. Pinning capacity to the seat count makes that loop empty
            // (LocalSeatCount..Capacity is 4..4) rather than declaring an unused producer just to satisfy it.
            CapacityRaw: WorldPopulationLimits.LocalSeatCount,
            ReconnectGraceSeconds: 3.0f
        );

        var playerDefaults = new WorldPlayerDefaults(
            IdentitiesRaw: [
                new WorldIdentitySeed(Id: WorldSafeName.Parse(candidate: "amber"), Name: "amber", Color: "#ED8530"),
            ],
            NeutralColorRaw: "#8C8C8C",
            ColorSequenceRaw: new WorldSequence(Name: WorldSequence.Additive, Offset: 0, Step: 0.618034f),
            Saturation: 0.65f,
            Value: 0.85f,
            ColorSearchLimit: 64,
            NoseFactor: 0.35f,
            PickerThreshold: 0.6f,
            PickerNeutralColorRaw: "#8C8C8C",
            PickerNeutralBlend: 0.5f,
            // Explicit — this fixture states what its seats feel like, matching the shipped worlds' numbers.
            SeatLookRaw: new WorldSeatLook(
                InvertPitch: false,
                InvertYaw: false,
                PitchSensitivity: 0.001f,
                StickLookRate: 2.6f,
                YawSensitivity: 0.001f
            )
        );

        var addons = new WorldAddonRow[] {
            // The strict-parse sabotage target (StrictParseLawTests / Fixtures.SabotagedAddonBytes) — the exact row
            // kind docs/verification/strict-definition-parse (now ported in-process) was originally named against.
            // Enabled:false — this suite never mounts an addon runtime, so the row is inert furniture, never a
            // live-mount attempt against a WASM module that does not exist on disk.
            new(Name: "probe", ModulePath: "probe.wasm", Hash: "sha256-64/0123456789abcdef", Fuel: 1_000_000UL, Enabled: false),
        };

        var testPatternScreen = new WorldScreen(
            Index: TestPatternScreenIndex,
            Origin: new Vector3(x: 0f, y: 1f, z: 0f),
            Right: new Vector3(x: 1f, y: 0f, z: 0f),
            Up: new Vector3(x: 0f, y: 1f, z: 0f),
            HalfWidth: 1f,
            HalfHeight: 1f,
            HalfDepth: 0.1f,
            Round: 0f,
            Source: new WorldScreenSource.TestPattern(Height: 240, Width: 320),
            // Passive: EngageAuthorityLawTests calls Server.Engagement.Engage directly (the authority door itself),
            // never the screen-policy precheck (proximity/auto-insert/machine-presence) WorldServer.ApplyCommand
            // layers on top — so the route policy fields are inert for this suite's purposes.
            Route: WorldScreenRoute.Passive
        );

        return new WorldDefinition(
            MotionRaw: new WorldMotionDefaults(MaxSmoothError: 3f, MoveSpeed: 4f, TurnSpeed: 2.5f),
            SpawnPointsRaw: spawnPoints,
            RenderRaw: null,
            ScreensRaw: [testPatternScreen],
            CamerasRaw: [],
            PopulationRaw: population,
            PlayerDefaultsRaw: playerDefaults,
            ChannelsRaw: channels,
            TargetRegistersRaw: [],
            BodyMotionProgramsRaw: bodyMotionPrograms,
            KitsRaw: BuildKits(seatCollider: seatCollider),
            DefaultSeatKitRaw: "traveler",
            AssignmentRaw: new WorldRowAssignment(Sequence: new WorldSequence(Name: WorldSequence.R1, Offset: 1, Step: 0f), Rows: []),
            AddonsRaw: addons,
            BindingOverlaysRaw: [],
            StorageRaw: WorldStorageDefaults.None,
            CreationsRaw: creations,
            PlacementsRaw: placements,
            AuthoringRaw: StandardAuthoring,
            SpeakersRaw: [],
            TunesRaw: [],
            PatchesRaw: [],
            AudioRaw: null,
            CollisionRaw: collision,
            HostRaw: StandardHost,
            // The engine ships no rig (Assets/worlds/standard.world.json authors the standard one), and a nonzero
            // census must author a views section, so the fixture carries the standard chase framing itself.
            ViewsRaw: StandardViews,
            LooksRaw: [],
            LookAssignmentRaw: new WorldRowAssignment(Sequence: new WorldSequence(Name: WorldSequence.R1, Offset: 129, Step: 0f), Rows: []),
            LinksRaw: [],
            GrantsRaw: [],
            HudRaw: new WorldHudSection(
                Defaults: new WorldHudDefaults(Enabled: true),
                Panels: []
            ),
            StateRaw: new WorldStateSection(World: []),
            // Authored seconds now (WorldDefinition.InputHold is the AUTHORED shape) — 120/60/0 ticks at the
            // fixture's authored 240 Hz is 0.5/0.25/0 seconds.
            InputHoldRaw: new WorldInputHoldAuthoring(CeilingSeconds: 0.5f, DefaultSeconds: 0f, EqualizeByDefault: true, LowerAfterSeconds: 0.25f, Participants: []),
            // The engine holds no rate of its own (absence is a rate-0 resident world), so the stepping fixture
            // authors the standard 240 Hz itself, like its views section.
            Simulation: new WorldSimulationDefaults(RateHz: 240)
        );
    }

    /// <summary>The standard host row (the values <c>standard.world.json</c> authors), for fixtures whose documents
    /// must carry an authored host (serialization round-trips, the host-member strict-parse laws) — the engine no
    /// longer carries one.</summary>
    public static WorldHostDefaults StandardHost { get; } = new(
        Presentation: WorldHostPresentation.Windowed,
        Backend: WorldBackendPreference.Auto,
        Width: 1280,
        Height: 800,
        SurfaceFormat: Puck.Abstractions.Presentation.SurfaceFormat.R8G8B8A8Unorm,
        Fullscreen: false,
        PresentMode: Puck.Abstractions.Presentation.PresentMode.Immediate,
        TargetHertz: 0.0,
        ExitAfterSeconds: 0,
        RayQuery: true,
        Timing: false,
        Genlock: null,
        Listen: null,
        Authority: null
    );

    /// <summary>The standard authoring policy row (the values <c>standard.world.json</c> authors), for fixtures
    /// that exercise editor/placement behavior — the engine no longer carries one.</summary>
    public static WorldAuthoringDefaults StandardAuthoring { get; } = new(
        AuthoringHeadroomPlacements: 8,
        AuthoringHeadroomScreens: 4,
        CandidateCap: 16,
        CandidateRadius: 32f,
        DerivedFaceScreens: 4,
        MaxPlacementScale: 5.0f,
        MinPlacementScale: 0.2f,
        PreviewDeadlineFrames: 12
    );

    /// <summary>The placed ball's actual surface radius, in world units — sized to "a few" per
    /// <see cref="GradientUpContactLawTests"/>'s brief, never a raw magic number at the call site.</summary>
    public const float BallSurfaceRadius = 3f;
    /// <summary>The standard chase framing, mirroring what <c>src/Puck.World/Assets/worlds/standard.world.json</c>
    /// authors. The engine holds no rig of its own, and a document whose census implies a body is refused for
    /// authoring no <c>views</c>, so a C#-built fixture states the numbers the way a document would.</summary>
    public static WorldCameraProgram StandardSeatRig { get; } = new(
        Name: "seatChase",
        Version: WorldCameraProgram.CurrentVersion,
        Operations: [
            new WorldCameraProgramOp.Orbit(
                Distance: 5.4626001f,
                Yaw: 0f,
                Pitch: 0.4145069f,
                PivotOffset: new DocumentVector3(value: Vector3.Zero)
            ),
            new WorldCameraProgramOp.LookAt(
                Subject: new WorldCameraSubject.Reference(),
                TargetOffset: new DocumentVector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
                WorldAxes: false
            ),
            new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 0.9599311f)),
            new WorldCameraProgramOp.Smooth(Rate: 6f),
        ]
    );

    private static WorldViewDefaults StandardViews { get; } = new(
        SeatRig: StandardSeatRig,
        SeatControl: new WorldSeatViewControl(
            MaxPitch: 1.2f,
            MinPitch: -0.35f,
            YawReference: WorldSeatYawReference.World
        ),
        Layouts: []
    );

    /// <summary>The seat slot <see cref="GradientUpContactLawTests"/> joins and repositions onto the ball's flank —
    /// slot 0 maps directly to body index 0 (the 0-based seat/body correspondence
    /// <see cref="EngageAuthorityLawTests"/> also relies on), and is the ONE spawn point
    /// <see cref="BuildGradientUpDocument"/> relocates.</summary>
    public const int GradientUpSeatSlot = 0;

    /// <summary><c>Puck.Forge.Authoring.CreationGeometry</c>'s own canonical <c>SdfSolidPrimitive.Sphere</c> local
    /// radius — that table's constant is private, so this mirrors its grepped value rather than referencing it, to
    /// size <see cref="BuildBallCreation"/>'s shape <c>Scale</c> against a known local unit. A change to the
    /// upstream table's value is exactly the kind of drift <see cref="BuildBallCreation"/>'s canonicalize-at-build
    /// hash pin cannot silently accept: only the RESULTING surface radius matters here, so a mismatch just resizes
    /// the ball, never breaks the fixture.</summary>
    private const float SphereLocalRadius = 1f;
    /// <summary>How far off vertical (as a fraction of <see cref="BallSurfaceRadius"/>) the flank point
    /// <see cref="GradientUpContactLawTests"/> grounds on sits — 0.9 puts the surface normal at
    /// <c>acos(sqrt(1-0.9^2))</c> ~= 64 degrees off world +Y, comfortably past the fixture's 60-degree
    /// <c>maxSlopeDegrees</c> (the brief's own sizing).</summary>
    private const float FlankHorizontalRatio = 0.9f;
    /// <summary>How far above the ball's surface, along the flank ray, the seat spawns — small enough that the
    /// FLAT-UP control arm's straight vertical fall still lands on the same steep face (proving the control's push
    /// is real contact, not a body that free-falls past the ball entirely) rather than missing the sphere outright.</summary>
    private const float FlankSpawnClearance = 0.3f;

    /// <summary>The unit direction from the ball's center to its flank contact point — <see cref="FlankHorizontalRatio"/>
    /// out along world X, the rest along +Y (already unit-length by construction: the two components are
    /// <c>sin</c>/<c>cos</c> of the same angle). Radial gravity under <see cref="WorldContactRequirement.GradientDerivedUp"/>
    /// falls exactly along this ray toward the origin (no tangential velocity is ever introduced — see
    /// <see cref="BuildGradientUpDocument"/>'s remarks), so a body spawned anywhere on it lands on the SAME flank
    /// point every time.</summary>
    private static Vector3 FlankDirection { get; } = new(x: FlankHorizontalRatio, y: MathF.Sqrt(x: (1f - (FlankHorizontalRatio * FlankHorizontalRatio))), z: 0f);
    /// <summary>The seat spawn position <see cref="GradientUpContactLawTests"/> relocates <c>seat-1</c> to — on the
    /// flank ray, <see cref="FlankSpawnClearance"/> world units above the ball's surface.</summary>
    private static Vector3 GradientUpSpawnPosition { get; } = (FlankDirection * (BallSurfaceRadius + FlankSpawnClearance));

    /// <summary>Builds the ONE code-built creation <see cref="GradientUpContactLawTests"/> needs: a single Sphere
    /// shape scaled so the placed ball's surface radius is exactly <see cref="BallSurfaceRadius"/>. The hash is
    /// COMPILER-MAINTAINED — computed through the same pipeline <c>WorldDefinitionValidator.ValidateCreations</c>
    /// re-derives and compares against, never hand-pinned (this suite's whole point).</summary>
    private static WorldCreation BuildBallCreation() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: (BallSurfaceRadius / SphereLocalRadius)),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "ball",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "ball");

        return new WorldCreation(Id: "ball", Document: canonical.Document, HashRaw: canonical.Hash);
    }

    /// <summary>Extends <see cref="BuildDocumentCore"/> with the ONE fixture <see cref="GradientUpContactLawTests"/>
    /// needs: <see cref="BuildBallCreation"/> placed at the origin with <c>solid.margin</c> 0, a capsule collider on
    /// the seat kit (the shipped shape: endpoint (0,1,0), radius 0.35 — no other law in this suite needs a
    /// collider, so <see cref="BuildDocument"/>'s own kit stays colliderless), and <c>seat-1</c>'s spawn point
    /// relocated to <see cref="GradientUpSpawnPosition"/>, on the ball's steep flank. <paramref name="gradientUp"/>
    /// selects the ONE discriminating fact — <see cref="WorldContactRequirement.GradientDerivedUp"/> alongside
    /// <see cref="WorldContactRequirement.SmoothUnionContact"/>, or the latter alone (the control arm) — the
    /// geometry, collider, and spawn position are byte-identical between the two calls.</summary>
    /// <param name="gradientUp">Whether the compiled collision authors <see cref="WorldContactRequirement.GradientDerivedUp"/>.</param>
    public static WorldDefinition BuildGradientUpDocument(bool gradientUp) {
        var creation = BuildBallCreation();
        var spawnPoints = BuildSpawnPoints();

        spawnPoints[0] = (spawnPoints[0] with { Position = GradientUpSpawnPosition });

        var requirements = (gradientUp
            ? new[] { WorldContactRequirement.SmoothUnionContact, WorldContactRequirement.GradientDerivedUp }
            : new[] { WorldContactRequirement.SmoothUnionContact });

        return BuildDocumentCore(
            spawnPoints: spawnPoints,
            collision: new WorldCollision(ContactSkin: 0.02f, GradientProbe: 0f, MaxIterations: 4, MaxSlopeDegrees: 60f, Requirements: requirements),
            seatCollider: new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f),
            creations: [creation],
            placements: [
                new WorldPlacement(Id: "ball", CreationId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f)),
            ]
        );
    }
    /// <summary>Extends <see cref="BuildDocument"/> with the inhabited <c>camera-seat-0</c> placement a
    /// camera-targeting <c>seatModes</c> state needs a body from — the coupling
    /// <c>WorldDefinitionValidator.ValidateSeatModes</c> refuses a document for missing.</summary>
    public static WorldDefinition BuildCameraBodyDocument() {
        var creation = BuildBallCreation();
        var document = BuildDocumentCore(
            spawnPoints: BuildSpawnPoints(),
            collision: new WorldCollision(ContactSkin: 0.02f, GradientProbe: 0f, MaxIterations: 4, MaxSlopeDegrees: 60f, Requirements: []),
            seatCollider: null,
            creations: [creation],
            placements: [
                new WorldPlacement(
                    Id: $"{WorldSeatModeState.CameraPlacementIdPrefix}0",
                    CreationId: creation.Id,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(
                        Kit: SeatKitName,
                        Look: null,
                        Source: IntentSource.Idle,
                        Distribution: WorldDistribution.Default
                    )
                ),
            ]
        );

        // One body of headroom past the seats: an inhabited placement draws from the census, and the shared core
        // pins capacity to the seat count.
        return (document with {
            PopulationRaw = (document.Population with { CapacityRaw = (WorldPopulationLimits.LocalSeatCount + 1) }),
        });
    }
    /// <summary>The code-built document's canonical UTF-8 bytes — <see cref="WorldDefinitionSerialization.Serialize"/>
    /// over <see cref="BuildDocument"/>, freshly built and serialized on every call (cheap, and it keeps a caller
    /// free to mutate its own copy without a shared-buffer hazard). This is also the round-trip proof: the fixture
    /// is only trustworthy if <c>Deserialize(Serialize(BuildDocument()))</c> both succeeds AND validates, which
    /// every consumer of this method exercises simply by using it (<see cref="FreshServer"/> deserializes these
    /// exact bytes back into a <see cref="WorldDefinition"/> and constructs a live <see cref="WorldServer"/> from
    /// the result).</summary>
    public static byte[] DefaultWorldBytes() => WorldDefinitionSerialization.Serialize(definition: BuildDocument());
    /// <summary>Serializes the code-built document, injects <c>bogusField: true</c> into the first row of
    /// <c>addons</c> (mirroring <c>docs/verification/strict-definition-parse</c>'s own choice of a
    /// <see cref="WorldAddonRow"/> as the target — the row the strict-parse gap was originally named against), and
    /// returns the re-serialized bytes. Starts from the canonical writer's own output
    /// (<see cref="DefaultWorldBytes"/>), so the ONLY thing that could make deserialization refuse is the injected
    /// member.</summary>
    public static byte[] SabotagedAddonBytes() {
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: DefaultWorldBytes()))!.AsObject();
        var addons = node["addons"]!.AsArray();

        addons[0]!.AsObject()["bogusField"] = true;

        return node.ToJsonBytes();
    }
    /// <summary>Serializes the code-built document and REMOVES <c>playerDefaults.seatLook</c>, returning the
    /// re-serialized bytes — proves the member parses absent and resolves through <see cref="WorldSeatLook.Default"/>.
    /// Starts from the canonical writer's own output (<see cref="DefaultWorldBytes"/>), so the removal is the only
    /// difference from a document known to parse clean.</summary>
    public static byte[] MissingSeatLookBytes() {
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: DefaultWorldBytes()))!.AsObject();

        _ = node["playerDefaults"]!.AsObject().Remove(propertyName: "seatLook");

        return node.ToJsonBytes();
    }
    /// <summary>Serializes the code-built document and REMOVES <c>host.presentation</c>, returning the re-serialized
    /// bytes. The member has no C# default, so the source-generated context requires it of a document — but a
    /// generated schema marking a member <c>required</c> proves nothing about the LOADER, which is exactly the
    /// disagreement this fixture exists to pin: before
    /// <see cref="Puck.World.WorldJsonContext"/> respected required constructor parameters, an absent
    /// <c>presentation</c> was silently filled with enum 0 and the document lost the argument. Starts from the
    /// canonical writer's own output (<see cref="DefaultWorldBytes"/>), so the removal is the only thing that could
    /// make it refuse.</summary>
    public static byte[] MissingHostPresentationBytes() {
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: DefaultWorldBytes()))!.AsObject();

        _ = node["host"]!.AsObject().Remove(propertyName: "presentation");

        return node.ToJsonBytes();
    }

    private static byte[] ToJsonBytes(this JsonNode node) => Encoding.UTF8.GetBytes(s: node.ToJsonString());

    /// <summary>Builds a FRESH, isolated, in-process <see cref="WorldServer"/> over <paramref name="definition"/>
    /// (<see cref="BuildDocument"/>'s own output when omitted) — the same construction shape
    /// <see cref="Puck.World.WorldReplaySnapshot.Drive"/> uses to rehydrate an authoritative world for offline
    /// replay verification (no GPU, no window, no client): a fresh <see cref="WorldPopulation"/>, an unconfigured
    /// <see cref="WorldRenderEnvelope"/> (reads as "fits" — no render-growing edit is exercised here), a
    /// <see cref="WorldMachineHost"/> with no registered engines (no screen ever boots a machine), and a
    /// scratch-directory <see cref="WorldOwnedWorlds"/> catalog seeded from the same document. Every caller —
    /// including one passing its own document — crosses the SAME serialize/deserialize round-trip
    /// <see cref="DefaultWorldBytes"/>'s own doc names as the fixture's trustworthiness proof. Callers own disposal
    /// via <see cref="WorldFixture.Dispose"/>.</summary>
    /// <param name="definition">The document to boot the server from, or <see langword="null"/> for <see cref="BuildDocument"/>.</param>
    public static WorldFixture FreshServer(WorldDefinition? definition = null) {
        var bytes = WorldDefinitionSerialization.Serialize(definition: (definition ?? BuildDocument()));

        definition = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-world-tests-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines);

        return new WorldFixture(machines: machines, server: server, stateDirectory: stateDirectory);
    }

    /// <summary>The tick duration every fixture step advances by — <see cref="EngineTicks.PerRate"/> at the fixed
    /// 240 Hz simulation rate, computed once and reused so every <see cref="WorldFixture.Step"/> call advances by
    /// the identical amount.</summary>
    public static ulong StepTicks { get; } = EngineTicks.PerRate(ratePerSecond: SimulationRateHz);

    /// <summary>Skips the calling law, by name, when <see cref="WorldReplayTape"/>'s REAL on-disk
    /// <c>Replays</c> directory (under <see cref="WorldStateRoot.Resolve"/> — this test project has no seam to
    /// redirect it, and <c>WorldStateRoot.Override</c> can only ever be applied ONCE per process, so no individual
    /// law may safely pull that lever) is not writable in the CURRENT environment. Adversarial-review G6's own
    /// finding: four replay laws failed in the reviewer's read-only sandbox not because the fix was wrong but
    /// because nothing distinguished "the environment cannot write here" from "the code is broken" — every law that
    /// calls <see cref="WorldReplayTape.StopRecording"/> (which persists unconditionally) should call this FIRST so
    /// a genuinely read-only environment reads as a skip with a named reason, never a red law.</summary>
    public static void SkipIfReplayDirectoryUnwritable() {
        string probePath;

        try {
            probePath = Path.Combine(path1: WorldReplayTape.Directory(), path2: $".write-probe-{Guid.NewGuid():N}");

            File.WriteAllBytes(bytes: [0], path: probePath);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Assert.Skip(reason: $"the Replays directory is not writable in this environment ({exception.GetType().Name}: {exception.Message}) — this law needs a real on-disk write to prove anything");

            return;
        }

        try {
            File.Delete(path: probePath);
        } catch (IOException) {
        }
    }
}
/// <summary>A fresh, disposable <see cref="WorldServer"/> plus the resources its construction owns
/// (<see cref="WorldMachineHost"/>, the scratch profile-catalog directory) — bundled so a law body drives the
/// server without having to know what else a fresh boot required.</summary>
internal sealed class WorldFixture : IDisposable {
    private readonly WorldMachineHost m_machines;
    private readonly string m_stateDirectory;

    private ulong m_tick;
    private ulong m_elapsedTicks;

    internal WorldFixture(WorldServer server, WorldMachineHost machines, string stateDirectory) {
        Server = server;
        m_machines = machines;
        m_stateDirectory = stateDirectory;
    }

    /// <summary>The live server under test.</summary>
    public WorldServer Server { get; }

    /// <summary>Drains one tick — enqueues nothing itself; a law body enqueues a mutation/grant/undo first, then
    /// calls this to reach the tick boundary where it applies (the mutation substrate's one pipeline: buffer,
    /// drain FIFO at the tick boundary, compose, revalidate, swap). Mirrors the composition root's own
    /// <c>WorldInstanceHost</c>'s per-instance tick counter (out of reach here — <c>Puck.World</c> is not
    /// referenced by this project).</summary>
    public void Step(ulong? stepTicks = null) {
        var width = (stepTicks ?? Fixtures.StepTicks);

        m_elapsedTicks = checked((m_elapsedTicks + width));
        var context = new FixedStepContext(ElapsedTicks: m_elapsedTicks, StepTicks: width, Tick: m_tick);

        Server.Step(context: in context);
        m_tick++;
    }
    /// <summary>The live document's current bytes — the byte-identity probe the all-or-nothing law compares
    /// before/after an apply attempt.</summary>
    public byte[] DefinitionBytes() => WorldDefinitionSerialization.Serialize(definition: Server.Definition);
    /// <inheritdoc/>
    public void Dispose() {
        m_machines.Dispose();

        try {
            Directory.Delete(path: m_stateDirectory, recursive: true);
        } catch (IOException) {
            // Best-effort scratch cleanup; a locked handle on a slow CI disk must never fail the test itself.
        }
    }
}
/// <summary>An <see cref="IWorldAddonHost"/> that mounts and pumps nothing — the addon-less shadow host tests
/// unrelated to the addon seam wire a <see cref="WorldReplayTape"/>'s required <c>addonHostFactory</c> parameter
/// with, since this project cannot reference <c>Puck.World.Addons</c>.</summary>
internal sealed class NullAddonHost : IWorldAddonHost {
    /// <inheritdoc/>
    public bool AnyEverPumped => false;
    /// <inheritdoc/>
    public int MountedCount => 0;
    /// <inheritdoc/>
    public IReadOnlyList<WorldAddonReceipt> Receipts => [];

    /// <inheritdoc/>
    public void ApplyContributions(ulong tick) { }
    /// <inheritdoc/>
    public void CompleteMutation(int addonIndex, ushort actOrdinal, bool applied) { }
    /// <inheritdoc/>
    public string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels) => null;
    /// <inheritdoc/>
    public void Dispose() { }
    /// <inheritdoc/>
    public string Mount(string name, string modulePath, string hash, ulong fuel, IReadOnlyList<WorldCapabilityRequest>? requests) => $"'{name}' refused — no addon host is mounted (NullAddonHost)";
    /// <inheritdoc/>
    public void TickAddons(ulong tick) { }
    /// <inheritdoc/>
    public string Unmount(string name) => $"'{name}' refused — no addon host is mounted (NullAddonHost)";
    /// <inheritdoc/>
    public void ResolveReads(ulong tick) { }
}
