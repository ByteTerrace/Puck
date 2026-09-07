// GENERATED FILE — do not hand-edit.
// Produced by scripts/generateWorldTypes.mjs from a puck.world.def.v1 schema bundle
// (`puck.exe schema --bundle <path>` produces the bundle; this script never fetches it itself).
// Regenerate: dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish
//             ./src/Puck.Cli/publish/puck.exe schema --bundle <bundle.json>
//             node scripts/generateWorldTypes.mjs --bundle <bundle.json>
// Source bundle: schemaVersion=puck.world.def.v1 generator=Puck.World.WorldSchema

/**
 * The definition of this world — the aggregate describing what the world is, distinct from the live session state that plays in it. It gathers named spawn points (SpawnPoints), motion defaults (Motion), and render-lever defaults and quality presets (Render). Every consumer takes it by construction.
 */
export type WorldDefinition = {
  motion?: WorldMotionDefaults | null;
  spawnPoints?: WorldSpawnPoint[] | null;
  render?: WorldRenderDefaults | null;
  screens?: (WorldScreen | null)[] | null;
  cameras?: (WorldCamera | null)[] | null;
  bodies?: WorldBodiesDefaults | null;
  seatDefaults?: WorldPlayerDefaults | null;
  channels?: (WorldChannel | null)[] | null;
  targetRegisters?: (WorldTargetRegister | null)[] | null;
  bodyMotionPrograms?: (BodyMotionProgram | null)[] | null;
  kits?: WorldKitsSection | null;
  defaultSeatKit?: string | null;
  addons?: (WorldAddonRow | null)[] | null;
  bindingOverlays?: (WorldBindingOverlay | null)[] | null;
  storage?: WorldStorageDefaults | null;
  prototypes?: (WorldPrototype | null)[] | null;
  placements?: WorldPlacementsSection | null;
  speakers?: (WorldSpeaker | null)[] | null;
  tunes?: (WorldTune | null)[] | null;
  patches?: (WorldPatch | null)[] | null;
  audio?: WorldAudioDefaults | null;
  collision?: WorldCollision | null;
  gravity?: WorldGravity | null;
  host?: WorldHostDefaults | null;
  views?: WorldViewDefaults | null;
  looks?: WorldLooksSection | null;
  dynamics?: (DynamicsRow | null)[] | null;
  grants?: WorldGrant[] | null;
  hud?: WorldHudSection | null;
  icons?: WorldIconographySection | null;
  state?: WorldStateSection | null;
  inputHold?: WorldInputHoldAuthoring | null;
  theme?: WorldThemeSection | null;
  markers?: (WorldMarkerRow | null)[] | null;
  rules?: (WorldRule | null)[] | null;
  identity?: WorldIdentityDefinition | null;
  groups?: WorldGroupsSection | null;
  properties?: WorldPropertyRegistrySection | null;
  interactions?: WorldInteractionsSection | null;
  generation?: WorldGenerationDefaults | null;
  generators?: (GeneratorRow | null)[] | null;
  references?: (WorldReference | null)[] | null;
  portals?: WorldPortalsSection | null;
  simulation?: WorldSimulationDefaults | null;
  destinations?: (WorldDestination | null)[] | null;
  admission?: (WorldAdmissionEntry | null)[] | null;
  adjacencies?: (WorldAdjacency | null)[] | null;
  text?: TextFontCatalogDefinition | null;
  metadata?: WorldMetadataSection | null;
  update?: WorldUpdateDefaults | null;
  music?: (WorldMusicRow | null)[] | null;
  seatModes?: (WorldSeatModeFamily | null)[] | null;
  probes?: (WorldProbe | null)[] | null;
  captures?: WorldCapturesSection | null;
  curves?: (WorldCurveRow | null)[] | null;
  navigation?: WorldNavigationSection | null;
  patterns?: (PatternRow | null)[] | null;
  tables?: (TableRow | null)[] | null;
  search?: WorldSearchSection | null;
  /**
   * Gets the basis document this file layers over, as a file path resolved against this document's own directory — the document-composition member (see WorldDocumentBasis). A file naming a basis is a delta: it authors only what differs, inheriting every omitted member from the (recursively composed) basis chain. Unrelated to the coordinate basis the validator's geometry speaks of.
   */
  basis?: string | null;
  /**
   * Gets the ordered fragment documents this file imports — each a WorldImport naming a file path resolved against this document's own directory and an optional alias its names compose under — the fan-in half of composition beside Basis's single-parent chain (see WorldDocumentBasis). Composition order is the basis chain, then each import in list order, then this file's own body.
   */
  imports?: (WorldImport | null)[] | null;
  /**
   * Gets the surface this document offers a host that imports it (WorldExports): the names the host may read, drive, and bind to. Every other name the document declares is private to it. Consumed where the document is imported (WorldModuleExports), so a live document always carries null here and the validator refuses anything else.
   */
  exports?: WorldExports | null;
  /**
   * Gets the stable document id used when this world submits to another document.
   */
  documentId?: string | null;
  /**
   * Gets the document schema tag — SchemaVersion for a well-formed document.
   */
  schema?: "puck.world.def.v1";
  /**
   * This interface was referenced by `undefined`'s JSON-Schema definition
   * via the `patternProperty` "^[$_]".
   */
  [k: string]: unknown;
} | null;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DocumentVector3".
 */
export type DocumentVector3 = [number, number, number] | string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ShadowTier".
 */
export type ShadowTier = "Off" | "Low" | "Medium" | "High";
/**
 * The SAFE, enumerated world render-scale tiers a run pins for the settled REVEALED room view — the demo/user-facing quality option layered over the engine's CONTINUOUS render-scale knob (SdfViewSnapshot.RenderScale, quantized to SdfWorldEngine.RenderScaleQ). The player picks one of these KNOWN-GOOD steps, never a free numeric value; each tier is pinned to an exact quantized numerator so the reduced render extent (worldRenderDims) is a stable integer at any window size and the bilinear upsample stays artifact-free. The continuous knob stays reachable PROGRAMMATICALLY (layout eases, dev tooling) — the enumerated policy lives only here at the user surface. Names + scales are the ONE definition (WorldRenderScaleTiers); the world document's quality presets, the console world.render-scale verb, and the boot resolution all read them there.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderScaleTier".
 */
export type WorldRenderScaleTier = "Native" | "ThreeQuarter" | "Half" | "Quarter" | "Eighth";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderExtensionEntry".
 */
export type WorldRenderExtensionEntry = WorldRenderExtensionEntry1;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DocumentVector2".
 */
export type DocumentVector2 = [number, number] | string;
/**
 * The signal carried by a WorldScreen's lit face. A source declares which provider feeds a slot; the engine resolves and samples it. The $type string is the JSON discriminator; a new source kind is a new derived record plus its JsonDerivedTypeAttribute line.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSource".
 */
export type WorldScreenSource =
  | WorldScreenSourceNone
  | WorldScreenSourceTestPattern
  | WorldScreenSourceMachine
  | WorldScreenSourceCamera
  | WorldScreenSourceView
  | WorldScreenSourceCapture
  | WorldScreenSourceConsole
  | WorldScreenSourceQr
  | WorldScreenSourceSession
  | WorldScreenSourceText
  | WorldScreenSourceProbe;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSensor".
 */
export type WorldCameraSensor = "Color" | "Infrared";
/**
 * How a Session face's destination render projects onto the face — an ordinary head-on camera image, or a WINDOW whose image shears with the viewer's own eye so the destination scene parallaxes against the aperture the way a real opening would.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenProjection".
 */
export type WorldScreenProjection = "Camera" | "Window";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StringList".
 */
export type StringList = (string | null)[];
/**
 * WHERE a placeable thing rides — the one shared pose-target vocabulary a placeable WorldCamera and a placeable WorldSpeaker both consume through the SAME resolver, distinct from HOW the thing looks at or emits from that pose (a WorldCameraProgram, a feed). The $type string is the JSON discriminator; a new anchor kind is a new derived record plus its JsonDerivedTypeAttribute line.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchor".
 */
export type WorldAnchor =
  | WorldAnchorEntity
  | WorldAnchorEntityPart
  | WorldAnchorPlacement
  | WorldAnchorGroup
  | WorldAnchorSeat
  | WorldAnchorRecentSpeaker;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "Int32List".
 */
export type Int32List = number[];
/**
 * One instruction in an authored camera program's ordered op list — the presentation-side pose algebra bodyMotionPrograms established for sim-side movement, promoted to cameras: an authored rig is an ordered list of these trivial ops rather than a bespoke closed motion/aim union, so a new camera behavior is authored data, never new engine code. This is the AUTHORING vocabulary only: Puck.World.Client.WorldCameraRigCompiler translates it to Puck.SdfVm.Views.SdfCameraOp and resolves each frame's bindings and subject poses, and Puck.SdfVm.Views.SdfCameraProgramEvaluator — which parses no document — walks the result. Floats throughout; presentation carries no fixed-point burden.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOp".
 */
export type WorldCameraProgramOp =
  | WorldCameraProgramOpAnchor
  | WorldCameraProgramOpOffset
  | WorldCameraProgramOpLookAt
  | WorldCameraProgramOpOrbit
  | WorldCameraProgramOpPath
  | WorldCameraProgramOpDynamics
  | WorldCameraProgramOpClampPitch
  | WorldCameraProgramOpFov
  | WorldCameraProgramOpBlend
  | WorldCameraProgramOpSelect;
/**
 * The subject an Anchor/LookAt op resolves against — the closed "what a camera program can key off" vocabulary. Distinct from WorldAnchor (WHERE a whole placeable camera or speaker rides, resolved OUTSIDE the program and handed in as one reference pose): this is presentation math INSIDE the program, so it stays float and needs no live entity table — a placement's pose resolves through the same static stamped-transform math a placeable camera's own anchor and a speaker read (Puck.World.Client.WorldAnchorGeometry).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSubject".
 */
export type WorldCameraSubject =
  WorldCameraSubjectReference | WorldCameraSubjectPlacement | WorldCameraSubjectWorldPoint;
/**
 * The subject an Anchor/LookAt op resolves against — the closed "what a camera program can key off" vocabulary. Distinct from WorldAnchor (WHERE a whole placeable camera or speaker rides, resolved OUTSIDE the program and handed in as one reference pose): this is presentation math INSIDE the program, so it stays float and needs no live entity table — a placement's pose resolves through the same static stamped-transform math a placeable camera's own anchor and a speaker read (Puck.World.Client.WorldAnchorGeometry).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSubjectNullable".
 */
export type WorldCameraSubjectNullable =
  WorldCameraSubjectReference | WorldCameraSubjectPlacement | WorldCameraSubjectWorldPoint;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindableScalar".
 */
export type BindableScalar = number | string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSelectCaseList".
 */
export type WorldCameraSelectCaseList = WorldCameraSelectCase[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpList".
 */
export type WorldCameraProgramOpList = WorldCameraProgramOp[];
/**
 * An overlay element's visibility condition — the presentation twin of ActionPredicate, over OverlayFacts. Absent on an element means always visible.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateNullable".
 */
export type OverlayPredicateNullable =
  | OverlayPredicateNow
  | OverlayPredicateRecently
  | OverlayPredicateAll
  | OverlayPredicateAny
  | OverlayPredicateNot
  | OverlayPredicateSpeaking
  | OverlayPredicateNear
  | OverlayPredicateState;
/**
 * A presentation fact an overlay element's OverlayPredicate reads. Every fact is per local seat and never enters the simulation; a world-scope element (a hud.panels row) reads a fact as true when it holds for any joined local seat.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayFact".
 */
export type OverlayFact = "SeatInput" | "PointerMotion" | "WheelOpen" | "ConsoleOpen" | "SeatCameraApplication";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateListNonNullable".
 */
export type OverlayPredicateListNonNullable = OverlayPredicateNullable[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateList".
 */
export type OverlayPredicateList = OverlayPredicateNullable[];
/**
 * An overlay element's visibility condition — the presentation twin of ActionPredicate, over OverlayFacts. Absent on an element means always visible.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicate".
 */
export type OverlayPredicate =
  | OverlayPredicateNow
  | OverlayPredicateRecently
  | OverlayPredicateAll
  | OverlayPredicateAny
  | OverlayPredicateNot
  | OverlayPredicateSpeaking
  | OverlayPredicateNear
  | OverlayPredicateState;
/**
 * Who a subject-bearing presentation predicate is about — a seat's avatar, a placement, an entity, or a quantifier over seats or speakers. Presentation-only: a subject resolves to a body through the seat's perceived body (so possession follows) and never enters the simulation.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubject".
 */
export type OverlaySubject =
  | OverlaySubjectSeat
  | OverlaySubjectPlacement
  | OverlaySubjectEntity
  | OverlaySubjectAnySeat
  | OverlaySubjectRecentSpeaker;
/**
 * Who a subject-bearing presentation predicate is about — a seat's avatar, a placement, an entity, or a quantifier over seats or speakers. Presentation-only: a subject resolves to a body through the seat's perceived body (so possession follows) and never enters the simulation.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubjectNullable".
 */
export type OverlaySubjectNullable =
  | OverlaySubjectSeat
  | OverlaySubjectPlacement
  | OverlaySubjectEntity
  | OverlaySubjectAnySeat
  | OverlaySubjectRecentSpeaker;
/**
 * A fixed comparison admitted by a compiled state predicate.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionStateComparison".
 */
export type ActionStateComparison = "Equal" | "NotEqual" | "Less" | "LessOrEqual" | "Greater" | "GreaterOrEqual";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "SeatActivationPolicy".
 */
export type SeatActivationPolicy = "Eager" | "OnDemand";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "IntentSource".
 */
export type IntentSource = ("Live" | "Idle") | Shape;
/**
 * A closed spatial region vocabulary consumed by WorldDistribution.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistributionRegion".
 */
export type WorldDistributionRegion =
  | WorldDistributionRegionDisc
  | WorldDistributionRegionPoints
  | WorldDistributionRegionLattice
  | WorldDistributionRegionNoise
  | WorldDistributionRegionScatter;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldObserverDisclosureMode".
 */
export type WorldObserverDisclosureMode = "All" | "Radius" | "SelfOnly";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "SafeName".
 */
export type SafeName = string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ChannelShape".
 */
export type ChannelShape = "Bipolar" | "Unipolar" | "Binary";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ChannelRole".
 */
export type ChannelRole =
  | "MoveAdvance"
  | "MoveStrafe"
  | "Turn"
  | "MoveUp"
  | "Pitch"
  | "Roll"
  | "FaceX"
  | "FaceY"
  | "FaceZ"
  | "MoveX"
  | "MoveY"
  | "MoveZ";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ChannelFrame".
 */
export type ChannelFrame = "World" | "Camera" | "Heading";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyProgramKind".
 */
export type BodyProgramKind = "Motion" | "Producer";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyMotionOp".
 */
export type BodyMotionOp =
  | "SenseNearestInCone"
  | "ProduceSteeringIntent"
  | "FaceSensorTarget"
  | "ProduceFlockIntent"
  | "ResolveYawAttitudeAndPlanarFrame"
  | "IntegrateLocalAttitude"
  | "ComputePlanarTargetVelocity"
  | "ComputeLocalTargetVelocity"
  | "ShapeVelocity"
  | "SnapYawToPlanarIntent"
  | "ResolveDriveFrame"
  | "ResolveHold"
  | "RunActionTriggers"
  | "ApplyHold"
  | "IntegratePlanarAndVerticalVelocity"
  | "IntegrateScratchVelocity"
  | "CommitPose"
  | "SetVerticalVelocity"
  | "ScaleVerticalVelocity"
  | "PlanarImpulse"
  | "SetState"
  | "AddState"
  | "StartTimer"
  | "Designate"
  | "Generate";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyTargetSource".
 */
export type BodyTargetSource =
  BodyTargetSourceSensed | BodyTargetSourceDesignated | BodyTargetSourceCurveFollow | BodyTargetSourceNavigated;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyTargetScope".
 */
export type BodyTargetScope = "Seats" | "Bodies";
/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through a RuleVocabulary (see ExtendJson) rather than by editing this list.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateNullable2".
 */
export type ActionPredicateNullable2 =
  | ActionPredicateCompareState
  | ActionPredicateCompareValue
  | ActionPredicateAll
  | ActionPredicateAny
  | ActionPredicateNot
  | WorldPredicateNow
  | WorldPredicateRecently
  | WorldPredicateTimerElapsed
  | WorldPredicateHeld;
/**
 * A bounded postfix numeric expression evaluated by a world rule, decision, or flock affinity. Each token either pushes a value or consumes preceding values; the compiler proves stack shape and numeric kind before simulation begins. Authored either as an infix string (ExpressionSpelling, "min(damage, hp) * 2") or as the postfix { "tokens": [...] } object; both parse to the same tokens and each writes back in its own spelling.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueExpression".
 */
export type ValueExpression = string | ValueExpressionTokens;
/**
 * One authored token in a ValueExpression.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueToken".
 */
export type ValueToken =
  | ValueTokenConstant
  | ValueTokenState
  | ValueTokenAdd
  | ValueTokenSubtract
  | ValueTokenMultiply
  | ValueTokenDivide
  | ValueTokenMin
  | ValueTokenMax
  | ValueTokenClamp
  | ValueTokenModulo
  | ValueTokenBitAnd
  | ValueTokenBitOr
  | ValueTokenBitXor
  | ValueTokenBitNot
  | ValueTokenShiftLeft
  | ValueTokenShiftRight
  | ValueTokenShiftRightLogical
  | ValueTokenEqual
  | ValueTokenNotEqual
  | ValueTokenLess
  | ValueTokenLessOrEqual
  | ValueTokenGreater
  | ValueTokenGreaterOrEqual
  | ValueTokenSelect
  | ValueTokenPopCount
  | ValueTokenLeadingZeroCount
  | ValueTokenTrailingZeroCount
  | ValueTokenLowestSetBit
  | ValueTokenClearLowestSetBit
  | ValueTokenRotateLeft
  | ValueTokenRotateRight
  | ValueTokenByteSwap
  | ValueTokenBitReverse
  | ValueTokenReplicationMask
  | ValueTokenRepeatBits
  | ValueTokenNegate
  | ValueTokenAbs
  | ValueTokenSign
  | ValueTokenParallelBitExtract
  | ValueTokenParallelBitDeposit
  | ValueTokenBitField
  | ValueTokenBitInsert
  | ValueTokenBoardShift
  | ValueTokenBoardFill
  | ValueTokenBoardImage
  | ValueTokenPair
  | ValueTokenPairX
  | ValueTokenPairY
  | ValueTokenPairSwap
  | ValueTokenPairMax
  | ValueTokenPairMin
  | ValueTokenPairSum
  | ValueTokenPairDifference
  | ValueTokenPairTranslate
  | ValueTokenPairScale
  | ValueTokenMorton
  | ValueTokenMortonX
  | ValueTokenMortonY
  | ValueTokenHilbert
  | ValueTokenHilbertX
  | ValueTokenHilbertY
  | ValueTokenHexIndex
  | ValueTokenHexQ
  | ValueTokenHexR
  | ValueTokenHexRadius
  | ValueTokenHexEuclideanSquared
  | ValueTokenHexDistance
  | ValueTokenHexNeighbor
  | ValueTokenHexRotate
  | ValueTokenHexMirror
  | ValueTokenHexSwap
  | ValueTokenHexAdd
  | ValueTokenHexSubtract
  | ValueTokenHexMultiply
  | ValueTokenHexScale
  | ValueTokenHexTranslate
  | ValueTokenLayer
  | ValueTokenLayerOffset
  | ValueTokenLayerStart
  | ValueTokenLayerSize
  | ValueTokenSquareRoot
  | ValueTokenSine
  | ValueTokenCosine
  | ValueTokenSquareIndex
  | ValueTokenSquareX
  | ValueTokenSquareY
  | ValueTokenSquareRadius
  | ValueTokenSquareLength
  | ValueTokenSquareEuclideanSquared
  | ValueTokenSquareDistance
  | ValueTokenSquareChebyshev
  | ValueTokenSquareNeighbor
  | ValueTokenSquareRotate
  | ValueTokenSquareMirror
  | ValueTokenSquareSwap
  | ValueTokenSquareAdd
  | ValueTokenSquareSubtract
  | ValueTokenSquareMultiply
  | ValueTokenSquareScale
  | ValueTokenSquareTranslate
  | ValueTokenGreatestCommonDivisor
  | ValueTokenLeastCommonMultiple
  | ValueTokenFloorModulo
  | ValueTokenCycleForward
  | ValueTokenCycleDistance
  | ValueTokenSmallestMissing
  | ValueTokenIsPrime
  | ValueTokenPrime
  | ValueTokenChoose
  | ValueTokenFactorial
  | ValueTokenSubsetRank
  | ValueTokenSubsetAt
  | ValueTokenSubsetMember
  | ValueTokenArrangementRank
  | ValueTokenArrangementAt
  | ValueTokenArrangementMember;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "CellKind".
 */
export type CellKind = "Int" | "Fixed" | "Bool" | "Text";
/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through a RuleVocabulary (see ExtendJson) rather than by editing this list.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateNullable".
 */
export type ActionPredicateNullable =
  | ActionPredicateCompareState
  | ActionPredicateCompareValue
  | ActionPredicateAll
  | ActionPredicateAny
  | ActionPredicateNotNonNullable
  | WorldPredicateNow
  | WorldPredicateRecently
  | WorldPredicateTimerElapsed
  | WorldPredicateHeld;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateList".
 */
export type ActionPredicateList = ActionPredicateNullable[];
/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through a RuleVocabulary (see ExtendJson) rather than by editing this list.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicate".
 */
export type ActionPredicate =
  | ActionPredicateCompareState
  | ActionPredicateCompareValue
  | ActionPredicateAll
  | ActionPredicateAnyNonNullable
  | ActionPredicateNot
  | WorldPredicateNow
  | WorldPredicateRecently
  | WorldPredicateTimerElapsed
  | WorldPredicateHeld;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionFact".
 */
export type ActionFact =
  | "Grounded"
  | "Airborne"
  | "Rising"
  | "Falling"
  | "AffectedBy"
  | "InMedium"
  | "AtMediumBand"
  | "HoldingUnwalkable"
  | "Unsupported"
  | "Resting";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateListNonNullable".
 */
export type ActionPredicateListNonNullable = ActionPredicateNullable[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "MotionMoveFrame".
 */
export type MotionMoveFrame = "Heading" | "World";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyHoldBond".
 */
export type BodyHoldBond = "Surface" | "Free" | "Medium";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyHoldKind".
 */
export type BodyHoldKind = "None" | "Gravity" | "Pull" | "Lift";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyHoldForward".
 */
export type BodyHoldForward = "Heading" | "Intent" | "Velocity";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFlockSpace".
 */
export type WorldFlockSpace = "Tangent" | "Volume";
/**
 * An authored effect row a rule fires when its gate holds. The arms declared here are the ones this library owns — every one addresses a state row and nothing else; a document project appends its own derived arms (a participant's kinematics, a presentation cue, a whole-row document upsert) through a RuleVocabulary (see ExtendJson) rather than by editing this list.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffect".
 */
export type ActionEffect =
  | ActionEffectSetState
  | ActionEffectAddState
  | ActionEffectPushState
  | ActionEffectTransformState
  | ActionEffectCountdownState
  | ActionEffectGenerate
  | ActionEffectRemoveStateCell
  | ActionEffectScheduleState
  | ActionEffectTransaction
  | WorldEffectSetVerticalVelocity
  | WorldEffectScaleVerticalVelocity
  | WorldEffectPlanarImpulse
  | WorldEffectStartTimer
  | WorldEffectDesignate
  | WorldEffectEmitCue
  | WorldEffectSetBodyVerticalVelocity
  | WorldEffectScaleBodyVerticalVelocity
  | WorldEffectApplyBodyImpulse
  | WorldEffectDesignateBody
  | WorldEffectPaintField
  | WorldEffectUpsertHudPanel
  | WorldEffectRemoveHudPanel
  | WorldEffectUpsertPlacement
  | WorldEffectRemovePlacement
  | WorldEffectSave
  | WorldEffectPose
  | WorldEffectSetIdentityFact;
/**
 * The participant an action effect addresses. A document-scope rule has no participant to select, so only Self is admitted there; the other members belong to a host's per-participant action programs, which share the SetState/AddState shapes with the rule compiler and therefore carry the member on the wire.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionTarget".
 */
export type ActionTarget = "Self" | "ProducerTarget" | "AffectingSubject";
/**
 * The closed set of atomic state transforms. Each folds one candidate document and journals once.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransform".
 */
export type StateTransform =
  | StateTransformTransfer
  | StateTransformSetRay
  | StateTransformShuffle
  | StateTransformSortZone
  | StateTransformSortKeyed
  | StateTransformWriteSet
  | StateTransformBoardCombine
  | StateTransformArrange
  | StateTransformPush
  | StateTransformClearEnclosed
  | StateTransformObserve;
/**
 * Selection of a single token from a zone.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ZoneSelector".
 */
export type ZoneSelector = "Key" | "First" | "Last" | "Random" | "Slice";
/**
 * What BoardCombine writes into its board, cell by cell. A cell is a member of a board when its value is not the board's declared empty.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BoardCombineOp".
 */
export type BoardCombineOp = "Copy" | "Fill" | "Clear" | "And" | "Or" | "Xor" | "AndNot" | "Not" | "Shift" | "Image";
/**
 * How a rule-triggered body designation chooses its target.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBodyDesignationKind".
 */
export type WorldBodyDesignationKind = "Body" | "Clear";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFieldWriteOp".
 */
export type WorldFieldWriteOp = "Set" | "Add";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudLayer".
 */
export type WorldHudLayer = "Under" | "Over" | "Replace";
/**
 * A WorldHudPanel's chrome recipe — the authored twin of Puck.Overlays.OverlayPanelStyle (Puck.World.Schema must not reference Puck.Overlays; the renderer maps this token to the concrete style).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudPanelStyle".
 */
export type WorldHudPanelStyle = "Panel" | "Strip" | "Chip";
/**
 * A WorldHudElement's rendered kind — the schema→render expansion cost differs per kind (see WorldHudCapacity).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudElementKind".
 */
export type WorldHudElementKind = "Rect" | "Text" | "Gauge" | "Frame";
/**
 * A WorldHudElement's color role — a curated authored subset of Puck.Overlays.OverlayColorRole (Puck.World.Schema must not reference Puck.Overlays; the renderer maps this token to the concrete role).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudStyleToken".
 */
export type WorldHudStyleToken = "Primary" | "Dim" | "Accent" | "Positive" | "Warning" | "Danger";
/**
 * The WorldScreenSource arms that produce a sampled frame — Camera, View, Probe, and Capture — carrying its own JsonPolymorphicAttribute over the SAME four discriminators (camera/view/probe/capture) their parent union uses, so a member declared as this narrower type round-trips the identical JSON a full WorldScreenSource screen row does. A WorldProbe socket is the driving consumer: it plugs one of these into each named input rather than the wider union, which would legally admit a decal/machine/console/qr/text/none source no probe kernel can sample.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFrameSource".
 */
export type WorldFrameSource =
  WorldScreenSourceCamera | WorldScreenSourceView | WorldScreenSourceProbe | WorldScreenSourceCapture;
/**
 * How a Frame element maps its sampled frame's aspect ratio onto its own rect — the same uv-mapping choice a screen material or a UI image element makes.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudFrameFit".
 */
export type WorldHudFrameFit = "Cover" | "Contain" | "Stretch";
/**
 * The WorldScreenSource arms that produce a sampled frame — Camera, View, Probe, and Capture — carrying its own JsonPolymorphicAttribute over the SAME four discriminators (camera/view/probe/capture) their parent union uses, so a member declared as this narrower type round-trips the identical JSON a full WorldScreenSource screen row does. A WorldProbe socket is the driving consumer: it plugs one of these into each named input rather than the wider union, which would legally admit a decal/machine/console/qr/text/none source no probe kernel can sample.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFrameSourceNonNullable".
 */
export type WorldFrameSourceNonNullable =
  WorldScreenSourceCamera | WorldScreenSourceView | WorldScreenSourceProbe | WorldScreenSourceCapture;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudElementList".
 */
export type WorldHudElementList = WorldHudElement[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPortalTravel".
 */
export type WorldPortalTravel = "party" | "body";
/**
 * Where a traveler lands at a WorldPlacementPortal's destination — the positional-continuity decision a portal facet authors on top of which destination it names.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPortalArrival".
 */
export type WorldPortalArrival = "spawn" | "mapped";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementFaceList".
 */
export type WorldPlacementFaceList = WorldPlacementFace[];
/**
 * How long a filled WorldPlacementContribution slot keeps the piece a federation partner put in it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldContributionTenure".
 */
export type WorldContributionTenure = "Presence" | "Endowed";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPrincipal".
 */
export type WorldPrincipal = string;
/**
 * The closed union a response entry's When speaks. Both arms evaluate against the SAME per-tick document the rule frame folds a rule's writes into before this sweep runs (Server.WorldServer.SweepPlacementResponses runs after EvaluateWorldRules' own end-of-tick fold), so a cell a rule wrote this tick already reads through StateCondition on the same tick's sweep.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementResponseCondition".
 */
export type WorldPlacementResponseCondition =
  WorldPlacementResponseConditionFieldCondition | WorldPlacementResponseConditionStateCondition;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementResponseList".
 */
export type WorldPlacementResponseList = WorldPlacementResponse[];
/**
 * How a tabletop board binding treats an illegal move — the author's choice per table, engine-side and game-agnostic: the judge that computes Verdict is authored per world, and this field only decides what the engine itself does once that verdict refuses.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBoardEnforcement".
 */
export type WorldBoardEnforcement = "Record" | "Return";
/**
 * The contract a named placement-local spatial volume contributes to queries.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementSpatialRole".
 */
export type WorldPlacementSpatialRole = "Occupation" | "Clearance" | "Influence";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpatialShapeKind".
 */
export type WorldSpatialShapeKind = "Box" | "Sphere";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementSpatialVolumeList".
 */
export type WorldPlacementSpatialVolumeList = WorldPlacementSpatialVolume[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectList".
 */
export type ActionEffectList = ActionEffect[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionTriggerMode".
 */
export type ActionTriggerMode = "Level" | "Edge";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCollider".
 */
export type WorldCollider = WorldColliderSphere | WorldColliderCapsule | WorldColliderBox | WorldColliderFromCreation;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DocumentQuaternion".
 */
export type DocumentQuaternion = [number, number, number, number] | string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBodyContactMode".
 */
export type WorldBodyContactMode = "Overlap" | "Solid";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPadElement".
 */
export type WorldPadElement =
  | "LeftStickX"
  | "LeftStickY"
  | "RightStickX"
  | "RightStickY"
  | "LeftTrigger"
  | "RightTrigger"
  | "South"
  | "East"
  | "West"
  | "North"
  | "DpadUp"
  | "DpadDown"
  | "DpadLeft"
  | "DpadRight"
  | "LeftShoulder"
  | "RightShoulder"
  | "Start"
  | "Back";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DocumentIdentifier".
 */
export type DocumentIdentifier = string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DocumentIdentifierList".
 */
export type DocumentIdentifierList = DocumentIdentifier[];
/**
 * The coarse capability verbs a WorldGrant confers — the closed set the server checks a submission's WorldPrincipal against at each write boundary. A genre world arrives as different data (new subjects, new sections), never a new capability.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCapability".
 */
export type WorldCapability = "Drive" | "Observe" | "Control" | "Mutate" | "Edit";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GrantSubject".
 */
export type GrantSubject = string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "CommandPhase".
 */
export type CommandPhase = "Started" | "Active" | "Completed" | "Canceled";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingActivatorMode".
 */
export type BindingActivatorMode = "Held" | "Tapped";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingEntryMode".
 */
export type BindingEntryMode = "Hold" | "Toggle";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingPageEntryDefinitionList".
 */
export type BindingPageEntryDefinitionList = BindingPageEntryDefinition[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingWheelSpatialSelectionMode".
 */
export type BindingWheelSpatialSelectionMode = "Disabled" | "Angle" | "HitTarget";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingWheelPlacement".
 */
export type BindingWheelPlacement = "Pointer" | "ViewportCenter";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingWheelRingSelectionMode".
 */
export type BindingWheelRingSelectionMode = "Explicit" | "Excursion";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingBarEdge".
 */
export type BindingBarEdge = "Bottom" | "Top" | "Left" | "Right";
/**
 * The canonical (validated + normalized) creation document.
 */
export type CreationDocument = {
  schema?: string | null;
  name?: unknown;
  palette?:
    | ({
        color: string;
        emissive?: number | null;
        specular?: number | null;
        shininess?: number | null;
      } | null)[]
    | null;
  shapes?:
    | ({
        id: number;
        name?: unknown;
        type: "Sphere" | "Box" | "Torus" | "Cylinder" | "Capsule" | "Ellipsoid" | "RoundCone" | "Plane" | "Cone";
        position: unknown;
        rotation: unknown;
        scale: unknown;
        material?: number | null;
        blend?:
          | "Union"
          | "SmoothUnion"
          | "Subtraction"
          | "Intersection"
          | "Xor"
          | "SmoothIntersection"
          | "SmoothSubtraction"
          | "ChamferUnion"
          | "ChamferIntersection"
          | "ChamferSubtraction"
          | null;
        smooth?: number | null;
        group?: number | null;
        twist?: number | null;
        onion?: number | null;
        bend?: number | null;
        dilate?: number | null;
        domain?:
          | ((
              | {
                  $type?: "symmetry";
                  normal: unknown;
                  offset?: number | null;
                }
              | {
                  $type?: "repeat";
                  spacing: unknown;
                  limit?: {
                    [k: string]: unknown;
                  };
                }
              | {
                  $type?: "polar";
                  count: number;
                  axis?: "X" | "Y" | "Z" | null;
                  mirror?: boolean | null;
                  materialStride?: number | null;
                }
              | {
                  $type?: "wallpaper";
                  group:
                    | "P1"
                    | "P2"
                    | "Pm"
                    | "Pg"
                    | "Cm"
                    | "Pmm"
                    | "Pmg"
                    | "Pgg"
                    | "Cmm"
                    | "P4"
                    | "P4M"
                    | "P4G"
                    | "P3"
                    | "P3M1"
                    | "P31M"
                    | "P6"
                    | "P6M";
                  cell: unknown;
                  limit?: {
                    [k: string]: unknown;
                  };
                  plane?: "XZ" | "XY" | "YZ" | null;
                  materialStride?: number | null;
                  lodDistance?: number | null;
                }
            ) &
              (
                | (
                    | {
                        $type?: "symmetry";
                        normal: unknown;
                        offset?: number | null;
                      }
                    | {
                        $type?: "repeat";
                        spacing: unknown;
                        limit?: {
                          [k: string]: unknown;
                        };
                      }
                    | {
                        $type?: "polar";
                        count: number;
                        axis?: "X" | "Y" | "Z" | null;
                        mirror?: boolean | null;
                        materialStride?: number | null;
                      }
                    | {
                        $type?: "wallpaper";
                        group:
                          | "P1"
                          | "P2"
                          | "Pm"
                          | "Pg"
                          | "Cm"
                          | "Pmm"
                          | "Pmg"
                          | "Pgg"
                          | "Cmm"
                          | "P4"
                          | "P4M"
                          | "P4G"
                          | "P3"
                          | "P3M1"
                          | "P31M"
                          | "P6"
                          | "P6M";
                        cell: unknown;
                        limit?: {
                          [k: string]: unknown;
                        };
                        plane?: "XZ" | "XY" | "YZ" | null;
                        materialStride?: number | null;
                        lodDistance?: number | null;
                      }
                  )
                | ((
                    | {
                        $type?: "symmetry";
                        normal: unknown;
                        offset?: number | null;
                      }
                    | {
                        $type?: "repeat";
                        spacing: unknown;
                        limit?: {
                          [k: string]: unknown;
                        };
                      }
                    | {
                        $type?: "polar";
                        count: number;
                        axis?: "X" | "Y" | "Z" | null;
                        mirror?: boolean | null;
                        materialStride?: number | null;
                      }
                    | {
                        $type?: "wallpaper";
                        group:
                          | "P1"
                          | "P2"
                          | "Pm"
                          | "Pg"
                          | "Cm"
                          | "Pmm"
                          | "Pmg"
                          | "Pgg"
                          | "Cmm"
                          | "P4"
                          | "P4M"
                          | "P4G"
                          | "P3"
                          | "P3M1"
                          | "P31M"
                          | "P6"
                          | "P6M";
                        cell: unknown;
                        limit?: {
                          [k: string]: unknown;
                        };
                        plane?: "XZ" | "XY" | "YZ" | null;
                        materialStride?: number | null;
                        lodDistance?: number | null;
                      }
                  ) &
                    null)
              ))[]
          | null;
        swings?:
          | ({
              driver: string;
              pivot: unknown;
              axis: unknown;
              amplitude: unknown;
              phase?: {
                [k: string]: unknown;
              };
              wave?: string | null;
            } | null)[]
          | null;
        slides?:
          | ({
              driver: string;
              axis: unknown;
              amplitude: unknown;
              phase?: {
                [k: string]: unknown;
              };
              wave?: string | null;
            } | null)[]
          | null;
        parent?: string | null;
        joint?: {
          [k: string]: unknown;
        };
      } | null)[]
    | null;
  frames?:
    | ({
        name: string;
        transforms: ({
          id: number;
          position: unknown;
          rotation: unknown;
          scale: unknown;
        } | null)[];
      } | null)[]
    | null;
  chains?:
    | ({
        id: number;
        name?: string | null;
        shapes: number[];
        kind?: string | null;
        goal?: unknown;
        pole?: unknown;
      } | null)[]
    | null;
  cameras?:
    | ({
        id: number;
        shapeId: number;
        position: unknown;
        yaw?: number | null;
        pitch?: number | null;
        fov?: number | null;
        focus?: number | null;
        feed?: string | null;
      } | null)[]
    | null;
  behavior?: CreationBehaviorDocument;
  textRuns?:
    | ({
        text: string;
        position: unknown;
        rotation: unknown;
        emHeight: number;
        depth?: number | null;
        mode?: string | null;
        material?: number | null;
        font?: string | null;
        maxWidth?: number | null;
        align?: string | null;
        tracking?: number | null;
        lineSpacing?: number | null;
        shapeId?: number | null;
      } | null)[]
    | null;
  parts?:
    | ({
        id: string;
        shapeId: number;
      } | null)[]
    | null;
  noise?: CreationNoiseDocument;
  drivers?:
    | ({
        name: string;
        signal: string;
        cadence: unknown;
        when?: (string | null)[] | null;
      } | null)[]
    | null;
  effectors?:
    | ({
        name: string;
        chain: (string | null)[];
        tip: string;
        target: {
          kind: string;
          direction?: {
            [k: string]: unknown;
          };
          reach?: {
            [k: string]: unknown;
          };
          standoff?: {
            [k: string]: unknown;
          };
          index?: number | null;
          offset?: {
            [k: string]: unknown;
          };
          reference?: string | null;
        };
        when?: (string | null)[] | null;
        weight?: {
          [k: string]: unknown;
        };
        plant?: {
          driver: string;
          window: unknown;
        } | null;
      } | null)[]
    | null;
} | null;
export type CreationBehaviorDocument = {
  locomotion?: string | null;
  faces?:
    | ({
        name: string;
        shapeId?: number | null;
        defaultSource?: string | null;
      } | null)[]
    | null;
  sounds?:
    | ({
        name: string;
        shapeId?: number | null;
        patch: {
          schema?: string | null;
          name?: string | null;
          oscillator?: "Pulse" | "Saw" | "Triangle" | "Sine" | "Noise" | null;
          dutyThousandths?: number | null;
          polynomial?: number | null;
          attackFrames?: number | null;
          decayFrames?: number | null;
          sustainThousandths?: number | null;
          releaseFrames?: number | null;
          pitchMillihertz: number;
          sweepMillihertzPerFrame?: number | null;
          vibratoDepthMillihertz?: number | null;
          vibratoRateMillihertz?: number | null;
          durationFrames?: number | null;
        };
        level?: number | null;
        radius?: number | null;
      } | null)[]
    | null;
} | null;
export type CreationNoiseDocument = {
  frequency: number;
  amplitude: number;
  octaves?: number | null;
  gain?: number | null;
  lacunarity?: number | null;
  seed?: number | null;
} | null;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeaker".
 */
export type WorldSpeaker = WorldSpeakerFixed | WorldSpeakerAnchored | WorldSpeakerBed;
/**
 * The signal a WorldSpeaker's feed taps — a shared source identity, never an inline payload: the runtime drains each distinct source once per mix block and every feed tapping it shares that one pull, so "stereo = two rows sharing a source" costs one drain. The $type string is the JSON discriminator, matching WorldScreenSource's convention; a new source kind is a new derived record plus its JsonDerivedTypeAttribute line.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerSource".
 */
export type WorldSpeakerSource =
  WorldSpeakerSourceNone | WorldSpeakerSourceMachine | WorldSpeakerSourceTune | WorldSpeakerSourceSynth;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldContactRequirement".
 */
export type WorldContactRequirement = "SmoothUnionContact" | "GradientDerivedUp";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravitySolver".
 */
export type WorldGravitySolver = "Pairwise" | "FastMonopole" | "AdaptiveFmm";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaMode".
 */
export type WorldGravityAreaMode = "Combine" | "Replace";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaBounds".
 */
export type WorldGravityAreaBounds = WorldGravityAreaBoundsSphereBounds | WorldGravityAreaBoundsBoxBounds;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaAcceleration".
 */
export type WorldGravityAreaAcceleration = WorldGravityAreaAccelerationDirectional | WorldGravityAreaAccelerationRadial;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHostPresentation".
 */
export type WorldHostPresentation = "Windowed" | "None" | "Offscreen";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "SurfaceFormat".
 */
export type SurfaceFormat = "r8g8b8a8" | "b8g8r8a8";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PresentMode".
 */
export type PresentMode = "Vsync" | "Mailbox" | "Immediate" | "Adaptive";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBackendPreference".
 */
export type WorldBackendPreference = "auto" | "directx" | "vulkan";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatYawReference".
 */
export type WorldSeatYawReference = "World" | "Body";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLookSource".
 */
export type WorldLookSource = WorldLookSourceCatalog | WorldLookSourceCreation;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ChannelReachMask".
 */
export type ChannelReachMask = number;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ChannelConsentMask".
 */
export type ChannelConsentMask = number;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "MutationKindMask".
 */
export type MutationKindMask = string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DocumentWriteMask".
 */
export type DocumentWriteMask = string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudCursorRole".
 */
export type WorldHudCursorRole = "TextPrimary" | "TextDim" | "Accent" | "Phosphor";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldStateRow".
 */
export type WorldStateRow = WorldStateRow1;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ShapeNonNullable".
 */
export type ShapeNonNullable = number | string | boolean;
/**
 * What a StateCycle cell reads: the rotation as a step count, a fraction of a turn or a unit-rotation component, or the rotation carried along a symmetry-lattice orbit as a node index, its ring or a projected coordinate. The integer outputs belong to Int cells and the fixed outputs to Fixed cells.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "CycleOutput".
 */
export type CycleOutput = "Step" | "Turns" | "Cos" | "Sin" | "Node" | "ProjectionX" | "ProjectionY" | "Ring";
/**
 * What an observer learns about a row's cells it may not read.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "HiddenCells".
 */
export type HiddenCells = "Omit" | "Count" | "Placeholder";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "CellName".
 */
export type CellName = string;
/**
 * The closed vocabulary of a StateGenerator's draw shape — which of its fields are read, and what one emission produces: a Markov text walk, a multiset draw, a uniform range, a weighted numeric table, and a raw stream draw are sources of one family, never parallel primitives with their own seeding, cursoring, and refusal stories.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GeneratorSource".
 */
export type GeneratorSource = "Markov" | "UniformRange" | "WeightedNumeric" | "StreamDraw" | "SymmetryOrbit";
/**
 * How a StateGenerator's entries — a Markov context's alternatives, or a weighted numeric source's outcomes — are consumed: the multiset-sampling vocabulary. Authored, never inferred: exhaustion behaviour is a declaration, not a fallback the engine picks.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GeneratorMode".
 */
export type GeneratorMode = "WithReplacement" | "WithoutReplacement" | "RestartOnExhaustion";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DrawTiming".
 */
export type DrawTiming = "Boot" | "TickPeriod" | "Event";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeFill".
 */
export type WorldLatticeFill =
  WorldLatticeFillRect | WorldLatticeFillNoise | WorldLatticeFillScatter | WorldLatticeFillDraw;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionStateKind".
 */
export type ActionStateKind = "Counter" | "Timer";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionFactNullable".
 */
export type ActionFactNullable =
  | "Grounded"
  | "Airborne"
  | "Rising"
  | "Falling"
  | "AffectedBy"
  | "InMedium"
  | "AtMediumBand"
  | "HoldingUnwalkable"
  | "Unsupported"
  | "Resting";
/**
 * The authored values a player-writable durable slot admits in this world.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionStateEnvelope".
 */
export type ActionStateEnvelope = ActionStateEnvelopeRange | ActionStateEnvelopeSet;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopology".
 */
export type LatticeTopology =
  | LatticeTopologyGrid
  | LatticeTopologyRing
  | LatticeTopologyHex
  | LatticeTopologyBox
  | LatticeTopologyGraph
  | LatticeTopologyTiling
  | WorldFieldTopology;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TopologyWrap".
 */
export type TopologyWrap = "None" | "X" | "Y" | "Both";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TilingFamily".
 */
export type TilingFamily =
  | "Triangular"
  | "Kagome"
  | "TruncatedSquare"
  | "Rhombitrihexagonal"
  | "TruncatedHexagonal"
  | "ElongatedTriangular"
  | "TruncatedTrihexagonal"
  | "Penrose";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReaction".
 */
export type WorldReaction =
  | WorldReactionDiffuse
  | WorldReactionDecay
  | WorldReactionTransform
  | WorldReactionEmit
  | WorldReactionExpose
  | WorldReactionFlow;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindableColor".
 */
export type BindableColor = string;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMarkerSource".
 */
export type WorldMarkerSource = WorldMarkerSourceSpeakers | WorldMarkerSourcePoint;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDecisionMode".
 */
export type WorldDecisionMode = "HighestScore" | "Weighted";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupOwnershipPolicy".
 */
export type WorldGroupOwnershipPolicy = "None" | "LeaderDecides" | "RoundRobin" | "FreeForAll";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupLifetime".
 */
export type WorldGroupLifetime = "Ephemeral" | "Persistent";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupEvictionPolicy".
 */
export type WorldGroupEvictionPolicy = "Remove" | "Disband";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OwnershipSubjectKind".
 */
export type OwnershipSubjectKind = "Group";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OwnershipOwnerKind".
 */
export type OwnershipOwnerKind = "Principal" | "Group" | "Escrow";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldInteractionCoOccurrence".
 */
export type WorldInteractionCoOccurrence = "Distance" | "Region";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldTransferFullPolicy".
 */
export type WorldTransferFullPolicy = "Retry" | "Refuse";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDestinationDurability".
 */
export type WorldDestinationDurability = "ephemeral" | "persisted";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDestinationScope".
 */
export type WorldDestinationScope = "user" | "group" | "global";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupSelector".
 */
export type WorldGroupSelector = WorldGroupSelectorNamed | WorldGroupSelectorTagged;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAdmissionTrustMode".
 */
export type WorldAdmissionTrustMode = "SignsDirectly" | "Vouches" | "FederatedAuthority";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDisclosureTier".
 */
export type WorldDisclosureTier = "Frames" | "Presentation" | "Replica";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAdjacencyUnavailable".
 */
export type WorldAdjacencyUnavailable = "Closed";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeBinding".
 */
export type WorldProbeBinding = WorldProbeBindingAxis | WorldProbeBindingParameter | WorldProbeBindingControl;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeParameterTarget".
 */
export type WorldProbeParameterTarget = WorldProbeParameterTargetExtension | WorldProbeParameterTargetProbe;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldNavigationKind".
 */
export type WorldNavigationKind = "Surface" | "Volume" | "Medium";
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldNavigationConnectivity".
 */
export type WorldNavigationConnectivity = "Axis" | "FacesAndEdges" | "Full";
/**
 * The closed pattern vocabulary over a row's cell values, matched against the whole word. Complement and intersection are first-class, so "no two adjacent kings" and "holds a 2 and a 5" are single patterns rather than rule arithmetic.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNode".
 */
export type PatternNode =
  | PatternNodeSymbol
  | PatternNodeAnySymbol
  | PatternNodeExcept
  | PatternNodeNothing
  | PatternNodeNone
  | PatternNodeSequence
  | PatternNodeChoice
  | PatternNodeBoth
  | PatternNodeComplement
  | PatternNodeOptional
  | PatternNodeStar
  | PatternNodePlus
  | PatternNodeRepeat;
/**
 * The closed pattern vocabulary over a row's cell values, matched against the whole word. Complement and intersection are first-class, so "no two adjacent kings" and "holds a 2 and a 5" are single patterns rather than rule arithmetic.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeNullable".
 */
export type PatternNodeNullable =
  | PatternNodeSymbol
  | PatternNodeAnySymbol
  | PatternNodeExcept
  | PatternNodeNothing
  | PatternNodeNone
  | PatternNodeSequence
  | PatternNodeChoice
  | PatternNodeBoth
  | PatternNodeComplement
  | PatternNodeOptional
  | PatternNodeStar
  | PatternNodePlus
  | PatternNodeRepeat;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeList".
 */
export type PatternNodeList = PatternNodeNullable[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeListNonNullable".
 */
export type PatternNodeListNonNullable = PatternNodeNullable[];
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShape".
 */
export type WorldSearchShape =
  | WorldSearchShapeRelocate
  | WorldSearchShapeDrop
  | WorldSearchShapeJump
  | WorldSearchShapePaired
  | WorldSearchShapePromote
  | WorldSearchShapeTransferred;
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "SearchMethod".
 */
export type SearchMethod = "Negamax" | "Tree";

/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMotionDefaults".
 */
export interface WorldMotionDefaults {
  /**
   * Locomotion speed in world units per second — the profileless fallback a stand-in advances on (a seated player whose identity CLAIMS a rate reads that claim instead, live, so identity.motion stays real-time; an identity claiming none rides the kit's own rate).
   */
  moveSpeed?: number;
  /**
   * Turn speed in radians per second (the profileless fallback counterpart to MoveSpeed).
   */
  turnSpeed?: number;
  /**
   * The largest server-correction position error, in world units, that presentation may ease instead of snapping.
   */
  maxSmoothError?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpawnPoint".
 */
export interface WorldSpawnPoint {
  /**
   * The stable spawn name, unique within the definition.
   */
  id?: string;
  /**
   * The seat's spawn position.
   */
  position?: DocumentVector3;
  /**
   * The spawn yaw about +Y, in degrees.
   */
  yawDegrees?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderDefaults".
 */
export interface WorldRenderDefaults {
  /**
   * The boot soft-shadow tier.
   */
  shadows?: ShadowTier;
  /**
   * The boot soft-shadow crowd radius (world units).
   */
  shadowCrowdRadius?: number;
  /**
   * Whether ambient occlusion boots on.
   */
  ambientOcclusion?: boolean;
  /**
   * The boot render-scale tier.
   */
  renderScale?: WorldRenderScaleTier;
  /**
   * The boot reduced-resolution reconstruction blend (0 bilinear .. 1 Catmull-Rom).
   */
  upscaleSharpness?: number;
  /**
   * The world.quality low preset.
   */
  low?: WorldQualityPreset | null;
  /**
   * The world.quality medium preset.
   */
  medium?: WorldQualityPreset | null;
  /**
   * The world.quality high preset.
   */
  high?: WorldQualityPreset | null;
  /**
   * The post-render extension chain, composed over the world's rendered output in list order — e.g. [{ "id": "sdf-film-grain", "config": { "intensity": 0.08 } }]. Optional; an absent or empty list is the byte-identical default path (no extension composed). Every id must name a shipped shader set — a puck.shader.v1 manifest's file stem (checked at document load); each entry's own config is validated against that manifest's declared config schema at boot and by puck schema.
   */
  extensions?: (WorldRenderExtensionEntry | null)[] | null;
  /**
   * The scene's directional sun and ambient term. Optional, and every field within it is optional individually — an absent section, or an absent field within it, resolves to SdfFrame's pinned default for that field, so a world renders unchanged until it authors one.
   */
  lighting?: WorldRenderLighting | null;
  /**
   * The procedural sky — a gradient, sun disc, star field, and distance fog. Optional; an absent section renders the pinned two-stop gradient and 0.015 fog density bit-exactly, as before this section existed.
   */
  sky?: WorldRenderSky | null;
  /**
   * Lighting and sky keyed over a state row's value (a day/night cycle when that row advances). Optional; absent leaves Lighting/Sky static.
   */
  cycle?: WorldRenderCycle | null;
  /**
   * The far distance in world units: the depth at which every camera march ends — the far plane the renderer's fine march exits at, the reach of the beam's cone proofs, and the depth the fog and depth ramps are measured against. Geometry beyond it is never marched, so an infinite plane ends on a visible horizon curve at this depth unless the sky fog has absorbed it (render.sky.fogDensity). Optional; absent resolves to the engine's pinned 40 — exactly the value every world marched to before this field existed. Must lie within [MinFarDistance, MaxFarDistance]. Re-read on every definition revision (a world.row.set render lands on the next frame); world.budget echoes it with its derived costs.
   */
  farDistance?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldQualityPreset".
 */
export interface WorldQualityPreset {
  /**
   * The soft-shadow tier the preset selects.
   */
  shadows?: ShadowTier;
  /**
   * Whether the preset enables ambient occlusion.
   */
  ambientOcclusion?: boolean;
  /**
   * The render-scale tier the preset selects.
   */
  renderScale?: WorldRenderScaleTier;
}
export interface WorldRenderExtensionEntry1 {
  /**
   * The shader set id (its puck.shader.v1 manifest's file stem) — checked against the shipped vocabulary at document load (IsRegisteredPostRenderExtension), never interpreted here.
   */
  id: "sdf-film-grain";
  /**
   * The set's config values, or null when the manifest declares none or every field has a default. Not validated at document load — the manifest's declared config schema validates it at boot (matching Machine's Options, the identical shallow-then-deep precedent), refusing boot with the set id and reason on a malformed value.
   */
  config?: {
    [k: string]: unknown;
  };
}
/**
 * The scene's directional sun and ambient term — threads unchanged into the engine's existing per-frame lighting fields (SdfFrame.SunDirection/SunWeight/SunColor/AmbientBase/ AmbientHemisphere/AmbientColor). Every field, at every level, is optional individually — an absent one resolves to SdfFrame's pinned default for that field, so an unauthored section, or a partially authored one, renders exactly as a world that declares neither.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderLighting".
 */
export interface WorldRenderLighting {
  /**
   * The directional sun.
   */
  sun?: WorldRenderSun | null;
  /**
   * The ambient (hemisphere) term.
   */
  ambient?: WorldRenderAmbient | null;
}
/**
 * One directional sun. Every field is optional individually — absent resolves to SdfFrame's pinned default for that field.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderSun".
 */
export interface WorldRenderSun {
  /**
   * The direction from a lit surface toward the light, any nonzero length (normalized host-side before upload).
   */
  direction?: DocumentVector3;
  /**
   * The sun's diffuse weight.
   */
  weight?: number | null;
  /**
   * The sun's linear #RRGGBB color.
   */
  color?: string | null;
}
/**
 * The ambient (hemisphere) term. Every field is optional individually — absent resolves to SdfFrame's pinned default for that field.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderAmbient".
 */
export interface WorldRenderAmbient {
  /**
   * The ambient floor.
   */
  base?: number | null;
  /**
   * The hemisphere gradient, scaling surface normal Y.
   */
  hemisphere?: number | null;
  /**
   * The ambient linear #RRGGBB color.
   */
  color?: string | null;
}
/**
 * The procedural sky — a three-stop gradient, sun disc, star field, and distance fog, authored as world data. Absent is a hard gate: every existing world renders the pinned two-stop gradient and 0.015 fog density bit-exactly, as before this section existed, until it authors one.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderSky".
 */
export interface WorldRenderSky {
  /**
   * The straight-up sky color, as #RRGGBB. Optional; absent takes the pinned zenith.
   */
  zenith?: string | null;
  /**
   * The horizon-band color (the gradient's middle stop), as #RRGGBB. Optional; absent takes the midpoint between the pinned ground and zenith.
   */
  horizon?: string | null;
  /**
   * The straight-down (nadir) color, as #RRGGBB. Optional; absent takes the pinned ground.
   */
  ground?: string | null;
  /**
   * The exponential distance-fog density fading toward the sky color. Optional; absent takes the pinned 0.015 — the exact value the fog term used before this field existed.
   */
  fogDensity?: number | null;
  /**
   * The visible sun disc, drawn about the lighting sun's direction. Optional; absent draws no disc.
   */
  sun?: WorldRenderSkySun | null;
  /**
   * The procedural star field. Optional; absent draws no stars.
   */
  stars?: WorldRenderSkyStars | null;
  /**
   * The procedural cloud layer. Optional; absent draws no clouds.
   */
  clouds?: WorldRenderSkyClouds | null;
}
/**
 * The visible sun disc — an additive highlight about the lighting sun's direction.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderSkySun".
 */
export interface WorldRenderSkySun {
  /**
   * The disc's angular half-radius in radians, in (0, π/2]. Controls the falloff sharpness: a smaller disc reads sharper.
   */
  discRadians: number;
  /**
   * The disc's peak additive brightness.
   */
  intensity: number;
}
/**
 * The procedural star field: a deterministic per-cell hash over an octahedral sky projection. No texture, no session state — the identical density/seed always draws the identical field.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderSkyStars".
 */
export interface WorldRenderSkyStars {
  /**
   * The star grid's cell count per octahedral axis. A higher value packs more, smaller stars.
   */
  density: number;
  /**
   * The peak per-star brightness.
   */
  brightness: number;
  /**
   * The hash seed folded into every cell — a different seed reshuffles the field.
   */
  seed: number;
  /**
   * Scintillation for a share of the stars. Optional; absent twinkles none.
   */
  twinkle?: WorldRenderSkyTwinkle | null;
}
/**
 * Scintillation: a hash-chosen share of the stars dip and recover on the simulation clock, each at its own harmonic and phase of one authored rate, so no two twinkle in step. Presentation-only, keyed on the tick.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderSkyTwinkle".
 */
export interface WorldRenderSkyTwinkle {
  /**
   * The fraction of stars that twinkle, in [0, 1]. Zero twinkles none.
   */
  share: number;
  /**
   * How far a twinkling star dips below its steady brightness, in [0, 1]: 0 holds steady, 1 dips to black.
   */
  depth: number;
  /**
   * The fundamental scintillation rate in hertz; each star twinkles at a small harmonic of it.
   */
  rate: number;
}
/**
 * The procedural cloud layer: a deterministic hashed-lattice noise (warped, four octaves) on a plane above the camera, thresholded by coverage, drawn over the gradient, sun disc and stars and fading into the horizon. No texture, no session state — the identical settings and seed always draw the identical layer at the identical tick.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderSkyClouds".
 */
export interface WorldRenderSkyClouds {
  /**
   * The fraction of the sky the layer covers, in [0, 1]. Zero draws none.
   */
  coverage: number;
  /**
   * The width of a cloud's edge, in (0, 1]: small is hard-edged cumulus, large is a diffuse haze.
   */
  softness: number;
  /**
   * The size of one cloud cell in layer units (the layer sits at unit height, so a scale of 1 spans about 45° overhead). Larger is broader clouds.
   */
  scale: number;
  /**
   * The hash seed folded into the lattice — a different seed reshapes the layer.
   */
  seed: number;
  /**
   * The cloud colour, as #RRGGBB or a state.<row>.<key> binding. Optional; absent is white.
   */
  color?: string | null;
  /**
   * The layer's wind, in layer units per second along world X and Z, integrated on the tick clock. Optional; absent holds still.
   */
  drift?: DocumentVector2;
  /**
   * The layer's rotation about the zenith in radians per second — the planetary-scale turning a rotating frame gives a broad flow; positive is counter-clockwise seen from below. Optional; absent is none.
   */
  spin?: number | null;
  /**
   * The Coriolis twist: how far, in radians, the layer is wound about the zenith at 45° elevation, the winding falling off toward the horizon and the zenith so bands spiral in rather than shear apart. Positive winds counter-clockwise. Optional; absent is none.
   */
  curl?: number | null;
  /**
   * The wind of the SHAPING field relative to the cloud field, in layer units per second: the two slide past each other, so clouds boil and re-form as they drift rather than glide as a fixed picture. Optional; absent holds their shapes.
   */
  shear?: DocumentVector2;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderCycle".
 */
export interface WorldRenderCycle {
  /**
   * The state row read (its slot cell; Fixed or Int).
   */
  state: string;
  /**
   * At least two keys, strictly ascending At in [0, 1).
   *
   * Items: One point on a WorldRenderCycle.
   */
  keys: (WorldRenderCycleKey | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRenderCycleKey".
 */
export interface WorldRenderCycleKey {
  /**
   * The row-value fraction this key sits at, in [0, 1).
   */
  at: number;
  /**
   * The lighting fields this key moves, or null.
   */
  lighting?: WorldRenderLighting | null;
  /**
   * The sky fields this key moves, or null.
   */
  sky?: WorldRenderSky | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreen".
 */
export interface WorldScreen {
  /**
   * The engine screen-surface index (0..MaxScreenSurfaces−1) this slab declares — the key the source/light providers bind under.
   */
  index: number;
  /**
   * The front face's world-space center (the sampled surface origin); the geometry center sits one HalfDepth behind it along the face normal.
   */
  origin: DocumentVector3;
  /**
   * The unit world axis the sampled U increases along (the slab's local +X in world space). Must be orthogonal to Up: the client derives the slab's orientation and UV frame from the pair while the server's collider projects the half-extents onto it, so a skewed pair would render and collide as different solids.
   */
  right: DocumentVector3;
  /**
   * The unit world axis the sampled V increases against — V = 0 at the top (the slab's local +Y in world space).
   */
  up: DocumentVector3;
  /**
   * The face half-width (the slab's local X half-extent).
   */
  halfWidth: number;
  /**
   * The face half-height (the slab's local Y half-extent).
   */
  halfHeight: number;
  /**
   * The slab's local Z half-extent (its thickness behind the face).
   */
  halfDepth: number;
  /**
   * The corner-rounding radius.
   */
  round: number;
  /**
   * The signal the lit face carries.
   */
  source: WorldScreenSource;
  /**
   * The engage-route policy.
   */
  route: WorldScreenRoute;
  /**
   * The screen slab's solidity facet (a box collider derived from the slab's oriented frame + Margin by Server.WorldColliderSet), or null for a decorative screen. Omitted from the wire when null.
   */
  solid?: WorldSolid | null;
  /**
   * The per-screen source magazine (the cycle primitive), or null for a screen with no magazine — nothing to cycle. Omitted from the wire when null — the whole-row UpsertScreen carries it for free, so no new mutation kind is needed.
   */
  magazine?: WorldScreenMagazine | null;
}
/**
 * No provider is bound — the engine lights the slot with its procedural no-signal fallback (an animated test-card / striped no-signal look, never black).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceNone".
 */
export interface WorldScreenSourceNone {
  $type?: "none";
}
/**
 * The deterministic animated test pattern (Puck.SdfVm.Views.TestPatternSource), rendered from the world's sim tick (never the wall clock) into a CPU buffer and uploaded each frame.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceTestPattern".
 */
export interface WorldScreenSourceTestPattern {
  $type?: "testPattern";
  /**
   * The pattern framebuffer width in pixels.
   */
  width: number;
  /**
   * The pattern framebuffer height in pixels.
   */
  height: number;
}
/**
 * An arbitrary deterministic machine's unresampled framebuffer — resolved against a registered IScreenMachineEngine by Engine id. The world never names a concrete machine: the engine owns its Options vocabulary (a GamingBrick reads a dmg/cgb/agb model + a dmgspeed pin).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceMachine".
 */
export interface WorldScreenSourceMachine {
  $type?: "machine";
  /**
   * The screen-machine engine id (e.g. gaming-brick).
   */
  engine: string;
  /**
   * The content the machine boots, resolved against the document's own directory: a ROM image, or — when the path ends in CartridgeDocumentSuffix — an authored puck.cartridge.v1 document the host compiles through the engine's own forge at bind (Puck.World.WorldScreenMachineEngines.CartridgeCompilers), booting the compiled bytes exactly as it boots a ROM file; the source's canonical hash and the compiled image's hash ride the slot beside the file's own content pin, and screen.state echoes them. A cartridge path on an engine that compiles none refuses at validation by name; a document the forge refuses faults the bind with the forge's own message. Empty when the screen is unconfigured — the binder faults the slot gracefully (no crash, no-signal card) rather than booting.
   */
  contentPath: string;
  /**
   * The engine-specific options string, or null for the engine's defaults.
   */
  options: string | null;
  /**
   * This machine's cable port (see WorldMachineCable), or null for an unlinked machine. Legal only on a declared screens row's own source — a magazine entry or a placement face's source is refused one (validated), because a cable is a standing physical connection of the machine that owns the slot, not of whatever content happens to rotate through it. Omitted from the wire when null, so every machine source authored before cables existed round-trips unchanged.
   */
  cable?: WorldMachineCable | null;
}
/**
 * One machine's cable-port declaration — the machine-tier home of cable linking. A cable is the SET of declared machine sources naming the same cable name, derived by MachineCableGroups; no port ever points at another port, so reciprocity holds by construction and a one-port cable is a validation refusal rather than a dangling half-pair.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMachineCable".
 */
export interface WorldMachineCable {
  /**
   * The cable's stable kebab-case name, shared by every plugged port.
   */
  name: string;
  /**
   * This machine's 0-based place in cable order — contiguous across the cable's ports (validated), and what decides the linking engine's deterministic player order.
   */
  position: number;
}
/**
 * The platform's default live camera feed. The platform may negotiate a nearby extent; every screen and probe socket naming the same sensor shares one feed, opened at the richest profile any screen row requests.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceCamera".
 */
export interface WorldScreenSourceCamera {
  $type?: "camera";
  /**
   * The preferred capture extent and maximum upload cadence, or null for the platform default — a probe socket never needs one. Omitted from the wire when null.
   */
  profile?: WorldFeedProfile | null;
  /**
   * The authored device-control state (WorldCameraControls), or null to leave every control at its driver default. One physical device carries one control state across color and infrared, so the FIRST declared camera screen authoring this wins regardless of sensor (matching the shared-device model); a later UpsertScreen mutation re-resolves and applies the change live. Omitted from the wire when null.
   */
  controls?: WorldCameraControls | null;
  /**
   * Which physical sensor this row's shared feed opens: Color (the default) or Infrared — the infrared frame source a Windows Hello capable device carries. Each sensor gets its own shared feed, so different rows may request different sensors at once; the engine honors a Windows Face Authentication Profile V2 when published and admits simultaneous capture only after both native streams prove live. A legacy provider available only to the Windows biometric broker is not a public dual-camera graph. An absent infrared source faults the bind loudly (the slot shows the no-signal card).
   */
  sensor?: WorldCameraSensor;
  /**
   * The 1-based local seat this row names — a camera is an input device seated like a pad, never hardware named directly. null means the enclosing seat scope (an identity's HUD panel, a seat-scoped probe socket) or seat 1 at world scope — the same explicit-index convention $channel:<seat> and axis bindings already use. Validated within 1..population.localSeats when present. Omitted from the wire when null.
   */
  seat?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFeedProfile".
 */
export interface WorldFeedProfile {
  /**
   * Requested output width in pixels.
   */
  width?: number;
  /**
   * Requested output height in pixels.
   */
  height?: number;
  /**
   * Maximum pull/upload cadence; it must divide the engine time base exactly.
   */
  refreshRateHz?: number;
}
/**
 * The authored control state for the shared physical camera — one device-wide state across its color and infrared streams — and the standard UVC camera/image controls (pan/tilt/zoom, exposure, focus, color). Every member is optional: an ABSENT member leaves that control at the device's own driver default (automatic where the device supports it), and a PRESENT member drives the control manually at that value. The device remains authoritative — a value outside the device's reported range is clamped (and step-snapped) at apply, a control the device lacks is skipped, and screen.camera reads the resulting live state (each control's device range, mode, and current value) back over the pipe. Removing a previously authored member restores that control's driver default on the next apply. Ranges are device-specific by design, so validation admits any integer rather than guessing one device's envelope.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraControls".
 */
export interface WorldCameraControls {
  /**
   * Horizontal framing offset (digital pan on webcams).
   */
  pan?: number | null;
  /**
   * Vertical framing offset (digital tilt on webcams).
   */
  tilt?: number | null;
  /**
   * Magnification — on sensor-cropping webcams a region-of-interest zoom (e.g. 100..500 = 1x..5x on a Logitech BRIO), which pairs with Pan/Tilt to frame a region.
   */
  zoom?: number | null;
  /**
   * Manual exposure time, typically log2 seconds (e.g. -5 = 1/32 s); absent = auto exposure.
   */
  exposure?: number | null;
  /**
   * Manual focus distance; absent = autofocus.
   */
  focus?: number | null;
  /**
   * Image brightness offset.
   */
  brightness?: number | null;
  /**
   * Image contrast.
   */
  contrast?: number | null;
  /**
   * Color saturation (0 is grayscale on most devices).
   */
  saturation?: number | null;
  /**
   * Edge sharpening strength.
   */
  sharpness?: number | null;
  /**
   * Sensor gain (ISO-like amplification).
   */
  gain?: number | null;
  /**
   * Manual white-balance color temperature in kelvin; absent = auto white balance.
   */
  whiteBalance?: number | null;
  /**
   * Backlight compensation (devices commonly report a 0..1 toggle).
   */
  backlightCompensation?: number | null;
  /**
   * The lens field of view in DEGREES — a vendor-extension control (hardware-verified on the Logitech BRIO: discrete 90/78/65, a true sensor crop composing with Zoom); the device snaps to its nearest supported value, and a camera without the vendor unit skips it. Absent = the device default (widest).
   */
  fieldOfView?: number | null;
  /**
   * Raw vendor-extension writes — byte-sized (selector, value) pairs on the device's vendor unit, applied in order after the named controls. The engine assigns NO semantics: the author names the selector, the device decides what it means (e.g. the BRIO's unconfirmed HDR candidate is selector 12), and a removed row is NOT restored (its default is unknowable). screen.camera reads each authored selector back. Omitted from the wire when null.
   */
  vendor?: WorldCameraVendorControl[] | null;
}
/**
 * One raw vendor-extension write on the shared camera (see WorldCameraControls's Vendor member): a byte-sized selector on the device's vendor unit and the value to write. Deliberately semantics-free — the honest vocabulary for device controls no standard names.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraVendorControl".
 */
export interface WorldCameraVendorControl {
  /**
   * The vendor extension unit's control selector.
   */
  id: number;
  /**
   * The byte value to write (0..255; the device clamps or refuses out-of-range writes).
   */
  value: number;
}
/**
 * A named view from the presentation view stack, such as a monitor showing another camera's output.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceView".
 */
export interface WorldScreenSourceView {
  $type?: "view";
  /**
   * The registered view name this slot samples.
   */
  cameraName: string;
}
/**
 * A live compositor capture feed — a desktop window keyed by title, or a whole monitor keyed by index. The selector is the altitude of the primitive: MonitorIndex null is window mode; non-null is whole-monitor mode (and WindowTitle is unused).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceCapture".
 */
export interface WorldScreenSourceCapture {
  $type?: "capture";
  /**
   * The captured window's title (window mode; ignored when MonitorIndex is set).
   */
  windowTitle: string;
  /**
   * This capture consumer's output extent and maximum refresh cadence.
   */
  profile: WorldFeedProfileNonNullable;
  /**
   * The 0-based monitor to capture whole (0 = primary), or null for window mode.
   */
  monitorIndex?: number | null;
}
/**
 * A live screen feed's requested output policy. It belongs to the source declaration rather than the binder, so two window captures can choose different extents and cadences. Camera extents are preferences because a physical device remains authoritative for its negotiated format.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFeedProfileNonNullable".
 */
export interface WorldFeedProfileNonNullable {
  /**
   * Requested output width in pixels.
   */
  width?: number;
  /**
   * Requested output height in pixels.
   */
  height?: number;
  /**
   * Maximum pull/upload cadence; it must divide the engine time base exactly.
   */
  refreshRateHz?: number;
}
/**
 * A screen showing the developer console as an object in the world — the diegetic half of the control plane the unification contract names ("the on-screen panel and process stdin"). The frame is CPU-composed into a CRT-styled framebuffer and pushed through IGpuSurfaceUpload, exactly as the ported console feed does; nothing about it is a render-graph node. Complementary to — never a duplicate of — ConsoleTape, which publishes the same content to the screen-space overlay. At most one console source may be live (declared) at a time; an unselected console entry sitting in a magazine is legal.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceConsole".
 */
export interface WorldScreenSourceConsole {
  $type?: "console";
  /**
   * Console text rows the framebuffer composes, 1..120. Sizes the CPU buffer.
   */
  rows?: number;
  /**
   * Console text columns, 1..400.
   */
  columns?: number;
  /**
   * When true the slot shows the sibling generated pattern instead of console text — carried as a mode of this variant rather than as a seventh union case.
   */
  procedural?: boolean;
}
/**
 * An authorable QR code (ISO/IEC 18004) — the document names a payload string and the engine derives the scannable module grid (QrEncoder), rendered CPU-side into a static B8G8R8A8 framebuffer and uploaded once, never re-derived from the tick like TestPattern. The driving case is a link one human hands another off an in-world screen. This record is the document-authored half only — nothing here mints a payload at runtime; screen.source <index> qr is the live-authoring twin, and world.identify is the one caller that mints its payload (the running world's own documentId and content-address pin) rather than being handed one.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceQr".
 */
export interface WorldScreenSourceQr {
  $type?: "qr";
  /**
   * The encoded string, UTF-8 byte mode. Must fit within version MaxSupportedVersion at EcLevel — validation refuses an oversized payload by name (its byte count against the level's capacity), never truncates it.
   */
  payload: string;
  /**
   * The error-correction level: L, M, Q, or H (case-insensitive, parsed by TryParse). Defaults to M.
   */
  ecLevel?: string;
  /**
   * The white quiet-zone border width in modules on every side. ISO/IEC 18004 recommends at least 4; a smaller value authors a QR a real scanner may refuse to read (a borderless QR does not scan) — the document may still author it (validation only refuses a negative width), since a screen's physical framing sometimes supplies the margin itself.
   */
  quietZoneModules?: number;
}
/**
 * A live rendered view of another world, resolved through a destinations row (docs/vision.md, "Observation and display"). The face/screen resolves the same resolver-owned identity a portal crossing at the same door would land in (WorldSessionResolver), attaches an observation lease to the resolved instance's server, and mirrors just enough of its delivered definition/snapshots to render its static authored geometry through CameraName (or the destination's default projection). It never re-derives durability/scope/generation itself — those are the destination row's own facts.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceSession".
 */
export interface WorldScreenSourceSession {
  $type?: "session";
  /**
   * The Name this face/screen observes. Must resolve to a declared destinations row — an undeclared name refuses at boot (validated, like a portal facet's own destination).
   */
  destination: string;
  /**
   * The destination's own placeable-camera name to render through, or null for its default projection (its first declared camera, else a fixed overview derived from its spawn points). Wire name camera — plain Camera would collide with the sibling Camera arm's own type name inside this enclosing record. Validated only as non-empty when present at author time — the destination's own definition is not joined at boot (references assert naming intent, not reachability), so an unknown camera name is refused loudly at bind time instead, once the destination is actually resolved, falling back to the default projection rather than refusing the whole bind. Ignored under Window (see Projection).
   */
  camera?: string | null;
  /**
   * How the destination render projects onto this face (see WorldScreenProjection). Default Camera — unauthored worlds and every session facet authored before this member existed render byte-identically. Optional and trailing (the same widen-without-moving-existing-members shape CameraName itself already follows). Window requires this same face's Portal to author Mapped with a Counterpart — refused by name otherwise (see WorldDefinitionValidator); a top-level screens row or magazine entry carries no face to pair with, so window is refused there unconditionally.
   */
  projection?: WorldScreenProjection;
  /**
   * The offscreen target's [width, height] in pixels, or null for the engine default (Puck.SdfVm.Views.WorldSessionView.DefaultWidth x DefaultHeight — today's 160x144 panel, unchanged for an unauthored facet). Each axis is validated within 1..WorldDefinitionValidator.MaxSurfaceDimension. Omitted from the wire when null.
   */
  resolution?: WorldScreenResolution;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenResolution".
 */
export interface WorldScreenResolution {
  [k: string]: unknown;
}
/**
 * Authored reading text on the screen face, rendered through the engine's glyph-decal tier (Puck.SdfVm.SdfWorldEngine.SetScreenDecal): a fixed monospace cell grid sampled from the world's packed font atlas at shade time — the dense-text sibling of a creation's textRuns, which stamp marched Glyph geometry. Signs, plaques, books, and monitors author this; short sculptural lettering stays a text run. Requires the world to declare a text font catalog (Text); the decal bypasses the CRT image pipeline, so no image source competes with it on the slot.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceText".
 */
export interface WorldScreenSourceText {
  $type?: "text";
  /**
   * The text rows, top-down; each row maps onto one grid row of cells (row-major, one scalar per cell — no kerning or shaping on this tier).
   */
  lines: StringList;
  /**
   * The text catalog font name; null selects the catalog's default font.
   */
  font?: string | null;
  /**
   * The cell grid's column count, or null to fit the widest line. The grid (columns x rows) is capped by the engine's per-screen decal cell budget (Puck.SignedDistance.SdfScreenDecalLayout.MaxScreenDecalCells).
   */
  columns?: number | null;
  /**
   * The cell grid's row count, or null to fit the line count.
   */
  rows?: number | null;
  /**
   * The letter color as #RRGGBB, or null for white.
   */
  foreground?: string | null;
  /**
   * The cell background color as #RRGGBB, or null for black.
   */
  background?: string | null;
}
/**
 * A declared probe's texture output — the frame a kind that declares an output writes each cycle (a relit camera frame, a mask), published like a camera feed. The probe must be a probes row of this document; whether its kind writes a texture is checked at boot against the kind's manifest.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenSourceProbe".
 */
export interface WorldScreenSourceProbe {
  $type?: "probe";
  /**
   * The probes[].id whose output this slot shows.
   */
  id: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenRoute".
 */
export interface WorldScreenRoute {
  /**
   * Whether a player may engage this screen.
   */
  engageable?: boolean;
  /**
   * The world-unit radius a player must be inside to engage (meaningful only when Engageable). Validated finite and non-negative.
   */
  engageRadius?: number;
  /**
   * When set, engaging the screen first boots the selected magazine entry (the "walk over, press the button, the screen lights" gesture), so the interaction is one act rather than an insert then an engage.
   */
  autoInsert?: boolean;
  /**
   * The world-event channel whose arrival on a body engages this screen, or null (the default) for a route that does not answer gestures. The author chooses this name freely; the engine never special-cases a spelling. Omitted from the wire when null.
   */
  engageChannel?: string | null;
  /**
   * Same, for advancing the magazine selector. Omitted from the wire when null.
   */
  cycleChannel?: string | null;
  /**
   * The declared channel names an application onto this screen reaches — a masked-out channel keeps flowing to the source body's own pose (when the own-body application is retained) but never reaches this screen. null (the default) reaches every declared channel. Omitted from the wire when null.
   */
  channels?: (string | null)[] | null;
  /**
   * The Name whose Pad map an application onto this screen wears, or null for the engine's default pad map. The named kit must carry a pad map — refused by name otherwise. Omitted from the wire when null.
   */
  kit?: string | null;
}
/**
 * A row's solidity facet — it participates in contact resolution using its own declared shape. Presence is the whole switch; null means decoration — the row is drawn but bodies pass through it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSolid".
 */
export interface WorldSolid {
  /**
   * The signed skin added to the shape for contact purposes. Positive fattens the collider past the drawn surface; negative lets a body sink in. Compensates the smooth-union blend.
   */
  margin: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldScreenMagazine".
 */
export interface WorldScreenMagazine {
  /**
   * The ordered source list (at least one entry).
   *
   * Items: The signal carried by a WorldScreen's lit face. A source declares which provider feeds a slot; the engine resolves and samples it. The $type string is the JSON discriminator; a new source kind is a new derived record plus its JsonDerivedTypeAttribute line.
   */
  entries: (WorldScreenSource | null)[];
  /**
   * The 0-based entry the selector starts on (what screen.select advances from), not what the screen boots showing — a screen always wakes on its declared Source (the one-live-console ceiling depends on this). Live selection drifts from this and is folded back by world.save (see Puck.World.WorldSessionCapture).
   */
  selected?: number;
  /**
   * Whether advancing past the last entry returns to the first (the arcade cabinet's wrapping cycle); when false the selector clamps at both ends.
   */
  wrap?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCamera".
 */
export interface WorldCamera {
  /**
   * The camera's stable name — the handle a View screen / layout slot samples by.
   */
  name: string;
  /**
   * What the camera rides, or null for the world reference frame (or for Anchors to decide).
   */
  anchor: WorldAnchor | null;
  /**
   * The independent local motion, aim, and lens axes.
   */
  rig: WorldCameraProgram;
  /**
   * The offscreen render width in pixels.
   */
  renderWidth: number;
  /**
   * The offscreen render height in pixels.
   */
  renderHeight: number;
  /**
   * Ranked anchor candidates, first holding wins each frame — a portrait camera that rides the speaking character while they speak and the seat's own avatar otherwise. Refused beside Anchor; a single unconditional anchor is Anchor.
   */
  anchors?: (WorldCameraAnchorCandidate | null)[] | null;
}
/**
 * Rides one population entity's ROOT pose — a walking avatar's whole-body position and orientation.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchorEntity".
 */
export interface WorldAnchorEntity {
  $type?: "entity";
  /**
   * The 0-based entity index, bounded by the world's authored population capacity.
   */
  index: number;
}
/**
 * Rides one entity look's authored part pose rather than its whole-body root. The active look publishes the mapping from PartId to its packed transform slot; the slot remains an engine detail.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchorEntityPart".
 */
export interface WorldAnchorEntityPart {
  $type?: "entityPart";
  /**
   * The 0-based entity index.
   */
  index: number;
  /**
   * The ordinal, case-sensitive part identifier published by the entity's active look.
   */
  partId: string;
}
/**
 * Rides a placement INSTANCE's stamped transform — a creation stamped into the world by reference (the same placement-reference shape CreationCameraDocument uses), optionally narrowed to one of its own authored shapes rather than the stamp's root.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchorPlacement".
 */
export interface WorldAnchorPlacement {
  $type?: "placement";
  /**
   * The referenced Id (must resolve).
   */
  placementId: string;
  /**
   * The referenced creation's ShapeDocument.Id to ride, or null for the placement's own stamped root transform.
   */
  shapeId: number | null;
}
/**
 * Rides the smoothed CENTROID of a set of population entities — the establishing-shot anchor. Also publishes the set's SPREAD (mean distance from the centroid), which a camera program's Offset consumes through its SpreadPullback. A group has no facing, so its orientation resolves to identity.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchorGroup".
 */
export interface WorldAnchorGroup {
  $type?: "group";
  /**
   * The 0-based entity indices in the set, or null for the whole live population (every active entity). Each index is validated against the authored population capacity.
   */
  indices: Int32List | null;
  /**
   * The exponential smoothing rate (per second) the centroid/spread ease at (validated positive and finite) — seeded un-smoothed on first resolve so a camera does not fly in from the origin.
   */
  smoothRate: number;
}
/**
 * Rides a local seat's avatar — the body the seat perceives as its own, so possession follows — at its root or at a named part. Numbernull is the enclosing seat scope (the seat a HUD frame or view is being resolved for), an explicit number is 1-based. Presentation-only: a camera or speaker on this anchor is resolved per seat.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchorSeat".
 */
export interface WorldAnchorSeat {
  $type?: "seat";
  number?: number | null;
  partId?: string | null;
}
/**
 * Rides the body that most recently spoke (see OverlayPredicate.Speaking), at its root or a named part; resolves nothing until something has spoken.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAnchorRecentSpeaker".
 */
export interface WorldAnchorRecentSpeaker {
  $type?: "recentSpeaker";
  partId?: string | null;
}
/**
 * An authored camera program — the ordered op list a rig resolves through every frame.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgram".
 */
export interface WorldCameraProgram {
  /**
   * The stable name a Blend op selects this program by.
   */
  name: string;
  /**
   * The instruction-set version.
   */
  version: string;
  /**
   * The selected ops, in authored evaluation order.
   */
  operations: WorldCameraProgramOpList;
  /**
   * Gets the program's Anchor op, or null when absent (an absent anchor starts from Reference implicitly).
   */
  anchorOp?: WorldCameraProgramOpAnchorNullable | null;
  /**
   * Gets the program's Blend op, or null.
   */
  blendOp?: WorldCameraProgramOpBlendNullable | null;
  /**
   * Gets the program's ClampPitch op, or null.
   */
  clampPitchOp?: WorldCameraProgramOpClampPitchNullable | null;
  /**
   * Gets the program's Fov op, or null.
   */
  fovOp?: WorldCameraProgramOpFovNullable | null;
  /**
   * Gets the program's LookAt op, or null.
   */
  lookAtOp?: WorldCameraProgramOpLookAtNullable | null;
  /**
   * Gets the program's Offset op, or null.
   */
  offsetOp?: WorldCameraProgramOpOffsetNullable | null;
  /**
   * Gets the program's Orbit op, or null.
   */
  orbitOp?: WorldCameraProgramOpOrbitNullable | null;
  /**
   * Gets the program's Path op, or null.
   */
  pathOp?: WorldCameraProgramOpPathNullable | null;
  /**
   * Gets the program's Dynamics op, or null.
   */
  dynamicsOp?: WorldCameraProgramOpDynamicsNullable | null;
  /**
   * Gets the program's Select op, or null.
   */
  selectOp?: WorldCameraProgramOpSelectNullable | null;
}
/**
 * Establishes the CURRENT subject — the pose Offset/Orbit place the eye relative to, and LookAt aims along the facing of when it names no subject of its own. Also re-seeds the eye at the resolved subject's own position (the "first person" default before any Offset/Orbit runs). At most one per program, and it must be the first operation when present; a program that omits it starts from Reference implicitly.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpAnchor".
 */
export interface WorldCameraProgramOpAnchor {
  $type?: "anchor";
  /**
   * The subject to resolve.
   */
  subject: WorldCameraSubject;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * The program's externally supplied reference pose — a named camera's own WorldAnchor, or a seat rig's currently perceived body (the seat's own avatar, or a possessed camera body). Resolved outside the program and handed to it as the SdfAnchor the compiled rig receives every frame.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSubjectReference".
 */
export interface WorldCameraSubjectReference {
  $type?: "reference";
}
/**
 * Rides a placement's authored stamped transform (position only — a placement carries no live facing through this static resolve, the same limitation a placeable camera's own Placement arm and a speaker share).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSubjectPlacement".
 */
export interface WorldCameraSubjectPlacement {
  $type?: "placement";
  /**
   * The referenced Id (must resolve).
   */
  placementId: string;
  /**
   * The referenced creation's own shape to ride, or null for the placement's stamped root transform.
   */
  shapeId?: number | null;
}
/**
 * A fixed world-space point.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSubjectWorldPoint".
 */
export interface WorldCameraSubjectWorldPoint {
  $type?: "worldPoint";
  /**
   * The world-space position.
   */
  point: DocumentVector3;
}
/**
 * Places the eye at an offset from the current subject's pose.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpOffset".
 */
export interface WorldCameraProgramOpOffset {
  $type?: "offset";
  /**
   * The offset.
   */
  value: DocumentVector3;
  /**
   * Whether Value uses world axes rather than the subject's own.
   */
  worldAxes?: boolean;
  /**
   * The group-spread multiplier applied to the offset — meaningful only when the program's OWN externally supplied reference is a Group establishing shot (zero, and thus inert, everywhere else).
   */
  spreadPullback?: number;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Sets the aim target.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpLookAt".
 */
export interface WorldCameraProgramOpLookAt {
  $type?: "lookAt";
  /**
   * The subject to look at, or null to look along the current subject's own forward axis from the eye, at FocusDistance.
   */
  subject: WorldCameraSubjectNullable | null;
  /**
   * An offset from the resolved Subject's pose; ignored when Subject is null.
   */
  targetOffset?: DocumentVector3;
  /**
   * Whether TargetOffset uses world axes rather than the subject's own.
   */
  worldAxes?: boolean;
  /**
   * The finite target distance along the current subject's forward axis, used only when Subject is null.
   */
  focusDistance?: number;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Orbits the eye about its pivot. Yaw and Pitch are live-bindable like Fov's field of view: a seat rig whose yaw reads a state cell turns the CAMERA when that cell changes — look behind is state.look.behind flipping between 0 and π — while the seat's facing (what steering and movement resolve against) is untouched, because the facing never includes the rig's offsets.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpOrbit".
 */
export interface WorldCameraProgramOpOrbit {
  $type?: "orbit";
  /**
   * The finite positive orbit distance.
   */
  distance: number;
  /**
   * The orbit heading in radians, as a literal or numeric state binding.
   */
  yaw: BindableScalar;
  /**
   * The orbit tilt in radians, as a literal or numeric state binding.
   */
  pitch: BindableScalar;
  /**
   * The world-axis offset from the current subject's origin to the pivot.
   */
  pivotOffset?: DocumentVector3;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Establishes the CURRENT subject at a point sampled from a named curves row by arc-length fraction, and re-seeds the eye there — the same subject-seeding role Anchor plays, so at most one of the two may appear in a program, and when present this must be the first operation. The sampled subject's orientation faces the curve's own tangent direction at that point. A following Offset with WorldAxes: false (the default) rotates its offset into that tangent frame, and a following LookAt with no subject of its own looks ahead along it; a following Orbit pivots at the traveling point but resolves its own yaw/pitch as literal world-frame angles regardless of the tangent — it never reads the subject's orientation. A dollied eye is path + LookAt + Fov; a dollied pivot is path + Orbit with an authored yaw that already accounts for the curve's own heading. Composes with Dynamics's boom follower untouched. Carries no rate field: a constant-rate dolly binds Fraction to a state row carrying the advance trait (base + rate·ticks) rather than duplicating a rate spelling here.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpPath".
 */
export interface WorldCameraProgramOpPath {
  $type?: "path";
  /**
   * The referenced curves row name; must resolve.
   */
  curve: string;
  /**
   * The curve's arc-length fraction to sample, in [0, 1] for an open row (values outside clamp) or unrestricted for a closed row (values wrap) — a literal, or a state binding.
   */
  fraction: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Sets the second-order response the resolved eye/target boom eases through. Read by the caller after resolving a frame — never affects the resolved pose itself. At most one per program.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpDynamics".
 */
export interface WorldCameraProgramOpDynamics {
  $type?: "dynamics";
  /**
   * The referenced dynamics row name; must resolve.
   */
  row: string;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Clamps the effective pitch the NEXT Orbit op resolves with (its authored Pitch plus any live seat delta) to [MinPitch, MaxPitch]. At most one per program; must precede the Orbit op it governs. A joined seat's own views.seatControl band already clamps its LIVE delta before this ever runs — this op exists for a program with no seat behind it (a named camera orbiting a state-bound pitch).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpClampPitch".
 */
export interface WorldCameraProgramOpClampPitch {
  $type?: "clampPitch";
  /**
   * The minimum pitch, radians.
   */
  minPitch: number;
  /**
   * The maximum pitch, radians.
   */
  maxPitch: number;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Sets the rendered vertical field of view, radians — a literal, or a state.<row>[.<key>] binding so a world rule can pull focus or frame a moment (decisions in the sim, framing in presentation). At most one per program.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpFov".
 */
export interface WorldCameraProgramOpFov {
  $type?: "fov";
  /**
   * The field of view.
   */
  fieldOfViewRadians: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Evaluates two other authored programs by name and linearly interpolates their resolved eye, target, and field of view — the whole document's camera-program table (every cameras[].rig plus views.seatRig/views.cameraRig) is the namespace A/B resolve against. At most one per program; refused when it would create a reference cycle.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpBlend".
 */
export interface WorldCameraProgramOpBlend {
  $type?: "blend";
  /**
   * The program resolved at Weight 0.
   */
  a: string;
  /**
   * The program resolved at Weight 1.
   */
  b: string;
  /**
   * The blend weight in [0, 1] — a literal, or a state binding.
   */
  weight: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Evaluates exactly one of several other named programs, chosen by a live key — the state-driven counterpart of Blend's two-way continuous mix: a rule-advanced integer row selects a whole framing outright rather than easing toward one, so a document can drive which of N stations a single camera program shows without a per-frame console override. The winning program's eye, target, field of view, and dynamics response pass through unchanged (no lerp). At most one per program; refused when it would create a reference cycle.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpSelect".
 */
export interface WorldCameraProgramOpSelect {
  $type?: "select";
  /**
   * The selecting value — a literal, or (the intended use) a state.<row>[.<key>] binding — rounded to the nearest whole number and matched against Cases.
   */
  key: BindableScalar;
  /**
   * The candidate programs, keyed by Value. At most one case per value.
   */
  cases: WorldCameraSelectCaseList;
  /**
   * The program resolved when no case's value matches Key.
   */
  default: string;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * One Select candidate: the program named by Program wins when the op's key rounds to Value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraSelectCase".
 */
export interface WorldCameraSelectCase {
  /**
   * The matching key value.
   */
  value: number;
  /**
   * The candidate program's name — the same blend namespace Blend resolves against (every cameras[].rig, plus views.seatRig/views.cameraRig).
   */
  program: string;
}
/**
 * Establishes the CURRENT subject — the pose Offset/Orbit place the eye relative to, and LookAt aims along the facing of when it names no subject of its own. Also re-seeds the eye at the resolved subject's own position (the "first person" default before any Offset/Orbit runs). At most one per program, and it must be the first operation when present; a program that omits it starts from Reference implicitly.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpAnchorNullable".
 */
export interface WorldCameraProgramOpAnchorNullable {
  /**
   * The subject to resolve.
   */
  subject: WorldCameraSubject;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Evaluates two other authored programs by name and linearly interpolates their resolved eye, target, and field of view — the whole document's camera-program table (every cameras[].rig plus views.seatRig/views.cameraRig) is the namespace A/B resolve against. At most one per program; refused when it would create a reference cycle.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpBlendNullable".
 */
export interface WorldCameraProgramOpBlendNullable {
  /**
   * The program resolved at Weight 0.
   */
  a: string;
  /**
   * The program resolved at Weight 1.
   */
  b: string;
  /**
   * The blend weight in [0, 1] — a literal, or a state binding.
   */
  weight: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Clamps the effective pitch the NEXT Orbit op resolves with (its authored Pitch plus any live seat delta) to [MinPitch, MaxPitch]. At most one per program; must precede the Orbit op it governs. A joined seat's own views.seatControl band already clamps its LIVE delta before this ever runs — this op exists for a program with no seat behind it (a named camera orbiting a state-bound pitch).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpClampPitchNullable".
 */
export interface WorldCameraProgramOpClampPitchNullable {
  /**
   * The minimum pitch, radians.
   */
  minPitch: number;
  /**
   * The maximum pitch, radians.
   */
  maxPitch: number;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Sets the rendered vertical field of view, radians — a literal, or a state.<row>[.<key>] binding so a world rule can pull focus or frame a moment (decisions in the sim, framing in presentation). At most one per program.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpFovNullable".
 */
export interface WorldCameraProgramOpFovNullable {
  /**
   * The field of view.
   */
  fieldOfViewRadians: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Sets the aim target.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpLookAtNullable".
 */
export interface WorldCameraProgramOpLookAtNullable {
  /**
   * The subject to look at, or null to look along the current subject's own forward axis from the eye, at FocusDistance.
   */
  subject: WorldCameraSubjectNullable | null;
  /**
   * An offset from the resolved Subject's pose; ignored when Subject is null.
   */
  targetOffset?: DocumentVector3;
  /**
   * Whether TargetOffset uses world axes rather than the subject's own.
   */
  worldAxes?: boolean;
  /**
   * The finite target distance along the current subject's forward axis, used only when Subject is null.
   */
  focusDistance?: number;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Places the eye at an offset from the current subject's pose.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpOffsetNullable".
 */
export interface WorldCameraProgramOpOffsetNullable {
  /**
   * The offset.
   */
  value: DocumentVector3;
  /**
   * Whether Value uses world axes rather than the subject's own.
   */
  worldAxes?: boolean;
  /**
   * The group-spread multiplier applied to the offset — meaningful only when the program's OWN externally supplied reference is a Group establishing shot (zero, and thus inert, everywhere else).
   */
  spreadPullback?: number;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Orbits the eye about its pivot. Yaw and Pitch are live-bindable like Fov's field of view: a seat rig whose yaw reads a state cell turns the CAMERA when that cell changes — look behind is state.look.behind flipping between 0 and π — while the seat's facing (what steering and movement resolve against) is untouched, because the facing never includes the rig's offsets.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpOrbitNullable".
 */
export interface WorldCameraProgramOpOrbitNullable {
  /**
   * The finite positive orbit distance.
   */
  distance: number;
  /**
   * The orbit heading in radians, as a literal or numeric state binding.
   */
  yaw: BindableScalar;
  /**
   * The orbit tilt in radians, as a literal or numeric state binding.
   */
  pitch: BindableScalar;
  /**
   * The world-axis offset from the current subject's origin to the pivot.
   */
  pivotOffset?: DocumentVector3;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Establishes the CURRENT subject at a point sampled from a named curves row by arc-length fraction, and re-seeds the eye there — the same subject-seeding role Anchor plays, so at most one of the two may appear in a program, and when present this must be the first operation. The sampled subject's orientation faces the curve's own tangent direction at that point. A following Offset with WorldAxes: false (the default) rotates its offset into that tangent frame, and a following LookAt with no subject of its own looks ahead along it; a following Orbit pivots at the traveling point but resolves its own yaw/pitch as literal world-frame angles regardless of the tangent — it never reads the subject's orientation. A dollied eye is path + LookAt + Fov; a dollied pivot is path + Orbit with an authored yaw that already accounts for the curve's own heading. Composes with Dynamics's boom follower untouched. Carries no rate field: a constant-rate dolly binds Fraction to a state row carrying the advance trait (base + rate·ticks) rather than duplicating a rate spelling here.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpPathNullable".
 */
export interface WorldCameraProgramOpPathNullable {
  /**
   * The referenced curves row name; must resolve.
   */
  curve: string;
  /**
   * The curve's arc-length fraction to sample, in [0, 1] for an open row (values outside clamp) or unrestricted for a closed row (values wrap) — a literal, or a state binding.
   */
  fraction: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Sets the second-order response the resolved eye/target boom eases through. Read by the caller after resolving a frame — never affects the resolved pose itself. At most one per program.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpDynamicsNullable".
 */
export interface WorldCameraProgramOpDynamicsNullable {
  /**
   * The referenced dynamics row name; must resolve.
   */
  row: string;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * Evaluates exactly one of several other named programs, chosen by a live key — the state-driven counterpart of Blend's two-way continuous mix: a rule-advanced integer row selects a whole framing outright rather than easing toward one, so a document can drive which of N stations a single camera program shows without a per-frame console override. The winning program's eye, target, field of view, and dynamics response pass through unchanged (no lerp). At most one per program; refused when it would create a reference cycle.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraProgramOpSelectNullable".
 */
export interface WorldCameraProgramOpSelectNullable {
  /**
   * The selecting value — a literal, or (the intended use) a state.<row>[.<key>] binding — rounded to the nearest whole number and matched against Cases.
   */
  key: BindableScalar;
  /**
   * The candidate programs, keyed by Value. At most one case per value.
   */
  cases: WorldCameraSelectCaseList;
  /**
   * The program resolved when no case's value matches Key.
   */
  default: string;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCameraAnchorCandidate".
 */
export interface WorldCameraAnchorCandidate {
  /**
   * What the camera rides while this candidate wins.
   */
  anchor: WorldAnchor | null;
  /**
   * The condition, evaluated for the seat the view is resolved for.
   */
  when?: OverlayPredicateNullable | null;
}
/**
 * The fact holds this frame.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateNow".
 */
export interface OverlayPredicateNow {
  $type?: "now";
  fact: OverlayFact;
}
/**
 * The fact held within the last WindowSeconds.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateRecently".
 */
export interface OverlayPredicateRecently {
  $type?: "recently";
  /**
   * The fact whose recency is tested.
   */
  fact: OverlayFact;
  /**
   * How long after the fact last held the predicate still holds in full.
   */
  windowSeconds: number;
  /**
   * How long after the window the predicate's PRESENCE eases from 1 to 0 — a surface reading presence rather than the boolean fades out instead of cutting, and the predicate still HOLDS (presence above 0) throughout the fade. 0 cuts at the window's end.
   */
  fadeSeconds?: number;
}
/**
 * Every predicate holds.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateAll".
 */
export interface OverlayPredicateAll {
  $type?: "all";
  predicates: OverlayPredicateListNonNullable;
}
/**
 * At least one predicate holds.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateAny".
 */
export interface OverlayPredicateAny {
  $type?: "any";
  predicates: OverlayPredicateList;
}
/**
 * The predicate does not hold.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateNot".
 */
export interface OverlayPredicateNot {
  $type?: "not";
  predicate: OverlayPredicate;
}
/**
 * The subject spoke within the last WindowSeconds — a chat line, a dialogue line, a live voice: every speech path stamps the same presentation clock, so the predicate reads one fact whatever produced it. Presence eases across FadeSeconds after the window like Recently.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateSpeaking".
 */
export interface OverlayPredicateSpeaking {
  $type?: "speaking";
  subject: OverlaySubject;
  windowSeconds: number;
  fadeSeconds?: number;
}
/**
 * A local seat's avatar — null is the enclosing seat scope (the seat whose panel, bar, or camera is being evaluated), an explicit number is 1-based like Camera.Seat.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubjectSeat".
 */
export interface OverlaySubjectSeat {
  $type?: "seat";
  number?: number | null;
}
/**
 * A placement instance, optionally one of its shapes.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubjectPlacement".
 */
export interface OverlaySubjectPlacement {
  $type?: "placement";
  placementId: string;
  shapeId?: number | null;
}
/**
 * A population entity by 0-based index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubjectEntity".
 */
export interface OverlaySubjectEntity {
  $type?: "entity";
  index: number;
}
/**
 * Any joined local seat's avatar — the predicate holds when it holds for at least one.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubjectAnySeat".
 */
export interface OverlaySubjectAnySeat {
  $type?: "anySeat";
}
/**
 * The body that most recently spoke (see Speaking); nothing until something has.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlaySubjectRecentSpeaker".
 */
export interface OverlaySubjectRecentSpeaker {
  $type?: "recentSpeaker";
}
/**
 * The subject is within Distance world units of Of (the enclosing seat's avatar when null), read off the presentation poses each frame; an unresolvable subject is infinitely far.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateNear".
 */
export interface OverlayPredicateNear {
  $type?: "near";
  subject: OverlaySubject;
  distance: number;
  of?: OverlaySubjectNullable | null;
}
/**
 * A state cell compares to a literal: Binding is state.<row>[.<key>], and exactly one of Value (numeric rows) or Text (text rows, compared ordinally; only Equal/NotEqual) is authored. The same cell a bar's layoutCell or a wheel sector writes.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OverlayPredicateState".
 */
export interface OverlayPredicateState {
  $type?: "state";
  binding: string;
  comparison?: ActionStateComparison;
  value?: number | null;
  text?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBodiesDefaults".
 */
export interface WorldBodiesDefaults {
  localSeats?: number | null;
  seatActivation?: SeatActivationPolicy[] | null;
  networkPlayers?: number;
  defaultPeerSource?: IntentSource;
  seatSpawns?: (string | null)[] | null;
  distribution?: WorldDistribution;
  peerVariation?: WorldPopulationVariation;
  seatVariation?: WorldPopulationVariation;
  peerColors?: WorldSequenceNullable;
  capacity?: number | null;
  reconnectGraceSeconds?: number;
  capacityRow?: string | null;
  disclosure?: WorldObserverDisclosure | null;
  scaleRow?: string | null;
  sleepAfterTicks?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "Shape".
 */
export interface Shape {
  $type: "producer";
  name: string;
}
/**
 * A spatial region composed with the deterministic sequence that fills it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistribution".
 */
export interface WorldDistribution {
  /**
   * The region to fill.
   */
  region: WorldDistributionRegion;
  /**
   * The per-index fill sequence.
   */
  fill: WorldSequence;
}
/**
 * A planar disc centered on the consumer's origin.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistributionRegionDisc".
 */
export interface WorldDistributionRegionDisc {
  $type?: "disc";
  /**
   * The disc radius.
   */
  radius: number;
  /**
   * The radial fill count, or null to use the consumer's requested count.
   */
  sampleCount?: number | null;
}
/**
 * A cycle of authored spawn poses, each expanded by a planar square.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistributionRegionPoints".
 */
export interface WorldDistributionRegionPoints {
  $type?: "points";
  /**
   * The spawn-point names.
   */
  names: (string | null)[];
  /**
   * The square half-extent on X and Z.
   */
  halfExtent: number;
}
/**
 * A finite two-axis lattice local to a placement.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistributionRegionLattice".
 */
export interface WorldDistributionRegionLattice {
  $type?: "lattice";
  /**
   * The first per-copy step.
   */
  stepA: DocumentVector3;
  /**
   * The copy count along StepA.
   */
  countA: number;
  /**
   * The second per-copy step.
   */
  stepB: DocumentVector3;
  /**
   * The copy count along StepB.
   */
  countB: number;
}
/**
 * Deterministic hash-lattice fBm patch admission over a placement-local grid, centered on the placement — one instance at the center of every admitted cell. The placement twin of Noise: same fixed-point fBm, same seed fold against generation.worldSeed, same threshold semantics; here admission stamps a creation copy instead of writing a field value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistributionRegionNoise".
 */
export interface WorldDistributionRegionNoise {
  $type?: "noise";
  /**
   * The local grid's cubic cell edge, world units.
   */
  cellSize: number;
  /**
   * Cells along the placement's local +X.
   */
  width: number;
  /**
   * Cells along the placement's local +Z.
   */
  depth: number;
  /**
   * Noise-cell edge in lattice cells (see Frequency). At least 1.
   */
  frequency: number;
  /**
   * The patch admission level in [0, 1); higher = sparser patches.
   */
  threshold?: number;
  /**
   * Octave count, 1..4.
   */
  octaves?: number;
  /**
   * The hash seed, folded with the world seed.
   */
  seed?: number;
}
/**
 * One jittered instance per Spacing-cell block over a placement-local grid, centered on the placement. The placement twin of Scatter: same integer PCG3D block jitter, same seed fold — every block materializes exactly the one jittered point (never a filled neighborhood), so the instance count is exact and seed-independent: ceil(Width/Spacing) x ceil(Depth/Spacing).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDistributionRegionScatter".
 */
export interface WorldDistributionRegionScatter {
  $type?: "scatter";
  /**
   * The local grid's cubic cell edge, world units.
   */
  cellSize: number;
  /**
   * Cells along the placement's local +X.
   */
  width: number;
  /**
   * Cells along the placement's local +Z.
   */
  depth: number;
  /**
   * The scatter block edge in cells (at least 2).
   */
  spacing: number;
  /**
   * The jitter inset in cells (at least 1; at most spacing/2 — a point never leaves its block, mirroring Radius's own bound).
   */
  radius?: number;
  /**
   * The hash seed, folded with the world seed.
   */
  seed?: number;
}
/**
 * A deterministic index-to-sample declaration shared by distributions, row assignment, color, and population variation. The document selects the sequence and its phase; the engine owns its exact arithmetic.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSequence".
 */
export interface WorldSequence {
  /**
   * The sequence name: None, Index, Additive, R1, or R2.
   */
  name: string;
  /**
   * The signed phase added to the caller's stable index before sampling.
   */
  offset: number;
  /**
   * The turn-sized increment for Additive; zero for every other sequence.
   */
  step: number;
}
/**
 * The three independently authored sequences that seed a body's producer state.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPopulationVariation".
 */
export interface WorldPopulationVariation {
  /**
   * The angular phase sequence.
   */
  phase: WorldSequence;
  /**
   * The scalar weave-frequency variation sequence.
   */
  weave: WorldSequence;
  /**
   * The paired activity-rate and altitude variation sequence.
   */
  activity: WorldSequence;
}
/**
 * A deterministic index-to-sample declaration shared by distributions, row assignment, color, and population variation. The document selects the sequence and its phase; the engine owns its exact arithmetic.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSequenceNullable".
 */
export interface WorldSequenceNullable {
  /**
   * The sequence name: None, Index, Additive, R1, or R2.
   */
  name: string;
  /**
   * The signed phase added to the caller's stable index before sampling.
   */
  offset: number;
  /**
   * The turn-sized increment for Additive; zero for every other sequence.
   */
  step: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldObserverDisclosure".
 */
export interface WorldObserverDisclosure {
  /**
   * Which bodies an observer is delivered.
   */
  mode?: WorldObserverDisclosureMode;
  /**
   * The disclosure radius in metres for Radius; authored only for that mode.
   */
  radius?: number | null;
  /**
   * The minimum interval between remote projection snapshots. Zero requests every authority tick; the default is 0.03 seconds (30 Hz on the standard 240 Hz authority after whole-step quantization). Local in-process sinks are unaffected.
   */
  updateSeconds?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlayerDefaults".
 */
export interface WorldPlayerDefaults {
  identities?: (WorldIdentitySeed | null)[] | null;
  neutralColor?: string | null;
  colorSequence?: WorldSequenceNullable | null;
  saturation?: number;
  value?: number;
  colorSearchLimit?: number;
  noseFactor?: number;
  pickerThreshold?: number;
  pickerNeutralColor?: string | null;
  pickerNeutralBlend?: number;
  seatCameraFeel?: WorldSeatCameraFeel | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIdentitySeed".
 */
export interface WorldIdentitySeed {
  /**
   * The stable profile id.
   */
  id: SafeName;
  /**
   * The display name.
   */
  name: string;
  /**
   * The body color as #RRGGBB.
   */
  color: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatCameraFeel".
 */
export interface WorldSeatCameraFeel {
  /**
   * The yaw response in radians per pixel of raw pointer motion along the X axis.
   */
  yawSensitivity: number;
  /**
   * The pitch response in radians per pixel of raw pointer motion along the Y axis.
   */
  pitchSensitivity: number;
  /**
   * Whether the final semantic yaw response (pointer, stick, and gyro) is inverted.
   */
  invertYaw: boolean;
  /**
   * Whether the final semantic pitch response (pointer, stick, and gyro) is inverted.
   */
  invertPitch: boolean;
  /**
   * The look stick's yaw/pitch rate in radians per second at full deflection.
   */
  stickLookRate: number;
  /**
   * The optional full-axis gyro projection. Absent resolves to Default.
   */
  gyro?: WorldSeatGyro | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatGyro".
 */
export interface WorldSeatGyro {
  /**
   * The dimensionless multiplier applied after projection.
   */
  scale?: number;
  /**
   * Independent X/Y/Z dead zones in radians per second.
   *
   * @minItems 3
   * @maxItems 3
   */
  deadZone?: [number, number, number];
  /**
   * Whether the physical X angular-velocity axis is inverted before projection.
   */
  invertX?: boolean;
  /**
   * Whether the physical Y angular-velocity axis is inverted before projection.
   */
  invertY?: boolean;
  /**
   * Whether the physical Z angular-velocity axis is inverted before projection.
   */
  invertZ?: boolean;
  /**
   * The X/Y/Z projection weights producing semantic look-right angular velocity.
   *
   * @minItems 3
   * @maxItems 3
   */
  yaw?: [number, number, number];
  /**
   * The X/Y/Z projection weights producing semantic look-up angular velocity.
   *
   * @minItems 3
   * @maxItems 3
   */
  pitch?: [number, number, number];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldChannel".
 */
export interface WorldChannel {
  /**
   * The channel's unique, non-empty name — the vocabulary key every binding, body.press, kit Actions entry, and the addon wire resolve against.
   */
  name: string;
  /**
   * The declared value shape: bipolar [-1, 1], unipolar [0, 1], or binary.
   */
  shape: ChannelShape;
  /**
   * The engine motion role this channel claims, or null for a composition channel.
   */
  role?: ChannelRole | null;
  /**
   * Whether this channel is a kit-composition trigger. Exactly one of Role or this must be set.
   */
  composition?: boolean;
  /**
   * The binary crossing threshold in [0, 1] raw units (binary channels only); null takes DefaultBinaryThreshold (One/2).
   */
  threshold?: number | null;
  /**
   * What the MoveAdvance/MoveStrafe pair is relative to when a binding row folds into it (see ChannelFrame); the two roles must declare the same frame, and every other channel leaves it at World. Omitted from a saved document at that default.
   */
  frame?: ChannelFrame;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldTargetRegister".
 */
export interface WorldTargetRegister {
  /**
   * The game-authored register name.
   */
  name: string;
  /**
   * The greatest designation distance.
   */
  maximumRange: number;
  /**
   * The widest accepted body-forward cone.
   */
  maximumHalfAngleDegrees: number;
  /**
   * Whether solid world geometry must leave the segment unobstructed.
   */
  requiresLineOfSight: boolean;
  /**
   * An optional durable counter slot supplying the player's requested range.
   */
  rangeState?: string | null;
  /**
   * An optional durable counter slot supplying the player's requested cone half-angle.
   */
  halfAngleState?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyMotionProgram".
 */
export interface BodyMotionProgram {
  /**
   * The stable name kits use to select the program.
   */
  name: string;
  /**
   * The instruction-set version.
   */
  version: string;
  /**
   * The declared program profile that gates operations and registers.
   */
  kind: BodyProgramKind | null;
  /**
   * The selected domain operations; their phases are intrinsic and cannot be reordered.
   */
  operations: BodyMotionOp[];
  /**
   * The single source supplying the program's target, when it uses target-aware operations.
   */
  target?: BodyTargetSource | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyTargetSourceSensed".
 */
export interface BodyTargetSourceSensed {
  $type?: "sensed";
  scope: BodyTargetScope;
  range: number;
  halfAngleDegrees: number;
  requiresLineOfSight: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyTargetSourceDesignated".
 */
export interface BodyTargetSourceDesignated {
  $type?: "designated";
  register: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyTargetSourceCurveFollow".
 */
export interface BodyTargetSourceCurveFollow {
  $type?: "curve";
  curve: string;
  rate: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyTargetSourceNavigated".
 */
export interface BodyTargetSourceNavigated {
  $type?: "navigated";
  domain: string;
  register: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldKitsSection".
 */
export interface WorldKitsSection {
  /**
   * The declared kits, in order.
   */
  rows?: (WorldKit | null)[] | null;
  /**
   * The kit→entity assignment policy — ABSENT resolves to Default.
   */
  assignment?: WorldRowAssignment | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldKit".
 */
export interface WorldKit {
  name: string;
  bodyMotionProgram: string;
  motion: WorldMotion;
  producers?: {
    /**
     * One kit's named arguments for an authored producer program.
     */
    [k: string]: BodyProgramParameters | null;
  } | null;
  actions?: {
    /**
     * One authored action: what fires on press, on release, and on a body fact's edge.
     */
    [k: string]: ActionSpec | null;
  } | null;
  collider?: WorldCollider | null;
  bodyContact?: WorldBodyContactMode;
  mass?: number;
  rigid?: WorldRigid | null;
  carry?: WorldCarry | null;
  tether?: WorldTether | null;
  pad?: {
    /**
     * The neutral pad element a Pad entry maps a channel onto. Named after the engine-neutral MachinePadState's own axis/button vocabulary; a pad entry picks exactly one.
     */
    [k: string]: WorldPadElement;
  } | null;
  autonomy?: WorldAutonomyCadence | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMotion".
 */
export interface WorldMotion {
  /**
   * The kit's movement rate (see WorldSpeed).
   */
  speed: WorldSpeed;
  /**
   * The kit's steering rate (see WorldTurn).
   */
  turn: WorldTurn;
  /**
   * The ordered velocity-shaping table (see WorldShaping) the ShapeVelocity operation reads — the first row whose when gate opens governs. Required (non-empty) for a kit whose program selects ShapeVelocity; null for a kit whose program never shapes planar velocity through it (a free-flight kit that owns its whole velocity channel directly).
   */
  shaping?: (WorldShaping | null)[] | null;
  /**
   * Which frame MoveAdvance/MoveStrafe resolve in. Heading explicitly rotates the commanded planar target by the body's own integrated heading. World (the default) takes the two channels as axes already in world frame — the seat's client composes the camera yaw into the submitted intent before it ever reaches the wire, so the sim never reads a camera pose (determinism: no camera state enters simulation).
   */
  moveFrame?: MotionMoveFrame;
  /**
   * Under World only: whether the body's drawn ATTITUDE snaps to Atan2 of the commanded planar direction every tick that carries input (no turn-rate ramp, no skid) — the body angles toward its travel, a strafe included — while its HEADING (the Turn role's integral, WorldBody.FixedYaw, the frame a Heading pair moves in) holds, and the attitude returns to the heading the tick movement stops. Only the Face roles turn the heading itself. Ignored under Heading, where attitude is the integrated heading by construction. true is the default.
   */
  facingSnap?: boolean;
  /**
   * The ordered list of what may hold this body — see WorldHold — read by the ResolveHold/ApplyHold operations. A Motion-kind kit authoring none refuses validation by name: the hold list is the only spelling of a vertical channel, so a kit with no vertical law of its own still authors one row of kind None.
   */
  holds?: (WorldHold | null)[] | null;
  /**
   * How fast a solved gravity field and a measured contact normal may each turn the body's up axis — see WorldUpTurnRates. Omitted (null) reproduces the engine's own steering rates exactly; read the effective rates from UpTurn.
   */
  upTurn?: WorldUpTurnRates | null;
  /**
   * The non-walkable contact witness's latch tuning — see WorldObstructionLatch. Omitted (null) reproduces the engine's own latch exactly; read the effective latch from Obstruction.
   */
  obstruction?: WorldObstructionLatch | null;
  /**
   * The inward speed (world units/second) a grounded body on a curving surface (the up policy is not world-up) is held against while it is not already moving into it at least that fast — depenetration removes whatever the surface curves away, so a walker's own speed cannot carry it clean off a convex surface between ticks. Independent of Speed: scaling this bias with a kit's own resolved move speed over-corrects a shallow slope, where a larger inward bias converts into downhill drift under depenetration faster than it converts into held contact. The default reproduces the engine's old hardcoded constant. Must remain positive after Q48.16 compilation.
   */
  groundStick?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeed".
 */
export interface WorldSpeed {
  /**
   * Locomotion speed in world units per second — the profileless fallback a stand-in advances on (a seated player reads its live profile's speed instead, so identity.motion stays real-time).
   */
  value: number;
  /**
   * The inclusive bound a seated player's live profile speed (and the profileless Value fallback) is clamped to at seat time, or null (the default) for no bound — a feel-pinned world authors this to keep a seat's speed inside its own kit's envelope regardless of what the player's identity requests. null reproduces an unclamped resolve exactly; Min == Max pins the effective speed outright; a narrower-than-wide-open range still admits a bounded profile override.
   */
  envelope?: MotionScalarEnvelope | null;
  /**
   * The held-multiplier channel (a "boost"/"sprint"), or null for a kit with no held speed multiplier. The multiplier applies AFTER Envelope clamps the resolved value: the envelope pins the base rate, the multiplier rides on top.
   */
  held?: WorldSpeedHeld | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "MotionScalarEnvelope".
 */
export interface MotionScalarEnvelope {
  /**
   * The least admitted value (inclusive).
   */
  min?: number;
  /**
   * The greatest admitted value (inclusive) — WorldDefinitionValidator refuses Max < Min by name. Equal to Min pins the scalar outright regardless of what a profile requests.
   */
  max?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeedHeld".
 */
export interface WorldSpeedHeld {
  /**
   * The declared composition channel name read while held.
   */
  channel: string;
  /**
   * The speed multiplier while the channel reads held. Required positive.
   */
  multiplier: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldTurn".
 */
export interface WorldTurn {
  /**
   * Turn speed in radians per second at full authority (the profileless fallback counterpart to Value).
   */
  rate: number;
  /**
   * The longitudinal (drive) or local (grounded/free) speed, world units per second, at which steering authority peaks — authority rises linearly from zero at standstill and falls linearly past it toward Falloff at the kit's resolved move speed. Omitted (the default): full authority at every speed, the behavior every kit authoring no curve keeps.
   */
  referenceSpeed?: number | null;
  /**
   * The fraction of full steering authority remaining at the kit's resolved move speed, in [0, 1]. Unread while ReferenceSpeed is omitted.
   */
  falloff?: number;
  /**
   * The pitch rate (rad/s) a drive kit's Pitch channel commands; 0 (the default) locks the drive frame planar (the ground and hover variants). Positive selects the flying variant's pitched facing, clamped inside the integrator so the frame can never flip past vertical.
   */
  pitchRate?: number;
  /**
   * The drive pitch clamp (radians): how far the flying variant's facing may climb or dive before the integrator holds it, so it can never flip past vertical and invert the yaw frame mid-flight. Unread while PitchRate is zero.
   */
  maxPitch?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldShaping".
 */
export interface WorldShaping {
  /**
   * The gate that must hold for this row to win, or null for the unconditional row (permitted only as the final row). The gate reuses the action-lane predicate vocabulary, admitting body-fact kinds (now/recently/all/any/not) and held (a composition channel's own live read) — never a per-body action-state predicate.
   */
  when?: ActionPredicateNullable2 | null;
  /**
   * The along facet — see WorldShapingAlong — or null for a Dynamics row.
   */
  along?: WorldShapingAlong | null;
  /**
   * The across facet — see WorldShapingAcross — selecting the drive decomposition, or null for a row that shapes the whole vector. Refused paired with Dynamics or without Along.
   */
  across?: WorldShapingAcross | null;
  /**
   * The dynamics row a second-order follower shapes velocity through instead of Along, or null (the default) for the response/drive law. Exactly one of the two is authored per row.
   */
  dynamics?: string | null;
  /**
   * The steering-authority multiplier while this row governs — the tightened drift arc's spelling, and the neutral default for every ordinary row.
   */
  turnScale?: number;
}
/**
 * Compares a named state cell against either a fixed authored value, or another named state cell/reserved channel read live at the same evaluation. Both spellings are authorable; exactly one of Value and ComparandState may be present (refused by name when both or neither are). The comparand-row spelling is what lets a gate track a moving threshold — $tick compared against a schedule row the rule's own effects advance is "every N ticks"; a round row compared against a declared length row is a round boundary — composition over the same two-sided comparison, never a new mechanism.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateCompareState".
 */
export interface ActionPredicateCompareState {
  $type?: "compareState";
  /**
   * A declared state-section row name, or one of the reserved channels the compiler's vocabulary answers (RuleFacts and whatever a document project registers). Inside a host's per-participant action program, a named counter slot the program declares.
   */
  state: string;
  /**
   * The comparison to apply.
   */
  comparison: ActionStateComparison;
  /**
   * The authored constant comparand, or null when ComparandState spells the comparand instead.
   */
  value?: number | null;
  /**
   * The cell inside State to read — null reads the row's slot cell, which a keyed row does not have (refused by name rather than silently reading cells[0]).
   */
  key?: string | null;
  /**
   * Another declared state-section row name, or a reserved channel, read live and compared instead of Value. A dotted spelling (an author reaching for row.key in one string) is refused by name — address the cell with ComparandKey instead. Comparing across incompatible cell kinds (an int row against a fixed row, say) is refused by name — mixing scales silently is worse than naming the mismatch.
   */
  comparandState?: string | null;
  /**
   * The cell inside ComparandState, on the same (row, key) terms as Key. Refused when ComparandState names a reserved channel or is absent.
   */
  comparandKey?: string | null;
}
/**
 * Comparison of two bounded numeric expressions. Arithmetic failure makes this comparison false.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateCompareValue".
 */
export interface ActionPredicateCompareValue {
  $type?: "compareValue";
  /**
   * Left numeric expression.
   */
  left: ValueExpression;
  /**
   * The comparison operation.
   */
  comparison: ActionStateComparison;
  /**
   * Right numeric expression.
   */
  right: ValueExpression;
  /**
   * The common Int or Fixed domain; no implicit conversion is performed.
   */
  kind?: CellKind;
}
/**
 * The postfix object spelling of a ValueExpression: { "tokens": [...] }. The converter reads this shape when an expression is authored as tokens and writes it back for an expression that carries no infix text.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueExpressionTokens".
 */
export interface ValueExpressionTokens {
  /**
   * The postfix tokens, in evaluation order.
   */
  tokens: ValueToken[];
}
/**
 * An exact authored decimal, converted to the destination row's numeric kind at compile time.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenConstant".
 */
export interface ValueTokenConstant {
  $type?: "constant";
  /**
   * The exact decimal literal.
   */
  value: number;
}
/**
 * A live state cell or reserved world-rule channel.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenState".
 */
export interface ValueTokenState {
  $type?: "state";
  /**
   * The state row or reserved-channel name.
   */
  name: string;
  /**
   * The optional keyed-row cell.
   */
  key?: string | null;
}
/**
 * Consumes two values and pushes their sum.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenAdd".
 */
export interface ValueTokenAdd {
  $type?: "add";
}
/**
 * Consumes two values and pushes left minus right.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSubtract".
 */
export interface ValueTokenSubtract {
  $type?: "subtract";
}
/**
 * Consumes two values and pushes their product in the destination row's numeric domain.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenMultiply".
 */
export interface ValueTokenMultiply {
  $type?: "multiply";
}
/**
 * Consumes two values and pushes left divided by right; zero fails evaluation. The caller closes a gate, rejects an effect/decision candidate, or supplies zero affinity according to its own contract.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenDivide".
 */
export interface ValueTokenDivide {
  $type?: "divide";
}
/**
 * Consumes two values and pushes the lesser.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenMin".
 */
export interface ValueTokenMin {
  $type?: "min";
}
/**
 * Consumes two values and pushes the greater.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenMax".
 */
export interface ValueTokenMax {
  $type?: "max";
}
/**
 * Consumes value, minimum, maximum (in that authored order) and pushes the inclusive clamp.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenClamp".
 */
export interface ValueTokenClamp {
  $type?: "clamp";
}
/**
 * Consumes two values and pushes the remainder of left divided by right, truncating toward zero (Int: 37 % 40 = 37; Fixed: the raw remainder, so 2.5 % 1 = 0.5). A zero divisor fails evaluation; a divisor of -1 yields zero.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenModulo".
 */
export interface ValueTokenModulo {
  $type?: "modulo";
}
/**
 * Consumes two Int values and pushes their bitwise AND. Int expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitAnd".
 */
export interface ValueTokenBitAnd {
  $type?: "bitAnd";
}
/**
 * Consumes two Int values and pushes their bitwise OR. Int expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitOr".
 */
export interface ValueTokenBitOr {
  $type?: "bitOr";
}
/**
 * Consumes two Int values and pushes their bitwise XOR. Int expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitXor".
 */
export interface ValueTokenBitXor {
  $type?: "bitXor";
}
/**
 * Consumes one Int value and pushes its bitwise complement. Int expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitNot".
 */
export interface ValueTokenBitNot {
  $type?: "bitNot";
}
/**
 * Consumes value, count and pushes value shifted left by count bits; bits leave the top without refusal, so 1 shiftLeft 63 is the sign bit. A count outside 0..63 fails evaluation. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenShiftLeft".
 */
export interface ValueTokenShiftLeft {
  $type?: "shiftLeft";
}
/**
 * Consumes value, count and pushes the arithmetic (sign-propagating) right shift. A count outside 0..63 fails evaluation. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenShiftRight".
 */
export interface ValueTokenShiftRight {
  $type?: "shiftRight";
}
/**
 * Consumes value, count and pushes the logical (zero-filling) right shift, the bitboard walk. A count outside 0..63 fails evaluation. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenShiftRightLogical".
 */
export interface ValueTokenShiftRightLogical {
  $type?: "shiftRightLogical";
}
/**
 * Consumes two same-kind values and pushes Int 1 when equal, else 0.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenEqual".
 */
export interface ValueTokenEqual {
  $type?: "equal";
}
/**
 * Consumes two same-kind values and pushes Int 1 when unequal, else 0.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenNotEqual".
 */
export interface ValueTokenNotEqual {
  $type?: "notEqual";
}
/**
 * Consumes two same-kind values and pushes Int 1 when left is less than right, else 0.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLess".
 */
export interface ValueTokenLess {
  $type?: "less";
}
/**
 * Consumes two same-kind values and pushes Int 1 when left is at most right, else 0.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLessOrEqual".
 */
export interface ValueTokenLessOrEqual {
  $type?: "lessOrEqual";
}
/**
 * Consumes two same-kind values and pushes Int 1 when left is greater than right, else 0.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenGreater".
 */
export interface ValueTokenGreater {
  $type?: "greater";
}
/**
 * Consumes two same-kind values and pushes Int 1 when left is at least right, else 0.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenGreaterOrEqual".
 */
export interface ValueTokenGreaterOrEqual {
  $type?: "greaterOrEqual";
}
/**
 * Consumes condition, whenTrue, whenFalse (in that authored order) and pushes whenTrue when the Int condition is nonzero, else whenFalse. The two branches must share a kind; the result takes it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSelect".
 */
export interface ValueTokenSelect {
  $type?: "select";
}
/**
 * Consumes one Int value and pushes the number of set bits (0..64): a bitboard's piece count. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPopCount".
 */
export interface ValueTokenPopCount {
  $type?: "popCount";
}
/**
 * Consumes one Int value and pushes the count of zero bits above the highest set bit (64 for zero): 63 - leadingZeroCount is the highest set square, the integer log2. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLeadingZeroCount".
 */
export interface ValueTokenLeadingZeroCount {
  $type?: "leadingZeroCount";
}
/**
 * Consumes one Int value and pushes the count of zero bits below the lowest set bit (64 for zero): the lowest occupied square's index. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenTrailingZeroCount".
 */
export interface ValueTokenTrailingZeroCount {
  $type?: "trailingZeroCount";
}
/**
 * Consumes one Int value and pushes its lowest set bit alone (x & -x; zero for zero): the next piece to visit. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLowestSetBit".
 */
export interface ValueTokenLowestSetBit {
  $type?: "lowestSetBit";
}
/**
 * Consumes one Int value and pushes it with its lowest set bit cleared (x & (x - 1)): the remaining pieces after a visit. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenClearLowestSetBit".
 */
export interface ValueTokenClearLowestSetBit {
  $type?: "clearLowestSetBit";
}
/**
 * Consumes value, count and pushes the 64-bit left rotation; a count outside 0..63 fails evaluation. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenRotateLeft".
 */
export interface ValueTokenRotateLeft {
  $type?: "rotateLeft";
}
/**
 * Consumes value, count and pushes the 64-bit right rotation; a count outside 0..63 fails evaluation. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenRotateRight".
 */
export interface ValueTokenRotateRight {
  $type?: "rotateRight";
}
/**
 * Consumes one Int value and pushes its eight bytes in reverse order: on an 8x8 bitboard, the board flipped rank for rank (a vertical mirror). Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenByteSwap".
 */
export interface ValueTokenByteSwap {
  $type?: "byteSwap";
}
/**
 * Consumes one Int value and pushes its 64 bits in reverse order: on an 8x8 bitboard, the board rotated a half turn. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitReverse".
 */
export interface ValueTokenBitReverse {
  $type?: "bitReverse";
}
/**
 * Consumes an Int block width and pushes a 64-bit mask with one bit at each block's bottom. Widths are 1, 2, 4, 8, 16, 32, or 64; other widths fail evaluation. Int expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenReplicationMask".
 */
export interface ValueTokenReplicationMask {
  $type?: "replicationMask";
}
/**
 * Consumes an Int pattern and block width, in that order, and repeats the pattern across 64 bits. The width must divide 64 and the pattern must have no set bits outside its block; otherwise evaluation fails. Width 64 accepts every bit pattern unchanged. Int expressions only; the result may be negative.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenRepeatBits".
 */
export interface ValueTokenRepeatBits {
  $type?: "repeatBits";
}
/**
 * Consumes one value and pushes its negation in the same kind; the carrier's minimum fails evaluation.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenNegate".
 */
export interface ValueTokenNegate {
  $type?: "negate";
}
/**
 * Consumes one value and pushes its magnitude in the same kind; the carrier's minimum fails evaluation.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenAbs".
 */
export interface ValueTokenAbs {
  $type?: "abs";
}
/**
 * Consumes one value of either kind and pushes Int -1, 0, or 1 by its sign.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSign".
 */
export interface ValueTokenSign {
  $type?: "sign";
}
/**
 * Consumes value, mask and pushes the bits of value at the mask's set positions, packed toward bit 0 in order (pext): a bitboard's occupancy along a chosen set of squares as a dense index. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenParallelBitExtract".
 */
export interface ValueTokenParallelBitExtract {
  $type?: "parallelBitExtract";
}
/**
 * Consumes value, mask and pushes the low bits of value scattered to the mask's set positions in order (pdep): a dense index back onto its squares. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenParallelBitDeposit".
 */
export interface ValueTokenParallelBitDeposit {
  $type?: "parallelBitDeposit";
}
/**
 * Consumes value, offset, width and pushes the unsigned field of width bits starting at bit offset; width outside 1..64 or offset + width above 64 fails evaluation. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitField".
 */
export interface ValueTokenBitField {
  $type?: "bitField";
}
/**
 * Consumes value, field, offset, width and pushes value with the width bits at offset replaced by the low bits of field; the same bounds as BitField. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBitInsert".
 */
export interface ValueTokenBitInsert {
  $type?: "bitInsert";
}
/**
 * Consumes one Int mask over the named topology's cells (bit c is cell ordinal c, at most 64 cells) and pushes it with every set bit moved to that cell's neighbour in the named direction; a cell with no neighbour that way drops its bit instead of wrapping, so an attack map never crosses an edge. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBoardShift".
 */
export interface ValueTokenBoardShift {
  $type?: "boardShift";
  /**
   * A discrete topology of state.lattices with at most 64 cells.
   */
  topology: string;
  /**
   * A direction of that topology.
   */
  direction: string;
}
/**
 * Consumes one Int mask over the named topology's cells and pushes the union of that mask and every repeated shift of it in the named direction until no bit has a neighbour that way — a whole file, rank, or diagonal from one seed bit (boardFill(1, board, N)), so no author hand-writes a wrap constant. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBoardFill".
 */
export interface ValueTokenBoardFill {
  $type?: "boardFill";
  /**
   * A discrete topology of state.lattices with at most 64 cells.
   */
  topology: string;
  /**
   * A direction of that topology.
   */
  direction: string;
}
/**
 * Consumes one Int mask over the named topology's cells and pushes it carried through a point-group element of that topology (a rotation or mirror of the board), so a rule authored from one side's view reads the other side's board through the half turn. Int only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenBoardImage".
 */
export interface ValueTokenBoardImage {
  $type?: "boardImage";
  /**
   * A discrete topology of state.lattices with at most 64 cells.
   */
  topology: string;
  /**
   * An element name world.topology lists for that topology.
   */
  element: string;
}
/**
 * Szudzik's pairing of two non-negative integers into one; pairX/pairY invert it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPair".
 */
export interface ValueTokenPair {
  $type?: "pair";
}
/**
 * The first component of a paired value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairX".
 */
export interface ValueTokenPairX {
  $type?: "pairX";
}
/**
 * The second component of a paired value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairY".
 */
export interface ValueTokenPairY {
  $type?: "pairY";
}
/**
 * The pair with its components exchanged, computed without unpairing.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairSwap".
 */
export interface ValueTokenPairSwap {
  $type?: "pairSwap";
}
/**
 * The larger component of a paired value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairMax".
 */
export interface ValueTokenPairMax {
  $type?: "pairMax";
}
/**
 * The smaller component of a paired value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairMin".
 */
export interface ValueTokenPairMin {
  $type?: "pairMin";
}
/**
 * The sum of a paired value's components.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairSum".
 */
export interface ValueTokenPairSum {
  $type?: "pairSum";
}
/**
 * The absolute difference of a paired value's components.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairDifference".
 */
export interface ValueTokenPairDifference {
  $type?: "pairDifference";
}
/**
 * The pair with both components moved by the second argument.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairTranslate".
 */
export interface ValueTokenPairTranslate {
  $type?: "pairTranslate";
}
/**
 * The pair with both components multiplied by the second argument.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPairScale".
 */
export interface ValueTokenPairScale {
  $type?: "pairScale";
}
/**
 * Bit-interleaves two non-negative integers below 2^31 (x on the even bits); mortonX/mortonY invert it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenMorton".
 */
export interface ValueTokenMorton {
  $type?: "morton";
}
/**
 * The even-bit component of a Morton code.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenMortonX".
 */
export interface ValueTokenMortonX {
  $type?: "mortonX";
}
/**
 * The odd-bit component of a Morton code.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenMortonY".
 */
export interface ValueTokenMortonY {
  $type?: "mortonY";
}
/**
 * The distance along the Hilbert curve of the given order (1..31) to (x, y); hilbertX/hilbertY invert it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHilbert".
 */
export interface ValueTokenHilbert {
  $type?: "hilbert";
}
/**
 * The x of the point at a Hilbert distance for the given order.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHilbertX".
 */
export interface ValueTokenHilbertX {
  $type?: "hilbertX";
}
/**
 * The y of the point at a Hilbert distance for the given order.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHilbertY".
 */
export interface ValueTokenHilbertY {
  $type?: "hilbertY";
}
/**
 * The ring-ordered index of the hex cell at Eisenstein coordinates (q, r); hexQ/hexR invert it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexIndex".
 */
export interface ValueTokenHexIndex {
  $type?: "hex";
}
/**
 * The q coordinate of a hex index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexQ".
 */
export interface ValueTokenHexQ {
  $type?: "hexQ";
}
/**
 * The r coordinate of a hex index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexR".
 */
export interface ValueTokenHexR {
  $type?: "hexR";
}
/**
 * The ring a hex index lies on.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexRadius".
 */
export interface ValueTokenHexRadius {
  $type?: "hexRadius";
}
/**
 * The Eisenstein squared straight-line distance from the origin q² − q·r + r² of a hex index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexEuclideanSquared".
 */
export interface ValueTokenHexEuclideanSquared {
  $type?: "hexEuclideanSquared";
}
/**
 * The step distance between two hex indices.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexDistance".
 */
export interface ValueTokenHexDistance {
  $type?: "hexDistance";
}
/**
 * The hex index one step away in direction 0..5 (counterclockwise from +q; any integer, taken modulo 6).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexNeighbor".
 */
export interface ValueTokenHexNeighbor {
  $type?: "hexNeighbor";
}
/**
 * The hex index rotated about the origin by sixth turns (any integer, taken modulo 6).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexRotate".
 */
export interface ValueTokenHexRotate {
  $type?: "hexRotate";
}
/**
 * The hex index reflected across the q axis.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexMirror".
 */
export interface ValueTokenHexMirror {
  $type?: "hexMirror";
}
/**
 * The hex index with q and r exchanged.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexSwap".
 */
export interface ValueTokenHexSwap {
  $type?: "hexSwap";
}
/**
 * The hex index of the coordinate sum.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexAdd".
 */
export interface ValueTokenHexAdd {
  $type?: "hexAdd";
}
/**
 * The hex index of the coordinate difference.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexSubtract".
 */
export interface ValueTokenHexSubtract {
  $type?: "hexSubtract";
}
/**
 * The hex index of the Eisenstein product.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexMultiply".
 */
export interface ValueTokenHexMultiply {
  $type?: "hexMultiply";
}
/**
 * The hex index scaled by an integer.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexScale".
 */
export interface ValueTokenHexScale {
  $type?: "hexScale";
}
/**
 * The hex index moved by (q, r).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenHexTranslate".
 */
export interface ValueTokenHexTranslate {
  $type?: "hexTranslate";
}
/**
 * The layer holding an index in a layer sequence (index, start, step, seed): a core of seed indices wrapped by layers of start, start+step, … indices.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLayer".
 */
export interface ValueTokenLayer {
  $type?: "layer";
}
/**
 * The position of an index within its layer, for the same (index, start, step, seed) sequence.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLayerOffset".
 */
export interface ValueTokenLayerOffset {
  $type?: "layerOffset";
}
/**
 * The first index of a layer, for a (layer, start, step, seed) sequence.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLayerStart".
 */
export interface ValueTokenLayerStart {
  $type?: "layerStart";
}
/**
 * The index count of a layer, for a (layer, start, step, seed) sequence.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLayerSize".
 */
export interface ValueTokenLayerSize {
  $type?: "layerSize";
}
/**
 * The square root: the floor root of a non-negative int, or the fixed-point root of a non-negative fixed value.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareRoot".
 */
export interface ValueTokenSquareRoot {
  $type?: "sqrt";
}
/**
 * The sine of a fixed-point angle in radians; fixed expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSine".
 */
export interface ValueTokenSine {
  $type?: "sin";
}
/**
 * The cosine of a fixed-point angle in radians; fixed expressions only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenCosine".
 */
export interface ValueTokenCosine {
  $type?: "cos";
}
/**
 * The shell-ordered index of the square cell at Gaussian coordinates (x, y); squareX/squareY invert it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareIndex".
 */
export interface ValueTokenSquareIndex {
  $type?: "square";
}
/**
 * The x coordinate of a square index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareX".
 */
export interface ValueTokenSquareX {
  $type?: "squareX";
}
/**
 * The y coordinate of a square index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareY".
 */
export interface ValueTokenSquareY {
  $type?: "squareY";
}
/**
 * The shell a square index lies on: its Chebyshev distance from the origin.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareRadius".
 */
export interface ValueTokenSquareRadius {
  $type?: "squareRadius";
}
/**
 * The Manhattan distance of a square index from the origin.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareLength".
 */
export interface ValueTokenSquareLength {
  $type?: "squareLength";
}
/**
 * The Gaussian squared straight-line distance from the origin x² + y² of a square index.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareEuclideanSquared".
 */
export interface ValueTokenSquareEuclideanSquared {
  $type?: "squareEuclideanSquared";
}
/**
 * The Manhattan (rook-step) distance between two square indices.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareDistance".
 */
export interface ValueTokenSquareDistance {
  $type?: "squareDistance";
}
/**
 * The Chebyshev (king-step) distance between two square indices.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareChebyshev".
 */
export interface ValueTokenSquareChebyshev {
  $type?: "squareChebyshev";
}
/**
 * The square index one step away in direction 0..3 (east, north, west, south; any integer, taken modulo 4).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareNeighbor".
 */
export interface ValueTokenSquareNeighbor {
  $type?: "squareNeighbor";
}
/**
 * The square index rotated about the origin by quarter turns (any integer, taken modulo 4).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareRotate".
 */
export interface ValueTokenSquareRotate {
  $type?: "squareRotate";
}
/**
 * The square index reflected across the x axis.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareMirror".
 */
export interface ValueTokenSquareMirror {
  $type?: "squareMirror";
}
/**
 * The square index with x and y exchanged.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareSwap".
 */
export interface ValueTokenSquareSwap {
  $type?: "squareSwap";
}
/**
 * The square index of the coordinate sum.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareAdd".
 */
export interface ValueTokenSquareAdd {
  $type?: "squareAdd";
}
/**
 * The square index of the coordinate difference.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareSubtract".
 */
export interface ValueTokenSquareSubtract {
  $type?: "squareSubtract";
}
/**
 * The square index of the Gaussian product.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareMultiply".
 */
export interface ValueTokenSquareMultiply {
  $type?: "squareMultiply";
}
/**
 * The square index scaled by an integer.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareScale".
 */
export interface ValueTokenSquareScale {
  $type?: "squareScale";
}
/**
 * The square index moved by (x, y).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSquareTranslate".
 */
export interface ValueTokenSquareTranslate {
  $type?: "squareTranslate";
}
/**
 * The greatest common divisor of two integers' magnitudes; gcd(dx, dy) == 1 is a lattice line with no interior point.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenGreatestCommonDivisor".
 */
export interface ValueTokenGreatestCommonDivisor {
  $type?: "gcd";
}
/**
 * The least common multiple of two integers' magnitudes.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenLeastCommonMultiple".
 */
export interface ValueTokenLeastCommonMultiple {
  $type?: "lcm";
}
/**
 * The floored remainder of a by m: for a positive m the value in [0, m) congruent to a, whatever a's sign.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenFloorModulo".
 */
export interface ValueTokenFloorModulo {
  $type?: "mod";
}
/**
 * The forward (clockwise) distance from a to b around an m-cycle: mod(b − a, m).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenCycleForward".
 */
export interface ValueTokenCycleForward {
  $type?: "cycleForward";
}
/**
 * The shortest distance between a and b around an m-cycle, in either direction.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenCycleDistance".
 */
export interface ValueTokenCycleDistance {
  $type?: "cycleDistance";
}
/**
 * The smallest non-negative integer missing from a 64-bit set — the lowest clear bit (the Sprague–Grundy value of a position whose options' values are the set).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSmallestMissing".
 */
export interface ValueTokenSmallestMissing {
  $type?: "smallestMissing";
}
/**
 * Whether a non-negative integer is prime, decided exactly over the whole 64-bit range in bounded work.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenIsPrime".
 */
export interface ValueTokenIsPrime {
  $type?: "isPrime";
}
/**
 * The i-th prime for i in 0..255 (2, 3, 5, … 1619): the bounded table a Gödel multiset or a coprime stride reaches for.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenPrime".
 */
export interface ValueTokenPrime {
  $type?: "prime";
}
/**
 * n choose k, exact; zero when k exceeds n.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenChoose".
 */
export interface ValueTokenChoose {
  $type?: "choose";
}
/**
 * n! for n in 0..20.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenFactorial".
 */
export interface ValueTokenFactorial {
  $type?: "factorial";
}
/**
 * The colexicographic rank of a k-subset of 0..n−1 given as a bitmask (bit e set = element e chosen), in [0, choose(n, k)); n at most 64.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSubsetRank".
 */
export interface ValueTokenSubsetRank {
  $type?: "subsetRank";
}
/**
 * The k-subset of 0..n−1 at a colexicographic rank, as a bitmask; the inverse of subsetRank.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSubsetAt".
 */
export interface ValueTokenSubsetAt {
  $type?: "subsetAt";
}
/**
 * The i-th smallest element (i from 0) of the k-subset of 0..n−1 at a colexicographic rank, without building the subset.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenSubsetMember".
 */
export interface ValueTokenSubsetMember {
  $type?: "subsetMember";
}
/**
 * The lexicographic (Lehmer) rank of a permutation of 0..n−1 packed as nibbles — position i in bits 4i..4i+3 — in [0, n!); n at most 16.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenArrangementRank".
 */
export interface ValueTokenArrangementRank {
  $type?: "arrangementRank";
}
/**
 * The permutation of 0..n−1 at a lexicographic rank, packed as nibbles; the inverse of arrangementRank.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenArrangementAt".
 */
export interface ValueTokenArrangementAt {
  $type?: "arrangementAt";
}
/**
 * The element at position i of the permutation of 0..n−1 at a lexicographic rank.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ValueTokenArrangementMember".
 */
export interface ValueTokenArrangementMember {
  $type?: "arrangementMember";
}
/**
 * Every inner predicate holds (conjunction).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateAll".
 */
export interface ActionPredicateAll {
  $type?: "all";
  predicates: ActionPredicateListNonNullable;
}
/**
 * At least one inner predicate holds (disjunction). The list must be non-empty.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateAny".
 */
export interface ActionPredicateAny {
  $type?: "any";
  /**
   * The non-empty child-predicate list.
   */
  predicates: ActionPredicateList;
}
/**
 * Inverts one predicate.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateNotNonNullable".
 */
export interface ActionPredicateNotNonNullable {
  $type?: "not";
  /**
   * The child predicate to invert.
   */
  predicate: ActionPredicate;
}
/**
 * At least one inner predicate holds (disjunction). The list must be non-empty.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateAnyNonNullable".
 */
export interface ActionPredicateAnyNonNullable {
  $type?: "any";
  predicates: ActionPredicateList;
}
/**
 * Inverts one predicate.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionPredicateNot".
 */
export interface ActionPredicateNot {
  $type?: "not";
  predicate: ActionPredicate;
}
/**
 * The fact holds this tick.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPredicateNow".
 */
export interface WorldPredicateNow {
  $type?: "now";
  /**
   * The body fact.
   */
  fact: ActionFact;
}
/**
 * The fact held within the last WindowSeconds — a per-instance recency clock, refreshed while the fact holds and decaying otherwise (coyote time is Recently(Grounded, w)).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPredicateRecently".
 */
export interface WorldPredicateRecently {
  $type?: "recently";
  /**
   * The body fact.
   */
  fact: ActionFact;
  /**
   * The recency window.
   */
  windowSeconds: number;
}
/**
 * Whether a named timer slot has drained.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPredicateTimerElapsed".
 */
export interface WorldPredicateTimerElapsed {
  $type?: "timerElapsed";
  /**
   * The timer slot.
   */
  state: string;
}
/**
 * The named composition channel's own live read is at or above its declared threshold. Legitimate only inside a kit's shaping-row gate, where the world's channel table resolves Channel to an ordinal at kit-compile time.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPredicateHeld".
 */
export interface WorldPredicateHeld {
  $type?: "held";
  /**
   * The declared composition channel name.
   */
  channel: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldShapingAlong".
 */
export interface WorldShapingAlong {
  /**
   * The whole-vector engage rate (u/s²) while the commanded target exceeds the body's current magnitude, or — paired with Across — the drive's longitudinal accel rate while throttle commands more speed. null means converge immediately.
   */
  engage?: number | null;
  /**
   * The drive's sign-reversal rate (u/s²) while back-throttle opposes forward travel. null means reverse sign immediately. Refused without a paired Across.
   */
  reversalRate?: number | null;
  /**
   * The whole-vector release rate (u/s²) while the target does not exceed the current magnitude, or — paired with Across — the drive's coast rate toward rest with throttle centered, and the decay rate while over the commanded speed. null means converge immediately.
   */
  release?: number | null;
  /**
   * The backward speed (u/s) full back-throttle converges on from rest; absence forbids travelling backward. Refused whenever authored without a paired Across.
   */
  backwardSpeed?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldShapingAcross".
 */
export interface WorldShapingAcross {
  /**
   * The lateral convergence rate (u/s²) toward zero slip while this row governs, or null to remove slip immediately.
   */
  lateral?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHold".
 */
export interface WorldHold {
  /**
   * The row's name, unique within the list — the body.hold read-back token.
   */
  name: string;
  /**
   * Whether this row needs a surface (Surface) or holds the body where it is (Free).
   */
  bond: BodyHoldBond;
  /**
   * What holds the body once the row is taken.
   */
  hold: BodyHoldKind;
  /**
   * The inclusive angle band, in degrees, between an admitted surface normal and gravity-up: [0, 60] is floors, [60, 120] walls, [0, 180] everything. Required for Surface, refused for Free.
   */
  cone?: DocumentVector2;
  /**
   * The inward pull, world units per second, under Pull. Applied as a positional standoff, so it closes a gap the surface opens without ever pushing the body through it.
   */
  pull?: number;
  /**
   * The fraction of gravity cancelled under Lift: 1 hovers, 0.5 halves the fall.
   */
  lift?: number;
  /**
   * The travel speed along the hold's tangent plane, world units per second, or null to ride the kit's own resolved move speed.
   */
  speed?: number | null;
  /**
   * How far a surface row's probes search, world units. Required positive for Surface.
   */
  reach?: number;
  /**
   * How far the body's up axis blends from gravity-up toward the surface normal, in [0, 1].
   */
  upLean?: number;
  /**
   * Where the hold's frame takes its forward direction when the surface leaves it free.
   */
  forward?: BodyHoldForward;
  /**
   * Whether driving into an admitted face takes this row with no channel press.
   */
  onDrive?: boolean;
  /**
   * The least alignment, in [0, 1], between the commanded direction and the face's inward normal before OnDrive takes it. Inert without it.
   */
  driveAlignment?: number;
  /**
   * The declared channel name whose held read drops this row, or null for a row no channel can drop.
   */
  release?: string | null;
  /**
   * What the row spends while held, or null for a row that spends nothing.
   */
  spend?: WorldHoldSpend | null;
  /**
   * The medium's own displacement law. Required for Medium and refused on every other bond.
   */
  medium?: WorldHoldMedium | null;
  /**
   * The vertical arc this row falls under. Required for Gravity and Lift; refused for Pull, None, and every Medium row.
   */
  gravity?: WorldHoldGravity | null;
  /**
   * The vertical-channel envelope Gravity's terminal fall speed and Medium's terminal rise/sink speeds share. Required for a Medium bond and for a Gravity/Lift hold short of full lift; refused otherwise.
   */
  envelope?: WorldHoldEnvelope | null;
  /**
   * The fraction of the kit's resolved move speed the MoveUp role commands vertically while this row holds, in every bond — 0 (the default) is no vertical thrust at all, 1 is fully isotropic. A non-Medium row commanding thrust takes the vertical channel outright for the tick, clearing the ballistic carry; a medium row folds it into the medium's own displacement before that law's convergence runs.
   */
  thrust?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHoldSpend".
 */
export interface WorldHoldSpend {
  /**
   * The declared state.body slot name.
   */
  state: string;
  /**
   * The positive rate the slot drains at, per second.
   */
  ratePerSecond: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHoldMedium".
 */
export interface WorldHoldMedium {
  /**
   * The idle vertical drift velocity below the equilibrium band, signed (u/s).
   */
  idleDrift: number;
  /**
   * The equilibrium line's depth below the medium surface, and the band's half-width (u).
   */
  equilibriumOffset: number;
  /**
   * The proportional gain (1/s) the equilibrium error scales by to reach a target velocity inside the band or while recovering a breach above the surface.
   */
  settleRate: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHoldGravity".
 */
export interface WorldHoldGravity {
  /**
   * The downward acceleration while rising (u/s²) — the floaty top of the arc.
   */
  rise: number;
  /**
   * The downward acceleration while falling (u/s²) — the snappy descent (heavier than the rise). The world's own solved gravity field, where one is authored, overrides the MAGNITUDE but keeps this row's rise-to-fall ratio as the arc's asymmetry.
   */
  fall: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHoldEnvelope".
 */
export interface WorldHoldEnvelope {
  /**
   * The terminal upward speed (u/s). Required for a Medium bond; refused (the arc never clamps a rise) for Gravity/Lift.
   */
  riseSpeed?: number | null;
  /**
   * The terminal downward speed (u/s) — a Gravity/Lift row's own terminal fall speed, or a Medium row's terminal sink speed.
   */
  sinkSpeed?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldUpTurnRates".
 */
export interface WorldUpTurnRates {
  /**
   * The ceiling on how fast a solved gravity field may turn the up axis. Crossing this rate lets an attractor's pull replace the world's own as one continuous roll rather than a single-tick inversion; every ordinary reorientation turns far slower and is untouched.
   */
  field?: number;
  /**
   * The ceiling on how fast a measured ground-contact normal may turn the up axis — a discontinuity filter, not a smoothing rate: it should sit an order of magnitude above the fastest curvature this kit's own top speed can walk, so ordinary running adopts the surface exactly and only a collider crease is spread across a few ticks. Both rates must remain positive after Q48.16 compilation.
   */
  contact?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldObstructionLatch".
 */
export interface WorldObstructionLatch {
  /**
   * World units a body must move from where the witness last (re)latched before it is considered to have genuinely moved on. Its squared comparison threshold must remain positive after Q48.16 compilation.
   */
  displacement?: number;
  /**
   * The raw MoveAdvance/MoveStrafe role-channel magnitude (each reads in [-1, 1]) below which the body counts as not actively driving.
   */
  idleThreshold?: number;
  /**
   * How long, in real time, an un-refreshed latch survives a solver pass reporting no push at all — absorbs ordinary query noise near a surface (a gradient/quantization boundary, or a body settled into a wall/ground corner under blended contact geometry). Must convert to a positive exact whole engine-tick count.
   */
  graceSeconds?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BodyProgramParameters".
 */
export interface BodyProgramParameters {
  /**
   * Fixed-point scalar arguments keyed by instruction-defined name.
   */
  scalars: {
    [k: string]: number;
  };
  /**
   * Authored channel arguments keyed by instruction-defined name.
   */
  channels: {
    [k: string]: string | null;
  };
  /**
   * Bounded perception and steering arguments for ProduceFlockIntent, absent otherwise.
   */
  flock?: WorldFlockProfile | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFlockProfile".
 */
export interface WorldFlockProfile {
  /**
   * Inclusive sensing radius, in world units.
   */
  range: number;
  /**
   * Personal-space radius, no greater than Range.
   */
  separationRadius: number;
  /**
   * Maximum inspected candidates, including rejected ones, per sensing update. A sensed target and local neighbors share this one sample, using the larger of their ranges.
   */
  candidateBudget: number;
  /**
   * Maximum nearest sampled neighbors retained, no greater than CandidateBudget.
   */
  maxNeighbors: number;
  /**
   * Neighbor and sensed-target refresh interval; zero means every simulation step. Sensed targets hold their last observed position between samples. Designations, route goals, and heading blend every step.
   */
  updateSeconds: number;
  /**
   * Tangent-plane or three-dimensional motion.
   */
  space: WorldFlockSpace;
  /**
   * Local repulsion weight in [0,1].
   */
  separation: number;
  /**
   * Neighbor mean-heading weight in [0,1].
   */
  alignment: number;
  /**
   * Neighbor centroid-attraction weight in [0,1].
   */
  cohesion: number;
  /**
   * Selected target or route waypoint weight in [0,1].
   */
  goal: number;
  /**
   * Current heading persistence weight in [0,1].
   */
  inertia: number;
  /**
   * Distance inside which the goal term becomes zero.
   */
  arrivalDistance: number;
  /**
   * Forward sensing half-angle in (0,180]. Coincident neighbors remain perceptible.
   */
  halfAngleDegrees: number;
  /**
   * Whether every sampled neighbor must pass a deterministic solid-field sight test.
   */
  requiresLineOfSight: boolean;
  /**
   * Optional volume/medium navigation domain constraining the body's integrated locomotion while this producer runs. Its agentRadius must enclose the kit's collider about the body root. Invalid steps stop, without teleporting or finding an escape route. Later external impulses, contacts, and authority teleports remain separate.
   */
  movementDomain?: string | null;
  /**
   * Optional Fixed expression weighting each retained neighbor in the centroid. Left is the observer, right the neighbor. State-backed facts only; sampled at UpdateSeconds, clamped to [0,1], arithmetic failure reads zero, absent reads one. This does not filter separation or increase the candidate budget.
   */
  cohesionAffinity?: ValueExpression;
  /**
   * Independent Fixed expression weighting each retained neighbor's heading, under the same contract as CohesionAffinity. Affinities select relative influence; the outer Alignment and Cohesion weights set term strength.
   */
  alignmentAffinity?: ValueExpression;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionSpec".
 */
export interface ActionSpec {
  /**
   * The press trigger.
   */
  onPress?: ActionTrigger | null;
  /**
   * The release trigger.
   */
  onRelease?: ActionTrigger | null;
  /**
   * The fact-edge triggers.
   */
  onFact?: (ActionFactTrigger | null)[] | null;
}
/**
 * One trigger of a kit's action program: the effects that fire, the gate that must hold, and the latch that keeps a press armed while the gate is closed.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionTrigger".
 */
export interface ActionTrigger {
  /**
   * The effects applied in order.
   */
  effects: ActionEffectList;
  /**
   * The predicate that must hold, or null for always.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * How long a press stays armed waiting for the gate (the jump buffer).
   */
  latchSeconds?: number;
}
/**
 * Writes a named state cell — a state-section row's cell at rule scope, a counter slot inside a host's per-participant action program.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectSetState".
 */
export interface ActionEffectSetState {
  $type?: "setState";
  /**
   * The state row name (or the host's counter slot).
   */
  state: string;
  /**
   * The literal value to write, or null when FromState spells a live operand to copy instead — exactly one of the source spellings is authored (refused by name when both or neither are present, the same duality CompareState's own comparand carries).
   */
  value?: number | null;
  /**
   * The addressed participant — meaningful only inside a host's per-participant program; a non-Self target is refused at rule scope, where there is no participant to select.
   */
  target?: ActionTarget;
  /**
   * The cell inside State — null writes the row's slot cell, which a keyed row does not have (refused by name).
   */
  key?: string | null;
  /**
   * Another declared state-section row name, or a reserved channel, read live at fire time and copied in place of an authored Value — the row that resets to another row's own current value (a shadow row mirroring a counter someone else advances), never only a standing literal. Resolved through the same operand walk CompareState's own ComparandState uses; mixing a fixed row into an int destination (or the reverse) is refused by name rather than coerced.
   */
  fromState?: string | null;
  /**
   * The cell inside FromState, on the same (row, key) terms as Key. Refused when FromState names a reserved channel or is absent.
   */
  fromKey?: string | null;
  /**
   * An alternative to Value for a kind=Int state row a companion CountdownState effect decrements once per simulation tick (a countdown/cooldown). Authored in seconds — a physical unit, not a tick count, so a document's rate can change without silently retuning every cooldown — and converted once at rule compile time to an exact whole engine-tick count via TryDurationEngineTicksExact, never re-derived at runtime and never rounded: a duration that is not an exact whole engine-tick count is refused rather than silently rounded away (DurationNotExactEngineTicks). Typed Decimal rather than float because JSON deserializes a number token to Decimal exactly (base-10, no binary-float intermediate), and most terminating decimals — the only ones an author can spell — have no exact binary float or fixed-point spelling either.
   */
  valueSeconds?: number | null;
  /**
   * The literal a kind=Text state row's cell takes — the fourth spelling beside Value/FromState/ValueSeconds, exactly one authored. Every state-bound document value re-resolves on the write, so this is how a rule restyles what a state-bound document row names.
   */
  text?: string | null;
  /**
   * A bounded numeric expression evaluated in the destination row's integer or fixed-point domain. Exactly one source spelling is authored.
   */
  expression?: ValueExpression;
}
/**
 * Adds to a named state cell — the same shape as SetState, here the source is the addend rather than the replacement.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectAddState".
 */
export interface ActionEffectAddState {
  $type?: "addState";
  /**
   * The state row name (or the host's counter slot).
   */
  state: string;
  /**
   * The literal addend, or null when FromState spells a live addend instead — see Value's remarks.
   */
  value?: number | null;
  /**
   * The addressed participant — see Target.
   */
  target?: ActionTarget;
  /**
   * The cell inside State — see Key.
   */
  key?: string | null;
  /**
   * See FromState's remarks; here the addend is read live rather than the replacement.
   */
  fromState?: string | null;
  /**
   * The cell inside FromState — see FromKey.
   */
  fromKey?: string | null;
  /**
   * See ValueSeconds's remarks; here the converted tick count is the addend rather than the replacement.
   */
  valueSeconds?: number | null;
  /**
   * See Expression.
   */
  expression?: ValueExpression;
}
/**
 * Pushes one numeric value into a history row's ring (see Ring), the same source spellings as SetState minus text: exactly one of Value, FromState, or Expression.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectPushState".
 */
export interface ActionEffectPushState {
  $type?: "pushState";
  /**
   * The history row.
   */
  state: string;
  /**
   * An exact decimal literal in the row's kind.
   */
  value?: number | null;
  /**
   * A state row or reserved channel read live at every firing.
   */
  fromState?: string | null;
  /**
   * The cell of FromState, or null for its slot.
   */
  fromKey?: string | null;
  /**
   * A bounded numeric expression evaluated in the row's kind.
   */
  expression?: ValueExpression;
}
/**
 * Applies a bounded state transform through the ordinary mutation pipeline.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectTransformState".
 */
export interface ActionEffectTransformState {
  $type?: "transformState";
  /**
   * The typed operation.
   */
  transform: StateTransform;
}
/**
 * Moves selected tokens, preserving identity. A random draw advances only when the whole transfer commits.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformTransfer".
 */
export interface StateTransformTransfer {
  $type?: "transfer";
  /**
   * The source zone — a zone name, or in an authored rule a live zone ($zones[<index>], an entry of the rule's Zones table selected before each firing; an index selecting none refuses the transfer by name).
   */
  from: string;
  /**
   * The destination zone, on the same terms as From.
   */
  to: string;
  /**
   * The source selector.
   */
  selector?: ZoneSelector;
  /**
   * The token key for key or slice selection. In an authored rule this may be a dynamic key, resolved from the active store before each transfer; direct mutations carry the resolved literal.
   */
  key?: string | null;
  /**
   * Insert at the first position rather than the last.
   */
  insertFirst?: boolean;
  /**
   * A streamDraw site for random selection; absent for other selectors.
   */
  draw?: string | null;
  /**
   * How many tokens move in this one transfer, 1..MaxTransferCount, each selected afresh from what remains (a five-card deal is one mutation); a key selection moves exactly one, and a slice selection moves the keyed token's whole tail (its count is 1).
   */
  count?: number;
}
/**
 * Writes the longest run a patterns row accepts, walked from the origin outward: the same prefix semantics as the $match operand's prefix facet, landed back on the board instead of read as a fact. Refuses when the accepted prefix is empty, so an author closes a run with the required symbol (a bracket capture is plus(through) . symbol(until)) rather than an unbounded one running off the board.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformSetRay".
 */
export interface StateTransformSetRay {
  $type?: "setRay";
  /**
   * The board row.
   */
  row: string;
  /**
   * The origin key, excluded from the read word and the write.
   */
  from: string;
  /**
   * A direction in the board's topology.
   */
  direction: string;
  /**
   * A patterns row over the board's own raw values (kind Int).
   */
  pattern: string;
  /**
   * The replacement value written to every cell of the accepted prefix.
   */
  value: number;
}
/**
 * Reorders a row's cells by value in place by one Fisher-Yates pass over the named redrawable integer streamDraw site: n cells consume n - 1 samples, so the site's cursor advances by exactly that and a replay reproduces the permutation.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformShuffle".
 */
export interface StateTransformShuffle {
  $type?: "shuffle";
  /**
   * Any ordered zone or keyed row.
   */
  row: string;
  /**
   * The integer streamDraw site supplying the samples.
   */
  draw: string;
}
/**
 * Reorders an ordered zone by attribute rows over its token domain, stably: the first key decides and each later key breaks the ties before it. The canonical order a pattern reads a hand in.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformSortZone".
 */
export interface StateTransformSortZone {
  $type?: "sortZone";
  /**
   * The ordered zone.
   */
  row: string;
  /**
   * The attribute keys, 1..MaxSortKeys distinct numeric rows keyed over the zone's token domain, in precedence order; each carries its own direction.
   */
  by: SortKey[];
}
/**
 * One key of a zone sort: a keyed numeric attribute row over the zone's token domain.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "SortKey".
 */
export interface SortKey {
  /**
   * The attribute row.
   */
  row: string;
  /**
   * Whether the greatest value comes first under this key.
   */
  descending?: boolean;
}
/**
 * Reorders a keyed numeric row by its own cell values, stably.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformSortKeyed".
 */
export interface StateTransformSortKeyed {
  $type?: "sortKeyed";
  /**
   * The keyed numeric row.
   */
  row: string;
  /**
   * Whether the greatest value comes first.
   */
  descending?: boolean;
}
/**
 * Writes one value into every cell of a board whose bit is set in a cell-set mask read from a state cell: the way a set built from $board:mask and the and/or/xor/not/shift/image expression ops lands back on the board. The one board-writing form for every topology of at most 64 cells; a wider topology has no transform of its own and composes through per-cell rules instead.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformWriteSet".
 */
export interface StateTransformWriteSet {
  $type?: "writeSet";
  /**
   * The board row, over a topology of at most 64 cells.
   */
  row: string;
  /**
   * The integer row the cell-set mask is read from.
   */
  set: string;
  /**
   * The cell of that row, or null for its slot cell: a literal cell key, or any dynamic key spelling a write accepts (a binding token, a registered key family, an expression key, or a $cell:<row>:<key> indirection).
   */
  setKey?: string | null;
  /**
   * The value written to every masked cell.
   */
  value?: number;
}
/**
 * Rewrites a board from one or two boards over the same topology, cell by cell, in one journaled mutation: the set algebra $board:mask and the bit operators give a board of at most 64 cells, for a board of any size. A cell is a member when its value is not its board's empty; every member of the result is written as Value and every other cell as the board's empty.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformBoardCombine".
 */
export interface StateTransformBoardCombine {
  $type?: "boardCombine";
  /**
   * The board written.
   */
  row: string;
  /**
   * What is written.
   */
  operation: BoardCombineOp;
  /**
   * The first source board, over the same topology; absent for Fill and Clear.
   */
  left?: string | null;
  /**
   * The second source board for the two-board operations.
   */
  right?: string | null;
  /**
   * The direction a Shift steps along.
   */
  direction?: string | null;
  /**
   * The point-group element an Image carries through.
   */
  element?: string | null;
  /**
   * The value written to every member; never the board's own empty.
   */
  value?: number;
}
/**
 * Reorders an ordered zone of at most 20 tokens into the arrangement at a Lehmer rank read from an integer cell — the inverse of $reduce:arrangementRank: rank 0 is the token domain's own order, and a rank at or past k! refuses.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformArrange".
 */
export interface StateTransformArrange {
  $type?: "arrange";
  /**
   * The ordered zone.
   */
  row: string;
  /**
   * The integer row the rank is read from.
   */
  from: string;
  /**
   * The cell of that row, or null for its slot cell.
   */
  fromKey?: string | null;
}
/**
 * Appends one value to a history row's ring, overwriting the oldest slot once the ring is full, and advances its cursor by one.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformPush".
 */
export interface StateTransformPush {
  $type?: "push";
  /**
   * The history row.
   */
  row: string;
  /**
   * The raw value pushed, in the row's kind.
   */
  value: number;
}
/**
 * Clears every group of cells valued Lower..Upper beside the cell From names that has no empty cell beside it, writing the board's empty value over their members. The write-path twin of $board:enclosedAt, applied after the value lands.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformClearEnclosed".
 */
export interface StateTransformClearEnclosed {
  $type?: "clearEnclosed";
  /**
   * The board.
   */
  row: string;
  /**
   * The placed value's cell: a literal cell key, or any dynamic key spelling a write accepts.
   */
  from: string;
  /**
   * The enclosed range's inclusive low end; the board's empty value lies outside it.
   */
  lower: number;
  /**
   * The enclosed range's inclusive high end.
   */
  upper: number;
}
/**
 * Refreshes a knowledge board from its declared source and visibility mask; authority only.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateTransformObserve".
 */
export interface StateTransformObserve {
  $type?: "observe";
  row: string;
}
/**
 * Decrements a state countdown by the current simulation step's engine-tick width, saturating at zero. The destination must be a kind=Int nonNegative=true row. Unlike an authored AddState constant, this effect consumes the runtime step width, so changing the document's authored tick rate never retunes the duration. When the remaining duration is shorter than one step, the computed decrement is exactly the remaining value; it reaches zero without asking the explicit-write door to admit a negative candidate.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectCountdownState".
 */
export interface ActionEffectCountdownState {
  $type?: "countdownState";
  /**
   * The countdown state-row name.
   */
  state: string;
  /**
   * The cell inside State; null addresses its slot.
   */
  key?: string | null;
}
/**
 * Redraws a draw site (a state row declaring a Draw). A draw's moment is authored through the rule that fires this: a TickPeriod site redraws on an ordinary $tick-scheduled rule and a Event site on an event-gated one, so timing costs no mutation ordinal.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectGenerate".
 */
export interface ActionEffectGenerate {
  $type?: "generate";
  /**
   * The draw site's row name. One name, not a (source, destination) pair: a site's source is its own facet and a site is a scalar slot, so there is nothing else to address.
   */
  row: string;
}
/**
 * Removes one addressed cell from a declared state row.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectRemoveStateCell".
 */
export interface ActionEffectRemoveStateCell {
  $type?: "removeStateCell";
  /**
   * The row to remove from.
   */
  state: string;
  /**
   * The optional cell key.
   */
  key?: string | null;
}
/**
 * Writes an absolute simulation due tick into an integer state cell. The delay is converted against the document's authored simulation rate and rounded up, so it never fires early. A companion rule compares $tick against the cell and removes it after handling, forming a bounded, document-backed scheduler.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectScheduleState".
 */
export interface ActionEffectScheduleState {
  $type?: "scheduleState";
  /**
   * The integer destination row.
   */
  state: string;
  /**
   * The non-negative delay, rounded up to simulation ticks.
   */
  delaySeconds: number;
  /**
   * The optional cell key.
   */
  key?: string | null;
}
/**
 * Applies a bounded list of effects atomically after preflight. When any effect refuses, none apply and OnFailure runs instead. The compiler refuses nested transactions and effects a document project has not admitted through AllowsTransaction.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionEffectTransaction".
 */
export interface ActionEffectTransaction {
  $type?: "transaction";
  /**
   * The main transaction branch.
   */
  effects: ActionEffect[];
  /**
   * The optional branch run after a main-branch refusal.
   */
  onFailure?: ActionEffect[] | null;
}
/**
 * Writes the body's vertical-velocity channel (the jump launch / the surge).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectSetVerticalVelocity".
 */
export interface WorldEffectSetVerticalVelocity {
  $type?: "setVerticalVelocity";
  velocity: number;
  target?: ActionTarget;
}
/**
 * Multiplies the body's vertical velocity (the jump cut; gate on Rising).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectScaleVerticalVelocity".
 */
export interface WorldEffectScaleVerticalVelocity {
  $type?: "scaleVerticalVelocity";
  factor: number;
  target?: ActionTarget;
}
/**
 * A timed planar velocity overlay (the dash): BodyDirection is rotated by the body's attitude at fire time and ridden as authored, never normalized, at Speed for DurationSeconds.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectPlanarImpulse".
 */
export interface WorldEffectPlanarImpulse {
  $type?: "planarImpulse";
  bodyDirection: DocumentVector3;
  speed: number;
  durationSeconds: number;
  target?: ActionTarget;
}
/**
 * Starts a named timer slot with an authored duration.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectStartTimer".
 */
export interface WorldEffectStartTimer {
  $type?: "startTimer";
  state: string;
  seconds: number;
  target?: ActionTarget;
}
/**
 * Submits the selected subject into a named target register.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectDesignate".
 */
export interface WorldEffectDesignate {
  $type?: "designate";
  /**
   * The authored target-register name.
   */
  register: string;
  /**
   * The subject source.
   */
  target?: ActionTarget;
}
/**
 * Emits a deterministic presentation-neutral cue (WorldGameplayCue).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectEmitCue".
 */
export interface WorldEffectEmitCue {
  $type?: "emitCue";
  /**
   * The cue name; see IsValidName.
   */
  name: string;
  /**
   * An optional bounded payload.
   */
  payload?: string | null;
  /**
   * The body the cue is about — a body index, a $cell: indirection, or a bound key.
   */
  key?: string | null;
}
/**
 * Writes a world-addressed body's vertical velocity.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectSetBodyVerticalVelocity".
 */
export interface WorldEffectSetBodyVerticalVelocity {
  $type?: "setBodyVerticalVelocity";
  key: string;
  velocity: number;
}
/**
 * Scales a world-addressed body's vertical velocity.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectScaleBodyVerticalVelocity".
 */
export interface WorldEffectScaleBodyVerticalVelocity {
  $type?: "scaleBodyVerticalVelocity";
  key: string;
  factor: number;
}
/**
 * Rides a unit BodyDirection at Speed on a world-addressed body for an exact whole-engine-tick duration.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectApplyBodyImpulse".
 */
export interface WorldEffectApplyBodyImpulse {
  $type?: "applyBodyImpulse";
  key: string;
  bodyDirection: DocumentVector3;
  speed: number;
  durationSeconds: number;
}
/**
 * Designates or clears a world-addressed body's target register.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectDesignateBody".
 */
export interface WorldEffectDesignateBody {
  $type?: "designateBody";
  key: string;
  register: string;
  kind: WorldBodyDesignationKind;
  targetKey?: string | null;
}
/**
 * Paints one lattice field cell, or the cube of Radius around it.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectPaintField".
 */
export interface WorldEffectPaintField {
  $type?: "paintField";
  field: string;
  x: number;
  y: number;
  z: number;
  value: number;
  operation?: WorldFieldWriteOp;
  radius?: number;
}
/**
 * Upserts a HUD panel document row.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectUpsertHudPanel".
 */
export interface WorldEffectUpsertHudPanel {
  $type?: "upsertHudPanel";
  panel: WorldHudPanel;
}
/**
 * One HUD panel row — a stable id (unique within the section), a normalized viewport rect in screen space, which band it draws in, its chrome style, and its child elements. WorldMutation.UpsertHudPanel carries the whole row (elements included) as one cross-row transaction boundary; WorldMutation.UpsertHudElement/ WorldMutation.RemoveHudElement read-modify-write a single element within an already-declared panel.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudPanel".
 */
export interface WorldHudPanel {
  /**
   * The panel's stable id (unique within the section — the world.row.set hud.panels/world.row.remove hud.panels mutation address).
   */
  id: string;
  /**
   * The panel's viewport rect, normalized to screen space.
   */
  rect: WorldHudRect;
  /**
   * Which band the panel draws in.
   */
  layer: WorldHudLayer;
  /**
   * The panel's chrome recipe.
   */
  style: WorldHudPanelStyle;
  /**
   * The panel's visibility condition over presentation facts, or null for always.
   */
  visible?: OverlayPredicateNullable | null;
  /**
   * Gets the panel's child elements. Absent and empty read identically to every consumer, and the coalesce lives in the accessor so no caller grows its own; every other list-valued row member that reads absent as empty cites this one.
   */
  elements: WorldHudElementList;
}
/**
 * A normalized rect (origin top-left, Y down) — a WorldHudPanel's rect is in screen space [0, 1] × [0, 1]; a WorldHudElement's rect is in its owning panel's local [0, 1] × [0, 1] space.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudRect".
 */
export interface WorldHudRect {
  /**
   * The rect's left edge, normalized.
   */
  x?: number;
  /**
   * The rect's top edge, normalized.
   */
  y?: number;
  /**
   * The rect's width, normalized — must be positive.
   */
  width?: number;
  /**
   * The rect's height, normalized — must be positive.
   */
  height?: number;
}
/**
 * One HUD element row inside a WorldHudPanel — a stable id (unique within the owning panel), its kind, its local rect, its color role, an authored literal string (meaningful for Text), and an optional binding into the closed HudBindingVocabulary (meaningful for Text and Gauge — a bound text element's live value replaces the authored literal; a bound gauge element's live value drives its fill; an unbound gauge draws empty).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudElement".
 */
export interface WorldHudElement {
  /**
   * The element's stable id (unique within the owning panel — the world.row.set hud.panels/ .remove mutation address).
   */
  id: string;
  /**
   * The element's rendered kind.
   */
  kind: WorldHudElementKind;
  /**
   * The element's rect, normalized to the owning panel's local space.
   */
  rect: WorldHudRect;
  /**
   * The element's color role.
   */
  style: WorldHudStyleToken;
  /**
   * The authored literal string a Text element draws when neither Binding nor Template is set; ignored for Rect and Gauge. Omitted from the wire when null.
   */
  text?: string | null;
  /**
   * A closed HudBindingVocabulary token, or null for an unbound element. Refused alongside Template — exactly one live-value source, never both. Omitted from the wire when null.
   */
  binding?: string | null;
  /**
   * A Text element's template string — authored literal text interleaved with {token} placeholders, each a closed HudBindingVocabulary token resolved through the same operand path Binding uses (see HudTemplate for the brace/escape grammar). A richer binding source than Binding — many live facts composed into one string instead of one — never both on the same element. Ignored for Rect and Gauge (a gauge's fill is one fraction; it has no composed string to show). Omitted from the wire when null.
   */
  template?: string | null;
  /**
   * A Frame element's sampled frame — the same WorldFrameSource vocabulary a screen row or probe socket plugs into (camera/view/probe/capture). Required for Frame; refused on every other kind. Omitted from the wire when null.
   */
  source?: WorldFrameSource | null;
  /**
   * A Frame element's aspect-fit policy. Ignored for every other kind. Omitted from the wire at its default (Cover).
   */
  fit?: WorldHudFrameFit;
  /**
   * Whether a Frame element flips its sampled frame horizontally — a face cam is conventionally mirrored. Ignored for every other kind. Omitted from the wire at its default (false).
   */
  mirror?: boolean;
  /**
   * A Frame element's corner-rounding radius, in pixels. Must be finite and non-negative. Ignored for every other kind. Omitted from the wire at its default (0).
   */
  radius?: number;
  /**
   * A Frame element's sampled-frame alpha, in [0, 1]. Ignored for every other kind.
   */
  opacity?: number;
  /**
   * A Frame element's ranked source candidates — a portrait that shows the speaking character, else the webcam when the player chose it, else the seat's own avatar. Refused beside Source: a bare Source is exactly a one-entry list with no condition. At most MaxFrameCandidatesPerElement entries, every one counted toward MaxFrameSources.
   */
  sources?: WorldHudFrameCandidate[] | null;
  /**
   * How long a Frame element cross-fades when its winning candidate changes; 0 cuts. Finite and non-negative.
   */
  fadeSeconds?: number;
}
/**
 * One ranked source candidate of a Frame element: the frame shown while When holds. Candidates are walked in authored order every frame and the first holding one wins; a null predicate always holds, so the last row is the default.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudFrameCandidate".
 */
export interface WorldHudFrameCandidate {
  /**
   * The sampled frame while this candidate wins.
   */
  source: WorldFrameSourceNonNullable;
  /**
   * The condition, evaluated for the panel's seat.
   */
  when?: OverlayPredicateNullable | null;
}
/**
 * Removes a HUD panel document row by id.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectRemoveHudPanel".
 */
export interface WorldEffectRemoveHudPanel {
  $type?: "removeHudPanel";
  id: string;
}
/**
 * Upserts a placement document row; inside a transaction it must sit in the closing suffix.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectUpsertPlacement".
 */
export interface WorldEffectUpsertPlacement {
  $type?: "upsertPlacement";
  placement: WorldPlacement;
}
/**
 * One placement instance row — a creation asset stamped into the world by reference: transform + facets as data, addressed by its stable Id. A placement whose creation carries timeline frames is animated: it replays client-side on the render clock through the reserved dynamic-transform pool (distribution/mirror facets are static-stamp-only and reject on an animated row). A placement carrying an Inhabit facet is a live population body rather than furniture (see WorldPlacementInhabit); its declared creation eyes derive WorldCamera feeds and its declared faces derive screens (both at the delivery boundary, never written to the document).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacement".
 */
export interface WorldPlacement {
  /**
   * The row's stable string id (its mutation address).
   */
  id: string;
  /**
   * The referenced Id (must resolve; removal of a referenced creation rejects loudly).
   */
  prototypeId: string;
  /**
   * The stamp position — world space when Parent is null (today's behavior, unchanged), or this row's own local frame relative to Parent's COMPOSED world frame otherwise (see Parent). Inert (still validated and stored, but read by nothing — neither the resolve nor the renderer) when Attach is set: the row's live position is the resolved attachment, never this authored one.
   */
  position: DocumentVector3;
  /**
   * The stamp yaw about +Y, degrees — world-space when Parent is null, added to the parent's own composed yaw otherwise. Same attach caveat as Position.
   */
  yawDegrees: number;
  /**
   * The uniform stamp scale (clamped to the placement policy envelope by validation).
   */
  scale: number;
  /**
   * The placement distribution, or null for a single copy. Static placements accept a Lattice, Noise, or Scatter region, each with a none fill — Lattice materializes a regular two-axis grid (PatternFor); Noise and Scatter materialize a deterministic hash-sampled instance set instead (SampledFixedOffsetsFor), the placement twin of the field lattice's own Noise/Scatter fills. Refused together with Attach.
   */
  distribution?: WorldDistribution | null;
  /**
   * The authored local reflection plane, or null for no reflected copy. Refused together with Attach.
   */
  mirror?: WorldPlacementMirror | null;
  /**
   * The placement's emission facet (a synth voice the stamp itself makes — see WorldEmission), or null for silent. Under Distribution the emission binds to the placement root only. Omitted from the wire when null. Composes with Attach: an attached row's source point rides the resolved live pose (Client.WorldStampPool.TryShapePosition) instead of the row's static position, and an inactive carrier silences the emitter rather than leaving it at a stale point.
   */
  emission?: WorldEmission | null;
  /**
   * The placement's solidity facet (see WorldSolid). Both contact providers compile the creation's emitted shapes; analytic collision uses per-primitive colliders, including exact half-spaces for planes. Omitted from the wire when null. Composes with Attach under the analytic provider only (WorldColliderSet.RefreshAttached recomputes an attached row's colliders every tick from the resolved live pose); still refused together under the field provider, which compiles every solid row's geometry once into one SDF program and never rebuilds it per tick.
   */
  solid?: WorldSolid | null;
  /**
   * The inhabit facet (null = decoration), binding the row to live population bodies. Omitted from the wire when null. Refused together with Attach — a row cannot both spawn its own driven bodies and ride another body's pose.
   */
  inhabit?: WorldPlacementInhabit | null;
  /**
   * Per-instance overrides of the creation's declared faces (null = every face shows its declared default). Omitted from the wire when null. Orthogonal to Attach (a content selector, not a transform) — composes freely, like every other facet that now tracks the dynamic pose.
   */
  faceSources?: WorldPlacementFaceList | null;
  /**
   * The placement's region facet (see WorldPlacementRegion) — a named volume the world-events feed watches for body enter/exit, or null for none. Omitted from the wire when null. Composes with Attach: an attached row's sensing sphere centers on the resolved live pose (Server.WorldEventFeed.CollectRegions) instead of the row's static position — see WorldPlacementRegion's own remarks.
   */
  region?: WorldPlacementRegion | null;
  /**
   * The placement's attach facet (see WorldPlacementAttach) — binds the row's resolved world pose to a live population body, or null for a static/authored transform (the default, unchanged behavior). Omitted from the wire when null.
   */
  attach?: WorldPlacementAttach | null;
  /**
   * The placement's contribution facet (see WorldPlacementContribution) — marks the row a host-authored slot a federation partner fills, or null for an ordinary placement. Omitted from the wire when null. Composes with every other facet: the facet governs which creation the row shows and for how long, never its transform.
   */
  contribution?: WorldPlacementContribution | null;
  /**
   * The placement's response facet (see WorldPlacementResponse) — the ordered state-driven prototype swaps a lattice-field condition can fire, or null for an ordinary placement that always shows PrototypeId. Omitted from the wire when null. Refused together with Attach, Inhabit, and FaceSources.
   */
  respond?: WorldPlacementResponseList | null;
  /**
   * The placement's grip facet (see WorldPlacementGrip) — overrides the world's default hold policy for this row's compiled surface(s), or null to inherit the world default. Omitted from the wire when null. Requires Solid (validated).
   */
  grip?: WorldPlacementGrip | null;
  /**
   * The placement's board facet (see WorldPlacementBoard) — the tabletop primitive, or null for no anchored topology. Omitted from the wire when null.
   */
  board?: WorldPlacementBoard | null;
  /**
   * Another placement's Id this row's frame composes over, or null for a world-space row (today's behavior, unchanged). Position/YawDegrees become this row's own LOCAL offset/heading in the parent's composed frame: the parent's resolved yaw rotates the offset before adding the parent's resolved position (the same local-offset-rotated-by-orientation convention WorldPlacementAttach already uses), and yaw adds. Resolved ONCE, statically, by WorldPlacementFrameCompilation — never per tick — into PlacementFrames, which every consumer of a placement's WORLD transform reads instead of this row's own Position/YawDegrees. Validated: must name a declared placement other than itself, must not close a cycle, and that placement must carry neither a Distribution nor a Mirror (an expanded row has no single frame to compose against). Parent scale multiplies the child's local offset and resolved scale.
   */
  parent?: string | null;
  /**
   * The placement's deal facet (see WorldPlacementDeal) — the row is a template whose instances are dealt from a keyed state row as child placements over its own Distribution region, or null for an ordinary placement. A template renders nothing and collides with nothing itself. Omitted from the wire when null. Requires Distribution; refused together with Inhabit, Attach, Respond, Mirror, and FaceSources.
   */
  deal?: WorldPlacementDeal | null;
  /**
   * A dealt child's reserved distribution slot, independent of its editable transform. Absent on ordinary placements and templates.
   */
  dealSlot?: number | null;
  /**
   * Named occupation, clearance, and influence volumes in this placement's local frame. The compiled static-query eligibility is exposed by Unsupported; ordinary inhabit/attach/distribution facets remain legal and are reported there when they have no single static frame.
   */
  spatial?: WorldPlacementSpatialVolumeList | null;
}
/**
 * A reflection plane in a placement's local frame.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementMirror".
 */
export interface WorldPlacementMirror {
  /**
   * The plane normal.
   */
  normal: DocumentVector3;
  /**
   * The signed plane offset along the normalized Normal.
   */
  offset: number;
}
/**
 * An emission facet — a synth voice a world row itself makes (phenomena sound like themselves; a creek is not a speaker). Nullable on WorldPlacement — a facet edit is the row's existing whole-row upsert. Under a repeat facet the emission binds to the placement root only (an 8×8 lattice must not become 64 voices; a per-copy flag is a future facet field, not a schema fork).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEmission".
 */
export interface WorldEmission {
  /**
   * The referenced Name (must resolve).
   */
  patchId: string;
  /**
   * The emitter level (1 = unity), bounded by MaxLevel.
   */
  level: number;
  /**
   * The audible support radius in world units, or null for the audio defaults' DefaultSpeakerRadius.
   */
  radius?: number | null;
}
/**
 * A placement's inhabit facet — the row's binding to live population bodies. An inhabited placement is a normal entry in the entity table: it holds a Puck.World.Server.WorldBody, integrates under the named kit, and is addressable as Entity like any avatar. Its stamp rides the body's pose instead of the row's static transform; the row's position/yaw become its spawn pose. Absent (null) = decoration, the unchanged furniture behaviour.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementInhabit".
 */
export interface WorldPlacementInhabit {
  /**
   * The Name the bodies move under. Null resolves the creation's own Locomotion token AS a kit name — a creation declaring "swim" inhabits the world's kit row named "swim". Neither resolving is a loud rejection naming every kit the world declares.
   */
  kit: string | null;
  /**
   * The Name the bodies wear, or null to wear an implicit creation look on this placement's own PrototypeId.
   */
  look: string | null;
  /**
   * The live, idle, or named producer source the bodies wake on.
   */
  source: IntentSource;
  /**
   * How many bodies, bounded by the world's authored peer capacity.
   */
  count?: number;
  /**
   * The region and deterministic fill sequence that place the bodies relative to the placement root.
   */
  distribution?: WorldDistribution | null;
}
/**
 * A per-instance override of one declared creation face's feed — the face twin of the emission facet's per-instance override channel.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementFace".
 */
export interface WorldPlacementFace {
  /**
   * The declared Name to override.
   */
  face: string;
  /**
   * The screen source the face shows, in the existing WorldScreenSource vocabulary.
   */
  source: WorldScreenSource;
  /**
   * The face's portal facet (see WorldPlacementPortal) — absent (the default) means this face is not a door. Optional and trailing deliberately: a face authored before this facet existed round-trips unchanged, and it composes freely with Source — the door and the screen it shows are independent facts about the same face.
   */
  portal?: WorldPlacementPortal | null;
}
/**
 * A WorldPlacementFace's portal facet — the authored decision that a face is a door: which WorldDestination row it leads to, and under what travel scope. Absent (the default) means the face is not a door — nothing here fires anything by itself; turning the decision into a diegetic step-into trigger is WorldInstanceHost.TriggerPortal's job, never this facet's. Durability, scope, and process-local instance selection live on the named WorldDestination row this facet points at, not here — a facet composes one destination selection with a travel scope, never re-authors how that destination is minted. Extensible deliberately (an optional-member record, the same widen-without-moving-existing-members shape WorldLatticeMedium's own remarks describe): a future fact-gate field (an authored predicate a traveler must satisfy to pass) adds cleanly as a new trailing member.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementPortal".
 */
export interface WorldPlacementPortal {
  /**
   * The Name of the destinations row naming the selected destination. Must resolve to an existing row — an undeclared name refuses by name (see WorldDefinitionValidator). No boot-time file-existence check on the destination's own referenced document — resolving the document is a future consumer's job; resolving the name, against this document's own destinations section, is this facet's own.
   */
  destination: string;
  /**
   * Who travels through (see WorldPortalTravel). Absent resolves against the world's own portals.portalDefaults.travel (see WorldPortalsSection), or Body when the world declares no portals section at all — see WorldDefinitionValidator's resolution order, echoed by world.portals. Omitted from the wire when null.
   */
  travel?: WorldPortalTravel | null;
  /**
   * Where a traveler lands at the destination (see WorldPortalArrival). Default Spawn — unauthored worlds and every facet authored before this member existed are unchanged. Optional and trailing (the same widen-without-moving-existing-members shape Travel itself already follows).
   */
  arrival?: WorldPortalArrival;
  /**
   * The destination document's border face this facet's frame maps onto under Mapped — "<placementId>/<face>", the placement id and declared face name whose own placement transform (position + yaw) is the arrival anchor. Required exactly when Arrival is Mapped (refused by name otherwise, in either direction — see WorldDefinitionValidator). No boot-time cross-document existence check: the named document is not resolved at boot (same reason Destination is not), so an absent placement/face refuses at transfer time instead, against the destination's own delivered definition (see WorldPortalCounterpart). Omitted from the wire when null.
   */
  counterpart?: string | null;
  /**
   * The maximum number of live bodies and outstanding reservations admitted through this border at once, or null to use the destination population's remaining capacity. A full border refuses the current attempt immediately; it never queues.
   */
  capacity?: number | null;
}
/**
 * A placement's region facet — a named volume row, not a trigger system: any placement may carry one, turning its stamp into a sensing volume the world-events feed watches for body enter/exit edges (see Server.WorldEventFeed and the observe region:<name> grant subject). The region's name is the carrying placement's Id — one identity, never a second string kept in sync by hand. The volume is a sphere centered on the placement's Position (the placement's own Scale/YawDegrees do not affect it — a region's size is its own authored radius, never derived from the creation's visual bounds). Presentation-only in itself (drawing no geometry); sensing reads the same document-authored center every tick, converted to fixed-point at the same boundary WorldSolid facets already cross through — unless the row also carries Attach, in which case the center is the resolved live body pose instead (Server.WorldEventFeed.CollectRegions, the same resolve world.attachments answers): the sensing sphere follows the carrier, and an inactive carrier senses nobody rather than sensing at a stale point.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementRegion".
 */
export interface WorldPlacementRegion {
  /**
   * The sensing radius, world units. Must be finite and positive (validated).
   */
  radius: number;
}
/**
 * A placement's attach facet — binds the row's stamp to a live population body's transform, so the resolved world pose follows that body every tick (an avatar's hat, held item, nameplate, or aura) instead of sitting at the row's own authored Position/YawDegrees. The offset rides the body's own local frame — rotated by the body's orientation before adding, the Puck.SdfVm.Views.OrientedFollowRig/FirstPersonRig convention for a moving anchor, never the world-axis FollowRig shape a fixed subject would use. The resolved pose is never written back into the document, and it is derived twice, at two clocks, from the one authored facet: the authoritative answer is fixed point — the body's fixed-point pose composed with this facet's authored (float, quantized at resolution like every other placement field) offset, by Puck.World.Server.WorldPlacementAttachment.TryResolve, on demand by world.attachments and once per tick by attached local gravity areas;the rendered pose is presentation float — the same composition over the client's interpolated body pose, packed every frame by Client.WorldStampPool, which is what makes an attached row visibly ride its body as smoothly as the body itself. An attached row draws through that reserved stamp pool and not as a static stamp (Client.WorldPlacementStamper.IsStaticStamp), and it charges MaxStampRegistrations like an animated row does. Region, solid (under the analytic contact provider), and emission were once refused on the same row as this one because each read the row's own static transform — all three now read the same resolved dynamic pose instead (Server.WorldEventFeed.CollectRegions, Server.WorldColliderSet.RefreshAttached, Server.WorldGravityField.RefreshAttachedAreas, Client.WorldStampPool.TryShapePosition/RootPose), so a region's aura, an analytic collider's hitbox, and an emission's voice all track the carrier: an equipped item's sensing sphere, hitbox, or source point rides the body it is attached to. What stays refused: distribution/mirror (static-stamp-only, the same rule an animated or inhabited row already enforces), inhabit (a row cannot both spawn its own driven bodies and ride another's), and solid specifically under the field contact provider (it compiles every solid row's geometry once into one SDF program and never rebuilds it per tick) — refused by name rather than defining a blend (see WorldDefinitionValidator).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementAttach".
 */
export interface WorldPlacementAttach {
  /**
   * The 0-based population entity index the placement rides — the same indexing Entity and the console's body:<n> grant subject use, not the 1-based player.* seat number (body:1 is "player 2"). Validated within 0..the world's authored population capacity; the target need not be active at author time (see remarks — an inactive body at runtime makes the row contribute nothing, it does not refuse).
   */
  bodyIndex: number;
  /**
   * The stamp's position offset in the body's own local frame, world units.
   */
  localOffset: DocumentVector3;
  /**
   * The stamp's yaw offset from the body's own heading, degrees. Zero rides the body's exact facing.
   */
  localYawDegrees?: number;
}
/**
 * A placement's contribution facet: the row is a slot whose frame (id, pose, scale, and these lifecycle terms) is host-authored and whose creation a federation partner supplies through ordinary mutations. Null is an ordinary placement.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementContribution".
 */
export interface WorldPlacementContribution {
  /**
   * The authored lifecycle.
   */
  tenure: WorldContributionTenure;
  /**
   * The host-owned Id the slot shows while unfilled and returns to on retraction. Must resolve to a declared creation row.
   */
  slotCreationId: string;
  /**
   * The authored adjacencies row name the presence check watches — the same key the $link:<adjacencyName> reserved rule channel reads. Required for Presence, refused beside Endowed.
   */
  link?: SafeName | null;
  /**
   * Seconds the watched link may stay unreachable before the piece retracts, compiled to simulation ticks through CompiledGrace at each sweep observation and stamped once into RetractDeadlineTick. Zero disables the grace (retract on the first observed outage); a rate-0 world compiles to Never, so nothing retracts. Refused beside Endowed unless zero.
   */
  graceSeconds?: number;
  /**
   * Server-stamped. The acting principal that filled the slot; null while unfilled. Never accepted from a submitted payload or an authored document.
   */
  contributor?: WorldPrincipal | null;
  /**
   * Server-stamped. The simulation tick at or after which the presence sweep retracts this slot; null while the link is reachable.
   */
  retractDeadlineTick?: number | null;
}
/**
 * One entry of a placement's response trait: while When holds, the row's rendered/collided prototype becomes PrototypeId instead of its currently authored one — the bridge that lets a placement react to live simulation state (a burning tree becomes a charred stump; a filled account swaps a granary's face). Absent Respond is today's behavior exactly: the placement always shows its own authored PrototypeId.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementResponse".
 */
export interface WorldPlacementResponse {
  /**
   * The condition tested every sweep — a lattice-field read or a state-cell read.
   */
  when: WorldPlacementResponseCondition;
  /**
   * The creation the placement shows/collides as while When holds. Must resolve to a declared, non-animated creation row.
   */
  prototypeId: string;
}
/**
 * The original lattice-field condition, unchanged from before this union existed: the named field read at the placement's own coupled cell, compared against a literal or another row's slot cell.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementResponseConditionFieldCondition".
 */
export interface WorldPlacementResponseConditionFieldCondition {
  $type?: "field";
  /**
   * The field read at the cell.
   */
  field: string;
  /**
   * The comparison.
   */
  comparison: ActionStateComparison;
  /**
   * The scalar compared against (literal or state-row reference) — the same WorldLatticeScalar grammar a fields.reactions Transform/Expose condition already uses.
   */
  value: WorldLatticeScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeScalar".
 */
export interface WorldLatticeScalar {
  [k: string]: unknown;
}
/**
 * A state-cell condition: compares State's cell (its slot cell when Key is absent, else the cell at Key) against Value, or — when ComparandState is authored instead — against another declared row's cell, read live at the same evaluation. Exactly one of Value and ComparandState may be present, the same one-comparand rule CompareState already enforces for a rule's own gate — this facet reuses that field convention rather than inventing a second reading of "compare a state cell." Independent of the placement's position, and of whether the document declares a fields section at all.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementResponseConditionStateCondition".
 */
export interface WorldPlacementResponseConditionStateCondition {
  $type?: "state";
  /**
   * The state row compared — any declared state.world row of a numeric kind (never Text).
   */
  state: string;
  /**
   * The comparison.
   */
  comparison: ActionStateComparison;
  /**
   * The literal comparand, or null when ComparandState spells the comparand instead.
   */
  value?: number | null;
  /**
   * The cell inside State, or null for its slot cell. Refused when State is keyed and this is absent, or unkeyed and this is present.
   */
  key?: string | null;
  /**
   * Another declared state.world row, read live and compared instead of Value. Must share State's cell kind (refused by name otherwise).
   */
  comparandState?: string | null;
  /**
   * The cell inside ComparandState, on the same terms as Key. Refused when ComparandState is absent.
   */
  comparandKey?: string | null;
}
/**
 * A placement's grip facet — overrides the world's DefaultHold hold policy for every collider this row compiles, composing as the tighter authoring layer: present, it decides; absent, the row's colliders fall back to the world default. Requires Solid (nothing else compiles a collider a grip trait could apply to). Every collider a distribution/mirror expands from one row shares the row's single grip decision — a lattice of holdable handholds is authored as one placement, not one per copy.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementGrip".
 */
export interface WorldPlacementGrip {
  /**
   * Whether a body's surface hold may take this row's compiled surface(s), overriding the world default.
   */
  holdable: boolean;
}
/**
 * A placement's board facet — the tabletop primitive. Anchors a state.lattices Grid topology (which already carries its own world-space origin/cellSize frame) to this placement, so a chess set, a checkers board, or a card table is one placement/body carrying one topology — carriable as a unit once an attachment primitive picks it up. A topology is carried by at most one placement (validated). Occupancy is the only row the engine reads; Turn/Verdict/ Move/Plan are author-named convenience bindings world.tabletop echoes together — ordinary declared rows, never engine-interpreted, so this facet stays a reusable primitive rather than a chess-specific feature.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementBoard".
 */
export interface WorldPlacementBoard {
  /**
   * The state.lattices Grid topology this placement anchors.
   */
  topology: string;
  /**
   * The board-typed row over Topology holding current occupant codes.
   */
  occupancy: string;
  /**
   * An optional phase row read back beside the frame.
   */
  turn?: string | null;
  /**
   * An optional row read back beside the frame (a ruling on the last recorded change). Required when Enforcement is Return (validated).
   */
  verdict?: string | null;
  /**
   * An optional keyed row (conventionally cells "from"/"to") read back beside the frame. Required when Enforcement is Return (validated).
   */
  move?: string | null;
  /**
   * An optional board-typed row over Topology a future addon paints candidate cells into for highlight rendering — the seam, not the addon.
   */
  plan?: string | null;
  /**
   * What the engine itself does once Verdict refuses a move.
   */
  enforcement?: WorldBoardEnforcement;
  /**
   * The Verdict value the judge writes to accept a move — any other value is a refusal.
   */
  accept?: number;
}
/**
 * A placement's deal facet: the row is a template whose instances are dealt from a keyed state row, one child placement per cell, laid out over the template's own Distribution region in cell order. The template itself renders nothing and collides with nothing; its prototype and its Solid/Grip/Region/ Emission facets are what every child is stamped with. A child is an ordinary placement row named <template>/<cellKey> (ChildId) with Parent naming the template and the region's dealt offset as its local position, written and removed through ordinary UpsertPlacement/RemovePlacement mutations under WorldPrincipal.World by the per-tick sweep (Server.WorldServer.SweepPlacementDeals), so a dealt instance journals, undoes, replays, and rebuilds colliders through the one placement door.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementDeal".
 */
export interface WorldPlacementDeal {
  /**
   * The keyed state.world row whose cells are dealt, of any cell kind. One child per cell, keyed by the cell's key.
   */
  row: string;
  /**
   * The optional variant selection: a second keyed row read at the same key, whose cell text (an integer cell spelled as text) selects a prototype from Map; a cell with no entry, or no cell at all, deals the template's own prototype.
   */
  variants?: WorldPlacementDealVariants | null;
  /**
   * Which properties of existing instances are owned by gameplay. Absent synchronizes copied properties with the template. New instances receive the template's supported copied facets.
   */
  preserve?: WorldPlacementDealPreserve | null;
  /**
   * Optional bounded rearrangement policy. Requires instance-owned transforms and a footprint.
   */
  reflow?: WorldPlacementReflow | null;
}
/**
 * A deal facet's variant selection: a keyed row read at the dealt cell's key, and the map from that cell's text to the prototype the child is dealt with.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementDealVariants".
 */
export interface WorldPlacementDealVariants {
  /**
   * The keyed state.world row read at the dealt cell's key. May be the dealt row itself.
   */
  row: string;
  /**
   * Cell text (an integer cell's value spelled as text) to the Id the child shows. Every value must resolve to a declared, non-animated creation.
   */
  map: {
    [k: string]: string | null;
  };
}
/**
 * Instance-owned properties which reconciliation seeds at creation and subsequently preserves. Membership and the allocated deal slot remain owned by the source row.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementDealPreserve".
 */
export interface WorldPlacementDealPreserve {
  /**
   * Preserve an existing child's local position, yaw, and scale.
   */
  transform?: boolean;
  /**
   * Preserve its prototype, including changes made by responses.
   */
  prototype?: boolean;
  /**
   * Preserve its other facets, including responses and face sources.
   */
  facets?: boolean;
}
/**
 * Author-selected work and payment policy for rearranging a deal template's children.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementReflow".
 */
export interface WorldPlacementReflow {
  /**
   * Maximum preparation, candidate, and overlap work units in one preview; preparation includes a conservative charge for sorting offsets. Exhaustion refuses without edits.
   */
  candidateBudget?: number;
  /**
   * Nonnegative Int units charged for each changed placement, including a spatial seed edit.
   */
  costPerMove?: number;
  /**
   * An Int state row paying the price. Required for a nonzero price.
   */
  costRow?: string | null;
  /**
   * The payer cell; absent selects an unkeyed row's slot.
   */
  costKey?: string | null;
}
/**
 * A named spatial volume authored on one placement. The name is placement-local metadata; an influence channel is opaque to the engine and is never interpreted as water, power, or another game noun.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementSpatialVolume".
 */
export interface WorldPlacementSpatialVolume {
  /**
   * The stable name returned by spatial queries.
   */
  name: string;
  /**
   * Whether the volume occupies, reserves clearance, or reports influence.
   */
  role: WorldPlacementSpatialRole;
  /**
   * The common bounded shape.
   */
  shape: WorldSpatialShape;
  /**
   * An author-defined influence label. Required only for influence volumes.
   */
  channel?: string | null;
  /**
   * Whether layout policy must preserve this volume's placement transform.
   */
  pinned?: boolean;
}
/**
 * A placement-local bounded shape. Box yaw is measured about +Y; Y extents are part of the contract.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpatialShape".
 */
export interface WorldSpatialShape {
  /**
   * The shape kind.
   */
  kind: WorldSpatialShapeKind;
  /**
   * The local shape center.
   */
  center: DocumentVector3;
  /**
   * Positive local half extents for a box; zero for a sphere.
   */
  halfExtents: DocumentVector3;
  /**
   * Positive radius for a sphere; zero for a box.
   */
  radius?: number;
  /**
   * The local box yaw, in degrees. Sphere yaw is ignored.
   */
  yawDegrees?: number;
}
/**
 * Removes a placement document row by id; inside a transaction it must sit in the closing suffix.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectRemovePlacement".
 */
export interface WorldEffectRemovePlacement {
  $type?: "removePlacement";
  id: string;
}
/**
 * Saves the world through the host's save tap.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectSave".
 */
export interface WorldEffectSave {
  $type?: "save";
}
/**
 * Teleports a world-addressed body to a spawn point, or to a literal position with angles; exactly one of SpawnPoint and Position is authored.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectPose".
 */
export interface WorldEffectPose {
  $type?: "pose";
  key: string;
  spawnPoint?: string | null;
  position?: DocumentVector3;
  yawDegrees?: number;
  pitchDegrees?: number;
  rollDegrees?: number;
}
/**
 * Writes one fact on the identity a world-addressed body drives under: the body's cell in the world's reserved WorldIdentityFactLane row and the identity's own persisted facts row, together. Exactly one of Value and Expression is authored; a body driving under no owned identity refuses the write rather than minting one.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldEffectSetIdentityFact".
 */
export interface WorldEffectSetIdentityFact {
  $type?: "setIdentityFact";
  /**
   * The body — an index, a $cell: indirection, or a bound key.
   */
  key: string;
  /**
   * The fact key on the identity's row.
   */
  fact: string;
  /**
   * The integer literal.
   */
  value?: number | null;
  /**
   * A bounded integer expression evaluated at fire time.
   */
  expression?: ValueExpression;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionFactTrigger".
 */
export interface ActionFactTrigger {
  /**
   * The body fact.
   */
  fact: ActionFact;
  /**
   * The effects applied in order.
   */
  effects: ActionEffect[];
  /**
   * The predicate that must hold, or null for always.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * Level or edge.
   */
  mode?: ActionTriggerMode;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldColliderSphere".
 */
export interface WorldColliderSphere {
  $type?: "sphere";
  /**
   * The sphere radius.
   */
  radius: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldColliderCapsule".
 */
export interface WorldColliderCapsule {
  $type?: "capsule";
  /**
   * The body-local vector from the lower sphere center to the upper sphere center.
   */
  endpoint: DocumentVector3;
  /**
   * The capsule radius.
   */
  radius: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldColliderBox".
 */
export interface WorldColliderBox {
  $type?: "box";
  /**
   * The positive half-extents.
   */
  halfExtents: DocumentVector3;
  /**
   * The body-local orientation.
   */
  rotation: DocumentQuaternion;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldColliderFromCreation".
 */
export interface WorldColliderFromCreation {
  $type?: "fromCreation";
  /**
   * The referenced Id.
   */
  prototypeId: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRigid".
 */
export interface WorldRigid {
  /**
   * The body's mass, in the same units gravity.attractors masses use. Must be strictly positive after fixed-point compilation and yield representable room-scale mass/inertia properties — a rigid body with no compiled mass is not a rigid body, it is a decoration.
   */
  mass: number;
  /**
   * The coefficient of restitution against the static world and against another rigid body, in [0, 1]. Zero (the default) is a dead-stop collision; one is a lossless bounce.
   */
  restitution?: number;
  /**
   * The Coulomb friction coefficient at a contact point, against the static world and — as the pair's average — against another rigid body: the tangential (slip) impulse the contact solver applies is clamped to this times the contact's own normal impulse magnitude, coupled through the body's own inertia rather than decaying linear and angular velocity independently. Applied only while in contact, so it never slows a free-flying body. Non-negative (a coefficient over 1 is physically ordinary); zero (the default) is frictionless.
   */
  friction?: number;
  /**
   * The angular-velocity decay rate, per second, applied while a rigid body is in contact with a surface — the resistance that lets a rolling ball settle instead of spinning forever. Non-negative; the applied decay is clamped so a wide step never reverses spin.
   */
  rollingFriction?: number;
  /**
   * The linear-velocity decay rate, per second — applied as (1 - rate·dt) each tick, so the same authored value decays velocity the same way whatever the world's simulation rate. Non-negative; zero (the default) applies none.
   */
  linearDamping?: number;
  /**
   * The angular-velocity decay rate, per second, on the same terms as LinearDamping. Non-negative; zero (the default) applies none.
   */
  angularDamping?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCarry".
 */
export interface WorldCarry {
  /**
   * The full-scale carry point, in the carrier's own body-local axes: a carried body's world pose tracks root + orientation·(Offset × Scale) every tick it stays attached.
   */
  offset: DocumentVector3;
  /**
   * The carrier's own notional mass, in the same units Mass uses. A locomotion kit authors no mass of its own (Mass is gravitational, not inertial), so this stands in for it when deriving MaxCarryFraction's ceiling. Must be strictly positive.
   */
  massEquivalent: number;
  /**
   * The fraction of MassEquivalent — scaled by the carrier's own live Scale under the same mass ∝ Scale³ law WorldRigid derives against — a candidate body's own scaled Mass may not exceed. Non-negative; 1 (the default) admits a body up to the carrier's own mass-equivalent.
   */
  maxCarryFraction?: number;
  /**
   * The greatest distance, in world units, between the carrier's and the target's positions body.carry admits. Must be strictly positive.
   */
  maxReach?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldTether".
 */
export interface WorldTether {
  /**
   * The non-negative, fixed-representable world-unit ceiling an attach aim searches along the body's facing direction — also the tether's rope length at attach (the resolved anchor's actual distance, always within this ceiling).
   */
  maxAnchorDistance: number;
  /**
   * The fixed-representable aim-assist cone half-angle, degrees, within [0, 180], an attach candidate's bearing must fall within around the body's facing. Honoured only by a collider-list contact provider; a field provider's own directed march has no candidate list to score bearings over and ignores it (see WorldSolidField.TryNearestSurfaceAlongDirection's remarks).
   */
  aimHalfAngleDegrees: number;
  /**
   * The non-negative, fixed-representable world-units-per-second the held ReelChannel reels the rope at — positive reels out, negative in (the channel's own sign selects direction; this is a magnitude).
   */
  lengthRate: number;
  /**
   * The non-negative, fixed-representable rope-length floor a reel-in clamps to.
   */
  minLength: number;
  /**
   * The non-negative, fixed-representable multiplier a detach applies to the body's velocity at the instant of release — 1 (the default) preserves it exactly, below 1 dampens, above 1 boosts.
   */
  releaseVelocityScale?: number;
  /**
   * The declared channel name (validated) whose rising edge attaches the tether. null leaves attach unreachable from any channel.
   */
  attachChannel?: string | null;
  /**
   * The declared channel name (validated) whose rising edge clears an active tether. null leaves detach unreachable from any channel.
   */
  detachChannel?: string | null;
  /**
   * The declared channel name (validated) whose held bipolar value drives the rope length every tick. null leaves reel inert.
   */
  reelChannel?: string | null;
  /**
   * The declared state.body/state.identity counter slot name this facet writes 1 while attached and 0 otherwise — the camera program's select op keys off it exactly as it keys off any other state.<row> value. null writes nothing.
   */
  modeState?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAutonomyCadence".
 */
export interface WorldAutonomyCadence {
  /**
   * How often the body's physics/motion program advances.
   */
  motionSeconds?: number;
  /**
   * How often its selected producer refreshes steering. The most recent image is reused between refreshes.
   */
  steeringSeconds?: number;
}
/**
 * The row-to-entity assignment declaration — nothing about Sequence/Rows is kit-specific, so the same primitive distributes the kit table (a way of moving) and the look table (a way of looking) across the population. Resolved once at construction into each entry's fixed row index (precompute; zero steady-state cost). The kit assignment affects the simulation (it selects the compiled tuning/action bindings); the look assignment is presentation-only (it selects the appearance row).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRowAssignment".
 */
export interface WorldRowAssignment {
  /**
   * The sequence that selects a row.
   */
  sequence: WorldSequence;
  /**
   * Gets the authored row-name view. The absence-coalesce lives in the accessor for the same reason Elements's does.
   */
  rows: DocumentIdentifierList;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAddonRow".
 */
export interface WorldAddonRow {
  /**
   * The addon's identifying name — unique within the definition; used by console verbs and logging.
   */
  name: string;
  /**
   * The WASM module file path (machine-local; existence/hash verification is the run path's job).
   */
  modulePath: string;
  /**
   * The content-address integrity pin (sha256-64/{16 hex}). required — a guest whose module is unpinned makes the state it touches depend on a file on disk, which is a determinism hole before it is a security one.
   */
  hash: string;
  /**
   * The per-tick fuel budget before a deterministic halt.
   */
  fuel: number;
  /**
   * Whether the addon starts enabled.
   */
  enabled: boolean;
  /**
   * The addon's manifest — what it asks for, as data (see WorldCapabilityRequest): a designation only, never authority. Deny by default holds regardless of what this names, and so does the converse — this is the left half of requests ∧ grants, so a hold the manifest never names materializes no handle and the guest can never reach it (see Server.WorldAddonRuntime). Null/empty means the row asked for nothing and therefore reaches nothing. Reviewed by an operator before mounting, or by the runtime's own loud mount-time line naming exactly which requested pairs the settled grant table (the permissive seed plus any Grants row already applied) honors for this addon's principal right now, which it withholds, and which it holds beyond the manifest.
   */
  requests?: WorldCapabilityRequest[] | null;
  /**
   * The addon's machine-memory watch rows (the fifth event family — see WorldAddonMemoryWatch): declared alongside Requests, materializing only where the settled grant table also holds Observe/screen:<n> with an event budget for the watched screen (the same requested ∧ granted rule every other capability here already enforces). Null/empty means no watches.
   */
  memoryWatches?: (WorldAddonMemoryWatch | null)[] | null;
  /**
   * The instance-revision token: any change from what a currently-mounted guest was prepared under means fresh guest instantiation (a clean reset to the module's initial state), even when every other field is unchanged. NOT promised monotonic: world.undo and a whole-document load legitimately move it backward (configuration undo, not time travel — the pre-undo guest's memory is not part of the document and is never restored), while an author driving it forward is expected to increment with checked overflow so a wrapped value can never alias an earlier revision. Defaults to 0.
   */
  revision?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCapabilityRequest".
 */
export interface WorldCapabilityRequest {
  /**
   * The capability requested.
   */
  capability?: WorldCapability;
  /**
   * The subject requested.
   */
  subject?: GrantSubject;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAddonMemoryWatch".
 */
export interface WorldAddonMemoryWatch {
  /**
   * The engine screen-surface index hosting the watched machine.
   */
  screen: number;
  /**
   * The first bus address to watch.
   */
  address: number;
  /**
   * The byte-range length, 1..8 (a watch's changed-value payload is a single zero-extended i64 lane on the wire, so a range wider than 8 bytes has nowhere to carry its value and is refused).
   */
  length: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingOverlay".
 */
export interface WorldBindingOverlay {
  /**
   * The overlay's stable id — its mutation address (unique within the definition; carries no meaning beyond identity).
   */
  id: string;
  /**
   * The overlay binding document merged into the composed mapping.
   */
  document: BindingProfileDocument;
  /**
   * The on-screen bar policy carried with this binding layer; null carries no policy on this layer, so bar resolution falls through to the world-authored policy, and to Absent (no bar drawn) when neither an identity nor the world authors one.
   */
  bindingBar?: WorldBindingBarAuthoring | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingProfileDocument".
 */
export interface BindingProfileDocument {
  version: string;
  modifiers: (BindingModifierDefinition | null)[];
  chords: (BindingChordDefinition | null)[];
  contexts?: (BindingContextDefinition | null)[] | null;
  wheels?: (BindingWheelDefinition | null)[] | null;
  bindingBar?: BindingBarPreferences | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingModifierDefinition".
 */
export interface BindingModifierDefinition {
  id: string;
  sources: (string | null)[];
  pressThreshold?: number;
  releaseThreshold?: number;
  label?: string | null;
  icon?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingChordDefinition".
 */
export interface BindingChordDefinition {
  group: DocumentIdentifier;
  chord?: (string | null)[] | null;
  page?: BindingPageDefinition | null;
  command?: BindingCommandDefinition | null;
  held?: (string | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingPageDefinition".
 */
export interface BindingPageDefinition {
  id: string;
  entries: BindingPageEntryDefinitionList;
  label?: string | null;
  icon?: string | null;
  inherits?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingPageEntryDefinition".
 */
export interface BindingPageEntryDefinition {
  sources: (string | null)[] | null;
  command?: string | null;
  channel?: ChannelRef;
  scale?: number | null;
  activateOn?: CommandPhase | null;
  label?: string | null;
  id?: string | null;
  value?: CommandValue;
  text?: string | null;
  activator?: BindingActivatorDefinition | null;
  mode?: BindingEntryMode;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ChannelRef".
 */
export interface ChannelRef {
  [k: string]: unknown;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "CommandValue".
 */
export interface CommandValue {
  [k: string]: unknown;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingActivatorDefinition".
 */
export interface BindingActivatorDefinition {
  sequence: (string | null)[];
  mode?: BindingActivatorMode;
  timeoutTicks?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingCommandDefinition".
 */
export interface BindingCommandDefinition {
  command?: string | null;
  channel?: ChannelRef;
  scale?: number | null;
  holdRelease?: boolean;
  label?: string | null;
  icon?: string | null;
  value?: CommandValue;
  mode?: BindingEntryMode;
  text?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingContextDefinition".
 */
export interface BindingContextDefinition {
  family: string;
  state: string;
  group: DocumentIdentifier;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingWheelDefinition".
 */
export interface BindingWheelDefinition {
  id: string;
  group: DocumentIdentifier;
  holdPages: (string | null)[];
  rings: BindingPageDefinition[];
  style?: BindingWheelStyleDefinition | null;
  labelRow?: string | null;
  iconRow?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingWheelStyleDefinition".
 */
export interface BindingWheelStyleDefinition {
  pointerSelection?: BindingWheelSpatialSelectionMode;
  placement?: BindingWheelPlacement;
  deadZoneFraction?: number;
  ringWidthFraction?: number;
  outerGraceRingFraction?: number;
  sectorOffset?: number;
  initialRing?: number;
  ringSelection?: BindingWheelRingSelectionMode;
  excursion?: BindingWheelExcursionDefinition | null;
  axisDeadZone?: number;
  selectionGraceSeconds?: number;
  switchFraction?: number;
  fadeOutSeconds?: number;
  fadeOutEase?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingWheelExcursionDefinition".
 */
export interface BindingWheelExcursionDefinition {
  deadZone: number;
  thresholds: number[];
  spatialTravelFraction?: number;
  hysteresis?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "BindingBarPreferences".
 */
export interface BindingBarPreferences {
  hideUnbound?: boolean | null;
  scale?: number | null;
  contrastBoost?: number | null;
  uiScale?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarAuthoring".
 */
export interface WorldBindingBarAuthoring {
  /**
   * The physical controls this bar shows, by input source id (gamepad.buttonSouth, mouse.button1, …) — the same vocabulary a binding entry's sources speak, every id validated against the engine's input-source catalog, unique, at most MaxSlots. A layout's bank places these; one it does not place is not shown on that bank.
   */
  slotSet: (string | null)[];
  /**
   * The stacked banks — at least one, at most MaxBanks, unique ids, each naming a page the composed binding profile actually declares. List order is draw order.
   *
   * Items: One stacked binding-bar bank: what it is — the page it renders and its opacities. Several banks of the same slot set render simultaneously, each showing what that bank's OWN page binds. Where a bank sits, and which plates it shows, is each layout's banks table's to say; draw order is this list's order (later draws on top).
   */
  banks: (WorldBindingBarBank | null)[];
  /**
   * Whether the bar is shown when no live override hides it.
   */
  enabled?: boolean;
  /**
   * Whether the bar draws the atlas text it composes — a badge whose icon row carries a Label (LB/RB, LT/RT, LS/RS, the menu trio, the exotics), the active page's name under the modifier indicators, and the chord-hint lines above them. false drops every label-content badge outright and leaves a purely pictographic bar: every plate, the glyph-content badges (the d-pad arrows and the face-position marks), the bound actions' icons, and the modifier indicators all still draw.
   */
  text?: boolean;
  /**
   * Whether the bar draws the modifier indicator row on its anchor line — one plate per modifier the composed profile carries (declared rows plus every chord/held token the compiler synthesizes), lit while held. false drops the row and leaves the slot clusters alone; the chord hints and page label are Text's, not this one's.
   */
  modifiers?: boolean;
  /**
   * Whether a slot with no bound act on its bank's page should not render at all, rather than drawing the disabled tier-0 plate. A player's own BindingBarPreferences.HideUnbound overrides this.
   */
  hideUnbound?: boolean;
  /**
   * The opacity every joined seat's bar renders at while two or more seats are joined — the split-screen quieting lever, multiplied into each bank's own alpha; 1 keeps multi-seat bars fully opaque.
   */
  multiSeatAlpha?: number;
  /**
   * The state row a slot's icon resolves through, spelled state.<row> — the row's CELL KEY is the bound row's id when it authors one, else its action (command or channel; a dotted command name therefore needs an id, since a cell key holds no dot), and its value is an icons name. The engine holds no action-to-icon vocabulary of its own: the association is ordinary authored state, so it is written, read, and MUTATED like any other state (world.state.cell.set) — a spell minted at runtime gets its icon by writing a cell, never by reshaping the document. null resolves no slot icons at all; a key the row does not carry simply draws no icon.
   */
  iconRow?: string | null;
  /**
   * The bar's layouts by name — a crossbar, a strip, whatever an author adds next — each a whole WorldBindingBarLayout.
   */
  layouts?: {
    /**
     * The authored layout of one on-screen binding bar: its tables, its banks, and its size tuning. Every tuning field is an optional override of the engine's resolved default (the Default* constants below): lengths are fractions of the seat viewport's height, every *Ratio is a multiple of the button size, and every *MinPx is a device-pixel floor.
     */
    [k: string]: WorldBindingBarLayout | null;
  } | null;
  /**
   * The name of the layout drawn when LayoutCell is absent or names none.
   */
  layout?: string | null;
  /**
   * A text state cell, state.<row>.<key>, whose value names the live entry of Layouts; a value naming none falls back to Layout. Ordinary state: a chord, a wheel sector, or the console switches the bar's whole shape by writing the cell.
   */
  layoutCell?: string | null;
  /**
   * A text state cell, state.<row>.<key>, whose value picks the bar's model: single draws one bar — the active page's bank, in the first authored bank's place, swapping in place as a chord is held or released; any other value (or no cell) draws every bank the live layout places, the active one at full alpha and the rest as wings. Ordinary state, flipped the same way LayoutCell is.
   */
  modelCell?: string | null;
  /**
   * The bar's visibility condition over presentation facts, or null for always.
   */
  visible?: OverlayPredicateNullable | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarBank".
 */
export interface WorldBindingBarBank {
  /**
   * The bank's stable id — its mutation address and its key in every layout's bank table.
   */
  id: string;
  /**
   * The BindingPageDefinition.Id this bank renders — validated to exist somewhere in the composed binding profile.
   */
  pageId: string;
  /**
   * This bank's opacity when it is NOT the seat's currently active page.
   */
  alpha: number;
  /**
   * This bank's opacity when it IS the seat's currently active page; null draws fully opaque (1.0) while active.
   */
  activeAlpha?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarLayout".
 */
export interface WorldBindingBarLayout {
  /**
   * The plate tables by name — a cross, a strip, a column — each authored once and placed by the banks' pieces as many times as the layout needs.
   */
  tables?: {
    [k: string]: (WorldBindingBarSlotPlacement | null)[] | null;
  } | null;
  /**
   * Where each bank sits and what it shows, by bank id. A bank with no row here is not drawn in this layout.
   */
  banks?: {
    /**
     * One bank's place on a layout — a bank is a bar: where it hangs and the pieces it is made of. A control in the slot set that no piece places is not shown on this bank; a source placed by two pieces takes the later one.
     */
    [k: string]: WorldBindingBarBankPlacement | null;
  } | null;
  /**
   * Where the bar's own anchor (the modifier row, page label, chord hints) hangs, and the anchor every bank without its own shares.
   */
  anchor?: WorldBindingBarAnchor | null;
  /**
   * The slot-plate size at most: the writer shrinks it so every anchor group fits its seat region, and a seat's stored bar scale multiplies it.
   */
  buttonSize?: number | null;
  /**
   * The badge's offset as a fraction of ButtonSize — each plate's badge takes its own signed multiples of it (Badge).
   */
  glyphOffsetRatio?: number | null;
  /**
   * The badge's size as a fraction of ButtonSize.
   */
  glyphSizeRatio?: number | null;
  /**
   * The modifier indicator's plate half-extent, in button sizes.
   */
  modifierHalfRatio?: number | null;
  /**
   * The modifier indicators' pitch, in button sizes.
   */
  modifierSpacingRatio?: number | null;
  /**
   * The modifier badge's half-extent, as a fraction of the modifier plate half.
   */
  modifierGlyphRatio?: number | null;
  /**
   * The page label's glyph-cell height, as a fraction of the modifier plate half.
   */
  labelCellRatio?: number | null;
  /**
   * The page label's glyph-cell floor, px.
   */
  labelCellMinPx?: number | null;
  /**
   * The page label's drop below the anchor, as a fraction of the modifier plate half.
   */
  labelGapRatio?: number | null;
  /**
   * A chord-hint line's glyph-cell height, as a fraction of the modifier plate half.
   */
  hintCellRatio?: number | null;
  /**
   * A chord-hint line's glyph-cell floor, px.
   */
  hintCellMinPx?: number | null;
  /**
   * The chord-hint line pitch, as a fraction of the hint cell height.
   */
  hintLineStepRatio?: number | null;
  /**
   * The hint stack's lift above the anchor, as a fraction of the modifier plate half.
   */
  hintBaseGapRatio?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarSlotPlacement".
 */
export interface WorldBindingBarSlotPlacement {
  /**
   * The physical control's input source id (a slotSet member).
   */
  source: string;
  /**
   * Pitches right of the anchor (negative = left).
   */
  x: number;
  /**
   * Pitches above the anchor (negative = below).
   */
  y: number;
  /**
   * Where the plate's physical-button badge sits, [x, y] as signed multiples of the layout's glyph offset: +1 right / up, −1 left / down, 0 centered. A plate in a cluster points its badge outward — a d-pad's up button badges up, its left button left — so the badge never lands on a neighbour. null is the up-right corner, [1, 1].
   */
  badge?: number[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarBankPlacement".
 */
export interface WorldBindingBarBankPlacement {
  /**
   * The placed tables, in order.
   *
   * Items: One placed table: a named table of the layout, moved by At.
   */
  pieces: (WorldBindingBarPiece | null)[];
  /**
   * Where this bank hangs, or null to share the layout's anchor (and so its frame: the pieces then nest against the other banks there).
   */
  anchor?: WorldBindingBarAnchor | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarPiece".
 */
export interface WorldBindingBarPiece {
  /**
   * The key of a table in the layout's tables.
   */
  table: string;
  /**
   * The displacement, [x, y] in button pitches (x right, y up); null is none.
   */
  at?: number[] | null;
  /**
   * A badge direction for every plate of this piece, overriding the table rows'; see Badge. null keeps each row's own.
   */
  badge?: number[] | null;
}
/**
 * Where a bar hangs: a viewport edge and how far in from it. Every bank anchored to the same edge and inset shares one frame — their plates are laid out together on one pitch grid and the nearest plate of the whole group sits at the inset — so a nested crossbar is five banks on one anchor, and a strip with side columns is three groups on three.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBindingBarAnchor".
 */
export interface WorldBindingBarAnchor {
  /**
   * The viewport edge.
   */
  edge?: BindingBarEdge;
  /**
   * The gap between that edge and the nearest plate edge of everything anchored here, in button pitches — the same ruler every plate position uses. Along the other axis the group is centered.
   */
  inset?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldStorageDefaults".
 */
export interface WorldStorageDefaults {
  /**
   * The per-user blob endpoint (a URI, e.g. https://blob.byteterrace.com), or null for none. Validated as an absolute URI when present. Feeds WorldStorageSyncHandle's target construction; a URI here is edge-shaped (platform-managed containers), a connection-string override (CLI-only — see the validator) is raw-shaped.
   */
  endpoint?: string | null;
  /**
   * An explicit user-id override (an Entra oid Guid string for a dev box or agent), or null to decline identity (local-only). Fed to the identity resolver's explicit-override source.
   */
  userId?: string | null;
  /**
   * The direct-to-account connection container listing uses when Endpoint resolves to an edge-shaped target — the platform edge cannot serve List at all (see AzureBlobObjectStorageTarget.DirectEndpoint's remarks), so an edge-shaped target with this null refuses discovery by name instead of a request the edge cannot answer. Validated as an absolute URI when present; a connection-string override (CLI-only — see the validator) is for the dev/emulator shape. Ignored when Endpoint is raw-shaped (a raw target lists directly, like it reads and writes).
   */
  discoveryEndpoint?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPrototype".
 */
export interface WorldPrototype {
  /**
   * The row's stable string id — its mutation address and the handle placements reference — authored literally or through a Text state cell.
   */
  id: DocumentIdentifier;
  document: CreationDocument;
  /**
   * The SHA-256 hex64 of the document's canonical bytes (Hash on the canonical result the compose boundary produces). ABSENT resolves to the hash computed from Document at load — an author never writes a content hash by hand; see Hash.
   */
  hash?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementsSection".
 */
export interface WorldPlacementsSection {
  /**
   * The placements, in order.
   */
  rows?: (WorldPlacement | null)[] | null;
  /**
   * The live-placement policy — ABSENT derives from Rows (DeriveFrom: no live authoring, the scale envelope the rows span).
   */
  policy?: WorldPlacementPolicyDefaults | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPlacementPolicyDefaults".
 */
export interface WorldPlacementPolicyDefaults {
  /**
   * Boot-consumed. The extra screen slots the probe reserves, bounded by the engine's MaxScreenSurfaces ceiling.
   */
  authoringHeadroomScreens: number;
  /**
   * Boot-consumed. The placement rows of headroom the probe reserves beyond the boot placements. Each slot reserves both a whole-creation stamp and the maximum per-shape instance count; these independent conservative buffer floors are not a strict row quota.
   */
  authoringHeadroomPlacements: number;
  /**
   * Live-consumed. The placement uniform-scale envelope's floor — a pure validator bound, revalidated on every placement mutation.
   */
  minPlacementScale: number;
  /**
   * Live-consumed. The placement uniform-scale envelope's ceiling — also the worst-case scale Client.WorldStampPool's probe bound-radius reads (bound radius is spatial-cull metadata, never a word-capacity term, so re-reading it live every build cannot desync the frozen capacity floor).
   */
  maxPlacementScale: number;
  /**
   * Live-consumed. The proximity-candidate radius (world units) around a seat's editor focus point — cycling never walks the whole world (the explicit candidate policy).
   */
  candidateRadius: number;
  /**
   * Live-consumed. The candidate-count cap: at most this many nearest in-radius rows enter the cycle ring.
   */
  candidateCap: number;
  /**
   * Live-consumed. The drag preview channel's missing-response fallback: a released overlay with no definition delivery after this many produced frames drops honestly.
   */
  previewDeadlineFrames: number;
  /**
   * Boot-consumed. The derived screen slots the binder reserves at boot for creation faces (a face declared by a placement's creation, lit by a feed), registered at [Client.WorldPrototypeFacets.DerivedFaceBase, DerivedFaceBase + this). Bounded so the range stays inside the engine screen table.
   */
  derivedFaceScreens: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerFixed".
 */
export interface WorldSpeakerFixed {
  $type?: "fixed";
  /**
   * The emitter position, world space.
   */
  position: DocumentVector3;
  /**
   * The speaker's stable name — its mutation address.
   */
  name: string;
  /**
   * The feed it plays (source + channel + gain).
   */
  feed: WorldSpeakerFeed;
  /**
   * The point-attenuation policy, or null to coalesce to the WorldAudioDefaults section. Beds carry their own radii instead and leave this null.
   */
  attenuation?: WorldSpeakerAttenuation | null;
}
/**
 * A speaker's feed — what it plays: a shared source identity, a stereo channel selector, and a gain. Stereo separation is two independent speaker rows sharing one source with left/right selectors and different geometry — no group/attachment construct. Mono sources (the synth) degenerate every selector to ChannelMix.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerFeed".
 */
export interface WorldSpeakerFeed {
  /**
   * The shared source identity this feed taps.
   */
  source: WorldSpeakerSource;
  /**
   * The stereo channel selector — ChannelMix, ChannelLeft, or ChannelRight.
   */
  channel: string;
  /**
   * The feed gain (1 = unity), bounded by MaxLevel — the one audio gain ceiling the validator enforces everywhere.
   */
  gain: number;
}
/**
 * No signal is bound — honest silence (the emitter holds its place; audio.emitters reads the state).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerSourceNone".
 */
export interface WorldSpeakerSourceNone {
  $type?: "none";
}
/**
 * A live screen-hosted machine's audio, identified by screen slot — screen index is machine identity for screen-hosted machines. The validator checks only that the screen row exists, never that its declared source is $type machine (runtime inserts overlay declared sources); no live machine at drain time is silence plus a state echo, never a reject.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerSourceMachine".
 */
export interface WorldSpeakerSourceMachine {
  $type?: "machine";
  /**
   * The declared Index whose hosted machine feeds this source.
   */
  screenIndex: number;
}
/**
 * A tune asset (WorldTune) played through a headless machine host — acquired while any speaker references it, released when orphaned (a runtime derivation, never a data concept).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerSourceTune".
 */
export interface WorldSpeakerSourceTune {
  $type?: "tune";
  /**
   * The referenced Name (must resolve).
   */
  tuneId: string;
}
/**
 * The world voice synth playing a patch asset (WorldPatch). Patches are mono by construction: the feed's channel selectors degenerate to mix — documented, never rejected.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerSourceSynth".
 */
export interface WorldSpeakerSourceSynth {
  $type?: "synth";
  /**
   * The referenced Name (must resolve).
   */
  patchId: string;
}
/**
 * A point speaker's distance-attenuation policy, or null on the row to coalesce to the WorldAudioDefaults section (DefaultSpeakerRadius/DefaultCurve).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerAttenuation".
 */
export interface WorldSpeakerAttenuation {
  /**
   * The finite audible support radius in world units — at or beyond it the emitter is culled (finite support is the cull).
   */
  radius: number;
  /**
   * The falloff curve token (CurveSmoothstep or CurveLinear), or null for the audio-defaults curve.
   */
  curve: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerAnchored".
 */
export interface WorldSpeakerAnchored {
  $type?: "anchored";
  /**
   * What the speaker rides (see WorldAnchor).
   */
  anchor: WorldAnchor | null;
  /**
   * The attachment point relative to the anchor's resolved pose, in anchor-local axes.
   */
  offset: DocumentVector3;
  /**
   * The speaker's stable name — its mutation address.
   */
  name: string;
  /**
   * The feed it plays (source + channel + gain).
   */
  feed: WorldSpeakerFeed;
  /**
   * The point-attenuation policy, or null to coalesce to the WorldAudioDefaults section. Beds carry their own radii instead and leave this null.
   */
  attenuation?: WorldSpeakerAttenuation | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSpeakerBed".
 */
export interface WorldSpeakerBed {
  $type?: "bed";
  /**
   * The region's extent center, world space.
   */
  center: DocumentVector3;
  /**
   * The region's outer radius — the envelope's zero and the cull edge.
   */
  radius: number;
  /**
   * The full-presence inner radius; 0 shoulders the envelope from the center.
   */
  innerRadius: number;
  /**
   * The presence slew bound in seconds (null = the audio defaults' DefaultBedFadeSeconds).
   */
  fadeSeconds?: number | null;
  /**
   * The speaker's stable name — its mutation address.
   */
  name: string;
  /**
   * The feed it plays (source + channel + gain).
   */
  feed: WorldSpeakerFeed;
  /**
   * The point-attenuation policy, or null to coalesce to the WorldAudioDefaults section. Beds carry their own radii instead and leave this null.
   */
  attenuation?: WorldSpeakerAttenuation | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldTune".
 */
export interface WorldTune {
  /**
   * The row's stable name — its mutation address and the handle a speaker's Tune references.
   */
  name: string;
  /**
   * The referenced document's file path.
   */
  source: string;
  /**
   * The SHA-256 hex64 of the referenced document's canonical bytes.
   */
  hash: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPatch".
 */
export interface WorldPatch {
  /**
   * The row's stable name — its mutation address; referenced by Synth and by PatchId facets.
   */
  name: string;
  /**
   * The referenced document's file path.
   */
  source: string;
  /**
   * The SHA-256 hex64 of the referenced document's canonical bytes.
   */
  hash: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAudioDefaults".
 */
export interface WorldAudioDefaults {
  /**
   * The master gain the mix path applies over every emitter (1 = unity), bounded by MaxLevel.
   */
  masterGain: number;
  /**
   * The audible support radius a point emitter coalesces to when its row declares no attenuation/radius.
   */
  defaultSpeakerRadius: number;
  /**
   * The falloff curve a point emitter coalesces to: CurveSmoothstep or CurveLinear.
   */
  defaultCurve: string;
  /**
   * The presence slew bound a bed coalesces to when it declares none.
   */
  defaultBedFadeSeconds: number;
  /**
   * The listener policy: ListenerFocus (the active view camera's pose listens), seat:<n> (that seat's view camera), or a declared camera name — so a stage or museum world can pin its listener without touching the runtime.
   */
  listener: string;
  /**
   * Gets the cue table. The absence-coalesce lives in the accessor for the same reason Elements's does.
   *
   * Items: One world-event → sound binding — a row of the Audio section's cue table: when the named engine event fires, the referenced patch voices through a short-lived transient emitter placed per Placement. Event tokens are either a published engine mechanism (EventTokens) or a cue name emitted by one of the same world's rules. A genre can therefore bind authored rule events without widening the engine vocabulary.
   */
  cues: (WorldAudioCue | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAudioCue".
 */
export interface WorldAudioCue {
  /**
   * The published engine token or rule-emitted cue name.
   */
  event: string;
  /**
   * The referenced Name the cue voices (must resolve).
   */
  patchId: string;
  /**
   * Where the cue sounds: PlacementAtSite (spatial, at the event's world position — the shimmer's audio twin; events with no derivable site fall back to the listener), PlacementListener (UI feedback — rides the listener pose, so distance 0 renders full gain and the mixer's on-top-of-listener pan hold centers it), or emitter:<name> (sounds from the named speaker's resolved pose and support radius).
   */
  placement: string;
  /**
   * The cue's voice gain in thousandths (1000 = unity), or null for unity. Bounded by MaxLevel × 1000 — the shared audio gain ceiling in the cue table's integer unit.
   */
  gainThousandths?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCollision".
 */
export interface WorldCollision {
  /**
   * The contact qualities the world requires. An empty list permits analytic primitive contact; any declared requirement selects the SDF field.
   *
   * Items: A contact quality authored by the world, independent of the engine implementation that supplies it.
   */
  requirements: WorldContactRequirement[];
  /**
   * The signed skin the solver keeps between a body and every surface (world units).
   */
  contactSkin: number;
  /**
   * The relaxation iteration count per tick (above 8 is a solver pathology, not a choice).
   */
  maxIterations: number;
  /**
   * The steepest surface a body still counts as standing on. A contact whose normal leans further from the body's up axis than this pushes the body but never grounds it — the walkable-slope limit.
   */
  maxSlopeDegrees: number;
  /**
   * The finite-difference step field contact samples the surface normal with, in world units; 0 takes the evaluator's own default. Meaningful only when a requirement selects field contact.
   */
  gradientProbe: number;
  /**
   * Whether a body's surface hold may take any solid surface by default. A placement's own WorldPlacementGrip overrides this for the colliders it compiles; the field lattice's own terrain, which no placement row owns, has only this. false (the default) holds nothing.
   */
  defaultHold?: boolean;
  /**
   * The bounded body-overlap event policy. ABSENT takes Default; author maxPairsPerBody: 0 to disable body-pair events while retaining ordinary world contact.
   */
  events?: WorldCollisionEvents | null;
  /**
   * The bounded dynamic-body depenetration policy. ABSENT takes Default. This is independent of overlap events.
   */
  bodyContacts?: WorldBodyContactPolicy | null;
  /**
   * The world-space cell edge of the distance grid the solid field bakes its program into, so a query far from every surface reads a corner bound instead of marching the program; 0 (the default) bakes no grid and every query reads the exact program. A smaller cell tightens the bound and enlarges the grid. Meaningful only when a requirement selects field contact.
   */
  gridCellSize?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCollisionEvents".
 */
export interface WorldCollisionEvents {
  /**
   * The most sweep-and-prune candidates inspected for one body while discovering new overlaps. Must be at least MaxPairsPerBody.
   */
  candidateBudget?: number;
  /**
   * The maximum retained overlap-event degree of one body; zero disables collision begin/end sensing.
   */
  maxPairsPerBody?: number;
  /**
   * The maximum collision-begin relationships admitted in one authority tick. Existing relationships and their ends are not delayed.
   */
  beginBudget?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldBodyContactPolicy".
 */
export interface WorldBodyContactPolicy {
  /**
   * The most sweep candidates inspected for one solid body. Must be at least MaxPairsPerBody.
   */
  candidateBudget?: number;
  /**
   * The most physical pair corrections incident to one body in a tick.
   */
  maxPairsPerBody?: number;
  /**
   * The most substeps one rigid body's static-contact integration may take in a tick — the derived-count's own ceiling: the actual count is derived per body per tick from its speed and collider size (a fast ball takes more, a resting one takes one), never authored directly, but the ceiling bounds the worst-case per-tick cost and is echoed in world.budget.
   */
  rigidSubstepCeiling?: number;
  /**
   * Below this linear speed (world units/second) a grounded rigid body counts toward the resting hold window (RigidRestHoldSeconds). Non-negative; the default reproduces the engine's original hard-coded threshold.
   */
  rigidRestLinearSpeed?: number;
  /**
   * Below this angular speed (radians/second) a grounded rigid body counts toward the resting hold window, on the same terms as RigidRestLinearSpeed. Non-negative.
   */
  rigidRestAngularSpeed?: number;
  /**
   * How long a rigid body must stay under both rest thresholds while grounded before the resting latch actually closes — long enough that crossing a contact skin's noise band for one tick never freezes a body mid-roll. Non-negative.
   */
  rigidRestHoldSeconds?: number;
  /**
   * The fraction of a rigid body's own bounding radius one continuous- collision substep may travel — the derived substep COUNT stays derived (never authored directly), but how conservative that derivation is IS a document field: a smaller fraction takes more, cheaper substeps for the same speed; a larger one risks a fast body tunneling through a thin wall before RigidSubstepCeiling forces it to stop deriving more. Strictly positive.
   */
  rigidSubstepTravelFraction?: number;
  /**
   * The floor under one continuous-collision substep's travel bound (world units), independent of RigidSubstepTravelFraction — guards a body whose collider is small enough that the fraction alone would derive a near-zero bound. Strictly positive.
   */
  rigidSubstepMinimumTravel?: number;
  /**
   * Below this closing speed (world units/second), a rigid-vs-rigid contact restitutes at zero rather than the authored coefficient — a pure momentum-conserving separation. A rigid pair carries no rising-edge latch (unlike a body-vs-static-world contact), so without this floor two touching bodies at rest would restitute a hair apart every tick they are found overlapping, separating, and falling back together — a stable micro-bounce that never reaches either rest threshold. Non-negative; small enough that a real strike is unaffected.
   */
  rigidPairRestitutionSpeed?: number;
  /**
   * The sequential-impulse pass count a box or capsule rigid body's own ground support manifold (SupportManifold — up to four box corners or two capsule cap points) resolves over each substep, distributing the normal impulse across every manifold point rather than one. Strictly positive.
   */
  rigidManifoldIterations?: number;
  /**
   * The most EXTRA full sweeps WorldPopulation.ResolveDynamicContacts runs past the first — fresh broadphase and narrowphase, over the same bodies' now-current positions — so an impulse crosses more than one pair-hop within the same tick (a rack break, a falling domino line). The count actually run is derived DOWN from this ceiling by RigidPairIterationBudget divided by the first pass's own resolved-pair count, so a lightly loaded tick gets every authored pass and a crowded one stays bounded. Strictly positive.
   */
  rigidPairIterationCeiling?: number;
  /**
   * The total pair-pass work one tick's extra rigid-pair sweeps may spend, before RigidPairIterationCeiling caps it — see that field. Strictly positive.
   */
  rigidPairIterationBudget?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravity".
 */
export interface WorldGravity {
  /**
   * The evaluation strategy.
   */
  solver: WorldGravitySolver;
  /**
   * The non-negative proportionality constant applied to every source mass.
   */
  gravitationalConstant: number;
  /**
   * The positive Plummer softening length that bounds the force at short range.
   */
  softeningLength: number;
  /**
   * The static sources, or empty for a field summed from bodies alone.
   *
   * Items: One gravitational source the world declares, riding a placement's authored transform.
   */
  attractors: (WorldGravityAttractor | null)[];
  /**
   * A constant acceleration added to every solved answer, in world units per second squared. Point masses cannot express a uniform field, so this is how a world keeps a flat floor underfoot while its attractors own the space around them: authored as an acceleration, it is already what a body integrates.
   */
  uniform?: DocumentVector3;
  /**
   * Ergonomic point/planet presets lowered to static masses through the same Plummer kernel as Attractors. Absent means none.
   */
  points?: (WorldGravityPoint | null)[] | null;
  /**
   * Bounded placement-relative influences evaluated after the global solve. Absent means none.
   */
  areas?: (WorldGravityArea | null)[] | null;
  /**
   * Gets a value indicating whether the section declares an active global or bounded local field.
   */
  isActive?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAttractor".
 */
export interface WorldGravityAttractor {
  /**
   * The placements row whose position the source sits at. The row need not be solid or visible; only its transform is read.
   */
  placementId: string;
  /**
   * The source's non-negative gravitational mass.
   */
  mass: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityPoint".
 */
export interface WorldGravityPoint {
  /**
   * The placements row at the point source's centre.
   */
  placementId: string;
  /**
   * The positive acceleration magnitude, in world units per second squared, promised at ReferenceRadius after the world's authored softening is applied.
   */
  surfaceGravity: number;
  /**
   * The positive distance from the source centre at which SurfaceGravity is promised. For a planet this is its surface radius; a source need not carry geometry.
   */
  referenceRadius: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityArea".
 */
export interface WorldGravityArea {
  /**
   * The placement whose position, yaw, scale, and optional body attachment the area follows.
   */
  placementId: string;
  /**
   * The deterministic composition priority. Lower values apply first; authored order breaks ties, so a later equal-priority row applies later.
   */
  priority: number;
  /**
   * Whether the matching area's acceleration combines with or replaces the accumulated answer.
   */
  mode: WorldGravityAreaMode;
  /**
   * The placement-relative analytic bound. Boundary points are included.
   */
  bounds: WorldGravityAreaBounds;
  /**
   * The placement-relative acceleration form.
   */
  acceleration: WorldGravityAreaAcceleration;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaBoundsSphereBounds".
 */
export interface WorldGravityAreaBoundsSphereBounds {
  $type?: "sphere";
  /**
   * The positive placement-local radius, multiplied by the placement's scale.
   */
  radius: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaBoundsBoxBounds".
 */
export interface WorldGravityAreaBoundsBoxBounds {
  $type?: "box";
  /**
   * The positive placement-local half extents, multiplied by the placement's scale.
   */
  halfExtents: DocumentVector3;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaAccelerationDirectional".
 */
export interface WorldGravityAreaAccelerationDirectional {
  $type?: "directional";
  /**
   * The acceleration vector in world units per second squared. Zero is admitted so Replace can author a zero-gravity pocket.
   */
  value: DocumentVector3;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGravityAreaAccelerationRadial".
 */
export interface WorldGravityAreaAccelerationRadial {
  $type?: "radial";
  /**
   * The positive acceleration magnitude in world units per second squared.
   */
  magnitude: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHostDefaults".
 */
export interface WorldHostDefaults {
  /**
   * Which boot shape the world composes — see WorldHostPresentation. Defaults to Windowed, so every world authored before this field existed boots byte-identically; the --headless CLI flag reflects None for a single run without editing the document.
   */
  presentation: WorldHostPresentation;
  /**
   * The window client width in pixels.
   */
  width: number;
  /**
   * The window client height in pixels.
   */
  height: number;
  /**
   * The swapchain surface format (Unknown is rejected by the validator).
   */
  surfaceFormat: SurfaceFormat;
  /**
   * Whether the window enters borderless fullscreen when first shown.
   */
  fullscreen: boolean;
  /**
   * The swapchain presentation algorithm.
   */
  presentMode: PresentMode;
  /**
   * The boot present-pacing target in Hz; 0 selects automatic display pacing. The world.target live lever owns "now" thereafter.
   */
  targetHertz: number;
  /**
   * Seconds before the world auto-exits; 0 runs until the window is closed.
   */
  exitAfterSeconds: number;
  /**
   * Whether the SDF renderer may use the ray-query hardware path.
   */
  rayQuery: boolean;
  /**
   * Whether GPU per-pass timing boots armed; the world.timing live lever owns it thereafter.
   */
  timing: boolean;
  /**
   * The external-clock election policy, consumed at boot by the clock registry (which tolerates an unknown source id): null for the launcher's automatic election, or a non-whitespace source id / off. Shape-only validation (null or non-whitespace); the registry, not the validator, interprets the id.
   */
  genlock: string | null;
  /**
   * The QUIC listen endpoint (host:port) the authoritative host binds for remote peer admission, or null to stay loopback-only (no socket ever opens). Durable configuration per the unification contract — the --listen CLI flag reflects it for a single run without editing the document. Shape-only validation (null or a non-whitespace host:port pair); Server.WorldPeerHost is what actually parses and binds it.
   */
  listen: string | null;
  /**
   * The QUIC endpoint at which this world's authority is reached when another world resolves it as a destination, or null when the authority is colocated with the resolver. Colocation short-circuits the authority transport; it does not select a separate transfer path.
   */
  authority?: string | null;
  /**
   * The preferred graphics backend (Auto is OS-portable), or null when BackendRow reads it from a row — omitting both reads as Auto.
   */
  backend?: WorldBackendPreference | null;
  /**
   * A scalar kind=Text state row whose slot names the backend token, read at boot after literal Backend. A boot-only site (HostBackend): the resolver draws it once at composition, writes the settled preference into Backend, clears this facet, and narrates the settlement on stderr — the only surface that can say the backend was drawn at all, since a settled field is indistinguishable from an authored one thereafter. Its natural spelling is a weighted text source over the backend tokens (auto/directx/ vulkan — a one-context Markov table with bound 1, the degenerate flat weighted draw), parsed through ParseBackend at settle. A token naming no backend refuses by name. Drawing the name rather than an ordinal is deliberate: an ordinal draw over an enum silently re-points itself the day a member is inserted, and reads at the authoring site as a number nothing explains. Declared together with Backend it is refused by name — this record is a class, so presence is honestly observable here, unlike bodies.capacityRow's struct-typed site.
   */
  backendRow?: string | null;
  /**
   * The undo horizon, in journal entries: world.undo can never reach past this many trailing entries. 0 is unbounded — every world authored before this field existed keeps growing its journal for the life of the process, exactly as before. A positive depth bounds it: once the journal holds more than this many entries, the oldest ones fold forward into the base the journal already keeps (the same document-level replay WorldServer.ApplyUndo performs, run forward), so a checkpoint restore and a replay from the new base plus the retained tail still reproduce the live definition bit-identically. world.status echoes the authored value; world.undo names it when a requested count reaches past what the horizon has kept.
   */
  journalDepth?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldViewDefaults".
 */
export interface WorldViewDefaults {
  /**
   * The chase framing every seat's view resolves through by default.
   */
  seatRig: WorldCameraProgram;
  /**
   * The structural constraints/reference for live seat camera input.
   */
  seatControl: WorldSeatViewControl;
  /**
   * The program a seat's view resolves through while its published mode state targets CameraTarget — null for a world that authors no camera-targeting mode state. Resolved through the ordinary Puck.World.Client.WorldCameraRigCompiler pipeline against whichever body the seat currently perceives from (the possessed camera body — see Puck.World.Server.WorldEngagement), exactly like SeatRig resolves against the seat's own avatar; no bespoke per-frame integrator reads this field.
   */
  cameraRig?: WorldCameraProgram | null;
  /**
   * Gets the authored named layouts. The absence-coalesce lives in the accessor for the same reason Elements's does.
   *
   * Items: One named window composition — an ordered list of WorldViewSlots plus a transition envelope, selected for a given session shape by its SeatCount (0 = the catch-all for any joined-seat count). The data-side replacement for a compiled layout switch: an author can see it, change it, and add arrangements.
   */
  layouts: (WorldViewLayout | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatViewControl".
 */
export interface WorldSeatViewControl {
  /**
   * What the camera yaw is relative to.
   */
  yawReference: WorldSeatYawReference;
  /**
   * The minimum live pitch offset in radians.
   */
  minPitch: number;
  /**
   * The maximum live pitch offset in radians.
   */
  maxPitch: number;
  /**
   * The follow camera: with no look input the camera yaw eases in behind the body's heading; any look input (a deflected look stick, a held orbit/steer) is free-look and the follow yields for as long as it lasts. Optional; absent is a still camera that goes only where look input sends it. Needs World — a body-relative yaw already rides the body.
   */
  follow?: WorldSeatFollow | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatFollow".
 */
export interface WorldSeatFollow {
  /**
   * The exponential rate (per second) the camera yaw closes on the heading — about 63% of the remaining angle per 1/rate seconds; larger is a stiffer follow.
   */
  rate: number;
  /**
   * Whether the follow also runs while the body has no movement input. false (the default) is the classic feel: after a free-look the camera stays where you left it until you move.
   */
  whileIdle?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldViewLayout".
 */
export interface WorldViewLayout {
  /**
   * The layout's stable name (the view.override layout override handle; unique within the section).
   */
  name: string;
  /**
   * The joined-seat count this layout composes for, or 0 for the catch-all.
   */
  seatCount: number;
  /**
   * How long the ease into this composition takes when it becomes active.
   */
  transitionSeconds: number;
  /**
   * The render scale (0, 1] applied to every slot mid-transition (a soft dip that sharpens on settle), the compiled director's 0.5f now authored per layout.
   */
  transitionRenderScale: number;
  /**
   * Gets the slots, in order. The absence-coalesce lives in the accessor for the same reason Elements's does.
   *
   * Items: One slot of a WorldViewLayout — a normalized rect (origin top-left, Y down) plus what fills it. A slot whose Camera is null shows the seat that owns this slot (the next joined seat in slot order); a named camera renders that authored view into the rect.
   */
  slots: WorldViewSlot[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldViewSlot".
 */
export interface WorldViewSlot {
  /**
   * The rect's left edge, normalized [0, 1].
   */
  x?: number;
  /**
   * The rect's top edge, normalized [0, 1].
   */
  y?: number;
  /**
   * The rect's width, normalized (0, 1].
   */
  width?: number;
  /**
   * The rect's height, normalized (0, 1].
   */
  height?: number;
  /**
   * The authored camera name filling this slot, or null for the seat that owns it.
   */
  camera?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLooksSection".
 */
export interface WorldLooksSection {
  /**
   * The declared looks, in order.
   */
  rows?: (WorldLook | null)[] | null;
  /**
   * The look→entity assignment policy — ABSENT resolves to Default.
   */
  assignment?: WorldRowAssignment | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLook".
 */
export interface WorldLook {
  /**
   * The look's stable kebab-case name, authored literally or through a Text state cell; it is unique within the definition and assignable by the look table.
   */
  name: DocumentIdentifier;
  /**
   * Where the appearance resolves from (a catalog rig or a creation).
   */
  source: WorldLookSource;
  /**
   * The uniform render scale. Appearance only — it does not resize the body's motion tuning or its collision volume.
   */
  scale: number;
  /**
   * How the look animates with the body (see WorldLookMotion).
   */
  motion: WorldLookMotion;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLookSourceCatalog".
 */
export interface WorldLookSourceCatalog {
  $type?: "catalog";
  /**
   * The procedural renderer catalog rig to pin, or null for the occupant-owned pick. A fresh occupant seeds that pick from its first local slot and carries it across authority transfers, so ordinary admission does not restyle it.
   */
  index: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLookSourceCreation".
 */
export interface WorldLookSourceCreation {
  $type?: "creation";
  /**
   * The referenced Id, authored literally or through a Text state cell; it must resolve at validation.
   */
  prototypeId: DocumentIdentifier;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLookMotion".
 */
export interface WorldLookMotion {
  /**
   * The catalog rig's limb-swing scale (1 = the pre-look default; 0 stills the gait).
   */
  gaitAmplitude?: number;
  /**
   * Whether a creation look replays its authored timeline on the render clock.
   */
  replayFrames?: boolean;
  /**
   * The creation timeline cadence when ReplayFrames is set.
   */
  secondsPerFrame?: number;
  /**
   * The creation look's cues (see WorldLookCue); a firing cue's frame overrides the replay cursor for its hold.
   */
  cues?: (WorldLookCue | null)[] | null;
  /**
   * The dynamics row a second-order follower drives the stamped ROOT through — the client's interpolated body pose is the target, the follower's output is what renders. null (the default) is today's behavior: the root follows the interpolated pose exactly, no lag.
   */
  dynamics?: string | null;
  /**
   * A creation part id to dynamics row map — each named part follows the root's own resolved transform with its own personality, layered ON TOP of Dynamics. Legitimate only on a creation source (a catalog rig exports no parts); each key must name a part the creation's own part table declares.
   */
  partDynamics?: {
    [k: string]: string | null;
  } | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLookCue".
 */
export interface WorldLookCue {
  /**
   * The creation timeline frame's name.
   */
  frame: string;
  /**
   * How long the frame holds, seconds.
   */
  holdSeconds: number;
  /**
   * The shortest rest between self-fires, seconds, or null with MaxSeconds for a cue that never self-fires.
   */
  minSeconds?: number | null;
  /**
   * The longest rest between self-fires, seconds.
   */
  maxSeconds?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "DynamicsRow".
 */
export interface DynamicsRow {
  /**
   * The row's stable name, unique within the section — the spelling every consumer's own dynamics/row reference resolves against.
   */
  name: string;
  /**
   * The natural frequency f, Hz. Must be finite and positive; higher is snappier. A value that rounds to zero at Q16, or whose derived oscillation rate is too close to critical to resolve at the Q32 coefficient scale, is refused (the document project's validator refuses it by name).
   */
  f: number;
  /**
   * The damping ratio ζ (dimensionless). 0 rings forever; <1 overshoots and rings down; 1 is critically damped (the fastest approach that never overshoots); >1 is overdamped (slower, still no overshoot).
   */
  zeta: number;
  /**
   * The initial response r (dimensionless). 0 eases in from rest; >0 reacts immediately to the target's own motion; >1 overshoots the target's motion before settling; <0 anticipates by initially moving opposite the target's motion.
   */
  r: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGrant".
 */
export interface WorldGrant {
  /**
   * The acting identity the grant is for.
   */
  principal?: WorldPrincipal;
  /**
   * The capability conferred.
   */
  capability?: WorldCapability;
  /**
   * The subject the capability scopes to.
   */
  subject?: GrantSubject;
  /**
   * Whether the grant is held exclusively (single holder per capability+subject).
   */
  exclusive?: boolean;
  /**
   * The per-tick dispatch allowance for the row's capability — compute, not space (a request costs a host dispatch, not a record in a region). Only an Observe or Drive grant to an untrusted principal (Addon/ Peer) may carry one today: the grant door (Server.WorldGrants.TryGrant) requires it there (a defaulted budget would silently decide a DoS ceiling), refuses 0 (accepted-and-inert — grant nothing instead), and refuses it everywhere else (a trusted principal's grant, or Present/Control/ Mutate/Edit) — those doors do not meter yet, and a field admitted ahead of enforcement would be a lie in the schema. The effective ceiling at the addon door is min(Budget, Puck.Scripting.AddonAbi.MaxOutCells) — deliberately not enforced here at the grant door, so the grant schema and the ABI capacity constant stay free to move independently.
   */
  budget?: number | null;
  /**
   * An untrusted contributor's channel reach on a Drive row, carried alone: which declared ordinals this contributor may touch at all. Reach is not consent; a reached channel contributes nothing until the occupying seat authors a positive ceiling for it.
   */
  reach?: ChannelReachMask | null;
  /**
   * The ordinals named by the occupying seat's ceiling gesture on its own Drive row. The seat may issue the gesture repeatedly to give different channels different ceilings; each gesture writes only the ordinals it names and leaves the rest unchanged, while revocation clears the whole ceiling value.
   */
  consent?: ChannelConsentMask | null;
  /**
   * The pool ceiling c (raw Q16.16, 0 < c ≤ One) bounding how far the untrusted pool may pull the channels Consent names away from the human's own value. It is one number per (seat, channel), authored by the seat, and the grant door enforces exactly that: a row carrying a ceiling must be the occupying seat's own row (seatN drive body:N) and must name the channels it applies to. It is never carried on a contributor's row and never derived across rows — no combination of contributor-declared numbers (max, sum, or min) is defensible, and a contract nobody can state as a single number is not one. 0 is refused at the door, mirroring Budget's 0 refusal verbatim: pool-but-never-reach is accepted-and-inert, so grant nothing instead of a ceiling that can never fire. A ceiling authored in the world document is withheld at boot (the row itself still applies) — see Server.WorldServer's constructor: the document may pre-wire a contributor's reach, but consent is a thing only a seated human grants live.
   */
  ceiling?: number | null;
  /**
   * The MutationKindMask a row admits — legal only on a Mutate row over a concrete Section, Creation, or Placement subject, or an Edit row over a concrete State subject (never the wildcard — "which kinds" presupposes one bounded target — and never any other capability). The grant door refuses a bit outside the target's own declared kind set (WorldMutationKindCatalog.KindsOf(section), where a row-scoped subject resolves to the section that owns it, or KindsOf(WorldSection.State) for an Edit row) and refuses an effective mask of zero (an admitted-but-inert bit set is a grant that lies — the identical "grant nothing instead" rule Budget's 0 and Ceiling's 0 already enforce). On an Edit row this is what separates bumping a state row from redefining it: verbs:UpsertStateCell,RemoveStateCell admits the per-cell writes while denying the whole-row UpsertStateRow/RemoveStateRow that could re-author the row's envelope. An unmasked Edit row keeps full reach over its subject, so deny-by-default plus opt-in narrowing holds and no seeded row changes meaning. A null mask on a re-grant of the same (Principal, Capability, Subject) row clears a previously-recorded mask — unlike Budget/Reach, which only ever write when the incoming grant carries one and otherwise leave the prior value untouched; a mask a re-grant does not repeat is a mask the operator meant to take back, not one this door defaults into surviving silently. Revoking the row clears it outright. When a principal holds both a concrete row and the (trusted-only) wildcard row, the deciding row from Rule governs which mask applies — ConcreteHold beats WildcardHold, exactly as it does for the bare allow/deny check.
   */
  kindMask?: MutationKindMask;
  /**
   * The per-tick event-cell allowance for an Observe row over an event-bearing subject (Body, Screen, Region, Seat, or Adjacency) — a grant-row property alongside Budget, metering a different cost: Budget meters query dispatch (a guest asking), this meters event push volume (the host telling) — two separate meters, never one renamed. A row with no EventBudget still observes normally (a bare observe body:<n> keeps working exactly as before) but receives no events for that subject. Required (refused by name otherwise) on an Observe row over Region, Seat, Screen, or Adjacency, since those subject kinds carry no other live meaning — an event-bearing subject with no event budget would be accepted-and-inert, the identical rule Budget's own 0-refusal enforces. That requirement stacks with (never replaces) the pre-existing rule that every untrusted principal's Observe row also needs Budget: the dispatch meter does not know a subject carries no query verb, so an observe region:<name> row needs both Budget and EventBudget today. Refused on any capability but Observe, and eventBudget:0 is refused unconditionally (grant nothing instead).
   */
  eventBudget?: number | null;
  /**
   * The timed-channel-press ceiling in raw Q16.16 seconds. Legal only on a Drive row and bounded by the server's engine backstop. Omission selects DefaultHoldSeconds; zero forbids timed holds while leaving live held input untouched.
   */
  holdCeiling?: number | null;
  /**
   * The DocumentWriteMask a row admits on the cross-document durable-state write-back channel — legal only on a Mutate row over a concrete State subject, the one door that speaks WorldDocumentWriteKind operations (see Server.WorldOwnedWorlds.Decide). A separate field from KindMask, not a second reading of it: the two vocabularies share a bit-lane shape and nothing else, and collapsing them into one ulong made bit 0 mean UpsertKit on one row and Set on another. Same zero/inadmissible-bit refusals and same clear-on-re-grant rule as KindMask.
   */
  writeMask?: DocumentWriteMask;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudSection".
 */
export interface WorldHudSection {
  /**
   * The section defaults.
   */
  defaults: WorldHudDefaults;
  /**
   * Gets the authored world-scope panels. The absence-coalesce lives in the accessor for the same reason Elements's does.
   *
   * Items: One HUD panel row — a stable id (unique within the section), a normalized viewport rect in screen space, which band it draws in, its chrome style, and its child elements. WorldMutation.UpsertHudPanel carries the whole row (elements included) as one cross-row transaction boundary; WorldMutation.UpsertHudElement/ WorldMutation.RemoveHudElement read-modify-write a single element within an already-declared panel.
   */
  panels: (WorldHudPanel | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudDefaults".
 */
export interface WorldHudDefaults {
  /**
   * Whether the world-scope HUD panels render at all — a world-level kill switch independent of any individual panel's row (a diegetic reveal gate can flip this without editing every panel).
   */
  enabled: boolean;
  /**
   * The drawn pointer cursor's presentation policy, or null for no drawn cursor at all — the engine draws no cursor of its own; the standard policy is AUTHORED, in Assets/worlds/standard.world.json. Whole-row replace semantics apply: a SetHudDefaults authored without it clears any earlier authored policy back to hidden.
   */
  cursor?: WorldHudCursor | null;
  /**
   * The visibility condition every world-scope panel is gated by, beside its own, or null for always.
   */
  visible?: OverlayPredicateNullable | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldHudCursor".
 */
export interface WorldHudCursor {
  /**
   * The cursor ray's hover reach in world units — how far into the world a pointer resolves a hovered row.
   */
  hoverRadius: number;
  /**
   * The drawn cursor's ring radius, pixels.
   */
  sizePx: number;
  /**
   * The bare cursor's palette role; hover lights the accent tier regardless.
   */
  role: WorldHudCursorRole;
  /**
   * The drawn cursor's visibility condition, or null for always.
   */
  visible?: OverlayPredicateNullable | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIconographySection".
 */
export interface WorldIconographySection {
  /**
   * The icon rows.
   */
  rows?: (WorldIconRow | null)[] | null;
  /**
   * The badge-mapping rows.
   */
  badges?: (WorldIconBadgeRow | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIconRow".
 */
export interface WorldIconRow {
  /**
   * The stable icon name.
   */
  name: string;
  /**
   * The glyph content, or null when this row carries Label instead.
   */
  glyph?: WorldIconGlyphRef | null;
  /**
   * The short text content, or null when this row carries Glyph instead.
   */
  label?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIconGlyphRef".
 */
export interface WorldIconGlyphRef {
  /**
   * The font id (WorldIconFontCatalog).
   */
  font: string;
  /**
   * The glyph spelling: one literal character, or U+XXXX.
   */
  glyph: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIconBadgeRow".
 */
export interface WorldIconBadgeRow {
  /**
   * The physical control's input source id (gamepad.buttonSouth, gamepad.leftTrigger, mouse.button1, …).
   */
  source: string;
  /**
   * The default icon name.
   */
  icon: string;
  /**
   * Family-specific overrides — ABSENT resolves to none.
   */
  overrides?: (WorldIconBadgeOverride | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIconBadgeOverride".
 */
export interface WorldIconBadgeOverride {
  /**
   * The controller family name.
   */
  family: string;
  /**
   * The icon name this family shows instead of the row's default.
   */
  icon: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldStateSection".
 */
export interface WorldStateSection {
  /**
   * Document-owned cell rows. These remain mutation-addressable through state:<name>.
   */
  world?: WorldStateRow[] | null;
  /**
   * Per-body ephemeral counters and timers, compiled into each body's bounded ordinal arrays.
   */
  body?: ActionStateSlot[] | null;
  /**
   * Per-body counters and timers synchronized through the durable identity-document seam.
   */
  identity?: ActionStateSlot[] | null;
  /**
   * The lattice topologies the section's lattice-shaped rows lie over (see LatticeTopology; the document adds the physical WorldFieldTopology case).
   */
  lattices?: (LatticeTopology | null)[] | null;
}
export interface WorldStateRow1 {
  /**
   * A validated state-section row name or cell key — the base SafeName rule plus no dot anywhere, which is what makes the state.<row>.<key> HUD binding grammar unambiguous by construction: splitting a bound token on '.' can never mistake part of a row or cell name for a grammar separator, because neither can hold one. A row's reserved slot key ("$value") is unaffected — '$' is neither a reserved character nor a dot, so it is already a legal CellName like any other author-chosen key, exactly the one reserved exception the substrate mints rather than authors.
   */
  name: string;
  /**
   * The closed set of cell value kinds a state row declares, shared by every cell the row carries. Carries no float kind: simulation state is float-free by the determinism contract (see Fixed for how a fractional value still rides here). A counter is represented as Fixed; a timer is Int with NonNegative set.
   */
  kind: "Int" | "Fixed" | "Bool" | "Text";
  value?: ShapeNonNullable;
  /**
   * The row's current cells (default empty). Refused past its effective capacity, and on a duplicate key, by name — unless Evicts is set, in which case a write that would grow past capacity evicts the oldest cell instead of refusing (see Evicts). A slot-shaped row (see IsSlot) holds exactly one cell keyed SlotKey; a keyed row may hold any author-chosen keys except SlotKey itself, which is reserved for the value sugar and refused as an authored cell key.
   */
  cells?: {
    key: string;
    value: ShapeNonNullable;
    advance?: StateAdvance;
    dynamics?: {
      row: string;
      y0: string;
      v0: string;
      epochTick?: number;
    };
    cycle?: StateCycle;
    visibility?: StateVisibility;
    /**
     * When a stored knowledge value was last seen and whether the latest observation still sees it.
     */
    observation?: {
      /**
       * The last observation tick.
       */
      tick: number;
      /**
       * Whether the latest explicit refresh sees this cell.
       */
      visible: boolean;
    };
    provenance?: string;
  }[];
  /**
   * The row-wide declared lower bound every cell's Value must satisfy, raw-encoded per Kind (raw FixedQ4816 bits for Fixed), or null for none. Present only together with Max — a range is authored as a pair or not at all. Legitimate only for Int/Fixed. Omitted from the wire when null.
   */
  min?: number | string;
  /**
   * The row-wide declared upper bound, raw-encoded per Kind, or null for none. Present only together with Min. Omitted from the wire when null.
   */
  max?: number | string;
  /**
   * The row's own cell-count ceiling (1..MaxCellsPerRow), or null to fall back to the implicit ceiling. A row declaring Capacity can never be a slot (IsSlot), even if it happens to carry exactly one cell — declaring a capacity is declaring table intent. Omitted from the wire when null.
   */
  capacity?: number;
  /**
   * Whether every cell's value must be non-negative, enforced regardless of any authored Min. Legitimate only for Int/Fixed. A timer is represented as Int with this set.
   */
  nonNegative?: boolean;
  /**
   * Whether this row is a bounded, FIFO-evicting table. Ordinarily a Capacity is a hard ceiling and a write that would exceed it is refused by name; with this set, such a write instead succeeds and, if it added a new key past capacity, evicts the row's oldest surviving cell. Eviction runs as a pure function of the candidate cells inside the compose step, so replay reproduces the identical victim, and the dropped key is named on the mutation's apply echo. Eviction is by insertion position, not recency of touch: a new key is appended to the end of Cells and eviction always drops index 0. Re-writing an existing key updates it in place without moving it — true FIFO, never LRU. Legitimate only together with a declared Capacity. Default false.
   */
  evicts?: boolean;
  advance?: StateAdvance;
  /**
   * The AUTHORED-RANDOMNESS facet: the declaration that a SITE's value is DRAWN rather than literal. One facet, one source family, one engine — an NPC-bark text site, a loot cell, a random census, and a drawn host backend are the SAME mechanism pointed at different sites.
   */
  draw?: {
    /**
     * The declared source to draw from, by name — or null when StateGenerator inlines one.
     */
    source?: CellName | null;
    /**
     * An inline anonymous source — or null when Source names a declared one.
     */
    generator?: StateGenerator | null;
    /**
     * When this site draws and whether it may be redrawn (see DrawTiming).
     */
    timing?: DrawTiming;
    /**
     * An authority-provisioned 256-bit secret for an independently keyed streamDraw sample at each cursor. Never sent in observations.
     */
    secret?: ClosedBitset256;
  };
  /**
   * How many samples this site's Draw has ever consumed — engine-minted bookkeeping and the position the engine re-seeks to (GeneratorEngine.AdvancesPerSample scales it into Pcg32XshRr advances, so resuming is an exact O(1) advance rather than a replay of the earlier draws). Stored in the document, so world.undo, world.save, and replay rewind a site's draw position with the same whole-document restore that rewinds an ordinary counter. Zero when Draw is null; refused negative.
   */
  drawCursor?: number;
  /**
   * This site's drawn masks — engine-minted bookkeeping a source under an exhausting GeneratorMode carries: one mask per context, by declaration ordinal, for a Markov source; exactly one for a weighted numeric source. Bit i is set when entry i has been drawn. Lives at the site rather than on the source row, which lets two sites reference one declared source and draw independently. Null or empty for a site whose source never exhausts.
   */
  drawnMasks?: string[];
  /**
   * How many values have ever been pushed into a Ring row — engine bookkeeping that names the next slot (cursor mod capacity) and how much of the ring is filled. Zero without the trait; refused negative.
   */
  historyCursor?: number;
  visibility?: StateVisibility;
  /**
   * A persisted knowledge layer refreshed explicitly by the authority.
   */
  knowledge?: {
    /**
     * The integer/boolean board observed.
     */
    source: string;
    /**
     * A boolean board over the same topology; true cells are currently observed.
     */
    mask: string;
  };
  /**
   * Marks a plain integer row as a guarded submission stamp: the row's own generation Sequence, the sole state a PhaseGuard checks and the mutation pipeline advances. Nothing about who may act, in what order, or under what deadline is engine knowledge any more — a turn order, a round counter, a ready or skipped bitset, and a deadline are all ordinary rows a world's own rules author and advance, and eligibility is the ordinary grant/admission system over whichever rows a rule ties to this one via PhaseOf. Submitting any mutation whose PhaseGuard matches this generation both admits the submission and, on success, advances the generation by one: the guard's presence on a mutation IS the turn's completion, so a world that wants several ungated moves before a turn ends simply leaves those rows untagged and reserves PhaseOf for the one row that ends it.
   */
  phase?: {
    /**
     * The generation. Advanced by the mutation pipeline after a guarded mutation naming this row succeeds; never written directly.
     */
    sequence?: number;
  };
  /**
   * The phase row required on external gameplay transforms that write this row.
   */
  phaseOf?: string;
  /**
   * The discrete topology whose cell ordinals this row's integer values name — a value-typing trait, independent of Domain (legitimate only alongside a KeysOf domain over Int cells).
   */
  valuesFrom?: string;
  /**
   * A StateRow's declared cell domain — the closed answer to "which keys does this row's storage admit" that IsKeyed/IsSlot/CellCeiling switch over, replacing the five hand-kept discriminators (a null Board, Tokens, Zone, KeysFrom, or History facet) inference used to read the same shape off of. Orthogonal traits — a row's Advance/Dynamics/Cycle/ Draw/Visibility/Knowledge/ Phase/PhaseOf/ Evicts/Min/Max/ NonNegative — are unaffected by which case a row declares; every combination the validator already refused (a lattice row carrying advance, a phase row carrying capacity) is refused the identical way with the case substituted for the old field.
   */
  domain?: StateDomainSlot | StateDomainKeys | StateDomainKeysOf | StateDomainCellsOf | StateDomainRing;
  /**
   * A CellsOf row's declared inverse: the board's cells are not authored directly but derived from a keyed Tokens row naming cells of the same topology and a Codes row keyed the same way, giving each token's code. Legitimate only on a Int row whose EffectiveDomain is CellsOf and that carries no field trait — a document project's validator enforces both.
   */
  inverse?: {
    /**
     * The keyed Int row whose cell values name cells of the board's topology — a value naming no cell means that token is off the board.
     */
    tokens: CellName;
    /**
     * The row giving each token's code, keyed the same as Tokens — the same cell keys, in the same order, so index i of one names the same token as index i of the other.
     */
    codes: CellName;
  };
  /**
   * The row's own (slot-cell) second-order easing trait, or null for an ordinary row whose slot value only changes through an explicit write. See StateDynamics. Legitimate only for Int/Fixed, only on a scalar (slot-eligible) row, and never together with Advance or Draw. A keyed row's own cells ease independently through Dynamics instead.
   */
  dynamics?: StateDynamics;
  cycle?: StateCycle;
  /**
   * Whether this row is a drive-admission gate. When set, this must be a keyed row (IsKeyed) whose per-body cell — keyed by the body's 0-based entity index — is consulted before admitting a drive or action intent for that body: a nonzero cell refuses the body's intents until the cell reads zero again, checked fresh every tick. The engine does not interpret the row's name; several independently named gate rows may exist at once, any one of which can refuse. Legitimate only on a row declaring Capacity, and only for Int/Fixed/ Bool. Default false.
   */
  gatesDrive?: boolean;
  /**
   * A state row's field trait — carried only by a row whose Domain is CellsOf over a Field-kind topology: the row holds one Fixed scalar per cell of that topology instead of sparse board cells. Values are authored DECIMAL (like every lattice quantity), not raw Q48.16 bits; a field row refuses slot/keyed members (cells, capacity, advance, dynamics, draw -- per-cell draws are the spatial-draw seam, refused until it lands).
   */
  field?: {
    /**
     * The value every cell starts at before paint.
     */
    initial?: number;
    /**
     * The least value a cell holds.
     */
    min?: number;
    /**
     * The greatest value a cell holds.
     */
    max?: number;
    /**
     * World units of solid surface per unit of value above the lattice origin -- 0 for a row that is not geometry.
     */
    heightScale?: number;
    /**
     * The color the row's surface shades with — a #RRGGBB literal or a state.<row>[.<key>] Text-cell binding (WorldColor's shared grammar, resolved live at emit); required when HeightScale is nonzero.
     */
    color?: string | null;
    /**
     * The initial fills, applied in order over Initial.
     */
    paint?: WorldLatticeFill[] | null;
    /**
     * Marks this field a fluid MEDIUM, or null for an ordinary field. A medium field's value times HeightScale over the lattice origin is a free surface every active body samples each tick at its coupled cell — the same body-coupling ceiling Emit/ Expose resolve against — refused unless HeightScale is greater than zero (a surface-less medium is meaningless).
     */
    medium?: WorldLatticeMedium | null;
  };
}
/**
 * A StateRow's continuous accumulation trait: the row's stored cell is a base value, and the read value advances with elapsed ticks at an exact per-tick rational rate from the tick it was last explicitly set (EpochTick). Used for regen, fractional accumulation, a day/night clock — anything that should move on its own between observations.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateAdvance".
 */
export interface StateAdvance {
  /**
   * The per-tick rate's signed numerator, in the row's own displayed unit (see this type's remarks). Negative accumulates downward (decay); zero is declared but inert.
   */
  rateNumerator: number;
  /**
   * The per-tick rate's denominator. Refused at zero or below.
   */
  rateDenominator: number;
  /**
   * The server tick the rate starts accumulating from — the tick the row's base value was last explicitly set, or the loaded document's own authored value for a row never set since. A negative value is refused; in practice this can only be violated by an authored boot document, since every live write rebases to the applying tick before validation sees it.
   */
  epochTick?: number;
}
/**
 * A row's or cell's tick-indexed rotation trait: the value is a pure function of the server tick through a generator of the symmetry lattice's reflection group — Puck.Maths.SymmetryWord, the lattice's own thirty-step cycle when no Word is authored — raised to Power once per step. The generator's order is the loop's period, derived from the word rather than authored: a word of order twelve is a twelve-position dial, one of order twenty-four a day. Nothing accumulates and nothing is rebased: the mapping is tick-absolute, so a replay, a reconnect, or a fresh read at any tick lands on the same bits.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateCycle".
 */
export interface StateCycle {
  /**
   * The generator as a word of reflections — mirror nodes, one to eight, applied first to last — or null for the lattice's own cycle. A word that moves no node is refused: it loops nothing.
   */
  word?: number[] | null;
  /**
   * How many applications of the generator one step is; nonzero, and smaller in magnitude than the generator's order, since a power reduces modulo the order and a multiple of it would be the identity.
   */
  power?: number;
  /**
   * What the cell reads; must suit the carrying row's CellKind.
   */
  output?: CycleOutput;
  /**
   * The server ticks one step lasts; refused at zero or below.
   */
  ticksPerStep?: number;
  /**
   * The server tick the step count is measured from; a tick before it reads as step zero. A negative value is refused.
   */
  epochTick?: number;
  /**
   * Elapsed ticks already accumulated toward the next step at EpochTick; must be non-negative and less than TicksPerStep.
   */
  substepTicks?: number;
}
/**
 * Opt-in observation policy. Null readers means public; an empty list means authority only. Row and cell policies intersect. Replica-tier authorities remain fully trusted.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateVisibility".
 */
export interface StateVisibility {
  /**
   * Canonical authenticated principal tokens; no seat or peer identity comes from the request payload.
   */
  readers?: string[] | null;
  /**
   * What an observer who may read the row learns about the cells it may not.
   */
  hidden?: HiddenCells;
  /**
   * A keyed text row whose cell texts are principal tokens admitted beside Readers: the live audience a rule widens by writing a token (a showdown reveals a hand) or narrows by clearing one. Either list alone, or both, keeps the row private; neither makes it public.
   */
  readersFrom?: string | null;
  /**
   * Gets a value indicating whether the policy admits the public observer: no reader list of either kind.
   */
  isPublic?: boolean;
}
/**
 * An authored stochastic source — the vocabulary for every randomness declaration in the document: a name generator, a dialogue line, a loot roll, a flat weighted draw, a multiset sample, a random census, and a drawn host backend all reduce to a source of this family, sampled at an authored moment into an authored site (see Draw).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateGenerator".
 */
export interface StateGenerator {
  /**
   * Which draw shape this source fires.
   */
  source?: GeneratorSource;
  /**
   * Markov only: the context every emission begins from. Must name a declared context.
   */
  start?: CellName | null;
  /**
   * Markov only: the maximum tokens one emission may draw before refusing by name, 1..MaxEmissionBound. Left at DefaultBound by a numeric source.
   */
  bound?: number;
  /**
   * Markov only: the declared contexts, at least one, uniquely keyed.
   */
  contexts?: GeneratorContext[] | null;
  /**
   * Markov, weighted numeric and symmetry orbit: how the entries are consumed (see GeneratorMode).
   */
  mode?: GeneratorMode;
  /**
   * UniformRange only: the closed range's inclusive lower bound — both bounds present or neither. Raw-encoded per the destination site's CellKind (raw FixedQ4816 bits for a fixed site) — unlike a site row's own min/max, which a fixed row authors as decimal text, since a source is not bound to one site and cannot know the kind it will write.
   */
  rangeMin?: number | null;
  /**
   * UniformRange only: the inclusive upper bound, same encoding as RangeMin.
   */
  rangeMax?: number | null;
  /**
   * WeightedNumeric only: the weighted numeric outcomes, at least one, at least one carrying a non-zero weight; under an exhausting Mode each outcome contributes Multiplicity units to the pass.
   */
  weighted?: GeneratorWeightedNumeric[] | null;
  /**
   * SymmetryOrbit only: the ring, 0..7, whose thirty nodes are the units — exactly one of Ring and Node.
   */
  ring?: number | null;
  /**
   * SymmetryOrbit only: the node, 0..239, whose orbit under Word is the units.
   */
  node?: number | null;
  /**
   * SymmetryOrbit beside Node only: the word of reflections (one to eight mirror nodes, applied first to last) the orbit is taken under, or null for the lattice's own cycle — the same generator vocabulary a StateCycle authors.
   */
  word?: number[] | null;
}
/**
 * One named context of a StateGenerator — the state the walk may be sitting in and the weighted alternatives it may pick while there. A context declaring NO alternatives is TERMINAL: reaching it ends the emission.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GeneratorContext".
 */
export interface GeneratorContext {
  /**
   * The stable context key, unique within the generator.
   */
  key: CellName;
  /**
   * The weighted alternatives out of this context, or empty for a terminal context.
   */
  alternatives?: GeneratorAlternative[] | null;
}
/**
 * One weighted alternative of a GeneratorContext: the token it emits, its relative weight, and the context the walk moves into after it is picked. The authored Next is what makes this a real Markov process rather than a bag of independent draws — the context key is the process state, so an author folds exactly as much history into it as the chain needs.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GeneratorAlternative".
 */
export interface GeneratorAlternative {
  /**
   * The opaque game-authored token this alternative emits. The engine never interprets it; it is space-joined with the emission's other tokens and written into the target text cell. Bounded by MaxTokenLength.
   */
  token: string;
  /**
   * The alternative's positive relative weight. At least one alternative in a context must carry a non-zero weight.
   */
  weight: number;
  /**
   * The context the walk moves into after this alternative is picked. Must name a declared Key; naming a context that declares NO alternatives ends the emission (a terminal is a context with nothing to say, never a reserved token spelling).
   */
  next: CellName;
  /**
   * How many units of this alternative one pass holds, at least one; null is one. Under WithReplacement a multiplicity only scales the weight; under an exhausting mode each unit is drawn once per pass. A context's units total at most MaxEntriesPerSet.
   */
  multiplicity?: number | null;
}
/**
 * One numeric outcome of a WeightedNumeric source: the raw value it writes and its relative weight — the numeric twin of GeneratorAlternative, minus Token (nothing to join into text) and Next (a numeric draw is one terminal pick, never a walk).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GeneratorWeightedNumeric".
 */
export interface GeneratorWeightedNumeric {
  /**
   * The raw value this outcome writes on selection (a plain integer for Int, raw FixedQ4816 bits for Fixed).
   */
  value: number;
  /**
   * The outcome's relative weight, fed straight to Puck.Maths.WeightedSampler's exact ulong overload. At least one outcome must carry a non-zero weight.
   */
  weight: number;
  /**
   * How many units of this outcome one pass holds, at least one; null is one. Under WithReplacement a multiplicity only scales the weight; under an exhausting mode each unit is drawn once per pass, so an outcome that should come out twice per pass declares 2 rather than being authored twice. A source's units total at most MaxEntriesPerSet.
   */
  multiplicity?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ClosedBitset256".
 */
export interface ClosedBitset256 {
  [k: string]: unknown;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateDomainSlot".
 */
export interface StateDomainSlot {
  $type?: "slot";
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateDomainKeys".
 */
export interface StateDomainKeys {
  $type?: "keys";
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateDomainKeysOf".
 */
export interface StateDomainKeysOf {
  $type?: "keysOf";
  /**
   * The row whose keys this row's own keys are drawn from.
   */
  row: CellName;
  /**
   * Whether cell order carries gameplay meaning (a pile) rather than being incidental.
   */
  ordered?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateDomainCellsOf".
 */
export interface StateDomainCellsOf {
  $type?: "cellsOf";
  /**
   * The state.lattices topology this row lies over.
   */
  topology: string;
  /**
   * The value a sparse (board) cell reads before it is ever written; meaningless for the dense physical-field case, whose unwritten value is the field trait's own initial instead.
   */
  empty?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateDomainRing".
 */
export interface StateDomainRing {
  $type?: "ring";
  /**
   * How many pushes the ring keeps, 1..128.
   */
  capacity: number;
  /**
   * The value read for an age older than the ring holds.
   */
  empty?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "StateDynamics".
 */
export interface StateDynamics {
  /**
   * The referenced dynamics row name; must resolve.
   */
  row: string;
  /**
   * The follower's position at EpochTick as raw FixedQ4816 bits, independent of the carrying row's stored-value kind. Keeping the continuous state fixed-native preserves sub-unit phase when an integer target is rebased.
   */
  y0: string;
  /**
   * The follower's velocity at EpochTick, per second, as raw FixedQ4816 bits. A sub-unit response kick therefore survives an integer-row rebase.
   */
  v0: string;
  /**
   * The server tick Y0/V0 were captured at.
   */
  epochTick?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeFillRect".
 */
export interface WorldLatticeFillRect {
  $type?: "rect";
  /**
   * The value written.
   */
  value: number;
  /**
   * The rectangle's least X, world units.
   */
  minX: number;
  /**
   * The rectangle's least Z, world units.
   */
  minZ: number;
  /**
   * The rectangle's greatest X, world units.
   */
  maxX: number;
  /**
   * The rectangle's greatest Z, world units.
   */
  maxZ: number;
  /**
   * Gets the filled row's name -- compile-stamped, never authored inside a trait.
   */
  field?: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeFillNoise".
 */
export interface WorldLatticeFillNoise {
  $type?: "noise";
  /**
   * The peak value written inside a patch.
   */
  value: number;
  /**
   * Lattice cells per noise cell reciprocal -- noise-cell edge in CELLS (e.g. 8 = one noise cell spans 8 lattice cells). At least 1.
   */
  frequency: number;
  /**
   * The patch admission level in [0, 1); higher = sparser patches.
   */
  threshold?: number;
  /**
   * Octave count, 1..4.
   */
  octaves?: number;
  /**
   * The hash seed, folded with the world seed.
   */
  seed?: number;
  /**
   * Gets the filled row's name -- compile-stamped, never authored inside a trait.
   */
  field?: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeFillScatter".
 */
export interface WorldLatticeFillScatter {
  $type?: "scatter";
  /**
   * The value written inside each disc.
   */
  value: number;
  /**
   * The scatter block edge in CELLS (at least 2).
   */
  spacing: number;
  /**
   * The disc radius in CELLS (at least 1; must leave the disc inside its block's jitter envelope, refused otherwise).
   */
  radius?: number;
  /**
   * The hash seed, folded with the world seed.
   */
  seed?: number;
  /**
   * Gets the filled row's name -- compile-stamped, never authored inside a trait.
   */
  field?: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeFillDraw".
 */
export interface WorldLatticeFillDraw {
  $type?: "draw";
  /**
   * A declared generators row, or null when Generator is inlined.
   */
  source?: CellName | null;
  /**
   * An inline numeric source, or null when Source is named.
   */
  generator?: StateGenerator | null;
  /**
   * Gets the filled row's name -- compile-stamped, never authored inside a trait.
   */
  field?: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldLatticeMedium".
 */
export interface WorldLatticeMedium {}
/**
 * Declares one named body-state slot shared by every kit action in the world. The carrying WorldStateSection lane selects whether it belongs to the body or its identity.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionStateSlot".
 */
export interface ActionStateSlot {
  /**
   * The stable slot name predicates and effects reference.
   */
  name: string;
  /**
   * Whether the slot stores a counter or a remaining timer.
   */
  kind: ActionStateKind;
  /**
   * The initial counter value or timer duration in seconds.
   */
  initial?: number;
  /**
   * An optional body fact that resets the slot to Initial while it holds.
   */
  resetFact?: ActionFactNullable | null;
  /**
   * Whether the identity driving the body may submit a value for the slot.
   */
  playerWritable?: boolean;
  /**
   * The visited world's admitted effective values. Required for a player-writable slot.
   */
  envelope?: ActionStateEnvelope | null;
}
/**
 * An inclusive numeric interval.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionStateEnvelopeRange".
 */
export interface ActionStateEnvelopeRange {
  $type?: "range";
  /**
   * The least admitted value.
   */
  minimum: number;
  /**
   * The greatest admitted value.
   */
  maximum: number;
}
/**
 * A closed numeric set. Values are authored labels encoded in the slot's deterministic numeric domain.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "ActionStateEnvelopeSet".
 */
export interface ActionStateEnvelopeSet {
  $type?: "set";
  /**
   * The admitted values.
   */
  values: number[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopologyGrid".
 */
export interface LatticeTopologyGrid {
  $type?: "grid";
  /**
   * Cells along +X.
   */
  width: number;
  /**
   * Cells along +Z.
   */
  depth: number;
  /**
   * Wrapped axes.
   */
  wrap?: TopologyWrap;
  /**
   * The vertical half-extent about the origin's Y a position must lie within to resolve to a cell (cellOf); 0 resolves any height, so a piece on the floor beneath a table still reads as on its square.
   */
  band?: number;
  /**
   * See Directions.
   */
  directions?: TopologyDirection[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: TopologyElementAlias[] | null;
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * One authored direction of a discrete LatticeTopology: the (X, Y, Z) cell step a neighbour walk, ray, or leaper offset takes, and the case-sensitive token a rule or $board:/$match: channel names it by. X/Y are the topology's own planar axes (a Grid's column/row, a Hex's q/r); Z is a Box's layer step and must be zero on every other kind.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TopologyDirection".
 */
export interface TopologyDirection {
  /**
   * The direction's token — dot-free, distinct within the topology, matched case-sensitively.
   */
  name: string;
  /**
   * The signed planar column step.
   */
  x: number;
  /**
   * The signed planar row step.
   */
  y: number;
  /**
   * The signed layer step; zero except on a Box.
   */
  z?: number;
}
/**
 * A friendlier name for one point-group element, resolved by Element alongside its canonical signed-axis spelling (ElementName always answers the canonical form).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TopologyElementAlias".
 */
export interface TopologyElementAlias {
  /**
   * The alias token a rule or console verb may use instead of Element.
   */
  name: string;
  /**
   * The canonical element name (a ElementName value) this alias resolves to.
   */
  element: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopologyRing".
 */
export interface LatticeTopologyRing {
  $type?: "ring";
  /**
   * The cycle length.
   */
  width: number;
  /**
   * See Directions.
   */
  directions?: TopologyDirection[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: TopologyElementAlias[] | null;
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopologyHex".
 */
export interface LatticeTopologyHex {
  $type?: "hex";
  /**
   * The axial hexagon radius.
   */
  radius: number;
  /**
   * See Directions.
   */
  directions?: TopologyDirection[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: TopologyElementAlias[] | null;
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopologyBox".
 */
export interface LatticeTopologyBox {
  $type?: "box";
  /**
   * Cells along +X.
   */
  width: number;
  /**
   * Cells along +Z.
   */
  depth: number;
  /**
   * Cells along +Y.
   */
  layers: number;
  /**
   * The world-space height of one layer, so a position's Y above the origin resolves to a layer the way X and Z resolve to a column; positive.
   */
  layerHeight: number;
  /**
   * See Directions.
   */
  directions?: TopologyDirection[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: TopologyElementAlias[] | null;
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopologyGraph".
 */
export interface LatticeTopologyGraph {
  $type?: "graph";
  /**
   * The cells, in ordinal order.
   *
   * Items: One cell of a Graph.
   */
  cells: (GraphCell | null)[];
  /**
   * The direction slots, each naming its opposite (possibly itself).
   *
   * Items: One direction slot of a Graph.
   */
  directions: (GraphDirection | null)[];
  /**
   * The edges.
   *
   * Items: One edge of a Graph: From reaches To along Direction, and unless OneWay, To reaches From along the direction's opposite.
   */
  edges: (GraphEdge | null)[];
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GraphCell".
 */
export interface GraphCell {
  /**
   * The id edges name it by; distinct within the graph.
   */
  id: string;
  /**
   * The centre, relative to the topology's origin, in world units.
   */
  centre: DocumentVector3;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GraphDirection".
 */
export interface GraphDirection {
  /**
   * The direction's name, as rules and patterns spell it.
   */
  name: string;
  /**
   * The direction an edge is followed back along; a symmetric link names itself.
   */
  opposite: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GraphEdge".
 */
export interface GraphEdge {
  /**
   * The source cell id.
   */
  from: string;
  /**
   * The destination cell id.
   */
  to: string;
  /**
   * The direction slot.
   */
  direction: string;
  /**
   * Whether the reverse edge is left unfilled (a ladder, a one-way street).
   */
  oneWay?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "LatticeTopologyTiling".
 */
export interface LatticeTopologyTiling {
  $type?: "tiling";
  /**
   * The tiling.
   */
  family: TilingFamily;
  /**
   * The patch radius in edge lengths, at least 1.
   */
  radius: number;
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFieldTopology".
 */
export interface WorldFieldTopology {
  $type?: "field";
  /**
   * Cells along +X.
   */
  width: number;
  /**
   * Cells along +Z.
   */
  depth: number;
  /**
   * Cells along +Y -- 1 is a ground lattice.
   */
  layers?: number;
  /**
   * Simulation ticks between reaction steps.
   */
  stepEveryTicks?: number;
  /**
   * The per-step reactions, applied in document order.
   */
  reactions?: (WorldReaction | null)[] | null;
  /**
   * The topology's name — what a row's cellsOf domain references.
   */
  name: string;
  /**
   * The minimum corner, world units.
   */
  origin: DocumentVector3;
  /**
   * The cubic cell edge, world units.
   */
  cellSize: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReactionDiffuse".
 */
export interface WorldReactionDiffuse {
  $type?: "diffuse";
  /**
   * The field diffused.
   */
  field: string;
  /**
   * The fraction per step, in [0, 1].
   */
  rate: WorldLatticeScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReactionDecay".
 */
export interface WorldReactionDecay {
  $type?: "decay";
  /**
   * The field decayed.
   */
  field: string;
  /**
   * The fraction per step, in [0, 1].
   */
  rate: WorldLatticeScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReactionTransform".
 */
export interface WorldReactionTransform {
  $type?: "transform";
  /**
   * The conditions, all of which must hold.
   *
   * Items: One per-cell condition of a Transform.
   */
  when: (WorldFieldCondition | null)[];
  /**
   * The writes, applied in order.
   *
   * Items: One per-cell write of a Transform.
   */
  then: (WorldFieldWrite | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFieldCondition".
 */
export interface WorldFieldCondition {
  /**
   * The field read at the cell.
   */
  field: string;
  /**
   * The comparison.
   */
  comparison: ActionStateComparison;
  /**
   * The scalar compared against (literal or state-row reference).
   */
  value: WorldLatticeScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldFieldWrite".
 */
export interface WorldFieldWrite {
  /**
   * The field written at the cell.
   */
  field: string;
  /**
   * Set or add.
   */
  op: WorldFieldWriteOp;
  /**
   * The constant written or added; the result clamps to the field's range.
   */
  value: WorldLatticeScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReactionEmit".
 */
export interface WorldReactionEmit {
  $type?: "emit";
  /**
   * The keyed int state row whose nonzero cells name the emitting bodies.
   */
  tag: string;
  /**
   * The field deposited into.
   */
  field: string;
  /**
   * The amount per step; the cell clamps to the field's range.
   */
  amount: WorldLatticeScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReactionExpose".
 */
export interface WorldReactionExpose {
  $type?: "expose";
  /**
   * The field sampled at the body.
   */
  field: string;
  /**
   * The comparison.
   */
  comparison: ActionStateComparison;
  /**
   * The constant compared against.
   */
  value: WorldLatticeScalar;
  /**
   * The keyed int state row written, keyed by body index.
   */
  row: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReactionFlow".
 */
export interface WorldReactionFlow {
  $type?: "flow";
  /**
   * The field transported.
   */
  field: string;
  /**
   * The fraction of a cell's per-direction share that actually moves each step, in [0, 1].
   */
  rate: WorldLatticeScalar;
  /**
   * The other lattice rows forming the terrain basis a downhill direction is measured against; empty or omitted means the field flows over its own height alone.
   */
  over?: (string | null)[] | null;
  /**
   * The scalar fixed-kind state row an edge cell's outward share accumulates into each step (a clamped add), or null to treat every lattice edge as a wall.
   */
  spillRow?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldInputHoldAuthoring".
 */
export interface WorldInputHoldAuthoring {
  ceilingSeconds: number;
  lowerAfterSeconds: number;
  defaultSeconds: number;
  equalizeByDefault: boolean;
  /**
   * Items: One participant-specific input-hold override, authored shape — see WorldInputHoldAuthoring. Mirrors WorldInputHoldParticipant field for field except Seconds in place of the compiled Ticks.
   */
  participants: (WorldInputHoldParticipantAuthoring | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldInputHoldParticipantAuthoring".
 */
export interface WorldInputHoldParticipantAuthoring {
  bodyIndex: number;
  seconds: number;
  equalized: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeSection".
 */
export interface WorldThemeSection {
  /**
   * The semantic color roles.
   */
  color: WorldThemeColor;
  /**
   * The spacing grid and component heights.
   */
  space: WorldThemeSpace;
  /**
   * The radius scale.
   */
  radius: WorldThemeRadius;
  /**
   * The type scale.
   */
  type: WorldThemeType;
  /**
   * The two-tier elevation recipe.
   */
  elevation: WorldThemeElevation;
  /**
   * The diegetic material recipe.
   */
  diegetic: WorldThemeDiegetic;
  /**
   * The motion recipe.
   */
  motion: WorldThemeMotion;
  /**
   * The procedural icon feel.
   */
  icon: WorldThemeIcon;
  /**
   * The CPU writers' own opacity/px chrome.
   */
  chrome: WorldThemeChrome;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeColor".
 */
export interface WorldThemeColor {
  surfaceBase: BindableColor;
  surfacePanel: BindableColor;
  surfaceRaised: BindableColor;
  surfaceInset: BindableColor;
  scrimPanel: WorldThemeScrim;
  scrimStrip: WorldThemeScrim;
  scrimChip: WorldThemeScrim;
  lineHair: BindableColor;
  lineSoft: BindableColor;
  lineStrong: BindableColor;
  lineInset: BindableColor;
  textPrimary: BindableColor;
  textDim: BindableColor;
  textMute: BindableColor;
  accent: BindableColor;
  accentQuiet: BindableColor;
  accentLine: BindableColor;
  accentInk: BindableColor;
  positive: BindableColor;
  warning: BindableColor;
  danger: BindableColor;
  phosphor: BindableColor;
  phosphorDim: BindableColor;
  phosphorCyan: BindableColor;
  badgeDark: BindableColor;
  badgeLight: BindableColor;
}
/**
 * One scrim's fill color plus its own alpha, split apart so a world can retheme opacity independent of hue — the two knobs a scrim (a translucent panel/strip/chip backing) actually varies. Alpha is clamped to ScrimMinAlpha at resolve time when it is a state binding (see WorldDefinitionValidator's theme validation for the literal-authoring floor).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeScrim".
 */
export interface WorldThemeScrim {
  /**
   * The scrim's opaque fill color.
   */
  color: BindableColor;
  /**
   * The scrim's opacity, in [0, 1].
   */
  alpha: BindableScalar;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeSpace".
 */
export interface WorldThemeSpace {
  space0: number;
  space1: number;
  space2: number;
  space3: number;
  space4: number;
  space5: number;
  space6: number;
  space8: number;
  heightBadge: number;
  heightBindBar: number;
  heightChip: number;
  heightConsoleHead: number;
  heightModeRow: number;
  heightPromptRow: number;
  heightTrackerBar: number;
  heightTrackerCell: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeRadius".
 */
export interface WorldThemeRadius {
  radius1: number;
  radius2: number;
  radius3: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeType".
 */
export interface WorldThemeType {
  bodySize: number;
  bodyLine: number;
  bodyWeight: number;
  labelSize: number;
  labelLine: number;
  labelTracking: number;
  labelWeight: number;
  microSize: number;
  microLine: number;
  microTracking: number;
  microWeight: number;
  monoSize: number;
  monoLine: number;
  monoTracking: number;
  monoWeight: number;
  monoBadgeSize: number;
  monoReadoutSize: number;
  titleSize: number;
  titleLine: number;
  titleTracking: number;
  titleWeight: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeElevation".
 */
export interface WorldThemeElevation {
  bloomHaloBlur: number;
  bloomHaloSpread: number;
  bloomHaloAlpha: BindableScalar;
  bloomRingWidth: number;
  bloomRingAlpha: BindableScalar;
  bloomNeutralHaloAlpha: BindableScalar;
  bloomNeutralRingAlpha: BindableScalar;
  bloomHeldInsetBlur: number;
  bloomHeldInsetSpread: number;
  bloomHeldInsetAlpha: BindableScalar;
  bloomAccent: WorldThemeBloomHue;
  bloomPositive: WorldThemeBloomHue;
  bloomWarning: WorldThemeBloomHue;
  bloomDanger: WorldThemeBloomHue;
  bloomNeutral: WorldThemeBloomHue;
  pressHeldGlowBlur: number;
  pressHeldGlowSpread: number;
  pressHeldGlowColor: BindableColor;
  pressHeldShadowBlur: number;
  pressHeldShadowOffsetY: number;
  pressHeldTranslateY: number;
  pressHeldShadowColor: BindableColor;
  shadowSeatBlur: number;
  shadowSeatOffsetY: number;
  shadowSeatSpread: number;
  shadowSeatStripSpread: number;
  shadowSeatColor: BindableColor;
  shadowSeatStripColor: BindableColor;
  catchlightOffsetY: number;
  catchlightColor: BindableColor;
  chipRestOpacity: number;
  edgeHairlineWidth: number;
  ringStatusWidth: number;
  ringStatusAlpha: number;
}
/**
 * One elevation bloom hue's lit ring + outer halo pair (see Puck.Overlays.DesignTokens.Elevation for the composite rule). Each color carries its own baked alpha (#RRGGBBAA).
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeBloomHue".
 */
export interface WorldThemeBloomHue {
  /**
   * The 1px lit ring color.
   */
  ring: BindableColor;
  /**
   * The outer distance-falloff halo color.
   */
  halo: BindableColor;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeDiegetic".
 */
export interface WorldThemeDiegetic {
  plateTop: BindableColor;
  plateMid: BindableColor;
  plateBottom: BindableColor;
  plateStripeColor: BindableColor;
  embossFill: BindableColor;
  engraveFill: BindableColor;
  embossShadowDropAlpha: number;
  embossShadowDropBlur: number;
  embossShadowDropOffsetY: number;
  embossShadowLitAlpha: number;
  embossShadowLitOffsetY: number;
  engraveShadowLipAlpha: number;
  engraveShadowLipOffsetY: number;
  engraveShadowRecessAlpha: number;
  engraveShadowRecessBlur: number;
  engraveShadowRecessOffsetY: number;
  screenWellOuter: BindableColor;
  screenWellInner: BindableColor;
  bezelOuter: BindableColor;
  bezelInner: BindableColor;
  bezelEdge: BindableColor;
  phosphorGlowBlur: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeMotion".
 */
export interface WorldThemeMotion {
  caretBlink: number;
  durFast: number;
  durMed: number;
  durPanel: number;
  easeStd: WorldThemeCubicBezier;
  easeOut: WorldThemeCubicBezier;
}
/**
 * One of the two settled cubic-bezier easings a theme's motion section authors.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeCubicBezier".
 */
export interface WorldThemeCubicBezier {
  /**
   * The first control point's x.
   */
  x1?: number;
  /**
   * The first control point's y.
   */
  y1?: number;
  /**
   * The second control point's x.
   */
  x2?: number;
  /**
   * The second control point's y.
   */
  y2?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeIcon".
 */
export interface WorldThemeIcon {
  /**
   * The hairline stroke half-width every procedural glyph/icon draws with, in glyph-local units.
   */
  strokeHalfWidth: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldThemeChrome".
 */
export interface WorldThemeChrome {
  /**
   * The one quiet-dim opacity every writer reaches for: an unbound bar slot, an unheld modifier indicator, a gauge track, a console selection band.
   */
  dimQuietAlpha: number;
  /**
   * The binding bar's page-name opacity.
   */
  barLabelAlpha: number;
  /**
   * The binding bar's chord-hint opacity.
   */
  barHintAlpha: number;
  /**
   * The drawn cursor's ring opacity.
   */
  cursorAlpha: number;
  /**
   * The cursor's center-dot half-extent, as a fraction of the ring radius.
   */
  cursorDotRatio: number;
  /**
   * The cursor center dot's half-extent ceiling, px.
   */
  cursorDotMaxHalf: number;
  /**
   * The hover label's clearance outside the ring, px.
   */
  cursorLabelGap: number;
  /**
   * A non-active wheel ring's stroke and label opacity.
   */
  wheelRingAlpha: number;
  /**
   * The active wheel ring's stroke opacity.
   */
  wheelActiveRingAlpha: number;
  /**
   * The second (heavier-shell) stroke's radial offset, px.
   */
  wheelActiveRingOffset: number;
  /**
   * The hub dot's, marker's, and active-ring labels' opacity.
   */
  wheelLabelAlpha: number;
  /**
   * The wheel hub dot's half-extent, px.
   */
  wheelHubDotHalf: number;
  /**
   * The hovered-sector marker dot's half-extent, px.
   */
  wheelMarkerHalf: number;
  /**
   * The marker's clearance outside the ring, as a multiple of the cell height.
   */
  wheelMarkerGapRatio: number;
  /**
   * The active-ring label's clearance below the hub dot, px.
   */
  wheelHubLabelGap: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMarkerRow".
 */
export interface WorldMarkerRow {
  /**
   * The row's stable id, unique within the section.
   */
  id: string;
  /**
   * Which rows (or literal point) this row projects a chip per.
   */
  source: WorldMarkerSource;
  /**
   * The icon name, resolved through icons.icons.
   */
  icon: string;
  /**
   * The row's look.
   */
  style: WorldMarkerStyle;
  /**
   * The radius-ring policy, or null for no ring.
   */
  ring?: WorldMarkerRing | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMarkerSourceSpeakers".
 */
export interface WorldMarkerSourceSpeakers {
  $type?: "speakers";
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMarkerSourcePoint".
 */
export interface WorldMarkerSourcePoint {
  $type?: "point";
  /**
   * The marker's world position.
   */
  position: DocumentVector3;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMarkerStyle".
 */
export interface WorldMarkerStyle {
  /**
   * The icon chip's opacity, in [0, 1].
   */
  chipAlpha: BindableScalar;
  /**
   * The icon chip's plate half-extent, px. Positive.
   */
  size: number;
  /**
   * The ring's stroke color. Required exactly when Ring is authored; omitted otherwise.
   */
  ringColor?: BindableColor | null;
  /**
   * The ring's opacity, in [0, 1]. Required exactly when Ring is authored; omitted otherwise.
   */
  ringAlpha?: BindableScalar | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMarkerRing".
 */
export interface WorldMarkerRing {
  /**
   * The source row field the ring radius reads. The only field Speakers admits is radius (Radius); a row that is not a bed draws no ring under this policy without refusing (v1's one closed field name — a future source kind names its own).
   */
  field: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldRule".
 */
export interface WorldRule {
  /**
   * The rule's stable name — unique within the section. A CellName, the same validated-identifier type a state row and a cell key ride: dot-free and free of the reserved character set, refused by name at the JSON converter. The reserved $ prefix is refused on top of that, by CompileAll — exactly as it is for a state row name, and for the same reason: $ marks what the engine mints, and nothing mints a rule.
   */
  name: CellName;
  /**
   * The effects applied in order when the rule fires.
   */
  effects: ActionEffect[];
  /**
   * The predicate that must hold, or null for always.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * Whether the rule fires every tick the gate holds (Level, the default) or once per crossing (Edge). A rule that writes a row almost always wants Edge: level-firing an addState writes one journal entry per tick.
   */
  mode?: ActionTriggerMode;
  /**
   * A keyed state row to iterate, $zones to iterate the rule's own Zones table (its non-empty indices, each bound to $each), or null for one evaluation per tick. With a row named, the gate and effects evaluate once per cell the row holds at the top of the tick, with $each bound to that cell's key — the quantifier that lets one rule tick a status for every participant carrying it, or one rule judge every piece or card of a keyed row. An integer key also binds the each participant reference; a non-integer key binds $each alone. The latch is kept per key, by the key's value when it is an integer and by its position in the row otherwise.
   */
  forEach?: string | null;
  /**
   * The optional choice policy; common effects run only when entering a selected option.
   */
  decision?: WorldDecision | null;
  /**
   * The values bound once per evaluation, in declared order, read as $bind:<name>.
   */
  bindings?: (RuleBinding | null)[] | null;
  /**
   * The rule's zone table, or null: ordered zones over one token domain, in index order, an empty entry holding an index no zone answers. Every row position in the rule — a compareState's state, a $reduce:/$match: row, a $zone: endpoint's zone, a transfer's from/to, an expression's row — may spell $zones[<index>] to select an entry live, the index being an infix cell key (game[from], $each, $bind:<name>, or any expression), so one rule serves every pile of a game. An evaluation applies only when every live index selects an entry — an index outside the table, or at an empty entry, reads the gate closed, so a table's gaps are the rule's own statement of which piles it is for. forEach: "$zones" iterates the table's own non-empty indices with $each bound to each.
   */
  zones?: (string | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDecision".
 */
export interface WorldDecision {
  /**
   * One to 32 uniquely named options, in deterministic tie-break order.
   *
   * Items: One candidate in a decision; its score is read only when its gate holds.
   */
  options: (WorldDecisionOption | null)[];
  /**
   * Positive exact engine-tick duration between reconsiderations. First evaluation is immediate.
   */
  periodSeconds: number;
  /**
   * Highest score or weighted choice.
   */
  mode?: WorldDecisionMode;
  /**
   * The common numeric kind, Int or Fixed; operands must match it.
   */
  scoreKind?: CellKind;
  /**
   * Non-negative exact duration protecting a newly selected option from ordinary reconsideration.
   */
  commitmentSeconds?: number;
  /**
   * Non-negative score bonus for retaining the current eligible option and individual. In weighted mode it cannot revive a non-positive weight.
   */
  incumbentBonus?: number;
  /**
   * Authored seed combined with the world seed, rule name, binding key, and body generation.
   */
  seed?: number;
  /**
   * An optional rising-edge predicate that bypasses period and commitment. Losing eligibility also bypasses both.
   */
  interrupt?: ActionPredicateNullable2 | null;
  /**
   * Effects fired when an enabled decision first finds no choice, loses its choice, or its enclosing gate closes while a choice is held.
   */
  onNoChoice?: ActionEffect[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDecisionOption".
 */
export interface WorldDecisionOption {
  /**
   * The stable, non-empty option name.
   */
  name: CellName;
  /**
   * A numeric expression in the decision's score kind. Arithmetic failure makes this option ineligible for that reconsideration.
   */
  score: ValueExpression;
  /**
   * Effects fired when entering this option; may be empty for an inspection-only choice.
   */
  effects: ActionEffect[];
  /**
   * Eligibility predicate, or null for always eligible.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * Optional bounded nearby-body expansion. Requires rule forEach; each and left identify the observer, right the candidate, only inside this option's gate, score, and effects. A different individual is a selection transition.
   */
  neighbors?: WorldDecisionNeighbors | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDecisionNeighbors".
 */
export interface WorldDecisionNeighbors {
  /**
   * Inclusive spherical radius, in world units, from one Q48.16 unit through 1,000,000.
   */
  range: number;
  /**
   * Maximum inspected points per reconsideration, including self, rejected points, and an incumbent recheck.
   */
  candidateBudget: number;
  /**
   * Maximum candidates scored, from 1 through 32 and no greater than CandidateBudget.
   */
  maxCandidates: number;
  /**
   * Forward cone half-angle in (0,180]; coincident points are perceptible.
   */
  halfAngleDegrees?: number;
  /**
   * Whether candidates must pass the world's solid-field sight query.
   */
  requiresLineOfSight?: boolean;
  /**
   * Spend one inspection on the current individual first. If still perceptible and eligible, reserve one retained slot for it. A budget of one can spend all attention on the incumbent.
   */
  retainCurrent?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "RuleBinding".
 */
export interface RuleBinding {
  /**
   * The binding's name — the token after BindPrefix.
   */
  name: CellName;
  /**
   * The value's cell kind, Int or Fixed.
   */
  kind: CellKind;
  /**
   * The postfix expression, evaluated in that kind.
   */
  expression: ValueExpression;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIdentityDefinition".
 */
export interface WorldIdentityDefinition {
  /**
   * The stable owned-world id.
   */
  id: SafeName;
  /**
   * The display name.
   */
  name: string;
  /**
   * The body color as #RRGGBB.
   */
  color: string;
  /**
   * The fixed state row supplying locomotion speed.
   */
  moveSpeedState: CellName;
  /**
   * The fixed state row supplying turn speed.
   */
  turnSpeedState: CellName;
  /**
   * Machine/device state-slot references used for controller pre-selection.
   */
  controllers?: (WorldControllerStateSlots | null)[] | null;
  /**
   * The identity's authored voice-babble selectors, or null for none (see WorldVoiceProfile).
   */
  voice?: WorldVoiceProfile | null;
  /**
   * The facts row this identity carries and its capacity, or null for Default.
   */
  facts?: WorldIdentityFacts | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldControllerStateSlots".
 */
export interface WorldControllerStateSlots {
  /**
   * The row containing the machine id.
   */
  machineState: CellName;
  /**
   * The row containing the device id.
   */
  deviceState: CellName;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldVoiceProfile".
 */
export interface WorldVoiceProfile {
  /**
   * The referenced Name (must resolve when declared).
   */
  patchId?: string | null;
  /**
   * The base inter-syllable tick spacing (must be positive when declared).
   */
  cadenceTicks?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldIdentityFacts".
 */
export interface WorldIdentityFacts {
  /**
   * The keyed Int row on the identity's document holding the facts.
   */
  state: CellName;
  /**
   * How many distinct facts the row holds; a write past it refuses by name.
   */
  capacity: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupsSection".
 */
export interface WorldGroupsSection {
  /**
   * The declared kind catalog.
   *
   * Items: A group kind — a policy bundle, never a size label. Every field here must be behavior-bearing: a kind that differs from another only in Capacity is a capacity value, not a kind, and WorldDefinitionValidator refuses that pair by name.
   */
  kinds: (WorldGroupKind | null)[];
  /**
   * The group roster — authored and runtime rows in one list (see WorldGroup).
   *
   * Items: One group row — a roster of principals under a kind. One shape whether the row was boot-authored (present in the server's own base document — re-seeded on every world.reset/.load/.reload) or formed live by WorldMutation.FormGroup (never written back to the base, so a whole-document rebuild simply does not carry it forward — the party-vs-roster split falls out of the ordinary document-swap machinery, not a bespoke flag on this type).
   */
  groups: (WorldGroup | null)[];
  /**
   * The ownership bindings (see WorldOwnership). May be empty.
   *
   * Items: One ownership binding — subject → principal-or-group-or-escrow, the second kind of the group+binding substrate. A row is document-authored (boot/reset/load-seeded, like every other row in this section), but its Owner moves live through WorldMutation.OfferOwnership (owner -> escrow) and WorldMutation.SettleOwnership (escrow -> recipient, or escrow -> offerer on timeout) — the escrow/transfer lane, riding this exact row shape. There is still no mutation kind that creates a row from nothing (only a document may declare a subject's first owner) or that widens Kind past Group — a later lane adding item/instance subjects is expected to add whatever mints their first row, riding this same shape.
   */
  ownership: (WorldOwnership | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupKind".
 */
export interface WorldGroupKind {
  /**
   * The kind's stable name, unique within Kinds — the vocabulary a KindName reference is validated against (unknown-by-name, the same shape as the state-row cell-existence refusal).
   */
  name: string;
  /**
   * The role→capability map (see WorldGroupRole). May be empty — a kind with no roles reaches no capability at all through its group principal, so any grant naming it is refused as unreachable.
   *
   * Items: One named role a WorldGroupKind declares — the role→capability half of the kind's policy bundle. A role names nothing about membership (this head keeps a membership row role-less; see Members); it exists so world.grant can refuse, at the door, a hold over a group principal that no role of its kind could ever exercise (the addon-reachability-honesty analog: an admitted-but- unreachable grant is a grant that lies).
   */
  roles: (WorldGroupRole | null)[];
  /**
   * The loot/ownership-distribution policy slot (see WorldGroupOwnershipPolicy).
   */
  ownershipPolicy: WorldGroupOwnershipPolicy;
  /**
   * The lifetime/persistence policy (see WorldGroupLifetime) — runtime groups only.
   */
  lifetime: WorldGroupLifetime;
  /**
   * What a kick does to the kicked member's row — and, under Disband, to the whole group (see WorldGroupEvictionPolicy).
   */
  evictionPolicy: WorldGroupEvictionPolicy;
  /**
   * The maximum concurrent members a group of this kind admits — one minor authored field within MaxMembersPerGroup, the population ceiling this substrate is bounded against.
   */
  capacity: number;
  /**
   * The name of a declared state row this kind's groups share, or null for none. Validated to reference an existing row (refused by name otherwise, the same cell-existence discipline the world-rules operand walk enforces) — the deeper "every member reads/writes this row together" semantic belongs to a later lane; this head only pins the reference honest.
   */
  sharedStateScope?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupRole".
 */
export interface WorldGroupRole {
  /**
   * The role's stable name, unique within its kind.
   */
  name: string;
  /**
   * The capabilities a member acting under this role may be granted through the group principal. Never empty — a role reaching nothing is a role that could not exist without lying about what it is for.
   */
  capabilities: WorldCapability[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroup".
 */
export interface WorldGroup {
  /**
   * The group's stable id, unique within Groups — the token Group carries as a grant principal. SafeName-typed (the For precedent: a scoped session's process-local instance name is composed from this id, literal separators and all, in WorldSessionResolver.MintInstanceName — typing it here is what makes that composition injective by construction rather than by an escaping step downstream that could collapse two distinct ids onto one name).
   */
  id: SafeName;
  /**
   * The owning kind's name — validated to reference a declared WorldGroupKind (unknown-by-name).
   */
  kindName: string;
  /**
   * The current membership — flat only: every entry is a principal, never a group (a Group entry is refused by name — a member holding another group's memberships by proxy is exactly what flat membership forbids), and never World/ Document (neither is a real actor a hold could ever reach). Bounded by the kind's own Capacity.
   */
  members: WorldPrincipal[];
  /**
   * The row's own tags — what a Tagged destination selector matches against: a traveler resolving a tagged destination is expected to hold exactly one membership among rows carrying the selector's tag, never zero or several. Validated non-empty and distinct when present; null/absent means this row carries none, which is not the same as an authored empty list (refused — omit the member instead).
   */
  tags?: (string | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldOwnership".
 */
export interface WorldOwnership {
  /**
   * The owned thing.
   */
  subject: OwnershipSubject;
  /**
   * Who owns it.
   */
  owner: OwnershipOwner;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OwnershipSubject".
 */
export interface OwnershipSubject {
  /**
   * The subject flavor.
   */
  kind?: OwnershipSubjectKind;
  /**
   * The subject's stable id — a group id for Group.
   */
  id?: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OwnershipOwner".
 */
export interface OwnershipOwner {
  /**
   * Whether the owner is a bare principal, a group, or an escrow row.
   */
  kind?: OwnershipOwnerKind;
  /**
   * The owning principal for Principal; null otherwise. Never Group — a group owner is spelled through GroupId, not this field, so the two branches never overlap.
   */
  principal?: WorldPrincipal | null;
  /**
   * The owning group's id for Group; null otherwise.
   */
  groupId?: string | null;
  /**
   * The escrow payload for Escrow; null otherwise.
   */
  escrow?: OwnershipEscrow | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "OwnershipEscrow".
 */
export interface OwnershipEscrow {
  /**
   * The principal that placed the subject into escrow — the sole reclaim beneficiary.
   */
  offerer?: WorldPrincipal;
  /**
   * The principal named to accept the subject — the sole accept beneficiary. Never equal to Offerer (refused by name — an offer to oneself is not a trade).
   */
  recipient?: WorldPrincipal;
  /**
   * The server tick at or after which WorldMutation.SettleOwnership's reclaim admits — the same tick unit EpochTick already rides. Before this tick, only an accept by Recipient can resolve the escrow.
   */
  deadlineTick?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPropertyRegistrySection".
 */
export interface WorldPropertyRegistrySection {
  /**
   * The declared property vocabulary — unique, non-empty, each naming a declared keyed int state row of the same name.
   */
  names: (string | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldInteractionsSection".
 */
export interface WorldInteractionsSection {
  /**
   * The declared interaction rows, in authoring/evaluation order.
   *
   * Items: One row of the world's interactions section — an authorable property x property (or property x region) -> effect table entry, the A x B -> F chemistry primitive. Evaluated over every carrier pair (or every occupant) each tick with the matched carriers bound as $left/$right, and compiled through the same effect compiler a WorldRule uses, so it rides the same edge/level latch (kept per pair), journal, and undo.
   */
  interactions: (WorldInteraction | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldInteraction".
 */
export interface WorldInteraction {
  /**
   * The interaction's stable name — unique within the section (a separate namespace from Name, so the two may coincide without colliding). A CellName, never $-prefixed — that prefix marks what the engine mints, and nothing mints an interaction.
   */
  name: CellName;
  /**
   * A property name, validated against the declared properties registry — every body whose cell in that keyed row reads nonzero is a left carrier.
   */
  left: string;
  /**
   * For Distance, a second property name, validated the same way as Left. For Region, a placement id carrying a region facet — validated against the declared placements section instead, never against the property registry.
   */
  right: string;
  /**
   * How the two operands' coming-together is detected.
   */
  coOccurrence: WorldInteractionCoOccurrence;
  /**
   * The distance threshold, for Distance alone — an exact JSON decimal lowered directly to deterministic fixed point, with no binary32 intermediate. Ignored (and unchecked) under Region.
   */
  range: number;
  /**
   * The effects applied in order when the interaction fires — see this type's remarks.
   */
  effects: ActionEffect[];
  /**
   * Whether the interaction fires every tick the co-occurrence holds For a distance interaction, at most this many right carriers per left carrier — the nearest first, ties broken by the lower body index — or null for every carrier in range. The work sheet prices the pair count at this budget. (Level) or once per crossing (Edge, the default — an interaction that transforms/spawns/despawns almost always wants Edge, for the same reason a rule that writes a row does: level-firing a spawn is a journal entry every tick the co-occurrence holds).
   */
  mode?: ActionTriggerMode;
  neighbours?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGenerationDefaults".
 */
export interface WorldGenerationDefaults {
  /**
   * Folded into every site's Pcg32XshRr starting state. Defaults to 0.
   */
  worldSeed?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "GeneratorRow".
 */
export interface GeneratorRow {
  /**
   * The source's name, unique within the section, and the spelling a site's Source resolves against.
   */
  name: CellName;
  /**
   * The source itself.
   */
  generator: StateGenerator;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldReference".
 */
export interface WorldReference {
  /**
   * The reference's own name — SafeName-shaped, unique within the section.
   */
  name: SafeName;
  /**
   * The referenced world's document path (e.g. "dive.world.json"), authored verbatim. Mutually exclusive with Owner/World.
   */
  document?: string | null;
  /**
   * The remote world's owning platform user id (an Entra oid) — worlds ARE users, so naming the owner names the world's account. Required together with World; refused alone.
   */
  owner?: string | null;
  /**
   * The remote world's own SafeName-shaped id within its owner's account. Required together with Owner; refused alone.
   */
  world?: SafeName | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPortalsSection".
 */
export interface WorldPortalsSection {
  /**
   * The travel default every portal facet in this document falls back to when it authors no Travel of its own.
   */
  portalDefaults: WorldPortalDefaults;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldPortalDefaults".
 */
export interface WorldPortalDefaults {
  /**
   * The default travel scope (see WorldPortalTravel).
   */
  travel: WorldPortalTravel;
  /**
   * How long a destination reservation is binding, converted from source simulation ticks through the exact 50400 engine-tick bridge.
   */
  holdSeconds?: number;
  /**
   * Whether the client may retry after an immediate full-border refusal.
   */
  full?: WorldTransferFullPolicy;
  /**
   * Whether a party transfer reserves and commits as one cohort. When false, each member is a separate atomic body transfer.
   */
  partyAllOrNothing?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSimulationDefaults".
 */
export interface WorldSimulationDefaults {
  /**
   * The simulation rate in Hz. Zero is a legal, distinct rate: a resident, non-stepping world — a static diorama the authoritative server never advances a fixed step for, though it still applies ordered submissions (mutations, session requests, connects/disconnects) through the administrative drain, so a rate-0 world can accept the very write that revives it. At rate 0, a simulation-tick duration authored as a positive value means never — not zero and not "already expired" — since there is no tick mapping for a world that never advances (see CompiledTickDuration, PopulationReconnectGraceTicks). A positive rate must be a divisor of TicksPerSecond (50400) exactly, so Puck.Hosting.EngineTicks.PerRate always derives a whole engine-tick step width — never truncated, never remainder-carried (WorldDefinitionValidator refuses a non-divisor, naming the nearest valid rates; a negative rate is refused outright, at any magnitude). 45 and 90 Hz — Steam Deck OLED's two refresh rates — both divide 50400 exactly (1120 and 560 engine ticks per step). The engine holds no rate of its own: an authored section states its rate, and a world authoring no simulation section runs at UnauthoredSimulationRateHz — the distinct rate-0 resident world is reached only by authoring rateHz 0 by name. The derived-floor seam. This record is deliberately the one place a follow-on validation pass adds the physics floor (from body size/speed), the interactivity floor (from input latency), the substep-derived contact clamp (contactHertz <= RateHz * n / 8 at substep count n — it coincides with RateHz / 4 only at n = 2), and the representable band — none of which is built yet. The clamp's n is a solver parameter, so its validator arrives with the solver landing that introduces it. A derived floor belongs here, beside the rate it constrains, never as a second section.
   */
  rateHz: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldDestination".
 */
export interface WorldDestination {
  /**
   * The destination's own name — SafeName-shaped, unique within the section. A Destination facet resolves against this name (see WorldDefinitionValidator).
   */
  name: SafeName;
  /**
   * The Name of the references row this destination selects. Must resolve to an existing row — an undeclared name refuses by name. Never repeats Document itself: several destinations may select one reference differently.
   */
  reference: string;
  /**
   * How this destination's target instance is minted (see WorldDestinationDurability).
   */
  durability: WorldDestinationDurability;
  /**
   * Which scoped identity/generation this destination selects (see WorldDestinationScope). Absent resolves to Global — today's behavior, unchanged for every row authored before this member existed. Trailing member: the same widen-without-moving-existing-members shape WorldLatticeMedium's own remarks describe.
   */
  scope?: WorldDestinationScope;
  /**
   * Which group a Group row resolves through (see WorldGroupSelector). Required exactly when Scope is Group — a selector on any other scope, or a group scope with none, refuses by name (see WorldDefinitionValidator).
   */
  selector?: WorldGroupSelector | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupSelectorNamed".
 */
export interface WorldGroupSelectorNamed {
  $type?: "named";
  /**
   * The Id this destination is bound to. Must resolve to a declared group row — an undeclared id refuses by name.
   */
  group: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldGroupSelectorTagged".
 */
export interface WorldGroupSelectorTagged {
  $type?: "tagged";
  /**
   * The tag every candidate membership is matched against. Must be non-empty.
   */
  tag: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAdmissionEntry".
 */
export interface WorldAdmissionEntry {
  /**
   * The trusted key's own id domain — a lowercase-hex SHA-256 fingerprint (64 characters). For Vouches this must be PublicKey's own fingerprint (a root is self-certifying). For SignsDirectly it names the domain namespace this individual is pinned under, which need not equal their own key's hash. For FederatedAuthority it is not a key id at all: it names the authenticated source-authority namespace, or AnyAuthority for any authority that completes the federation handshake.
   */
  domain: string;
  /**
   * The platform user id this entry pins, required for SignsDirectly and refused for Vouches (a root vouches for every subject its two-hop chain resolves, never one named here).
   */
  subject: string | null;
  /**
   * Whether this entry signs directly or vouches for a chain.
   */
  mode: WorldAdmissionTrustMode;
  /**
   * Exactly ecdsa-p256-sha256, the only signing algorithm enabled by the admission door's mandatory attestation-v1-base profile. Sealing algorithms and optional signing extensions are refused by document validation.
   */
  algorithm: string;
  /**
   * The pinned key's actual SubjectPublicKeyInfo bytes, base64-encoded — carried alongside the id because offline verification needs the real bytes, never a fetch (docs/vision.md, "Signed attestation": consulting the issuer at verification time is a ruled-out design).
   */
  publicKey: string;
  /**
   * What a peer verified under this entry is minted, INSTEAD OF the blanket Control/all every admitted peer used to receive unconditionally. Empty (never null) is a legitimate authored choice: a verified-but-granted-nothing identity, admitted onto the connection table and able to hold a socket open, but unable to submit anything the grant table would honor.
   *
   * Items: One capability a verified admission entry mints for the connecting peer once its identity checks out — the same fields WorldGrant carries for a Peer principal, minus Principal itself (unknowable until Server.WorldPopulation.TryAdmitRemotePeer assigns the connection's body index and generation) and minus the co-driving payloads (Reach/Consent/ Ceiling), which are seat-authored pool mechanics that presuppose a body already exists. Server.WorldServer.TryAdmitPeerConnection rebinds each template onto WorldPrincipal.Peer(index, generation) the moment the body is admitted, through the same Server.WorldServer.Grant door the document's own grants section goes through — an admission-minted grant is subject to the identical budget/exclusivity rules a live world.grant row is.
   */
  grants: WorldAdmissionGrant[];
  /**
   * How much of this world's document a peer verified under this entry receives (see WorldDisclosureTier). Absent resolves to Presentation, so an entry authored before this field existed keeps a projection-only wire and nothing has to be edited to stay closed. Replica is reachable only by authoring it.
   */
  disclosure?: WorldDisclosureTier | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAdmissionGrant".
 */
export interface WorldAdmissionGrant {
  /**
   * The capability minted.
   */
  capability?: WorldCapability;
  /**
   * The subject it scopes to; null means the body this admission assigns, resolved by SubjectFor once that index is known. An authored template cannot name a body index — the door runs before the population picks one — so a row that must follow the admitted body omits its subject.
   */
  subject?: GrantSubject | null;
  /**
   * Whether the mint is exclusive.
   */
  exclusive?: boolean;
  /**
   * The untrusted-principal per-tick dispatch budget — required by the live grant door on a Drive/Observe row, and on an untrusted Mutate/section:<name> row, exactly as for any other Peer/Addon grant; an admission entry that omits it on such a row mints a row the door refuses at admission time.
   */
  budget?: number | null;
  /**
   * The untrusted-principal per-tick event-push budget for an Observe row over an event-bearing subject.
   */
  eventBudget?: number | null;
  /**
   * The verb-scoped narrowing beneath a Mutate/section:<name> row — required by the live grant door on an untrusted principal's such a row (an absent mask there is refused rather than read as full reach).
   */
  kindMask?: MutationKindMask;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAdjacency".
 */
export interface WorldAdjacency {
  /**
   * This document's stable name for the edge.
   */
  name: SafeName;
  /**
   * A global persisted destination naming the neighbouring authority.
   */
  destination: string;
  /**
   * The reciprocal adjacency row in the destination document.
   */
  counterpart: string;
  /**
   * The invisible source-side ownership boundary.
   */
  boundary: WorldAdjacencyBoundary;
  /**
   * The authored failure treatment.
   */
  unavailable?: WorldAdjacencyUnavailable;
  /**
   * Optional declared channel pressed once on the body after the engine applies the failure treatment. Use a kit action on this channel for authored sound, animation, state, or other feedback; ownership safety never depends on the binding.
   */
  onUnavailable?: string | null;
  /**
   * The maximum number of live bodies and outstanding reservations admitted through this border at once, or null to use the destination population's remaining capacity — the same policy Capacity gives portal furniture. A full border refuses the current attempt immediately; it never queues.
   */
  capacity?: number | null;
  /**
   * How long this edge may go without a delivered neighbour refresh before the world calls the link dropped — the authored threshold behind the linkEstablished/linkDropped world event family and the $link:<name> reserved rule channel (LinkPrefix). Authored in seconds — a physical unit, so a world's SimulationRateHz can change without silently retuning the window — and compiled per document through AdjacencyLivenessGraceTicks, exactly the population.reconnectGraceSeconds idiom. 0 (the default) disables liveness sensing for this edge outright: no link edge is emitted for it and $link: reads 0 forever, so a world authoring none is unchanged. Per row, not per world: each seam carries its own tolerance. A world whose rate is 0 has no tick mapping for a positive value (see CompiledTickDuration), which reads as never dropped.
   */
  livenessGraceSeconds?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldAdjacencyBoundary".
 */
export interface WorldAdjacencyBoundary {
  /**
   * The boundary rectangle's center in this world's coordinates.
   */
  center: DocumentVector3;
  /**
   * The outward heading, in degrees (0 = +Z, 90 = +X).
   */
  outwardYawDegrees: number;
  /**
   * The outward elevation, in degrees (+90 = +Y, -90 = -Y).
   */
  outwardPitchDegrees: number;
  /**
   * The full span along the boundary's local right axis.
   */
  width: number;
  /**
   * The full span along the boundary's local up axis.
   */
  height: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TextFontCatalogDefinition".
 */
export interface TextFontCatalogDefinition {
  defaultFont: string;
  fonts: (TextFontDefinition | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TextFontDefinition".
 */
export interface TextFontDefinition {
  name: string;
  source: string;
  hash: string;
  codePointRanges: (string | null)[];
  characters?: string | null;
  pixelSize?: number | null;
  distanceRange?: number | null;
  faceIndex?: number | null;
  padding?: number | null;
  columns?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMetadataSection".
 */
export interface WorldMetadataSection {
  /**
   * The world's author-facing display name, distinct from its filename.
   */
  title?: string | null;
  /**
   * A free-form author description.
   */
  description?: string | null;
  /**
   * The credited authors.
   */
  authors?: (WorldMetadataAuthor | null)[] | null;
  /**
   * Free author vocabulary (genre, theme, whatever an author wants) — no built-in enum, the same posture Names already carries for a property name.
   */
  tags?: (string | null)[] | null;
  /**
   * A free-form author bag — see the type remarks for its two compose-time carve-outs.
   */
  custom?: {
    [k: string]: {
      [k: string]: unknown;
    };
  } | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMetadataAuthor".
 */
export interface WorldMetadataAuthor {
  /**
   * The author's display name.
   */
  name: string;
  /**
   * The author's Entra object id, when the author chooses to attach one — see WorldEntraObjectId. Authored, not authenticated: nothing here proves the name behind the id.
   */
  oid?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldUpdateDefaults".
 */
export interface WorldUpdateDefaults {
  /**
   * The release channel this install tracks (e.g. stable, beta). Null = the app's own default channel.
   */
  channel?: string | null;
  /**
   * The on-disk root staged versions and update state live under. Null = the app's own default.
   */
  cacheRoot?: string | null;
  /**
   * Seconds between automatic update.check attempts. Null = the app's own default; 0 disables automatic checking (a manual update.check still works).
   */
  checkIntervalSeconds?: number | null;
  /**
   * The number of most-recent staged versions to retain beyond the current one. Null = the app's own default.
   */
  keepVersions?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldMusicRow".
 */
export interface WorldMusicRow {
  /**
   * The row's stable name — its mutation address.
   */
  name: string;
  /**
   * The referenced document's file path.
   */
  source: string;
  /**
   * The SHA-256 hex64 of the referenced document's canonical bytes.
   */
  hash: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatModeFamily".
 */
export interface WorldSeatModeFamily {
  /**
   * The family's stable name. Must not collide with a built-in family name (roster, engagement, layout) or the reserved state: prefix.
   */
  name: string;
  /**
   * The state a seat publishes before any player.mode flip — one of States.
   */
  defaultState: string;
  /**
   * Gets the family's admitted states. The absence-coalesce lives in the accessor for the same reason Elements's does.
   *
   * Items: One state of a WorldSeatModeFamily.
   */
  states: (WorldSeatModeState | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSeatModeState".
 */
export interface WorldSeatModeState {
  /**
   * The state's stable name — the token player.mode <family> <state> takes and the value Client.WorldContextFamilies context rows key on.
   */
  name: string;
  /**
   * The control application this state drives, or null for an ordinary state (the seat drives its own body normally). "camera" is the only admitted value: entering the state possesses the seat's declared camera body (see CameraPlacementIdPrefix) through the ordinary Engage door (PlayerCommandModule.Mode.cs), diverting the seat's own body intent to Idle and resolving its view through views.cameraRig instead.
   */
  target?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbe".
 */
export interface WorldProbe {
  /**
   * The probe's own name — SafeName-shaped, unique among the rows; probe.status and probe.record address it.
   */
  id: string;
  /**
   * The registered probe kind id (a puck.probe.v1 manifest's file stem) — checked against the shipped vocabulary at document load (IsRegisteredProbeKind), never interpreted here. The document never states where the kind runs; the kind's own registration decides kernel-on-device versus out-of-process model.
   */
  kind: string;
  /**
   * The probe's cadence ceiling, 1..240 Hz — a rate its host may run slower than (latest-wins throughout the pipeline), never faster.
   */
  rateHz: number;
  /**
   * The kind's sockets, by name, each bound to the frame source that fills it — the live-hardware leg. The socket vocabulary itself lives behind the kind's manifest, unchecked here; the host checks a bound name against it by name at boot, the same shallow-then-deep split every kind-vocabulary field follows. Mutually exclusive with Track. Omitted from the wire when null.
   */
  inputs?: {
    [k: string]: WorldFrameSource;
  } | null;
  /**
   * A recorded puck.probe-track.v1 document path, resolved against the world document's own directory, played back in place of every socket at once — probe.record's own output shape, the hardware-free leg every probe admits. Mutually exclusive with Inputs. Omitted from the wire when null.
   */
  track?: string | null;
  /**
   * The kind's config values, or null when the kind declares none or every field has a default. Not validated at document load — the kind's own manifest config schema validates it at boot, matching Config's shallow-then-deep precedent.
   */
  config?: {
    [k: string]: unknown;
  };
  /**
   * What this probe's channels drive, or null for a probe that is only read back.
   */
  bindings?: (WorldProbeBinding | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeBindingAxis".
 */
export interface WorldProbeBindingAxis {
  $type?: "axis";
  /**
   * The probe channel name — checked against the kind's manifest at boot, never here.
   */
  channel: string;
  /**
   * The axis' own name — kebab-case, 1..64 characters, unique among every probe's axis bindings. Publishes the input source id probe.<Source> (Puck.Input.InputSources.Probe.Axis), an ordinary bindable Axis1D source any binding overlay may map like a stick.
   */
  source: string;
  /**
   * The deadband about the channel's neutral, in [0, 1) of the mapped [-1, 1] axis range.
   */
  deadband?: number;
  /**
   * The hysteresis band width, in [0, 1), applied at the deadband edge.
   */
  hysteresis?: number;
  /**
   * The fixed-point EMA smoothing factor, in [0, 1] — 0 disables smoothing.
   */
  smoothing?: number;
  /**
   * The output quantization width, 1..16 bits.
   */
  quantizeBits?: number;
  /**
   * How long a reading stays live before the axis returns to neutral and confidence to zero. Must be finite and positive.
   */
  maxAgeSeconds?: number;
  /**
   * The 1-based local seat this axis is captured for. Forbidden (refused at load) on a seat-relative probe — a row instanced once per occupied seat because at least one of its camera sockets carries no seat of its own — whose axis bindings always take their own instance's seat; required (defaulting to seat 1) on a single-instance probe, exactly as before. null is the wire default. Omitted from the wire when null.
   */
  seat?: number | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeBindingParameter".
 */
export interface WorldProbeBindingParameter {
  $type?: "parameter";
  /**
   * The probe channel name — checked against the kind's manifest at boot, never here.
   */
  channel: string;
  /**
   * The presentation destination.
   */
  target: WorldProbeParameterTarget;
  /**
   * The [min, max] presentation range a channel's mapped [-1, 1] axis value lerps across. Must be finite with X < Y.
   */
  range: DocumentVector2;
  /**
   * How long a reading stays live before this binding stops writing. Must be finite and positive.
   */
  maxAgeSeconds?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeParameterTargetExtension".
 */
export interface WorldProbeParameterTargetExtension {
  $type?: "extension";
  /**
   * The render.extensions[].id entry this targets — must name an entry the document itself composes.
   */
  id: string;
  /**
   * The extension's config field name — checked against its manifest at boot, never here (the same shallow-then-deep precedent every kind-vocabulary field follows).
   */
  field: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeParameterTargetProbe".
 */
export interface WorldProbeParameterTargetProbe {
  $type?: "probe";
  /**
   * The probes[].id this targets — a row of this document other than the binding's own.
   */
  id: string;
  /**
   * That probe's kind config field name — checked against its manifest at boot, never here.
   */
  field: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldProbeBindingControl".
 */
export interface WorldProbeBindingControl {
  $type?: "control";
  /**
   * The probe channel name — checked against the kind's manifest at boot, never here.
   */
  channel: string;
  /**
   * The WorldCameraControls member this binding writes — one of pan, tilt, zoom, exposure, focus, brightness, contrast, saturation, sharpness, gain, whiteBalance, backlightCompensation, or fieldOfView.
   */
  control: string;
  /**
   * The control value a channel's mapped -1 resolves to.
   */
  minimum: number;
  /**
   * The control value a channel's mapped 1 resolves to. Must exceed Minimum.
   */
  maximum: number;
  /**
   * How long a reading stays live before this binding stops writing. Must be finite and positive.
   */
  maxAgeSeconds?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCapturesSection".
 */
export interface WorldCapturesSection {
  /**
   * The output directory every scheduled capture in this document writes into, and where manifest.json (the puck.parity.manifest.v1 document) lands once at least one capture has landed — relative to the process's current directory unless rooted. A --capture-dir boot flag overrides this for a deployment run (the --state-dir pattern), so two backend legs of the same document can target sibling directories without two document copies.
   */
  directory: string;
  /**
   * The scheduled stations. Station names are unique; capacity MaxRows.
   *
   * Items: One tick-scheduled capture station: a stable name, the exact engine ticks it arms a composed-frame capture at, and the palette its per-pixel census sorts against. A station carries no camera reference of its own — what the composed frame shows at a given tick is the document's own doing (a state row plus rules driving a camera program's select op; see docs/views.md), so two backends that step the identical document capture the identical moment by construction, and this row only says WHEN to look, never AT WHAT.
   */
  rows: (WorldCaptureRow | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCaptureRow".
 */
export interface WorldCaptureRow {
  /**
   * The stable name — the manifest's station field and the frame filename's own prefix (<station>-<tick>.png). A CellName: dot-free, non-empty, free of the reserved character set.
   */
  station: CellName;
  /**
   * The exact simulation ticks (completed-tick coordinates, ascending, none repeated) this station arms a capture at. Capacity: MaxTicksPerRow.
   */
  ticks: number[];
  /**
   * The per-pixel census's material table — at least one entry, at most MaxPaletteEntriesPerRow, unique Material indices.
   *
   * Items: One material bucket a capture's per-pixel census sorts against: the nearest-color match in a station's authored palette, since the composed render target carries pixel colors, not a per-pixel material-id buffer, and this is the mechanically honest fallback the render path actually supports (see Puck.World.WorldCaptureScheduler's own remarks for the exact matching rule).
   */
  palette: (WorldCapturePaletteEntry | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCapturePaletteEntry".
 */
export interface WorldCapturePaletteEntry {
  /**
   * The material's index — the manifest's census key. Author-chosen, unique within a row; not read against any other section's material table.
   */
  material: number;
  /**
   * The material's reference color, #RRGGBB or #RRGGBBAA (alpha ignored — the composed frame carries none).
   */
  color: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCurveRow".
 */
export interface WorldCurveRow {
  /**
   * The row's stable name, unique within the section — the spelling every consumer's own curve reference resolves against.
   */
  name: string;
  /**
   * The authored knots, in curve order. An open curve needs at least two; a closed curve at least three; at most MaxKnots.
   *
   * Items: One authored knot of a named curves row: a planar position with elevation, a tangent direction, and the signed curvature the compiled spline reaches there — see CurvatureSplineKnot for the compiled fixed-point form and the exact cross2 convention.
   */
  knots: (WorldCurveKnot | null)[];
  /**
   * Whether the last knot connects back to the first. Defaults to false (open).
   */
  closed?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldCurveKnot".
 */
export interface WorldCurveKnot {
  /**
   * The knot's world position — X/Z are the planar curvature-solve inputs, Y is the elevation lift (outside the planar curvature and arc-length solve; carried through as a linear grade over each segment's arc length — see CurvatureSpline's remarks). A DocumentVector3, the SAME spelling every other authored position in this document uses — no bespoke 2-vector shape.
   */
  position: DocumentVector3;
  /**
   * The tangent direction, in radians, reduced to the canonical interval [-π, π] (a small validator slack absorbs float round-trip of a double π literal); the unit tangent is (cos, sin) in the XZ plane — the SAME facing convention SinCos and the engine's own facing path use, pinned once here: every consumer (the camera path op, the sim curve-follow target) reads this convention rather than re-deriving one. An angle outside the canonical interval is refused rather than normalized: FromDouble saturates a finite value outside its representable range instead of wrapping it, so silently accepting one would compile a direction unrelated to the authored angle.
   */
  tangentYaw: number;
  /**
   * The signed planar curvature at this knot, under the cross2(a, b) = a.X·b.Z − a.Z·b.X convention: positive curvature turns from the tangent toward +Z faster than toward −Z. Must fall within ±MaxCurvature.
   */
  curvature: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldNavigationSection".
 */
export interface WorldNavigationSection {
  /**
   * Finite rectangular domains, each compiled from the world's deterministic solid field.
   */
  domains?: (WorldNavigationDomain | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldNavigationDomain".
 */
export interface WorldNavigationDomain {
  /**
   * The stable domain name referenced by navigated producer targets.
   */
  name: string;
  /**
   * Whether cells follow ground, free 3D space, or a live medium.
   */
  kind: WorldNavigationKind;
  /**
   * The center of cell (0,0,0), relative to Parent when present and world-space otherwise; for a surface domain, resolved world Y is the ground-probe baseline.
   *
   * @minItems 3
   * @maxItems 3
   */
  origin: [number, number, number];
  /**
   * The square cell width in world units.
   */
  cellSize: number;
  /**
   * The number of cells along the frame's X axis.
   */
  width: number;
  /**
   * The number of cells along the frame's Z axis.
   */
  depth: number;
  /**
   * The solid-clearance sphere radius. A medium domain also keeps this whole volume submerged.
   */
  agentRadius: number;
  /**
   * How close a body must come before advancing to the next waypoint.
   */
  arrivalDistance: number;
  /**
   * The hard A* expansion budget for one route search.
   */
  maxExpandedNodes: number;
  /**
   * The hard stored-waypoint budget for one route.
   */
  maxPathNodes: number;
  /**
   * The number of cells along world Y; surface domains require one.
   */
  layers?: number;
  /**
   * The volume neighbour set; ignored by surface domains.
   */
  connectivity?: WorldNavigationConnectivity;
  /**
   * For a surface domain, the ground-search distance above origin Y.
   */
  probeUp?: number;
  /**
   * For a surface domain, the ground-search distance below origin Y.
   */
  probeDown?: number;
  /**
   * For a surface domain, the clearance capsule height.
   */
  agentHeight?: number;
  /**
   * For a surface domain, the greatest adjacent height delta.
   */
  maxStepHeight?: number;
  /**
   * For a surface domain, the greatest adjacent slope.
   */
  maxSlopeDegrees?: number;
  /**
   * For a medium domain, the named lattice field that cells must remain inside.
   */
  medium?: string | null;
  /**
   * Optional shared reverse-search policy. Absent uses independent bounded A* searches. Shared searches are queued and advanced on subsequent simulation ticks, independently of any leader.
   */
  shared?: WorldNavigationSharing | null;
  /**
   * Optional static placement frame. Origin and grid axes are local to that frame; probes remain vertical. The frame must be undistributed and unit scale.
   */
  parent?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldNavigationSharing".
 */
export interface WorldNavigationSharing {
  /**
   * Resident destination-cell trees. A full cache with pending work refuses another destination as capacity-limited; it never launches an unbudgeted independent search.
   */
  goalCapacity: number;
  /**
   * Total reverse-Dijkstra expansions per simulation tick, shared fairly between pending resident goals. Each expansion inspects at most 26 edges. A tree can eventually settle every domain cell; the independent A* MaxExpandedNodes bound does not truncate a shared tree.
   */
  expandedNodesPerTick: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternRow".
 */
export interface PatternRow {
  /**
   * The pattern name a rule references.
   */
  name: CellName;
  /**
   * The numeric kind of the values the word is read from: Int or Fixed.
   */
  kind: CellKind;
  /**
   * The alphabet, 1..32 named value ranges.
   *
   * Items: One symbol of a pattern's alphabet: the cell values in Min..Max (inclusive, in the pattern's kind) read as this letter. Symbols may overlap; the refined alphabet splits them.
   */
  symbols: (PatternSymbol | null)[];
  /**
   * The language.
   */
  pattern: PatternNode;
  /**
   * For a zone source, the keyed row (over the zone's token domain) whose cell values form the word, in pile order; null reads the source row's own cell values.
   */
  attribute?: string | null;
  /**
   * The machine-state budget the compile refuses past, 1..256.
   */
  maxStates?: number;
  /**
   * For a zone source, an expression in the pattern's kind evaluated once per token in pile order, where a state token keyed $token reads that token's cell of a row keyed over the zone's token domain: the word over a tuple of attributes (suit * 16 + rank) rather than one. Exclusive with Attribute.
   */
  value?: ValueExpression;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternSymbol".
 */
export interface PatternSymbol {
  /**
   * The symbol name a pattern node references.
   */
  name: CellName;
  /**
   * The least value the symbol accepts.
   */
  min: number;
  /**
   * The greatest value the symbol accepts.
   */
  max: number;
}
/**
 * One token whose value falls in the named symbol.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeSymbol".
 */
export interface PatternNodeSymbol {
  $type?: "symbol";
  name: string;
}
/**
 * One token of any value, named symbols and the unnamed remainder alike.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeAnySymbol".
 */
export interface PatternNodeAnySymbol {
  $type?: "any";
}
/**
 * One token whose value falls outside the named symbol.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeExcept".
 */
export interface PatternNodeExcept {
  $type?: "except";
  name: string;
}
/**
 * The empty word.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeNothing".
 */
export interface PatternNodeNothing {
  $type?: "empty";
}
/**
 * The empty language: no word at all, the zero of choice and the annihilator of sequence.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeNone".
 */
export interface PatternNodeNone {
  $type?: "none";
}
/**
 * The items matched one after another.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeSequence".
 */
export interface PatternNodeSequence {
  $type?: "sequence";
  items: PatternNodeListNonNullable;
}
/**
 * Any one of the items.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeChoice".
 */
export interface PatternNodeChoice {
  $type?: "choice";
  items: PatternNodeList;
}
/**
 * Every item at once: the word is in each item's language.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeBoth".
 */
export interface PatternNodeBoth {
  $type?: "all";
  items: PatternNodeList;
}
/**
 * Every word the item does not match.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeComplement".
 */
export interface PatternNodeComplement {
  $type?: "not";
  item: PatternNode;
}
/**
 * The item or nothing.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeOptional".
 */
export interface PatternNodeOptional {
  $type?: "optional";
  item: PatternNode;
}
/**
 * The item zero or more times.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeStar".
 */
export interface PatternNodeStar {
  $type?: "star";
  item: PatternNode;
}
/**
 * The item one or more times.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodePlus".
 */
export interface PatternNodePlus {
  $type?: "plus";
  item: PatternNode;
}
/**
 * The item between Min and Max times.
 *
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "PatternNodeRepeat".
 */
export interface PatternNodeRepeat {
  $type?: "repeat";
  item: PatternNode;
  min: number;
  max: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "TableRow".
 */
export interface TableRow {
  /**
   * The row's stable name.
   */
  name: string;
  /**
   * The referenced document's file path.
   */
  source: string;
  /**
   * The SHA-256 hex64 of the referenced document's canonical bytes.
   */
  hash: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchSection".
 */
export interface WorldSearchSection {
  /**
   * The declared jobs.
   */
  jobs?: (WorldSearchRow | null)[] | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchRow".
 */
export interface WorldSearchRow {
  /**
   * The stable job name.
   */
  name: string;
  /**
   * The keyed integer row whose cells are the tokens and whose values are the board cells they stand on; a value that is no cell is a token off the board, which the job leaves alone.
   */
  tokens: string;
  /**
   * The board row over the topology the token values index; absent for a job over Zones.
   */
  board?: string | null;
  /**
   * For a job over piles: the ordered zones (keysOf rows with ordered, all over the token domain Tokens names) whose ordinals are the job's cells — a token's cell is the zone it stands in, legal masks and best.to name zones, and the one shape is transfer. Exactly one of Board and Zones is authored.
   */
  zones?: (string | null)[] | null;
  /**
   * The slot row whose change marks an accepted relocation; absent, the tabletop board binding anchoring Board supplies it.
   */
  turn?: string | null;
  /**
   * The slot row the rules judge a relocation into; absent, the same board binding supplies it.
   */
  verdict?: string | null;
  /**
   * The candidate shapes the walk enumerates, in declared order, ahead of token and target/direction; absent or empty, the one default shape (Relocate, displace: true).
   */
  shapes?: (WorldSearchShape | null)[] | null;
  /**
   * An integer row keyed by the tokens receiving, per token, the mask of cells it may relocate to; requires a board of at most 64 cells.
   */
  legal?: string | null;
  /**
   * An integer board row over the same topology as Board receiving, at 1, the cells Held's named token may reach and, at its own empty value, every other cell; unlike Legal, works for a board of any size. Authored together with Held.
   */
  reach?: string | null;
  /**
   * An integer slot row naming the token whose accepted destinations are painted into Reach — its own ordinal in Tokens's cell order. A value out of range paints nothing.
   */
  held?: string | null;
  /**
   * An integer row keyed by the tokens receiving, per token, how many candidates it accepted; unlike Legal, works for a board of any size.
   */
  counts?: string | null;
  /**
   * The relocations judged per tick, at most what the work sheet leaves; absent derives that.
   */
  nodes?: number | null;
  /**
   * How many plies the job searches ahead; the depth-one walk this section always ran. A depth past one asks what the position is worth after the ply, not merely whether it is legal, and requires Score.
   */
  depth?: number;
  /**
   * An infix expression, in the rule expression grammar, evaluated over the frame after a ply from the perspective of the side that made it; iterative-deepening negamax with alpha-beta compares it across plies. Required when Depth exceeds one, or Best is authored.
   */
  score?: string | null;
  /**
   * A keyed integer row receiving the deepest completed depth's answer: token (the mover's ordinal in Tokens), to (its destination cell), and score (the negamax value).
   */
  best?: string | null;
  /**
   * How plies are compared by the score: Negamax to the depth cap, or Tree, which reads the score where no candidate is accepted or at the cap.
   */
  method?: SearchMethod;
  /**
   * How many tree iterations a Tree job runs before it lands.
   */
  iterations?: number;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShapeRelocate".
 */
export interface WorldSearchShapeRelocate {
  $type?: "relocate";
  /**
   * Whether the token standing on the target leaves the board.
   */
  displace?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShapeDrop".
 */
export interface WorldSearchShapeDrop {
  $type?: "drop";
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShapeJump".
 */
export interface WorldSearchShapeJump {
  $type?: "jump";
  /**
   * The directions tried, in the topology's own vocabulary (CompiledTopology.Direction). The single-element list ["any"] tries every direction the topology declares.
   */
  over: (string | null)[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShapePaired".
 */
export interface WorldSearchShapePaired {
  $type?: "pair";
  /**
   * The companion token's cell key in Tokens.
   */
  with: string;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShapePromote".
 */
export interface WorldSearchShapePromote {
  $type?: "promote";
  /**
   * An integer row keyed by the tokens holding each token's code.
   */
  codes: string;
  /**
   * The codes a token may take, at most MaxPromotions.
   */
  to: number[];
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldSearchShapeTransferred".
 */
export interface WorldSearchShapeTransferred {
  $type?: "transfer";
  /**
   * Which end of its zone a token must stand at to move: Last (default, the top of the pile) or First.
   */
  selector?: ZoneSelector;
  /**
   * Whether the token lands first in the destination rather than last.
   */
  insertFirst?: boolean;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldImport".
 */
export interface WorldImport {
  /**
   * The fragment's file path, resolved against the importing document's own directory exactly like basis.
   */
  document: string;
  /**
   * The alias, or null to compose the fragment's names unchanged. An alias is a bare identifier — a letter or underscore, then letters, digits, and underscores — so an aliased name still lexes as one bare name inside an infix expression and still parses as a CellName (TryValidateAlias).
   */
  as?: string | null;
}
/**
 * This interface was referenced by `undefined`'s JSON-Schema
 * via the `definition` "WorldExports".
 */
export interface WorldExports {
  /**
   * The names a host may read: gates, comparands, expression operands, key indirections, domain and zone references, a board facet's topology and read-back rows.
   */
  reads?: (string | null)[] | null;
  /**
   * The names a host may drive: the rows a kit action, a host rule, or a placement's board facet writes into.
   */
  actions?: (string | null)[] | null;
  /**
   * The names a host's presentation may bind: HUD elements and templates, overlay predicates, camera program scalars, binding wheels and bars.
   */
  bindings?: (string | null)[] | null;
}
