// GENERATED FILE — do not hand-edit.
// Written by `puck schema` from the puck.world.definition.v1 schema bundle; `puck schema --check` fails when it
// no longer matches the schema.
// Source bundle: schemaVersion=puck.world.definition.v1 generator=Puck.World.WorldSchema

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
  schedule?: WorldScheduleSection | null;
  curves?: (WorldCurveRow | null)[] | null;
  navigation?: WorldNavigationSection | null;
  patterns?: (PatternRow | null)[] | null;
  tables?: (TableRow | null)[] | null;
  search?: WorldSearchSection | null;
  machines?: (WorldMachine | null)[] | null;
  ruleGroups?: (RuleGroupDeclaration | null)[] | null;
  sets?: (CellSetRow | null)[] | null;
  /**
   * Gets the basis document this file layers over, as a file path resolved against this document's own directory — the document-composition member (see WorldDocumentBasis). A file naming a basis is a delta: it authors only what differs, inheriting every omitted member from the (recursively composed) basis chain. Unrelated to the coordinate basis the validator's geometry speaks of.
   */
  basis?: string | null;
  /**
   * Gets the stable document id used when this world submits to another document, or null when the document authors none.
   */
  documentId?: string | null;
  /**
   * Gets the surface this document offers a host that imports it (WorldExports): the names the host may read, drive, and bind to. Every other name the document declares is private to it. Consumed where the document is imported (WorldModuleExports), so a live document always carries null here and the validator refuses anything else.
   */
  exports?: WorldExports | null;
  /**
   * Gets the ordered fragment documents this file imports — each a WorldImport naming a file path resolved against this document's own directory and an optional alias its names compose under — the fan-in half of composition beside Basis's single-parent chain (see WorldDocumentBasis). Composition order is the basis chain, then each import in list order, then this file's own body.
   */
  imports?: (WorldImport | null)[] | null;
  /**
   * Gets the document schema tag — SchemaVersion for a well-formed document.
   */
  schema?: "puck.world.definition.v1";
  [k: string]: unknown;
} | null;

/**
 * An authored effect row a rule fires when its gate holds. The arms declared here are the ones this library owns — every one addresses a state row and nothing else; a document project appends its own derived arms (a participant's kinematics, a presentation cue, a whole-row document upsert) through its own rule vocabulary (RuleVocabulary.ExtendJson) rather than by editing this list.
 */
export type ActionEffect = ActionEffectSetState | ActionEffectAddState | ActionEffectPushState | ActionEffectTransformState | ActionEffectGenerate | ActionEffectRemoveStateCell | ActionEffectScheduleState | ActionEffectTransaction | ActionEffectIf | ActionEffectClaim | ActionEffectRelease | ActionEffectForEachPool | ActionEffectClaimPair | ActionEffectRewindGroup | WorldEffectSetVerticalVelocity | WorldEffectScaleVerticalVelocity | WorldEffectPlanarImpulse | WorldEffectStartTimer | WorldEffectDesignate | WorldEffectEmitCue | WorldEffectApplyRigidImpulse | WorldEffectPaintField | WorldEffectUpsertHudPanel | WorldEffectRemoveHudPanel | WorldEffectUpsertPlacement | WorldEffectRemovePlacement | WorldEffectSave | WorldEffectPoseCell | WorldEffectPose | WorldEffectSetIdentityFact | null;

/**
 * Adds to a named state cell — the same shape as SetState, here the source is the addend rather than the replacement.
 */
export type ActionEffectAddState = {
  $type?: "addState";
  /**
   * The state row name (or the host's counter slot).
   */
  state: StateChannelRefNonNullable;
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
  key?: StateChannelRefNonNullable;
  /**
   * See FromState's remarks; here the addend is read live rather than the replacement.
   */
  fromState?: StateChannelRefNonNullable;
  /**
   * The cell inside FromState — see FromKey.
   */
  fromKey?: StateChannelRefNonNullable;
  /**
   * See ValueSeconds's remarks; here the converted tick count is the addend rather than the replacement.
   */
  valueSeconds?: number | null;
  /**
   * See Expression.
   */
  expression?: ExpressionProgramNonNullable2;
};

/**
 * Claims one lowest-free instance from a declared pool and fires Effects with Binding bound to that fresh, generation-checked instance. A full pool, an initializer refusal, or any nested effect refusal rejects this effect and rewinds the enclosing firing.
 */
export type ActionEffectClaim = {
  $type?: "claim";
  /**
   * The declared pool to claim from.
   */
  pool: StateChannelRefNonNullable;
  /**
   * The lexical instance name available only inside Effects.
   */
  binding: CellName;
  /**
   * The ordered initializer and body effects run under the fresh binding.
   */
  effects: (ActionEffect | null)[];
};

/**
 * Claims one pair-pool instance for two currently bound endpoint instances, then runs its body under the fresh pair binding. Endpoint handles remain generation-checked, so a released/reclaimed endpoint cannot be paired through a stale alias.
 */
export type ActionEffectClaimPair = {
  $type?: "claimPair";
  /**
   * The declared pair pool.
   */
  pool: StateChannelRefNonNullable;
  /**
   * The lexical binding for the left endpoint.
   */
  left: CellName;
  /**
   * The lexical binding for the right endpoint.
   */
  right: CellName;
  /**
   * The lexical binding for the fresh pair instance.
   */
  binding: CellName;
  /**
   * The ordered body under Binding.
   */
  effects: (ActionEffect | null)[];
};

/**
 * Snapshots one pool's live generation-checked instances and fires Effects for each under Binding. A release or reclaim inside the body cannot make a replacement slot appear in the same sweep.
 */
export type ActionEffectForEachPool = {
  $type?: "forEachPool";
  /**
   * The declared pool to visit.
   */
  pool: StateChannelRefNonNullable;
  /**
   * The lexical binding available only inside Effects.
   */
  binding: CellName;
  /**
   * The ordered effects run for each snapshot instance.
   */
  effects: (ActionEffect | null)[];
};

/**
 * Redraws a draw site (a state row declaring a Draw). A draw's moment is authored through the rule that fires this: a TickPeriod site redraws on an ordinary $tick-scheduled rule and a Event site on an event-gated one, so timing costs no mutation ordinal.
 */
export type ActionEffectGenerate = {
  $type?: "generate";
  /**
   * The draw site's row name. One name, not a (source, destination) pair: a site's source is its own facet and a site is a scalar slot, so there is nothing else to address.
   */
  row: StateChannelRefNonNullable;
};

/**
 * Branches on a predicate: fires Then when it holds, else Else when present. Both branches read the frame at the effect's own position, so an earlier effect's same-firing write is visible to the condition exactly as it is to a later effect's own operand. A condition that fails to evaluate (an arithmetic fault, a missing table key) runs neither branch and is reported the same way a failing top-level effect is; a false condition with no branch that fires is not a failure. An effect inside a branch that itself refuses is refused on the same terms as a top-level effect — a branch is not a transaction, though a Transaction may appear inside one when this if is not itself inside a transaction.
 */
export type ActionEffectIf = {
  $type?: "if";
  /**
   * The gate.
   */
  condition: ActionPredicateNonNullable;
  /**
   * The branch fired when Condition holds.
   */
  then: (ActionEffect | null)[];
  /**
   * The branch fired when it does not, or null to fire nothing.
   */
  else?: (ActionEffect | null)[] | null;
};

export type ActionEffectList = (ActionEffect | null)[];

/**
 * Pushes one numeric value into a history row's ring (see Ring), the same source spellings as SetState minus text: exactly one of Value, FromState, or Expression.
 */
export type ActionEffectPushState = {
  $type?: "pushState";
  /**
   * The history row.
   */
  state: StateChannelRefNonNullable;
  /**
   * An exact decimal literal in the row's kind.
   */
  value?: number | null;
  /**
   * A state row or reserved channel read live at every firing.
   */
  fromState?: StateChannelRefNonNullable;
  /**
   * The cell of FromState, or null for its slot.
   */
  fromKey?: StateChannelRefNonNullable;
  /**
   * A bounded numeric expression evaluated in the row's kind.
   */
  expression?: ExpressionProgramNonNullable2;
};

/**
 * Releases the currently bound pool instance named by Binding. A stale or already released binding refuses the enclosing firing instead of selecting a later reclaim of the same slot.
 */
export type ActionEffectRelease = {
  $type?: "release";
  /**
   * The lexical instance binding to release.
   */
  binding: CellName;
};

/**
 * Removes one addressed cell from a declared state row.
 */
export type ActionEffectRemoveStateCell = {
  $type?: "removeStateCell";
  /**
   * The row to remove from.
   */
  state: StateChannelRefNonNullable;
  /**
   * The optional cell key.
   */
  key?: StateChannelRefNonNullable;
};

/**
 * Restores and removes the newest retained turn of a named undo group.
 */
export type ActionEffectRewindGroup = {
  $type?: "rewindGroup";
  /**
   * The undo-enabled rule group.
   */
  group: CellName;
};

/**
 * Writes an absolute simulation due tick into an integer state cell. The delay is converted against the document's authored simulation rate and rounded up, so it never fires early. A companion rule compares $tick against the cell and removes it after handling, forming a bounded, document-backed scheduler.
 */
export type ActionEffectScheduleState = {
  $type?: "scheduleState";
  /**
   * The integer destination row.
   */
  state: StateChannelRefNonNullable;
  /**
   * The non-negative delay, rounded up to simulation ticks.
   */
  delaySeconds: number;
  /**
   * The optional cell key.
   */
  key?: StateChannelRefNonNullable;
};

/**
 * Writes a named state cell — a state-section row's cell at rule scope, a counter slot inside a host's per-participant action program.
 */
export type ActionEffectSetState = {
  $type?: "setState";
  /**
   * The state row name (or the host's counter slot).
   */
  state: StateChannelRefNonNullable;
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
  key?: StateChannelRefNonNullable;
  /**
   * Another declared state-section row name, or a reserved channel, read live at fire time and copied in place of an authored Value — the row that resets to another row's own current value (a shadow row mirroring a counter someone else advances), never only a standing literal. Resolved through the same operand walk CompareState's own ComparandState uses; mixing a fixed row into an int destination (or the reverse) is refused by name rather than coerced.
   */
  fromState?: StateChannelRefNonNullable;
  /**
   * The cell inside FromState, on the same (row, key) terms as Key. Refused when FromState names a reserved channel or is absent.
   */
  fromKey?: StateChannelRefNonNullable;
  /**
   * An alternative to Value for a kind=Int state row: writes the row's raw engine-tick representation of a duration rather than the duration's own numeric value. Authored in seconds — a physical unit, not a tick count, so a document's rate can change without silently retuning the written duration — and converted once at rule compile time to an exact whole engine-tick count via TryDurationEngineTicksExact, never re-derived at runtime and never rounded: a duration that is not an exact whole engine-tick count is refused rather than silently rounded away (DurationNotExactEngineTicks). Typed Decimal rather than float because JSON deserializes a number token to Decimal exactly (base-10, no binary-float intermediate), and most terminating decimals — the only ones an author can spell — have no exact binary float or fixed-point spelling either.
   */
  valueSeconds?: number | null;
  /**
   * The literal a kind=Text state row's cell takes — the fourth spelling beside Value/FromState/ValueSeconds, exactly one authored. Every state-bound document value re-resolves on the write, so this is how a rule restyles what a state-bound document row names.
   */
  text?: string | null;
  /**
   * A bounded numeric expression evaluated in the destination row's integer or fixed-point domain. Exactly one source spelling is authored.
   */
  expression?: ExpressionProgramNonNullable2;
  /**
   * The base64url-encoded vector literal a vector state row's cell takes.
   */
  vector?: string | null;
};

/**
 * Applies a bounded list of effects atomically, as a savepoint inside the firing: each effect fires once, and when any refuses, the savepoint rewinds so none apply and OnFailure runs instead. The compiler refuses an empty main branch, a branch of more than MaxTransactionEffects effects, and a transaction anywhere inside another, including inside an If branch.
 */
export type ActionEffectTransaction = {
  $type?: "transaction";
  /**
   * The main transaction branch.
   */
  effects: (ActionEffect | null)[];
  /**
   * The optional branch run after a main-branch refusal.
   */
  onFailure?: (ActionEffect | null)[] | null;
};

/**
 * Applies a bounded state transform through the ordinary mutation pipeline.
 */
export type ActionEffectTransformState = {
  $type?: "transformState";
  /**
   * The typed operation.
   */
  transform: StateTransform;
};

export type ActionFact = "Grounded" | "Airborne" | "Rising" | "Falling" | "AffectedBy" | "InMedium" | "AtMediumBand" | "HoldingUnwalkable" | "Unsupported" | "Resting";

export type ActionFactNullable = "Grounded" | "Airborne" | "Rising" | "Falling" | "AffectedBy" | "InMedium" | "AtMediumBand" | "HoldingUnwalkable" | "Unsupported" | "Resting";

export type ActionFactTrigger = {
  /**
   * The body fact.
   */
  fact: ActionFact;
  /**
   * The effects applied in order.
   */
  effects: (ActionEffect | null)[];
  /**
   * The predicate that must hold, or null for always.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * Level or edge.
   */
  mode?: ActionTriggerMode;
};

/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through its own rule vocabulary (RuleVocabulary.ExtendJson) rather than by editing this list.
 */
export type ActionPredicate = ActionPredicateCompareState | ActionPredicateCompareValue | ActionPredicateAll | ActionPredicateAnyNonNullable | ActionPredicateNot | WorldPredicateNow | WorldPredicateRecently | WorldPredicateTimerElapsed | WorldPredicateHeld | null;

/**
 * Every inner predicate holds (conjunction).
 */
export type ActionPredicateAll = {
  $type?: "all";
  predicates: ActionPredicateListNonNullable;
};

/**
 * At least one inner predicate holds (disjunction). The list must be non-empty.
 */
export type ActionPredicateAny = {
  $type?: "any";
  /**
   * The non-empty child-predicate list.
   */
  predicates: ActionPredicateList;
};

/**
 * At least one inner predicate holds (disjunction). The list must be non-empty.
 */
export type ActionPredicateAnyNonNullable = {
  $type?: "any";
  predicates: ActionPredicateList;
};

/**
 * Compares a named state cell against either a fixed authored value, or another named state cell/reserved channel read live at the same evaluation. Both spellings are authorable; exactly one of Value and ComparandState may be present (refused by name when both or neither are). The comparand-row spelling is what lets a gate track a moving threshold — $tick compared against a schedule row the rule's own effects advance is "every N ticks"; a round row compared against a declared length row is a round boundary — composition over the same two-sided comparison, never a new mechanism.
 */
export type ActionPredicateCompareState = {
  $type?: "compareState";
  /**
   * A declared state-section row name, or one of the reserved channels the compiler's vocabulary answers (RuleFacts and whatever a document project registers). Inside a host's per-participant action program, a named counter slot the program declares.
   */
  state: StateChannelRefNonNullable;
  /**
   * The comparison to apply.
   */
  comparison: ExpressionComparison;
  /**
   * The authored constant comparand, or null when ComparandState spells the comparand instead.
   */
  value?: number | null;
  /**
   * The cell inside State to read — null reads the row's slot cell, which a keyed row does not have (refused by name rather than silently reading cells[0]).
   */
  key?: StateChannelRefNonNullable;
  /**
   * Another declared state-section row name, or a reserved channel, read live and compared instead of Value. A dotted spelling (an author reaching for row.key in one string) is refused by name — address the cell with ComparandKey instead. Comparing across incompatible cell kinds (an int row against a fixed row, say) is refused by name — mixing scales silently is worse than naming the mismatch.
   */
  comparandState?: StateChannelRefNonNullable;
  /**
   * The cell inside ComparandState, on the same (row, key) terms as Key. Refused when ComparandState names a reserved channel or is absent.
   */
  comparandKey?: StateChannelRefNonNullable;
};

/**
 * Comparison of two bounded numeric expressions. Arithmetic failure makes this comparison false.
 */
export type ActionPredicateCompareValue = {
  $type?: "compareValue";
  /**
   * Left numeric expression.
   */
  left: ExpressionProgramNonNullable2;
  /**
   * The comparison operation.
   */
  comparison: ExpressionComparison;
  /**
   * Right numeric expression.
   */
  right: ExpressionProgramNonNullable2;
  /**
   * The common Int or Fixed domain, or null to infer it the way a local's is: Int, unless both sides are constants alone and one holds a fraction, and the other domain when the first does not compile. No implicit conversion is performed between the sides.
   */
  kind?: CellKind | null;
};

export type ActionPredicateList = (ActionPredicateNullable | null)[];

export type ActionPredicateListNonNullable = (ActionPredicateNullable | null)[];

/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through its own rule vocabulary (RuleVocabulary.ExtendJson) rather than by editing this list.
 */
export type ActionPredicateNonNullable = ActionPredicateCompareState | ActionPredicateCompareValue | ActionPredicateAll | ActionPredicateAny | ActionPredicateNot | WorldPredicateNow | WorldPredicateRecently | WorldPredicateTimerElapsed | WorldPredicateHeld | null;

/**
 * Inverts one predicate.
 */
export type ActionPredicateNot = {
  $type?: "not";
  predicate: ActionPredicate;
};

/**
 * Inverts one predicate.
 */
export type ActionPredicateNotNonNullable = {
  $type?: "not";
  /**
   * The child predicate to invert.
   */
  predicate: ActionPredicate;
};

/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through its own rule vocabulary (RuleVocabulary.ExtendJson) rather than by editing this list.
 */
export type ActionPredicateNullable = ActionPredicateCompareState | ActionPredicateCompareValue | ActionPredicateAll | ActionPredicateAny | ActionPredicateNotNonNullable | WorldPredicateNow | WorldPredicateRecently | WorldPredicateTimerElapsed | WorldPredicateHeld | null;

/**
 * A data-composable gate over named state. A rule fires only while its gate holds. The $type string is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the ones this library owns, and a document project appends its own derived arms through its own rule vocabulary (RuleVocabulary.ExtendJson) rather than by editing this list.
 */
export type ActionPredicateNullable2 = ActionPredicateCompareState | ActionPredicateCompareValue | ActionPredicateAll | ActionPredicateAny | ActionPredicateNot | WorldPredicateNow | WorldPredicateRecently | WorldPredicateTimerElapsed | WorldPredicateHeld | null;

export type ActionSpec = {
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
};

/**
 * The authored values a player-writable durable slot admits in this world.
 */
export type ActionStateEnvelope = ActionStateEnvelopeRange | ActionStateEnvelopeSet | null;

/**
 * An inclusive numeric interval.
 */
export type ActionStateEnvelopeRange = {
  $type?: "range";
  /**
   * The least admitted value.
   */
  minimum: number;
  /**
   * The greatest admitted value.
   */
  maximum: number;
};

/**
 * A closed numeric set. Values are authored labels encoded in the slot's deterministic numeric domain.
 */
export type ActionStateEnvelopeSet = {
  $type?: "set";
  /**
   * The admitted values.
   */
  values: number[];
};

export type ActionStateKind = "Counter" | "Timer";

/**
 * Declares one named body-state slot shared by every kit action in the world. The carrying WorldStateSection lane selects whether it belongs to the body or its identity.
 */
export type ActionStateSlot = {
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
};

/**
 * The participant an action effect addresses. A document-scope rule has no participant to select, so only Self is admitted there; the other members belong to a host's per-participant action programs, which share the SetState/AddState shapes with the rule compiler and therefore carry the member on the wire.
 */
export type ActionTarget = "Self" | "ProducerTarget" | "AffectingSubject";

/**
 * One trigger of a kit's action program: the effects that fire, the gate that must hold, and the latch that keeps a press armed while the gate is closed.
 */
export type ActionTrigger = {
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
};

export type ActionTriggerMode = "Level" | "Edge";

export type BindableColor = string;

export type BindableScalar = number | string;

export type BindingActivatorDefinition = {
  sequence: (string | null)[];
  mode?: BindingActivatorMode;
  timeoutTicks?: number | null;
};

export type BindingActivatorMode = "Held" | "Tapped";

export type BindingBarEdge = "Bottom" | "Top" | "Left" | "Right";

export type BindingBarPreferences = {
  hideUnbound?: boolean | null;
  scale?: number | null;
  contrastBoost?: number | null;
  uiScale?: number | null;
};

export type BindingChordDefinition = {
  group: DocumentIdentifier;
  chord?: (string | null)[] | null;
  page?: BindingPageDefinition | null;
  command?: BindingCommandDefinition | null;
  held?: (string | null)[] | null;
};

export type BindingCommandDefinition = {
  command?: string | null;
  channel?: ChannelRef | null;
  scale?: number | null;
  holdRelease?: boolean;
  label?: string | null;
  icon?: string | null;
  value?: CommandValue | null;
  mode?: BindingEntryMode;
  text?: string | null;
};

export type BindingContextDefinition = {
  family: string;
  state: string;
  group: DocumentIdentifier;
};

export type BindingEntryMode = "Hold" | "Toggle";

export type BindingModifierDefinition = {
  id: string;
  sources: (string | null)[];
  pressThreshold?: number;
  releaseThreshold?: number;
  label?: string | null;
  icon?: string | null;
};

export type BindingPageDefinition = {
  id: string;
  entries: BindingPageEntryDefinitionList;
  label?: string | null;
  icon?: string | null;
  inherits?: string | null;
};

export type BindingPageEntryDefinition = {
  sources: (string | null)[] | null;
  command?: string | null;
  channel?: ChannelRef | null;
  scale?: number | null;
  activateOn?: CommandPhase | null;
  label?: string | null;
  id?: string | null;
  value?: CommandValue | null;
  text?: string | null;
  activator?: BindingActivatorDefinition | null;
  mode?: BindingEntryMode;
};

export type BindingPageEntryDefinitionList = (BindingPageEntryDefinition | null)[];

export type BindingProfileDocument = {
  version: string;
  modifiers: (BindingModifierDefinition | null)[];
  chords: (BindingChordDefinition | null)[];
  contexts?: (BindingContextDefinition | null)[] | null;
  wheels?: (BindingWheelDefinition | null)[] | null;
  bindingBar?: BindingBarPreferences | null;
};

export type BindingWheelDefinition = {
  id: string;
  group: DocumentIdentifier;
  holdPages: (string | null)[];
  rings: (BindingPageDefinition | null)[];
  style?: BindingWheelStyleDefinition | null;
  labelRow?: string | null;
  iconRow?: string | null;
};

export type BindingWheelExcursionDefinition = {
  deadZone: number;
  thresholds: number[];
  spatialTravelFraction?: number;
  hysteresis?: number;
};

export type BindingWheelPlacement = "Pointer" | "ViewportCenter";

export type BindingWheelRingSelectionMode = "Explicit" | "Excursion";

export type BindingWheelSpatialSelectionMode = "Disabled" | "Angle" | "HitTarget";

export type BindingWheelStyleDefinition = {
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
};

/**
 * What BoardCombine writes into its board, cell by cell. A cell is a member of a board when its value is not the board's declared empty.
 */
export type BoardCombineOp = "Copy" | "Fill" | "Clear" | "And" | "Or" | "Xor" | "AndNot" | "Not" | "Shift" | "Image";

export type BodyHoldBond = "Surface" | "Free" | "Medium";

export type BodyHoldForward = "Heading" | "Intent" | "Velocity";

export type BodyHoldKind = "None" | "Gravity" | "Pull" | "Lift";

export type BodyMotionOp = "SenseNearestInCone" | "ProduceSteeringIntent" | "FaceSensorTarget" | "ProduceFlockIntent" | "ResolveYawAttitudeAndPlanarFrame" | "IntegrateLocalAttitude" | "ComputePlanarTargetVelocity" | "ComputeLocalTargetVelocity" | "ShapeVelocity" | "SnapYawToPlanarIntent" | "ResolveDriveFrame" | "ResolveHold" | "RunActionTriggers" | "ApplyHold" | "IntegratePlanarAndVerticalVelocity" | "IntegrateScratchVelocity" | "CommitPose" | "SetVerticalVelocity" | "ScaleVerticalVelocity" | "PlanarImpulse" | "SetState" | "AddState" | "StartTimer" | "Designate" | "Generate";

export type BodyMotionProgram = {
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
};

export type BodyProgramKind = "Motion" | "Producer";

export type BodyProgramParameters = {
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
};

export type BodyTargetScope = "Seats" | "Bodies";

export type BodyTargetSource = BodyTargetSourceSensed | BodyTargetSourceDesignated | BodyTargetSourceCurveFollow | BodyTargetSourceNavigated | null;

export type BodyTargetSourceCurveFollow = {
  $type?: "curve";
  curve: string;
  rate: number;
};

export type BodyTargetSourceDesignated = {
  $type?: "designated";
  register: string;
};

export type BodyTargetSourceNavigated = {
  $type?: "navigated";
  domain: string;
  register: string;
};

export type BodyTargetSourceSensed = {
  $type?: "sensed";
  scope: BodyTargetScope;
  range: number;
  halfAngleDegrees: number;
  requiresLineOfSight: boolean;
};

export type CellKind = "Int" | "Fixed" | "Bool" | "Text" | "Vector";

/**
 * The closed set of cell value kinds a state row declares, shared by every cell the row carries. Carries no float kind: simulation state is float-free by the determinism contract (see Fixed for how a fractional value still rides here). A counter is represented as Fixed; a timer is Int declaring min zero.
 */
export type CellKindNonNullable = "Int" | "Fixed" | "Bool" | "Text" | "Vector";

export type CellName = string;

/**
 * The boolean-closed cell-set vocabulary: the same operators a pattern spells over a word, spelled over the positions of a family, a board, or a zone. Every expression lowers to one CellSet whose width is the carrier's own.
 */
export type CellSetExpression = CellSetExpressionBoard | CellSetExpressionFamily | CellSetExpressionZone | CellSetExpressionEverything | CellSetExpressionNothing | CellSetExpressionAny | CellSetExpressionBoth | CellSetExpressionComplement | null;

/**
 * The union of the items.
 */
export type CellSetExpressionAny = {
  $type?: "any";
  items: CellSetExpressionListNonNullable;
};

/**
 * The topology cells of a lattice row whose value falls in an inclusive range.
 */
export type CellSetExpressionBoard = {
  $type?: "board";
  /**
   * The lattice row.
   */
  row: CellName;
  /**
   * The least value a member carries.
   */
  low: number;
  /**
   * The greatest value a member carries.
   */
  high: number;
};

/**
 * The intersection of the items.
 */
export type CellSetExpressionBoth = {
  $type?: "both";
  /**
   * The items.
   */
  items: CellSetExpressionList;
};

/**
 * Every position the item does not hold.
 */
export type CellSetExpressionComplement = {
  $type?: "not";
  item: CellSetExpression;
};

/**
 * Every position of the carrier.
 */
export type CellSetExpressionEverything = {
  $type?: "all";
};

/**
 * The member rows of a family whose slot value falls in an inclusive range.
 */
export type CellSetExpressionFamily = {
  $type?: "family";
  /**
   * The family.
   */
  name: CellName;
  /**
   * The least value a member carries.
   */
  low: number;
  /**
   * The greatest value a member carries.
   */
  high: number;
};

export type CellSetExpressionList = (CellSetExpressionNullable | null)[];

export type CellSetExpressionListNonNullable = (CellSetExpressionNullable | null)[];

/**
 * No position at all.
 */
export type CellSetExpressionNothing = {
  $type?: "none";
};

/**
 * The boolean-closed cell-set vocabulary: the same operators a pattern spells over a word, spelled over the positions of a family, a board, or a zone. Every expression lowers to one CellSet whose width is the carrier's own.
 */
export type CellSetExpressionNullable = CellSetExpressionBoard | CellSetExpressionFamily | CellSetExpressionZone | CellSetExpressionEverything | CellSetExpressionNothing | CellSetExpressionAny | CellSetExpressionBoth | CellSetExpressionComplement | null;

/**
 * The pile positions of an ordered or keyed row whose value falls in an inclusive range.
 */
export type CellSetExpressionZone = {
  $type?: "zone";
  /**
   * The row.
   */
  row: CellName;
  /**
   * The least value a member carries.
   */
  low: number;
  /**
   * The greatest value a member carries.
   */
  high: number;
};

export type CellSetRow = {
  /**
   * The set's stable name — unique within the document.
   */
  name: CellName;
  /**
   * The expression the name stands for.
   */
  set: CellSetExpression;
};

export type CellValue = CellValueJsonConverterIntShape | CellValueJsonConverterFixedShape | CellValueJsonConverterBoolShape | CellValueJsonConverterTextShape | CellValueJsonConverterVectorShape;

export type CellValueJsonConverterBoolKind = "Bool";

/**
 * The schema-only Bool wire arm.
 */
export type CellValueJsonConverterBoolShape = {
  kind: CellValueJsonConverterBoolKind;
  value: boolean;
};

/**
 * The Fixed arm's sole kind token.
 */
export type CellValueJsonConverterFixedKind = "Fixed";

/**
 * The schema-only Fixed wire arm.
 */
export type CellValueJsonConverterFixedShape = {
  kind: CellValueJsonConverterFixedKind;
  value: number;
};

export type CellValueJsonConverterIntKind = "Int";

/**
 * The schema-only Int wire arm.
 */
export type CellValueJsonConverterIntShape = {
  kind: CellValueJsonConverterIntKind;
  value: number;
};

export type CellValueJsonConverterTextKind = "Text";

/**
 * The schema-only Text wire arm.
 */
export type CellValueJsonConverterTextShape = {
  kind: CellValueJsonConverterTextKind;
  value: string;
};

/**
 * The Vector arm's sole kind token.
 */
export type CellValueJsonConverterVectorKind = "Vector";

/**
 * The schema-only Vector wire arm.
 */
export type CellValueJsonConverterVectorShape = {
  kind: CellValueJsonConverterVectorKind;
  value: number[];
};

export type ChannelConsentMask = number;

export type ChannelFrame = "World" | "Camera" | "Heading";

export type ChannelReachMask = number;

export type ChannelRef = unknown;

export type ChannelRole = "MoveAdvance" | "MoveStrafe" | "Turn" | "MoveUp" | "Pitch" | "Roll" | "FaceX" | "FaceY" | "FaceZ" | "MoveX" | "MoveY" | "MoveZ";

export type ChannelShape = "Bipolar" | "Unipolar" | "Binary";

export type ClosedBitset256 = unknown;

export type CommandPhase = "Started" | "Active" | "Completed" | "Canceled";

export type CommandValue = unknown;

/**
 * What a StateCycle cell reads: the rotation as a step count, a fraction of a turn or a unit-rotation component, or the rotation carried along a symmetry-lattice orbit as a node index, its ring or a projected coordinate. The integer outputs belong to Int cells and the fixed outputs to Fixed cells.
 */
export type CycleOutput = "Step" | "Turns" | "Cos" | "Sin" | "Node" | "ProjectionX" | "ProjectionY" | "Ring";

export type DocumentIdentifier = string;

export type DocumentIdentifierList = DocumentIdentifier[];

export type DocumentQuaternion = [number, number, number, number] | string;

export type DocumentVector2 = [number, number] | string;

export type DocumentVector3 = [number, number, number] | string;

export type DocumentWriteMask = string;

export type DrawTiming = "Boot" | "TickPeriod" | "Event";

export type DynamicsRow = {
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
};

/**
 * A comparison as a document spells it: one of the six comparison operations of ExpressionOp (Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual), named by its member name exactly. Another operation's name, another casing, and a number are refused on read.
 */
export type ExpressionComparison = "Equal" | "NotEqual" | "Less" | "LessOrEqual" | "Greater" | "GreaterOrEqual";

/**
 * The one expression IR: a bounded postfix program a world rule, decision, cartridge step, or flock affinity evaluates. Each instruction either pushes a value or consumes preceding values; the compiler proves stack shape and numeric kind before simulation begins.
 */
export type ExpressionProgram = {
  /**
   * The postfix instructions, in evaluation order.
   */
  instructions: ShapeNonNullable20[];
  /**
   * Gets the shared subprograms this program's Call and fold instructions index into. The call graph is a directed acyclic graph over at most RuleCapacity.MaxSubprograms entries: the compiler refuses a cycle by name and compiles each subprogram once, so a call chain nests at most that many deep at evaluation.
   */
  subprograms?: ShapeNonNullable21[];
  [k: string]: unknown;
};

/**
 * The one expression IR: a bounded postfix program a world rule, decision, cartridge step, or flock affinity evaluates. Each instruction either pushes a value or consumes preceding values; the compiler proves stack shape and numeric kind before simulation begins.
 */
export type ExpressionProgramNonNullable = {
  /**
   * The postfix instructions, in evaluation order.
   */
  instructions: ShapeNonNullable30[];
  /**
   * Gets the shared subprograms this program's Call and fold instructions index into. The call graph is a directed acyclic graph over at most RuleCapacity.MaxSubprograms entries: the compiler refuses a cycle by name and compiles each subprogram once, so a call chain nests at most that many deep at evaluation.
   */
  subprograms?: ShapeNonNullable31[];
  [k: string]: unknown;
};

/**
 * The one expression IR: a bounded postfix program a world rule, decision, cartridge step, or flock affinity evaluates. Each instruction either pushes a value or consumes preceding values; the compiler proves stack shape and numeric kind before simulation begins.
 */
export type ExpressionProgramNonNullable2 = {
  /**
   * The postfix instructions, in evaluation order.
   */
  instructions: ShapeNonNullable40[];
  /**
   * Gets the shared subprograms this program's Call and fold instructions index into. The call graph is a directed acyclic graph over at most RuleCapacity.MaxSubprograms entries: the compiler refuses a cycle by name and compiles each subprogram once, so a call chain nests at most that many deep at evaluation.
   */
  subprograms?: ShapeNonNullable41[];
  [k: string]: unknown;
};

/**
 * One weighted alternative of a GeneratorContext: the token it emits, its relative weight, and the context the walk moves into after it is picked. The authored Next is what makes this a real Markov process rather than a bag of independent draws — the context key is the process state, so an author folds exactly as much history into it as the chain needs.
 */
export type GeneratorAlternative = {
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
};

/**
 * One named context of a StateGenerator — the state the walk may be sitting in and the weighted alternatives it may pick while there. A context declaring NO alternatives is TERMINAL: reaching it ends the emission.
 */
export type GeneratorContext = {
  /**
   * The stable context key, unique within the generator.
   */
  key: CellName;
  /**
   * The weighted alternatives out of this context, or empty for a terminal context.
   */
  alternatives?: GeneratorAlternative[] | null;
};

export type GeneratorContextVariant2 = {
  /**
   * The stable context key, unique within the generator.
   */
  key: CellName;
  /**
   * The weighted alternatives out of this context, or empty for a terminal context.
   */
  alternatives?: (GeneratorAlternative | null)[] | null;
};

/**
 * A source's extended-generator facet: an authored Pcg32Extended table replacing that generator's own self-seeding, so a site drawing from it is k-dimensionally equidistributed rather than merely 1-dimensionally so. The table is document data, exactly one rebuild cost (GeneratorEngine caches the built generator beside the cursor it corresponds to) — nothing about the site's persisted shape changes.
 */
export type GeneratorExtended = {
  /**
   * The extension table size: a power of two from MinExtendedTableSize to MaxExtendedTableSize.
   */
  k: number;
  /**
   * The whole extension table, exactly K words — or null when Script authors it instead.
   */
  table?: number[] | null;
  /**
   * Up to K values in the source's own output space, authoring the site's first draws directly — or null when Table is authored instead.
   */
  script?: number[] | null;
};

/**
 * How a StateGenerator's entries — a Markov context's alternatives, or a weighted numeric source's outcomes — are consumed: the multiset-sampling vocabulary. Authored, never inferred: exhaustion behaviour is a declaration, not a fallback the engine picks.
 */
export type GeneratorMode = "WithReplacement" | "WithoutReplacement" | "RestartOnExhaustion";

export type GeneratorRow = {
  /**
   * The source's name, unique within the section, and the spelling a site's Source resolves against.
   */
  name: CellName;
  /**
   * The source itself.
   */
  generator: StateGeneratorVariant2;
};

/**
 * The closed vocabulary of a StateGenerator's draw shape — which of its fields are read, and what one emission produces: a Markov text walk, a multiset draw, a uniform range, a weighted numeric table, and a raw stream draw are sources of one family, never parallel primitives with their own seeding, cursoring, and refusal stories.
 */
export type GeneratorSource = "Markov" | "UniformRange" | "WeightedNumeric" | "StreamDraw" | "SymmetryOrbit";

/**
 * One numeric outcome of a WeightedNumeric source: the raw value it writes and its relative weight — the numeric twin of GeneratorAlternative, minus Token (nothing to join into text) and Next (a numeric draw is one terminal pick, never a walk).
 */
export type GeneratorWeightedNumeric = {
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
};

export type GrantSubject = string;

export type Grantee = string;

export type GraphCell = {
  /**
   * The id edges name it by; distinct within the graph.
   */
  id: string;
  /**
   * The centre, relative to the topology's origin, in world units.
   */
  centre: DocumentVector3;
};

export type GraphDirection = {
  /**
   * The direction's name, as rules and patterns spell it.
   */
  name: string;
  /**
   * The direction an edge is followed back along; a symmetric link names itself.
   */
  opposite: string;
};

export type GraphEdge = {
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
  direction: CellName;
  /**
   * Whether the reverse edge is left unfilled (a ladder, a one-way street).
   */
  oneWay?: boolean;
};

/**
 * What an observer learns about a row's cells it may not read.
 */
export type HiddenCells = "Omit" | "Count" | "Placeholder";

export type IReadOnlyDictionaryStringJsonElement = {
  [k: string]: unknown;
};

export type Int32List = number[];

export type Int64List = number[];

export type IntentSource = "Live" | "Idle" | Shape;

export type LatticeTopology = LatticeTopologyGrid | LatticeTopologyRing | LatticeTopologyHex | LatticeTopologyBox | LatticeTopologyGraph | LatticeTopologyTiling | WorldFieldTopology | null;

export type LatticeTopologyBox = {
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
  directions?: (TopologyDirection | null)[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: (TopologyElementAlias | null)[] | null;
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
};

export type LatticeTopologyGraph = {
  $type?: "graph";
  /**
   * The cells, in ordinal order.
   */
  cells: (GraphCell | null)[];
  /**
   * The direction slots, each naming its opposite (possibly itself).
   */
  directions: (GraphDirection | null)[];
  /**
   * The edges.
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
};

export type LatticeTopologyGrid = {
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
  directions?: (TopologyDirection | null)[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: (TopologyElementAlias | null)[] | null;
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
};

export type LatticeTopologyHex = {
  $type?: "hex";
  /**
   * The axial hexagon radius.
   */
  radius: number;
  /**
   * See Directions.
   */
  directions?: (TopologyDirection | null)[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: (TopologyElementAlias | null)[] | null;
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
};

export type LatticeTopologyRing = {
  $type?: "ring";
  /**
   * The cycle length.
   */
  width: number;
  /**
   * See Directions.
   */
  directions?: (TopologyDirection | null)[] | null;
  /**
   * See ElementAliases.
   */
  elementAliases?: (TopologyElementAlias | null)[] | null;
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
};

export type LatticeTopologyTiling = {
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
};

export type MemberRefKind = "Local" | "Verified";

export type MotionMoveFrame = "Heading" | "World";

export type MotionScalarEnvelope = {
  /**
   * The least admitted value (inclusive).
   */
  min?: number;
  /**
   * The greatest admitted value (inclusive) — WorldDefinitionValidator refuses Max < Min by name. Equal to Min pins the scalar outright regardless of what a profile requests.
   */
  max?: number;
};

export type MutationKindMask = string;

/**
 * A presentation fact an overlay element's OverlayPredicate reads. Every fact is per local seat and never enters the simulation; a world-scope element (a hud.panels row) reads a fact as true when it holds for any joined local seat.
 */
export type OverlayFact = "SeatInput" | "PointerMotion" | "WheelOpen" | "ConsoleOpen" | "SeatCameraApplication";

/**
 * An overlay element's visibility condition — the presentation twin of ActionPredicate, over OverlayFacts. Absent on an element means always visible.
 */
export type OverlayPredicate = OverlayPredicateNow | OverlayPredicateRecently | OverlayPredicateAll | OverlayPredicateAny | OverlayPredicateNot | OverlayPredicateSpeaking | OverlayPredicateNear | OverlayPredicateState | null;

/**
 * Every predicate holds.
 */
export type OverlayPredicateAll = {
  $type?: "all";
  predicates: OverlayPredicateListNonNullable;
};

/**
 * At least one predicate holds.
 */
export type OverlayPredicateAny = {
  $type?: "any";
  predicates: OverlayPredicateList;
};

export type OverlayPredicateList = (OverlayPredicateNullable | null)[];

export type OverlayPredicateListNonNullable = (OverlayPredicateNullable | null)[];

/**
 * The subject is within Distance world units of Of (the enclosing seat's avatar when null), read off the presentation poses each frame; an unresolvable subject is infinitely far.
 */
export type OverlayPredicateNear = {
  $type?: "near";
  subject: OverlaySubject;
  distance: number;
  of?: OverlaySubjectNullable | null;
};

/**
 * The predicate does not hold.
 */
export type OverlayPredicateNot = {
  $type?: "not";
  predicate: OverlayPredicate;
};

/**
 * The fact holds this frame.
 */
export type OverlayPredicateNow = {
  $type?: "now";
  fact: OverlayFact;
};

/**
 * An overlay element's visibility condition — the presentation twin of ActionPredicate, over OverlayFacts. Absent on an element means always visible.
 */
export type OverlayPredicateNullable = OverlayPredicateNow | OverlayPredicateRecently | OverlayPredicateAll | OverlayPredicateAny | OverlayPredicateNot | OverlayPredicateSpeaking | OverlayPredicateNear | OverlayPredicateState | null;

/**
 * The fact held within the last WindowSeconds.
 */
export type OverlayPredicateRecently = {
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
};

/**
 * The subject spoke within the last WindowSeconds — a chat line, a dialogue line, a live voice: every speech path stamps the same presentation clock, so the predicate reads one fact whatever produced it. Presence eases across FadeSeconds after the window like Recently.
 */
export type OverlayPredicateSpeaking = {
  $type?: "speaking";
  subject: OverlaySubject;
  windowSeconds: number;
  fadeSeconds?: number;
};

/**
 * A state cell compares to a literal: Binding is state.<row>[.<key>], and exactly one of Value (numeric rows) or Text (text rows, compared ordinally; only Equal/NotEqual) is authored. The same cell a bar's layoutCell or a wheel sector writes.
 */
export type OverlayPredicateState = {
  $type?: "state";
  binding: string;
  comparison?: ExpressionComparison;
  value?: number | null;
  text?: string | null;
};

/**
 * Who a subject-bearing presentation predicate is about — a seat's avatar, a placement, an entity, or a quantifier over seats or speakers. Presentation-only: a subject resolves to a body through the seat's perceived body (so possession follows) and never enters the simulation.
 */
export type OverlaySubject = OverlaySubjectSeat | OverlaySubjectPlacement | OverlaySubjectEntity | OverlaySubjectAnySeat | OverlaySubjectRecentSpeaker | null;

/**
 * Any joined local seat's avatar — the predicate holds when it holds for at least one.
 */
export type OverlaySubjectAnySeat = {
  $type?: "anySeat";
};

/**
 * A population entity by 0-based index.
 */
export type OverlaySubjectEntity = {
  $type?: "entity";
  index: number;
};

/**
 * Who a subject-bearing presentation predicate is about — a seat's avatar, a placement, an entity, or a quantifier over seats or speakers. Presentation-only: a subject resolves to a body through the seat's perceived body (so possession follows) and never enters the simulation.
 */
export type OverlaySubjectNullable = OverlaySubjectSeat | OverlaySubjectPlacement | OverlaySubjectEntity | OverlaySubjectAnySeat | OverlaySubjectRecentSpeaker | null;

/**
 * A placement instance, optionally one of its shapes.
 */
export type OverlaySubjectPlacement = {
  $type?: "placement";
  placementId: string;
  shapeId?: number | null;
};

/**
 * The body that most recently spoke (see Speaking); nothing until something has.
 */
export type OverlaySubjectRecentSpeaker = {
  $type?: "recentSpeaker";
};

/**
 * A local seat's avatar — null is the enclosing seat scope (the seat whose panel, bar, or camera is being evaluated), an explicit number is 1-based like Camera.Seat.
 */
export type OverlaySubjectSeat = {
  $type?: "seat";
  number?: number | null;
};

export type OwnershipEscrow = {
  /**
   * The principal that placed the subject into escrow — the sole reclaim beneficiary.
   */
  offerer?: Principal;
  /**
   * The principal named to accept the subject — the sole accept beneficiary. Never equal to Offerer (refused by name — an offer to oneself is not a trade).
   */
  recipient?: Principal;
  /**
   * The server tick at or after which WorldMutation.SettleOwnership's reclaim admits — the same tick unit EpochTick already rides. Before this tick, only an accept by Recipient can resolve the escrow.
   */
  deadlineTick?: number;
};

export type OwnershipOwner = {
  /**
   * Whether the owner is a bare principal, a group, or an escrow row.
   */
  kind?: OwnershipOwnerKind;
  /**
   * The owning principal for Principal; null otherwise. A principal is never a group — a group owner is spelled through GroupId, so the two branches never overlap.
   */
  principal?: Principal | null;
  /**
   * The owning group's id for Group; null otherwise.
   */
  groupId?: string | null;
  /**
   * The escrow payload for Escrow; null otherwise.
   */
  escrow?: OwnershipEscrow | null;
};

export type OwnershipOwnerKind = "Principal" | "Group" | "Escrow";

export type OwnershipSubject = {
  /**
   * The subject flavor.
   */
  kind?: OwnershipSubjectKind;
  /**
   * The subject's stable id — a group id for Group.
   */
  id?: string;
};

export type OwnershipSubjectKind = "Group";

/**
 * The closed pattern vocabulary over a row's cell values, matched against the whole word. Complement and intersection are first-class, so "no two adjacent kings" and "holds a 2 and a 5" are single patterns rather than rule arithmetic.
 */
export type PatternNode = PatternNodeSymbol | PatternNodeAnySymbol | PatternNodeExcept | PatternNodeNothing | PatternNodeNone | PatternNodeSequence | PatternNodeChoice | PatternNodeBoth | PatternNodeComplement | PatternNodeOptional | PatternNodeStar | PatternNodePlus | PatternNodeRepeat | null;

/**
 * One token of any value, named symbols and the unnamed remainder alike.
 */
export type PatternNodeAnySymbol = {
  $type?: "any";
};

/**
 * Every item at once: the word is in each item's language.
 */
export type PatternNodeBoth = {
  $type?: "all";
  items: PatternNodeList;
};

/**
 * Any one of the items.
 */
export type PatternNodeChoice = {
  $type?: "choice";
  items: PatternNodeList;
};

/**
 * Every word the item does not match.
 */
export type PatternNodeComplement = {
  $type?: "not";
  item: PatternNode;
};

/**
 * One token whose value falls outside the named symbol.
 */
export type PatternNodeExcept = {
  $type?: "except";
  name: string;
};

export type PatternNodeList = (PatternNodeNullable | null)[];

export type PatternNodeListNonNullable = (PatternNodeNullable | null)[];

/**
 * The empty language: no word at all, the zero of choice and the annihilator of sequence.
 */
export type PatternNodeNone = {
  $type?: "none";
};

/**
 * The empty word.
 */
export type PatternNodeNothing = {
  $type?: "empty";
};

/**
 * The closed pattern vocabulary over a row's cell values, matched against the whole word. Complement and intersection are first-class, so "no two adjacent kings" and "holds a 2 and a 5" are single patterns rather than rule arithmetic.
 */
export type PatternNodeNullable = PatternNodeSymbol | PatternNodeAnySymbol | PatternNodeExcept | PatternNodeNothing | PatternNodeNone | PatternNodeSequence | PatternNodeChoice | PatternNodeBoth | PatternNodeComplement | PatternNodeOptional | PatternNodeStar | PatternNodePlus | PatternNodeRepeat | null;

/**
 * The item or nothing.
 */
export type PatternNodeOptional = {
  $type?: "optional";
  item: PatternNode;
};

/**
 * The item one or more times.
 */
export type PatternNodePlus = {
  $type?: "plus";
  item: PatternNode;
};

/**
 * The item between Min and Max times.
 */
export type PatternNodeRepeat = {
  $type?: "repeat";
  item: PatternNode;
  min: number;
  max: number;
};

/**
 * The items matched one after another.
 */
export type PatternNodeSequence = {
  $type?: "sequence";
  items: PatternNodeListNonNullable;
};

/**
 * The item zero or more times.
 */
export type PatternNodeStar = {
  $type?: "star";
  item: PatternNode;
};

/**
 * One token whose value falls in the named symbol.
 */
export type PatternNodeSymbol = {
  $type?: "symbol";
  name: string;
};

export type PatternRow = {
  /**
   * The pattern name a rule references.
   */
  name: CellName;
  /**
   * The numeric kind of the values the word is read from: Int or Fixed.
   */
  kind: CellKindNonNullable;
  /**
   * The alphabet, 1..MaxSymbols named value ranges.
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
   * The machine-state budget the compile refuses past, 1..MaxStates.
   */
  maxStates?: number;
  /**
   * For a zone source, an expression in the pattern's kind evaluated once per token in pile order, where a state token keyed $token reads that token's cell of a row keyed over the zone's token domain: the word over a tuple of attributes (suit * 16 + rank) rather than one. Exclusive with Attribute.
   */
  value?: ExpressionProgramNonNullable2;
};

export type PatternSymbol = {
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
};

export type PresentMode = "Vsync" | "Mailbox" | "Immediate" | "Adaptive";

export type Principal = string;

export type RuleGroupDeclaration = {
  /**
   * The group's stable name — unique within the section, and never a rule's name.
   */
  name: CellName;
  /**
   * Fixpoint or staged.
   */
  shape: RuleGroupShape;
  /**
   * The members, in authored order; for a staged group this is the step sequence, and the last step is terminal.
   */
  steps: (RuleGroupStep | null)[];
  /**
   * The pass ceiling a fixpoint group breaches into a counted refusal, or null for DefaultPasses.
   */
  passes?: number | null;
  /**
   * The gate that arms the group, or null for always.
   */
  trigger?: ActionPredicateNullable2 | null;
  /**
   * The retained turn rows and depth, or null when the group has no undo history.
   */
  undo?: RuleGroupUndo | null;
};

export type RuleGroupShape = "Fixpoint" | "Staged";

export type RuleGroupStep = {
  /**
   * The member rule's name.
   */
  rule: CellName;
  /**
   * What a refusal does to the cursor.
   */
  onRefusal?: RuleGroupStepPolicy;
};

export type RuleGroupStepPolicy = "Stall" | "Skip";

export type RuleGroupUndo = {
  rows: CellName[];
  depth: number;
};

export type RuleLocal = {
  /**
   * The local's name — the token after LocalPrefix.
   */
  name: CellName;
  /**
   * The postfix expression whose evaluated result the local holds.
   */
  expression: ExpressionProgramNonNullable2;
  /**
   * The value's cell kind, Int or Fixed, or null for the kind the rule compiler infers: Int when the expression compiles as one, otherwise Fixed. A comparison or sign may consume Fixed operands yet leave an Int result, which is the inferred binding kind.
   */
  kind?: CellKind | null;
};

export type RulePoolIteration = {
  /**
   * The declared pool to visit.
   */
  pool: StateChannelRefNonNullable;
  /**
   * The instance name available to the rule's locals, gate, and effects.
   */
  binding: CellName;
};

export type SafeName = string;

export type SearchMethod = "Negamax" | "MonteCarlo";

export type SeatActivationPolicy = "Eager" | "OnDemand";

export type ShadowTier = "Off" | "Low" | "Medium" | "High";

export type Shape = {
  $type: "producer";
  name: string;
};

export type ShapeNonNullable = "Add" | "Subtract" | "Multiply" | "Divide" | "Minimum" | "Maximum" | "Clamp" | "Remainder" | "BitAnd" | "BitOr" | "BitXor" | "BitNot" | "ShiftLeft" | "ShiftRight" | "ShiftRightLogical" | "Equal" | "NotEqual" | "Less" | "LessOrEqual" | "Greater" | "GreaterOrEqual" | "Select" | "SetBitCount" | "LeadingZeroCount" | "TrailingZeroCount" | "LowestSetBit" | "ClearLowestSetBit" | "RotateLeft" | "RotateRight" | "ByteSwap" | "ReverseBits" | "Negate" | "Absolute" | "ParallelBitExtract" | "ParallelBitDeposit" | "BitField" | "BitInsert" | "Sign" | "Pair" | "PairX" | "PairY" | "PairSwap" | "PairMaximum" | "PairMinimum" | "PairSum" | "PairDifference" | "PairTranslate" | "PairScale" | "MortonIndex" | "MortonX" | "MortonY" | "HilbertIndex" | "HilbertX" | "HilbertY" | "HexIndex" | "HexQ" | "HexR" | "HexRadius" | "HexEuclideanSquared" | "HexDistance" | "HexNeighbor" | "HexRotate" | "HexMirror" | "HexSwap" | "HexAdd" | "HexSubtract" | "HexMultiply" | "HexScale" | "HexTranslate" | "Layer" | "LayerOffset" | "LayerStart" | "LayerSize" | "SquareRoot" | "Sine" | "Cosine" | "SquareIndex" | "SquareX" | "SquareY" | "SquareRadius" | "SquareLength" | "SquareEuclideanSquared" | "SquareDistance" | "SquareChebyshev" | "SquareNeighbor" | "SquareRotate" | "SquareMirror" | "SquareSwap" | "SquareAdd" | "SquareSubtract" | "SquareMultiply" | "SquareScale" | "SquareTranslate" | "GreatestCommonDivisor" | "LeastCommonMultiple" | "FloorModulo" | "CycleForward" | "CycleDistance" | "SmallestMissing" | "IsPrime" | "PrimeAt" | "BinomialCoefficient" | "Factorial" | "SubsetRank" | "SubsetAt" | "SubsetMember" | "ArrangementRank" | "ArrangementAt" | "ArrangementMember" | "Floor" | "Ceiling" | "Round" | "ReplicationMask" | "RepeatBits" | "IsAbsent" | "Coalesce" | "Member";

export type ShapeNonNullable10 = string | number | ShapeNonNullable8 | ShapeNonNullable9;

export type ShapeNonNullable11 = {
  channel: string;
  arguments?: ShapeNonNullable10[];
};

export type ShapeNonNullable12 = {
  binding: string;
  field: string;
};

export type ShapeNonNullable13 = {
  pool: string;
  slot: number;
  field: string;
};

export type ShapeNonNullable14 = {
  op: "Operand";
  name: StateChannelRef;
  key?: StateChannelRef;
};

export type ShapeNonNullable15 = {
  $type: "cell";
  name: StateChannelRef;
  key?: StateChannelRef;
};

export type ShapeNonNullable16 = {
  $type: "literal";
  value: string;
};

export type ShapeNonNullable17 = {
  $type: "embed";
  text: string;
  space?: string;
};

export type ShapeNonNullable18 = ShapeNonNullable15 | ShapeNonNullable16 | ShapeNonNullable17 | null;

export type ShapeNonNullable19 = {
  op: "Dot" | "Similarity" | "Identical";
  left: ShapeNonNullable18;
  right: ShapeNonNullable18;
};

export type ShapeNonNullable2 = {
  op: ShapeNonNullable;
};

export type ShapeNonNullable20 = ShapeNonNullable2 | ShapeNonNullable3 | ShapeNonNullable4 | ShapeNonNullable5 | ShapeNonNullable6 | ShapeNonNullable7 | ShapeNonNullable14 | ShapeNonNullable19 | null;

export type ShapeNonNullable21 = {
  name?: string;
  arity?: number;
  instructions: ShapeNonNullable20[];
};

export type ShapeNonNullable22 = {
  zone: ExpressionProgram;
};

export type ShapeNonNullable23 = {
  expression: ExpressionProgram;
};

export type ShapeNonNullable24 = string | number | ShapeNonNullable22 | ShapeNonNullable23;

export type ShapeNonNullable25 = {
  channel: string;
  arguments?: ShapeNonNullable24[];
};

export type ShapeNonNullable26 = {
  op: "Operand";
  name: unknown;
  key?: unknown;
};

export type ShapeNonNullable27 = {
  $type: "cell";
  name: unknown;
  key?: unknown;
};

export type ShapeNonNullable28 = ShapeNonNullable27 | ShapeNonNullable16 | ShapeNonNullable17 | null;

export type ShapeNonNullable29 = {
  op: "Dot" | "Similarity" | "Identical";
  left: ShapeNonNullable28;
  right: ShapeNonNullable28;
};

export type ShapeNonNullable3 = {
  op: "Argument";
  index: number;
};

export type ShapeNonNullable30 = ShapeNonNullable2 | ShapeNonNullable3 | ShapeNonNullable4 | ShapeNonNullable5 | ShapeNonNullable6 | ShapeNonNullable7 | ShapeNonNullable26 | ShapeNonNullable29 | null;

export type ShapeNonNullable31 = {
  name?: string;
  arity?: number;
  instructions: ShapeNonNullable30[];
};

export type ShapeNonNullable32 = {
  zone: ExpressionProgramNonNullable;
};

export type ShapeNonNullable33 = {
  expression: ExpressionProgramNonNullable;
};

export type ShapeNonNullable34 = string | number | ShapeNonNullable32 | ShapeNonNullable33;

export type ShapeNonNullable35 = {
  channel: string;
  arguments?: ShapeNonNullable34[];
};

export type ShapeNonNullable36 = {
  op: "Operand";
  name: StateChannelRefNonNullable2;
  key?: StateChannelRefNonNullable2;
};

export type ShapeNonNullable37 = {
  $type: "cell";
  name: StateChannelRefNonNullable2;
  key?: StateChannelRefNonNullable2;
};

export type ShapeNonNullable38 = ShapeNonNullable37 | ShapeNonNullable16 | ShapeNonNullable17 | null;

export type ShapeNonNullable39 = {
  op: "Dot" | "Similarity" | "Identical";
  left: ShapeNonNullable38;
  right: ShapeNonNullable38;
};

export type ShapeNonNullable4 = {
  op: "BoardShift" | "BoardRay" | "BoardImage";
  topology: string;
  index: string;
};

export type ShapeNonNullable40 = ShapeNonNullable2 | ShapeNonNullable3 | ShapeNonNullable4 | ShapeNonNullable5 | ShapeNonNullable6 | ShapeNonNullable7 | ShapeNonNullable36 | ShapeNonNullable39 | null;

export type ShapeNonNullable41 = {
  name?: string;
  arity?: number;
  instructions: ShapeNonNullable40[];
};

export type ShapeNonNullable42 = number | string | boolean;

export type ShapeNonNullable43 = {
  epochTick?: number;
  epochEngineTick?: number;
  y0?: string;
  v0?: string;
  substepTicks?: number;
};

export type ShapeNonNullable5 = {
  op: "Call";
  subprogram: number;
};

export type ShapeNonNullable6 = {
  op: "Constant";
  value: number;
};

export type ShapeNonNullable7 = {
  op: "All" | "Any" | "Count" | "Sum";
  family: string;
  binder: string;
  subprogram: number;
};

export type ShapeNonNullable8 = {
  zone: unknown;
};

export type ShapeNonNullable9 = {
  expression: unknown;
};

/**
 * One numeric key of a Sort, in its declared precedence order.
 */
export type SortKey = {
  /**
   * The target itself for own-value sorting, or an attribute row over the zone's token domain.
   */
  row: StateChannelRefNonNullable;
  /**
   * Whether the greatest value comes first under this key.
   */
  descending?: boolean;
};

export type SourceDestination = "Presentation" | "Simulation" | "Passthrough";

/**
 * A StateRow's continuous accumulation trait: the row's stored cell is a base value, and the read value advances with elapsed engine ticks at an exact per-second rational rate from the engine tick it was last explicitly set (EpochEngineTick). Used for regen, fractional accumulation, a day/night clock — anything that should move on its own between observations.
 */
export type StateAdvance = {
  /**
   * The per-second rate's signed numerator, in the row's own displayed unit (see this type's remarks). Negative accumulates downward (decay); zero is declared but inert.
   */
  perSecondNumerator: number;
  /**
   * The per-second rate's denominator. Refused at zero or below.
   */
  perSecondDenominator: number;
};

/**
 * A cell's own value-over-time timing state — the epoch(s), the second-order follower's sampled position and velocity, and a rotation's carried substep remainder — moved off the trait records (StateAdvance/StateDynamics/StateCycle, which keep only their authored parameters) onto the cell they time, so a key a write mints later starts its own clock from the tick it was created rather than sharing one baked into a trait every cell of the row would otherwise repeat.
 */
export type StateCellClock = {
  /**
   * The simulation tick this clock's rotation is measured from — the tick the cell's base value was last explicitly set, or its own behavior last changed. Read by StateCycle. A negative value is refused; in practice this can only be violated by an authored boot document, since every live settle rebases to the applying tick before validation sees it.
   */
  epochTick?: number;
  /**
   * The engine tick (TicksPerSecond per second) this clock's accumulation is measured from — the engine tick the cell's base value was last explicitly set, or its own behavior last changed. Read by StateAdvance alone; never derived from EpochTick at a simulation rate, since a live rate change must move neither. A negative value is refused, for the same reason EpochTick is.
   */
  epochEngineTick?: number;
  /**
   * A StateDynamics follower's position at EpochTick, as raw FixedQ4816 bits, independent of the carrying row's stored-value kind.
   */
  y0?: number;
  /**
   * A StateDynamics follower's velocity at EpochTick, per second, as raw FixedQ4816 bits.
   */
  v0?: number;
  /**
   * Elapsed ticks a StateCycle has already accumulated toward its next step at EpochTick; must be non-negative and less than the cycle's own ticksPerStep.
   */
  substepTicks?: number;
};

/**
 * A document's one reference to a state row, a cell key, a pool field, or a reserved channel. A live zone ($zones[…]) is a call whose one argument is a Zone.
 */
export type StateChannelRef = string | ShapeNonNullable11 | ShapeNonNullable12 | ShapeNonNullable13;

/**
 * A document's one reference to a state row, a cell key, a pool field, or a reserved channel. A live zone ($zones[…]) is a call whose one argument is a Zone.
 */
export type StateChannelRefNonNullable = string | ShapeNonNullable25 | ShapeNonNullable12 | ShapeNonNullable13;

/**
 * A document's one reference to a state row, a cell key, a pool field, or a reserved channel. A live zone ($zones[…]) is a call whose one argument is a Zone.
 */
export type StateChannelRefNonNullable2 = string | ShapeNonNullable35 | ShapeNonNullable12 | ShapeNonNullable13;

/**
 * A row's or cell's tick-indexed rotation trait: the value is a pure function of the server tick through a generator of the symmetry lattice's reflection group — Puck.Maths.SymmetryWord, the lattice's own thirty-step cycle when no Word is authored — raised to Power once per step. The generator's order is the loop's period, derived from the word rather than authored: a word of order twelve is a twelve-position dial, one of order twenty-four a day. Nothing accumulates and nothing is rebased: the mapping is tick-absolute for a fixed epoch/substep pair, so a replay, a reconnect, or a fresh read at any tick lands on the same bits.
 */
export type StateCycle = {
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
};

export type StateDomainCellsOf = {
  $type?: "cellsOf";
  /**
   * The state.lattices topology this row lies over.
   */
  topology: string;
  /**
   * The value a sparse (board) cell reads before it is ever written; meaningless for the dense physical-field case, whose unwritten value is the field trait's own initial instead.
   */
  empty?: number;
};

export type StateDomainKeys = {
  $type?: "keys";
};

export type StateDomainKeysOf = {
  $type?: "keysOf";
  /**
   * The row whose keys this row's own keys are drawn from.
   */
  row: CellName;
  /**
   * Whether cell order carries gameplay meaning (a pile) rather than being incidental.
   */
  ordered?: boolean;
};

export type StateDomainRing = {
  $type?: "ring";
  /**
   * How many pushes the ring keeps, from 1 to MaxCellsPerRow.
   */
  capacity: number;
  /**
   * The value read for an age older than the ring holds.
   */
  empty?: number;
};

export type StateDomainSlot = {
  $type?: "slot";
};

/**
 * A StateCell/StateRow's second-order easing trait: the STORED value stays the TRUTH (what rules, gates, and comparands read), while an eased read (StateReader) computes a second-order follower's current sample from the carrying cell's own Y0/ V0 at its EpochTick, chasing the stored value as its target — the closed-form counterpart to StateAdvance's linear accumulation, no per-tick work either. An explicit write REBASES: the cell's clock Y0/V0 become the eased value and velocity computed AT the write's own tick, and EpochTick becomes that tick — the same rebase discipline StateAdvance's own write rule follows, so a retune never jumps. A cell composed with this trait but no clock yet (never settled or explicitly written) reads its own stored value as the follower's position, at rest — see TryEvaluateDynamics.
 */
export type StateDynamics = {
  /**
   * The referenced dynamics row name; must resolve.
   */
  row: string;
};

export type StateEnum = {
  /**
   * The enum's stable name, unique within the section.
   */
  name: CellName;
  /**
   * The member names in value order; member i is the value i.
   */
  members: CellName[];
};

export type StateFamily = {
  /**
   * The family's stable name; never a row name of its own.
   */
  name: CellName;
  /**
   * How many member rows the family holds, 1..MaxRows. When Indices or Members is declared, this is that list's length.
   */
  size: number;
  /**
   * The family index each member carries, in member order, or null when the members are indexed 0..Size-1 with no gaps. A family index is what <family>[i] selects on, so an index the list omits is a gap that selects no row.
   */
  indices?: number[] | null;
  /**
   * The member rows' own names, in member order, or null when the members are named by the <name><index> convention.
   */
  members?: CellName[] | null;
};

/**
 * An authored stochastic source — the vocabulary for every randomness declaration in the document: a name generator, a dialogue line, a loot roll, a flat weighted draw, a multiset sample, a random census, and a drawn host backend all reduce to a source of this family, sampled at an authored moment into an authored site (see Draw).
 */
export type StateGenerator = {
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
  /**
   * The extended-generator facet (see GeneratorExtended), or null for the ordinary Pcg32XshRr generator every other source draws through.
   */
  extended?: GeneratorExtended | null;
};

export type StateGeneratorVariant2 = {
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
  contexts?: (GeneratorContextVariant2 | null)[] | null;
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
  weighted?: (GeneratorWeightedNumeric | null)[] | null;
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
  /**
   * The extended-generator facet (see GeneratorExtended), or null for the ordinary Pcg32XshRr generator every other source draws through.
   */
  extended?: GeneratorExtended | null;
};

export type StateOverflow = "Refuse" | "Saturate";

export type StatePairPool = {
  name: CellName;
  record: CellName;
  leftPool: CellName;
  rightPool: CellName;
  maxLive: number;
  directed?: boolean;
  allowSelf?: boolean;
  snapshot?: StatePoolSnapshot | null;
  /**
   * Gets the immutable statically live pair slots.
   */
  initial?: (StatePoolSeed | null)[] | null;
};

export type StatePool = {
  name: CellName;
  record: CellName;
  capacity: number;
  snapshot?: StatePoolSnapshot | null;
  /**
   * Gets the immutable statically live instances.
   */
  initial?: (StatePoolSeed | null)[] | null;
};

export type StatePoolField = {
  name: CellName;
  kind?: CellKindNonNullable;
  min?: number | null;
  max?: number | null;
  overflow?: StateOverflow;
  enum?: CellName | null;
  space?: CellName | null;
  dimensions?: number | null;
  advance?: StateAdvance | null;
  /**
   * Gets an immutable snapshot of the declared default.
   */
  default?: CellValue | null;
};

/**
 * One statically live pool slot and its field overrides.
 */
export type StatePoolSeed = {
  slot: number;
  /**
   * Gets the immutable field overrides.
   */
  values?: (StatePoolValue | null)[] | null;
};

export type StatePoolSeedList = (StatePoolSeed | null)[];

/**
 * The complete persistent allocator continuation of one pool: every slot generation, including dead slots, plus the currently live instances and their field values.
 */
export type StatePoolSnapshot = {
  /**
   * Gets one generation per pool slot.
   */
  generations: Int64List;
  /**
   * Gets the immutable live instances in ascending slot order.
   */
  live?: StatePoolSeedList | null;
};

/**
 * One field override in a statically seeded pool instance.
 */
export type StatePoolValue = {
  field: CellName;
  clock?: StateCellClock | null;
  /**
   * Gets an immutable snapshot of the field value.
   */
  value: CellValue | null;
};

export type StateRecord = {
  name: CellName;
  /**
   * Gets the immutable field declarations in authored order.
   */
  fields?: (StatePoolField | null)[] | null;
};

export type StateSpace = {
  /**
   * The stable space name, unique within the section.
   */
  name: CellName;
  /**
   * The model name, non-empty and at most 128 characters.
   */
  model: string;
  /**
   * The model revision, non-empty and at most 128 characters.
   */
  revision: string;
  /**
   * The component dimension count, in [8, 1024].
   */
  dimensions: number;
};

/**
 * The closed set of atomic state transforms. Each folds one candidate document and journals once.
 */
export type StateTransform = StateTransformTransfer | StateTransformSetRay | StateTransformPushRay | StateTransformShuffle | StateTransformSort | StateTransformWriteSet | StateTransformBoardCombine | StateTransformArrange | StateTransformClearEnclosed | StateTransformObserve | StateTransformMix | StateTransformMean | StateTransformNearest | StateTransformRemember | null;

/**
 * Reorders an ordered zone of at most 20 tokens into the arrangement at a Lehmer rank read from an integer cell — the inverse of $reduce:arrangementRank: rank 0 is the token domain's own order, and a rank at or past k! refuses.
 */
export type StateTransformArrange = {
  $type?: "arrange";
  /**
   * The ordered zone.
   */
  row: StateChannelRefNonNullable;
  /**
   * The integer row the rank is read from.
   */
  from: StateChannelRefNonNullable;
  /**
   * The cell of that row, or null for its slot cell.
   */
  fromKey?: StateChannelRefNonNullable;
};

/**
 * Rewrites a board from one or two boards over the same topology, cell by cell, in one journaled mutation: the set algebra $board:mask and the bit operators give a board of at most 64 cells, for a board of any size. A cell is a member when its value is not its board's empty; every member of the result is written as Value and every other cell as the board's empty.
 */
export type StateTransformBoardCombine = {
  $type?: "boardCombine";
  /**
   * The board written.
   */
  row: StateChannelRefNonNullable;
  /**
   * What is written.
   */
  operation: BoardCombineOp;
  /**
   * The first source board, over the same topology; absent for Fill and Clear.
   */
  left?: StateChannelRefNonNullable;
  /**
   * The second source board for the two-board operations.
   */
  right?: StateChannelRefNonNullable;
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
};

/**
 * Clears every group of cells valued Lower..Upper beside the cell From names that has no empty cell beside it, writing the board's empty value over their members. The write-path twin of $board:enclosedAt, applied after the value lands.
 */
export type StateTransformClearEnclosed = {
  $type?: "clearEnclosed";
  /**
   * The board.
   */
  row: StateChannelRefNonNullable;
  /**
   * The placed value's cell: a literal cell key, or any dynamic key spelling a write accepts.
   */
  from: StateChannelRefNonNullable;
  /**
   * The enclosed range's inclusive low end; the board's empty value lies outside it.
   */
  lower: number;
  /**
   * The enclosed range's inclusive high end.
   */
  upper: number;
};

/**
 * Writes the normalized sum of every candidate cell of a table.
 */
export type StateTransformMean = {
  $type?: "mean";
  /**
   * The source vector table.
   */
  from: string;
  /**
   * The target vector cell or slot.
   */
  into: string;
  /**
   * Optional boolean filter row.
   */
  where?: StateChannelRefNonNullable;
};

/**
 * Writes the normalized sum of 1 to MaxMixTerms weighted vector terms.
 */
export type StateTransformMix = {
  $type?: "mix";
  /**
   * The target vector cell or slot.
   */
  into: string;
  /**
   * The weighted vector terms.
   */
  terms: (VectorTerm | null)[];
};

/**
 * Writes the closest cells of a table.
 */
export type StateTransformNearest = {
  $type?: "nearest";
  /**
   * The source vector table.
   */
  from: string;
  /**
   * The query vector cell, slot, or literal.
   */
  query: string;
  /**
   * The destination table or slot.
   */
  into: StateChannelRefNonNullable;
  /**
   * How many nearest results to recall.
   */
  k: number;
  /**
   * Optional threshold score.
   */
  threshold?: string | null;
  /**
   * Optional boolean row filter.
   */
  where?: StateChannelRefNonNullable;
  /**
   * Optional key to exclude from candidates.
   */
  exclude?: StateChannelRefNonNullable;
  /**
   * Whether to rank lower scores first.
   */
  farthest?: boolean;
};

/**
 * Refreshes a knowledge board from its declared source and visibility mask; authority only.
 */
export type StateTransformObserve = {
  $type?: "observe";
  /**
   * A row name or literal key as plain text, or an object identifying a reserved channel, lexical pool field, or static pool field.
   */
  row: StateChannelRefNonNullable;
};

/**
 * Moves one live origin token and the outward tokens selected by PushPattern one topology cell. A token selected by StopPattern blocks the whole move, including when it is also selected by PushPattern. Other occupants remain in place. The mover is the first run symbol; pushed tokens follow in ascending pool-slot order within each cell, and the first cell without a pushed token contributes its passable occupants, or Empty when physically empty, as the terminator. The whole word must match Pattern, otherwise no token moves.
 */
export type StateTransformPushRay = {
  $type?: "pushRay";
  /**
   * The sole pool whose live instances participate.
   */
  pool: CellName;
  /**
   * The integer field holding each token's topology cell ordinal.
   */
  cell: CellName;
  /**
   * The scalar field supplying each token's pattern symbol.
   */
  value: CellName;
  /**
   * The selected pool's live Cell field, which identifies the one mover; other tokens sharing its origin remain in place.
   */
  from: StateChannelRefNonNullable;
  /**
   * The lattice the cell ordinals address.
   */
  topology: CellName;
  /**
   * A direction in that topology.
   */
  direction: CellName;
  /**
   * The pattern matched against the movable run followed by the passable or empty terminator.
   */
  pattern: StateChannelRefNonNullable;
  /**
   * The integer pattern selecting occupants that join the moving run.
   */
  pushPattern: StateChannelRefNonNullable;
  /**
   * The integer pattern selecting occupants that block the move.
   */
  stopPattern: StateChannelRefNonNullable;
  /**
   * The explicit symbol contributed by the first unoccupied cell.
   */
  empty: number;
};

/**
 * Stores a vector unless the table already holds a near-duplicate.
 */
export type StateTransformRemember = {
  $type?: "remember";
  /**
   * The destination vector table.
   */
  into: StateChannelRefNonNullable;
  /**
   * The key to write.
   */
  key: StateChannelRefNonNullable;
  /**
   * The source vector.
   */
  from: string;
  /**
   * Cosine similarity threshold in [0, 1].
   */
  unlessWithin: string;
};

/**
 * Writes the longest run a patterns row accepts, walked from the origin outward: the same prefix semantics as the $match operand's prefix facet, landed back on the board instead of read as a fact. Refuses when the accepted prefix is empty, so an author closes a run with the required symbol (a bracket capture is plus(through) . symbol(until)) rather than an unbounded one running off the board.
 */
export type StateTransformSetRay = {
  $type?: "setRay";
  /**
   * The board row.
   */
  row: StateChannelRefNonNullable;
  /**
   * The origin key, excluded from the read word and the write.
   */
  from: StateChannelRefNonNullable;
  /**
   * A direction in the board's topology.
   */
  direction: CellName;
  /**
   * A patterns row over the board's own raw values (kind Int).
   */
  pattern: StateChannelRefNonNullable;
  /**
   * The replacement value written to every cell of the accepted prefix.
   */
  value: number;
};

/**
 * Reorders a row's cells by value in place by one Fisher-Yates pass over the named redrawable integer streamDraw site: n cells consume n - 1 samples, so the site's cursor advances by exactly that and a replay reproduces the permutation.
 */
export type StateTransformShuffle = {
  $type?: "shuffle";
  /**
   * Any ordered zone or keyed row.
   */
  row: StateChannelRefNonNullable;
  /**
   * The integer streamDraw site supplying the samples.
   */
  draw: StateChannelRefNonNullable;
};

/**
 * Stably reorders a row by numeric keys in precedence order. Naming the target row as the sole key orders its own values; otherwise the target is an ordered zone and the keys are token attributes.
 */
export type StateTransformSort = {
  $type?: "sort";
  /**
   * A keyed or ordered numeric row for own-value sorting, or an ordered zone for attributes.
   */
  row: StateChannelRefNonNullable;
  /**
   * One or more distinct numeric key rows, each carrying its own direction. Attribute rows must share the zone's token domain. A sole key naming the target reads its own stored values.
   */
  by: (SortKey | null)[];
};

/**
 * Moves selected tokens, preserving identity. A random draw advances only when the whole transfer commits.
 */
export type StateTransformTransfer = {
  $type?: "transfer";
  /**
   * The source zone — a zone name, or in an authored rule a live zone ($zones[<index>], an entry of the rule's Zones table selected before each firing; an index selecting none refuses the transfer by name).
   */
  from: StateChannelRefNonNullable;
  /**
   * The destination zone, on the same terms as From.
   */
  to: StateChannelRefNonNullable;
  /**
   * The source selector.
   */
  selector?: ZoneSelector;
  /**
   * The token key for key or slice selection. In an authored rule this may be a dynamic key, resolved from the active store before each transfer; direct mutations carry the resolved literal.
   */
  key?: StateChannelRefNonNullable;
  /**
   * Insert at the first position rather than the last.
   */
  insertFirst?: boolean;
  /**
   * A streamDraw site for random selection; absent for other selectors.
   */
  draw?: StateChannelRefNonNullable;
  /**
   * How many tokens move in this one transfer, 1..MaxTransferCount, each selected afresh from what remains (a five-card deal is one mutation); a key selection moves exactly one, and a slice selection moves the keyed token's whole tail (its count is 1).
   */
  count?: number;
};

/**
 * Writes one value into every cell of a board whose bit is set in a cell-set mask read from a state cell: the way a set built from $board:mask and the and/or/xor/not/shift/image expression ops lands back on the board. The one board-writing form for every topology of at most 64 cells; a wider topology has no transform of its own and composes through per-cell rules instead.
 */
export type StateTransformWriteSet = {
  $type?: "writeSet";
  /**
   * The board row, over a topology of at most 64 cells.
   */
  row: StateChannelRefNonNullable;
  /**
   * The integer row the cell-set mask is read from.
   */
  set: StateChannelRefNonNullable;
  /**
   * The cell of that row, or null for its slot cell: a literal cell key, or any dynamic key spelling a write accepts (a binding token, a registered key family, an expression key, or a $cell:<row>:<key> indirection).
   */
  setKey?: StateChannelRefNonNullable;
  /**
   * The value written to every masked cell.
   */
  value?: number;
};

/**
 * Opt-in observation policy. Null readers means public; an empty list means authority only. Row and cell policies intersect. Replica-tier authorities remain fully trusted.
 */
export type StateVisibility = {
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
};

export type StringList = (string | null)[];

export type SurfaceFormat = "r8g8b8a8" | "b8g8r8a8";

export type TableRow = {
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
};

export type TextFontCatalogDefinition = {
  defaultFont: string;
  fonts: (TextFontDefinition | null)[];
};

export type TextFontDefinition = {
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
};

export type TilingFamily = "Triangular" | "Kagome" | "TruncatedSquare" | "Rhombitrihexagonal" | "TruncatedHexagonal" | "ElongatedTriangular" | "TruncatedTrihexagonal" | "Penrose";

/**
 * One authored direction of a discrete LatticeTopology: the (X, Y, Z) cell step a neighbour walk, ray, or leaper offset takes, and the case-sensitive token a rule or $board:/$match: channel names it by. X/Y are the topology's own planar axes (a Grid's column/row, a Hex's q/r); Z is a Box's layer step and must be zero on every other kind.
 */
export type TopologyDirection = {
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
};

/**
 * A friendlier name for one point-group element, resolved by Element alongside its canonical signed-axis spelling (ElementName always answers the canonical form).
 */
export type TopologyElementAlias = {
  /**
   * The alias token a rule or console verb may use instead of Element.
   */
  name: string;
  /**
   * The canonical element name (a ElementName value) this alias resolves to.
   */
  element: string;
};

export type TopologyWrap = "None" | "X" | "Y" | "Both";

/**
 * One term of a Mix: a vector operand with an integer weight.
 */
export type VectorTerm = {
  /**
   * The vector operand.
   */
  from: string;
  /**
   * The integer weight in [-1000, 1000] and non-zero.
   */
  weight: number;
};

export type WorldAddonMemoryWatch = {
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
};

export type WorldAddonRow = {
  /**
   * The addon's identifying name — unique within the definition; used by console verbs and logging.
   */
  name: string;
  /**
   * The WASM module file path, resolved beside the document that authors the row (WorldDocumentPaths); existence and hash verification are the run path's job.
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
};

export type WorldAdjacency = {
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
  /**
   * An authored non-negative minimum ownership deadband in world units. The runtime still derives its contact/vertical safety threshold and uses the larger value; authoring this can widen a seam but never weaken its safety floor. Reciprocal rows must agree.
   */
  hysteresis?: number;
};

export type WorldAdjacencyBoundary = {
  /**
   * The boundary rectangle's center in this world's coordinates.
   */
  center: DocumentVector3;
  /**
   * The outward heading, in degrees (0 = +Z, 90 = +X). It also fixes the rectangle's own right axis, which is the yaw's horizontal right whatever the pitch — so on a face lying flat, where the heading contributes nothing to the outward direction, this is the rectangle's roll about the vertical and the arrival turn it gives the seam is still counterpartYaw - thisYaw - 180 (MapVector). An untwisted seam therefore authors its two yaws 180 degrees apart whether it stands as a wall or lies flat as a floor.
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
};

export type WorldAdjacencyUnavailable = "Closed";

export type WorldAdmissionEntry = {
  /**
   * The trusted key's own id domain — a lowercase-hex SHA-256 fingerprint (64 characters). For Vouches this must be PublicKey's own fingerprint (a root is self-certifying). For SignsDirectly it names the domain namespace this individual is pinned under, which need not equal their own key's hash. For FederatedAuthority it is not a key id at all: it names the authenticated source-authority namespace, or AnyAuthority for any authority that completes the federation handshake. For OAuth it is the exact HTTPS issuer.
   */
  domain: string;
  /**
   * The platform user id this entry pins, required for SignsDirectly and refused for Vouches (a root vouches for every subject its two-hop chain resolves, never one named here). OAuth requires an exact subject within its issuer, never a wildcard.
   */
  subject: string | null;
  /**
   * The trusted identity proof this row accepts.
   */
  mode: WorldAdmissionTrustMode;
  /**
   * Exactly ecdsa-p256-sha256, the only signing algorithm enabled by the admission door's mandatory attestation-v1-base profile. Sealing algorithms and optional signing extensions are refused by document validation. OAuth and federated-authority rows require an empty string.
   */
  algorithm: string;
  /**
   * The pinned key's actual SubjectPublicKeyInfo bytes, base64-encoded — carried alongside the id because offline verification needs the real bytes, never a fetch (docs/architecture/worlds.md, "Signed attestation": consulting the issuer at verification time is a ruled-out design). Empty for OAuth and federated-authority rows.
   */
  publicKey: string;
  /**
   * What a peer verified under this entry is minted, INSTEAD OF the blanket Control/all every admitted peer used to receive unconditionally. Empty (never null) is a legitimate authored choice: a verified-but-granted-nothing identity, admitted onto the connection table and able to hold a socket open, but unable to submit anything the grant table would honor.
   */
  grants: WorldAdmissionGrant[];
  /**
   * How much of this world's document a peer verified under this entry receives (see WorldDisclosureTier). Absent resolves to Presentation, so an entry authored before this field existed keeps a projection-only wire and nothing has to be edited to stay closed. Replica is reachable only by authoring it.
   */
  disclosure?: WorldDisclosureTier | null;
};

export type WorldAdmissionGrant = {
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
};

export type WorldAdmissionTrustMode = "SignsDirectly" | "Vouches" | "FederatedAuthority" | "OAuth";

/**
 * WHERE a placeable thing rides — the one shared pose-target vocabulary a placeable WorldCamera and a placeable WorldSpeaker both consume through the SAME resolver, distinct from HOW the thing looks at or emits from that pose (a WorldCameraProgram, a feed). The $type string is the JSON discriminator; a new anchor kind is a new derived record plus its JsonDerivedTypeAttribute line.
 */
export type WorldAnchor = WorldAnchorEntity | WorldAnchorEntityPart | WorldAnchorPlacement | WorldAnchorGroup | WorldAnchorSeat | WorldAnchorRecentSpeaker | null;

/**
 * Rides one population entity's ROOT pose — a walking avatar's whole-body position and orientation.
 */
export type WorldAnchorEntity = {
  $type?: "entity";
  /**
   * The 0-based entity index, bounded by the world's authored population capacity.
   */
  index: number;
};

/**
 * Rides one entity look's authored part pose rather than its whole-body root. The active look publishes the mapping from PartId to its packed transform slot; the slot remains an engine detail.
 */
export type WorldAnchorEntityPart = {
  $type?: "entityPart";
  /**
   * The 0-based entity index.
   */
  index: number;
  /**
   * The ordinal, case-sensitive part identifier published by the entity's active look.
   */
  partId: string;
};

/**
 * Rides the smoothed CENTROID of a set of population entities — the establishing-shot anchor. Also publishes the set's SPREAD (mean distance from the centroid), which a camera program's Offset consumes through its SpreadPullback. A group has no facing, so its orientation resolves to identity.
 */
export type WorldAnchorGroup = {
  $type?: "group";
  /**
   * The 0-based entity indices in the set, or null for the whole live population (every active entity). Each index is validated against the authored population capacity.
   */
  indices: Int32List | null;
  /**
   * The exponential smoothing rate (per second) the centroid/spread ease at (validated positive and finite) — seeded un-smoothed on first resolve so a camera does not fly in from the origin.
   */
  smoothRate: number;
};

/**
 * WHERE a placeable thing rides — the one shared pose-target vocabulary a placeable WorldCamera and a placeable WorldSpeaker both consume through the SAME resolver, distinct from HOW the thing looks at or emits from that pose (a WorldCameraProgram, a feed). The $type string is the JSON discriminator; a new anchor kind is a new derived record plus its JsonDerivedTypeAttribute line.
 */
export type WorldAnchorNonNullable = WorldAnchorEntity | WorldAnchorEntityPart | WorldAnchorPlacement | WorldAnchorGroup | WorldAnchorSeat | WorldAnchorRecentSpeaker | null;

/**
 * Rides a placement INSTANCE's stamped transform — a creation stamped into the world by reference (the same placement-reference shape CreationCameraDocument uses), optionally narrowed to one of its own authored shapes rather than the stamp's root.
 */
export type WorldAnchorPlacement = {
  $type?: "placement";
  /**
   * The referenced Id (must resolve).
   */
  placementId: string;
  /**
   * The referenced creation's ShapeDocument.Id to ride, or null for the placement's own stamped root transform.
   */
  shapeId: number | null;
};

/**
 * Rides the body that most recently spoke (see OverlayPredicate.Speaking), at its root or a named part; resolves nothing until something has spoken.
 */
export type WorldAnchorRecentSpeaker = {
  $type?: "recentSpeaker";
  partId?: string | null;
};

/**
 * Rides a local seat's avatar — the body the seat perceives as its own, so possession follows — at its root or at a named part. Numbernull is the enclosing seat scope (the seat a HUD frame or view is being resolved for), an explicit number is 1-based. Presentation-only: a camera or speaker on this anchor is resolved per seat.
 */
export type WorldAnchorSeat = {
  $type?: "seat";
  number?: number | null;
  partId?: string | null;
};

export type WorldAudioCue = {
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
};

export type WorldAudioDefaults = {
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
   */
  cues: (WorldAudioCue | null)[];
};

export type WorldAutonomyCadence = {
  /**
   * How often the body's physics/motion program advances.
   */
  motionSeconds?: number;
  /**
   * How often its selected producer refreshes steering. The most recent image is reused between refreshes.
   */
  steeringSeconds?: number;
};

export type WorldBackendPreference = "auto" | "directx" | "vulkan";

/**
 * Where a bar hangs: a viewport edge and how far in from it. Every bank anchored to the same edge and inset shares one frame — their plates are laid out together on one pitch grid and the nearest plate of the whole group sits at the inset — so a nested crossbar is five banks on one anchor, and a strip with side columns is three groups on three.
 */
export type WorldBindingBarAnchor = {
  /**
   * The viewport edge.
   */
  edge?: BindingBarEdge;
  /**
   * The gap between that edge and the nearest plate edge of everything anchored here, in button pitches — the same ruler every plate position uses. Along the other axis the group is centered.
   */
  inset?: number;
};

export type WorldBindingBarAuthoring = {
  /**
   * The physical controls this bar shows, by input source id (gamepad.buttonSouth, mouse.button1, …) — the same vocabulary a binding entry's sources speak, every id validated against the engine's input-source catalog, unique, at most MaxSlots. A layout's bank places these; one it does not place is not shown on that bank.
   */
  slotSet: (string | null)[];
  /**
   * The stacked banks — at least one, at most MaxBanks, unique ids, each naming a page the composed binding profile actually declares. List order is draw order.
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
};

export type WorldBindingBarBank = {
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
};

export type WorldBindingBarBankPlacement = {
  /**
   * The placed tables, in order.
   */
  pieces: (WorldBindingBarPiece | null)[];
  /**
   * Where this bank hangs, or null to share the layout's anchor (and so its frame: the pieces then nest against the other banks there).
   */
  anchor?: WorldBindingBarAnchor | null;
};

export type WorldBindingBarLayout = {
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
};

export type WorldBindingBarPiece = {
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
};

export type WorldBindingBarSlotPlacement = {
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
};

export type WorldBindingOverlay = {
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
  /**
   * The condition this layer composes under; null always composes it (today's behavior). Reuses StateCondition's field convention rather than a second reading of "compare a state cell" — the same state/key/comparison/value shape a placement's own response facet speaks. Re-evaluated whenever the routed definition changes, never per frame.
   */
  when?: WorldPlacementResponseConditionStateConditionBare | null;
};

/**
 * How a tabletop board binding treats an illegal move — the author's choice per table, engine-side and game-agnostic: the judge that computes Verdict is authored per world, and this field only decides what the engine itself does once that verdict refuses.
 */
export type WorldBoardEnforcement = "Record" | "Return";

export type WorldBodiesDefaults = {
  localSeats?: number | null;
  seatActivation?: SeatActivationPolicy[] | null;
  networkPlayers?: number;
  defaultPeerSource?: IntentSource;
  seatSpawns?: (string | null)[] | null;
  distribution?: WorldDistribution | null;
  peerVariation?: WorldPopulationVariation | null;
  seatVariation?: WorldPopulationVariation | null;
  peerColors?: WorldSequenceNullable | null;
  capacity?: number | null;
  reconnectGraceSeconds?: number;
  capacityRow?: string | null;
  disclosure?: WorldObserverDisclosure | null;
  scaleRow?: string | null;
  sleepAfterSeconds?: number;
};

export type WorldBodyContactMode = "Overlap" | "Solid";

export type WorldBodyContactPolicy = {
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
};

/**
 * How a rule-triggered body designation chooses its target.
 */
export type WorldBodyDesignationKind = "Body" | "Clear";

export type WorldCamera = {
  /**
   * The camera's stable name — the handle a View screen / layout slot samples by.
   */
  name: string;
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
   * What the camera rides, or null for the world reference frame (or for Anchors to decide).
   */
  anchor?: WorldAnchor | null;
  /**
   * Ranked anchor candidates, first holding wins each frame — a portrait camera that rides the speaking character while they speak and the seat's own avatar otherwise. Refused beside Anchor; a single unconditional anchor is Anchor.
   */
  anchors?: (WorldCameraAnchorCandidate | null)[] | null;
};

export type WorldCameraAnchorCandidate = {
  /**
   * What the camera rides while this candidate wins.
   */
  anchor: WorldAnchorNonNullable;
  /**
   * The condition, evaluated for the seat the view is resolved for.
   */
  when?: OverlayPredicateNullable | null;
};

/**
 * An authored camera program — the ordered op list a rig resolves through every frame.
 */
export type WorldCameraProgram = {
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
   * Gets the program's Dynamics op, or null.
   */
  dynamicsOp?: WorldCameraProgramOpDynamicsNullable | null;
  /**
   * Gets the program's FieldOfView op, or null.
   */
  fovOp?: WorldCameraProgramOpFieldOfViewNullable | null;
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
   * Gets the program's SelectProgram op, or null.
   */
  selectOp?: WorldCameraProgramOpSelectProgramNullable | null;
};

/**
 * One instruction in an authored camera program's ordered op list — the presentation-side pose algebra bodyMotionPrograms established for sim-side movement, promoted to cameras: an authored rig is an ordered list of these trivial ops rather than a bespoke closed motion/aim union, so a new camera behavior is authored data, never new engine code. This is the AUTHORING vocabulary only: Puck.World.Client.WorldCameraRigCompiler translates it to Puck.SdfVm.Views.SdfCameraOp and resolves each frame's bindings and subject poses, and Puck.SdfVm.Views.SdfCameraProgramEvaluator — which parses no document — walks the result. Floats throughout; presentation carries no fixed-point burden.
 */
export type WorldCameraProgramOp = WorldCameraProgramOpAnchor | WorldCameraProgramOpOffset | WorldCameraProgramOpLookAt | WorldCameraProgramOpOrbit | WorldCameraProgramOpPath | WorldCameraProgramOpDynamics | WorldCameraProgramOpClampPitch | WorldCameraProgramOpFieldOfView | WorldCameraProgramOpBlend | WorldCameraProgramOpSelectProgram | null;

/**
 * Establishes the CURRENT subject — the pose Offset/Orbit place the eye relative to, and LookAt aims along the facing of when it names no subject of its own. Also re-seeds the eye at the resolved subject's own position (the "first person" default before any Offset/Orbit runs). At most one per program, and it must be the first operation when present; a program that omits it starts from Reference implicitly.
 */
export type WorldCameraProgramOpAnchor = {
  $type?: "anchor";
  /**
   * The subject to resolve.
   */
  subject: WorldCameraSubject;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
};

/**
 * Establishes the CURRENT subject — the pose Offset/Orbit place the eye relative to, and LookAt aims along the facing of when it names no subject of its own. Also re-seeds the eye at the resolved subject's own position (the "first person" default before any Offset/Orbit runs). At most one per program, and it must be the first operation when present; a program that omits it starts from Reference implicitly.
 */
export type WorldCameraProgramOpAnchorNullable = {
  /**
   * The subject to resolve.
   */
  subject: WorldCameraSubject;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
};

/**
 * Evaluates two other authored programs by name and linearly interpolates their resolved eye, target, and field of view — the whole document's camera-program table (every cameras[].rig plus views.seatRig/views.cameraRig) is the namespace A/B resolve against. At most one per program; refused when it would create a reference cycle.
 */
export type WorldCameraProgramOpBlend = {
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
};

/**
 * Evaluates two other authored programs by name and linearly interpolates their resolved eye, target, and field of view — the whole document's camera-program table (every cameras[].rig plus views.seatRig/views.cameraRig) is the namespace A/B resolve against. At most one per program; refused when it would create a reference cycle.
 */
export type WorldCameraProgramOpBlendNullable = {
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
};

/**
 * Clamps the effective pitch the NEXT Orbit op resolves with (its authored Pitch plus any live seat delta) to [MinPitch, MaxPitch]. At most one per program; must precede the Orbit op it governs. A joined seat's own views.seatControl band already clamps its LIVE delta before this ever runs — this op exists for a program with no seat behind it (a named camera orbiting a state-bound pitch).
 */
export type WorldCameraProgramOpClampPitch = {
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
};

/**
 * Clamps the effective pitch the NEXT Orbit op resolves with (its authored Pitch plus any live seat delta) to [MinPitch, MaxPitch]. At most one per program; must precede the Orbit op it governs. A joined seat's own views.seatControl band already clamps its LIVE delta before this ever runs — this op exists for a program with no seat behind it (a named camera orbiting a state-bound pitch).
 */
export type WorldCameraProgramOpClampPitchNullable = {
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
};

/**
 * Sets the second-order response the resolved eye/target boom eases through. Read by the caller after resolving a frame — never affects the resolved pose itself. At most one per program.
 */
export type WorldCameraProgramOpDynamics = {
  $type?: "dynamics";
  /**
   * The referenced dynamics row name; must resolve.
   */
  row: string;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
};

/**
 * Sets the second-order response the resolved eye/target boom eases through. Read by the caller after resolving a frame — never affects the resolved pose itself. At most one per program.
 */
export type WorldCameraProgramOpDynamicsNullable = {
  /**
   * The referenced dynamics row name; must resolve.
   */
  row: string;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
};

/**
 * Sets the rendered vertical field of view, radians — a literal, or a state.<row>[.<key>] binding so a world rule can pull focus or frame a moment (decisions in the sim, framing in presentation). At most one per program.
 */
export type WorldCameraProgramOpFieldOfView = {
  $type?: "fieldOfView";
  /**
   * The field of view.
   */
  fieldOfViewRadians: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
};

/**
 * Sets the rendered vertical field of view, radians — a literal, or a state.<row>[.<key>] binding so a world rule can pull focus or frame a moment (decisions in the sim, framing in presentation). At most one per program.
 */
export type WorldCameraProgramOpFieldOfViewNullable = {
  /**
   * The field of view.
   */
  fieldOfViewRadians: BindableScalar;
  /**
   * Gets this op's authored $type token — the one spelling a refusal, a read-back, and the document all use.
   */
  opcode?: string | null;
};

export type WorldCameraProgramOpList = (WorldCameraProgramOp | null)[];

/**
 * Sets the aim target.
 */
export type WorldCameraProgramOpLookAt = {
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
};

/**
 * Sets the aim target.
 */
export type WorldCameraProgramOpLookAtNullable = {
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
};

/**
 * Places the eye at an offset from the current subject's pose.
 */
export type WorldCameraProgramOpOffset = {
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
};

/**
 * Places the eye at an offset from the current subject's pose.
 */
export type WorldCameraProgramOpOffsetNullable = {
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
};

/**
 * Orbits the eye about its pivot. Yaw and Pitch are live-bindable like FieldOfView's field of view: a seat rig whose yaw reads a state cell turns the CAMERA when that cell changes — look behind is state.look.behind flipping between 0 and π — while the seat's facing (what steering and movement resolve against) is untouched, because the facing never includes the rig's offsets.
 */
export type WorldCameraProgramOpOrbit = {
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
};

/**
 * Orbits the eye about its pivot. Yaw and Pitch are live-bindable like FieldOfView's field of view: a seat rig whose yaw reads a state cell turns the CAMERA when that cell changes — look behind is state.look.behind flipping between 0 and π — while the seat's facing (what steering and movement resolve against) is untouched, because the facing never includes the rig's offsets.
 */
export type WorldCameraProgramOpOrbitNullable = {
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
};

/**
 * Establishes the CURRENT subject at a point sampled from a named curves row by arc-length fraction, and re-seeds the eye there — the same subject-seeding role Anchor plays, so at most one of the two may appear in a program, and when present this must be the first operation. The sampled subject's orientation faces the curve's own tangent direction at that point. A following Offset with WorldAxes: false (the default) rotates its offset into that tangent frame, and a following LookAt with no subject of its own looks ahead along it; a following Orbit pivots at the traveling point but resolves its own yaw/pitch as literal world-frame angles regardless of the tangent — it never reads the subject's orientation. A dollied eye is path + LookAt + FieldOfView; a dollied pivot is path + Orbit with an authored yaw that already accounts for the curve's own heading. Composes with Dynamics's boom follower untouched. Carries no rate field: a constant-rate dolly binds Fraction to a state row carrying the advance trait (base + rate·ticks) rather than duplicating a rate spelling here.
 */
export type WorldCameraProgramOpPath = {
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
};

/**
 * Establishes the CURRENT subject at a point sampled from a named curves row by arc-length fraction, and re-seeds the eye there — the same subject-seeding role Anchor plays, so at most one of the two may appear in a program, and when present this must be the first operation. The sampled subject's orientation faces the curve's own tangent direction at that point. A following Offset with WorldAxes: false (the default) rotates its offset into that tangent frame, and a following LookAt with no subject of its own looks ahead along it; a following Orbit pivots at the traveling point but resolves its own yaw/pitch as literal world-frame angles regardless of the tangent — it never reads the subject's orientation. A dollied eye is path + LookAt + FieldOfView; a dollied pivot is path + Orbit with an authored yaw that already accounts for the curve's own heading. Composes with Dynamics's boom follower untouched. Carries no rate field: a constant-rate dolly binds Fraction to a state row carrying the advance trait (base + rate·ticks) rather than duplicating a rate spelling here.
 */
export type WorldCameraProgramOpPathNullable = {
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
};

/**
 * Evaluates exactly one of several other named programs, chosen by a live key — the state-driven counterpart of Blend's two-way continuous mix: a rule-advanced integer row selects a whole framing outright rather than easing toward one, so a document can drive which of N stations a single camera program shows without a per-frame console override. The winning program's eye, target, field of view, and dynamics response pass through unchanged (no lerp). At most one per program; refused when it would create a reference cycle.
 */
export type WorldCameraProgramOpSelectProgram = {
  $type?: "selectProgram";
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
};

/**
 * Evaluates exactly one of several other named programs, chosen by a live key — the state-driven counterpart of Blend's two-way continuous mix: a rule-advanced integer row selects a whole framing outright rather than easing toward one, so a document can drive which of N stations a single camera program shows without a per-frame console override. The winning program's eye, target, field of view, and dynamics response pass through unchanged (no lerp). At most one per program; refused when it would create a reference cycle.
 */
export type WorldCameraProgramOpSelectProgramNullable = {
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
};

/**
 * One SelectProgram candidate: the program named by Program wins when the op's key rounds to Value.
 */
export type WorldCameraSelectCase = {
  /**
   * The matching key value.
   */
  value: number;
  /**
   * The candidate program's name — the same blend namespace Blend resolves against (every cameras[].rig, plus views.seatRig/views.cameraRig).
   */
  program: string;
};

export type WorldCameraSelectCaseList = (WorldCameraSelectCase | null)[];

/**
 * The subject an Anchor/LookAt op resolves against — the closed "what a camera program can key off" vocabulary. Distinct from WorldAnchor (WHERE a whole placeable camera or speaker rides, resolved OUTSIDE the program and handed in as one reference pose): this is presentation math INSIDE the program, so it stays float and needs no live entity table — a placement's pose resolves through the same static stamped-transform math a placeable camera's own anchor and a speaker read (Puck.World.Client.WorldAnchorGeometry).
 */
export type WorldCameraSubject = WorldCameraSubjectReference | WorldCameraSubjectPlacement | WorldCameraSubjectWorldPoint | null;

/**
 * The subject an Anchor/LookAt op resolves against — the closed "what a camera program can key off" vocabulary. Distinct from WorldAnchor (WHERE a whole placeable camera or speaker rides, resolved OUTSIDE the program and handed in as one reference pose): this is presentation math INSIDE the program, so it stays float and needs no live entity table — a placement's pose resolves through the same static stamped-transform math a placeable camera's own anchor and a speaker read (Puck.World.Client.WorldAnchorGeometry).
 */
export type WorldCameraSubjectNullable = WorldCameraSubjectReference | WorldCameraSubjectPlacement | WorldCameraSubjectWorldPoint | null;

/**
 * Rides a placement's authored stamped transform (position only — a placement carries no live facing through this static resolve, the same limitation a placeable camera's own Placement arm and a speaker share).
 */
export type WorldCameraSubjectPlacement = {
  $type?: "placement";
  /**
   * The referenced Id (must resolve).
   */
  placementId: string;
  /**
   * The referenced creation's own shape to ride, or null for the placement's stamped root transform.
   */
  shapeId?: number | null;
};

/**
 * The program's externally supplied reference pose — a named camera's own WorldAnchor, or a seat rig's currently perceived body (the seat's own avatar, or a possessed camera body). Resolved outside the program and handed to it as the SdfAnchor the compiled rig receives every frame.
 */
export type WorldCameraSubjectReference = {
  $type?: "reference";
};

/**
 * A fixed world-space point.
 */
export type WorldCameraSubjectWorldPoint = {
  $type?: "worldPoint";
  /**
   * The world-space position.
   */
  point: DocumentVector3;
};

/**
 * The coarse capability verbs a WorldGrant confers — the closed set the server checks a submission's Principal against at each write boundary. A genre world arrives as different data (new subjects, new sections), never a new capability.
 */
export type WorldCapability = "Drive" | "Observe" | "Control" | "Mutate" | "Edit";

export type WorldCapabilityRequest = {
  /**
   * The capability requested.
   */
  capability?: WorldCapability;
  /**
   * The subject requested.
   */
  subject?: GrantSubject;
};

export type WorldCapturePaletteEntry = {
  /**
   * The material's index — the manifest's census key. Author-chosen, unique within a row; not read against any other section's material table.
   */
  material: number;
  /**
   * The material's reference color, #RRGGBB or #RRGGBBAA (alpha ignored — the composed frame carries none).
   */
  color: string;
};

export type WorldCaptureRow = {
  /**
   * The stable name — the manifest's station field and the first part of the capture's generated name (CaptureName, <station>~<tick>). A CellName: dot-free, non-empty, free of the reserved character set, and free of ~.
   */
  station: CellName;
  /**
   * The exact simulation ticks (completed-tick coordinates, ascending, none repeated) this station arms a capture at. Capacity: MaxTicksPerRow.
   */
  ticks: number[];
  /**
   * The per-pixel census's material table — at least one entry, at most MaxPaletteEntriesPerRow, unique Material indices.
   */
  palette: (WorldCapturePaletteEntry | null)[];
};

export type WorldCapturesSection = {
  /**
   * The scheduled stations. Station names are unique ignoring case, since each names capture files; capacity MaxRows.
   */
  rows: (WorldCaptureRow | null)[];
  /**
   * The output directory every scheduled capture in this document writes into, and where manifest.json (the puck.parity.manifest.v1 document) is written whenever a capture ends, whether it produced a frame or a named refusal. Absent, the default, is a captures directory under the run's state root (--state-dir, else the per-user state directory), so a boot that names no directory writes nothing under its working directory; an authored relative path resolves beside the document (WorldDocumentPaths). A --capture-dir boot flag overrides either for a deployment run (the --state-dir pattern), so two backend legs of the same document can target sibling directories without two document copies.
   */
  directory?: string | null;
};

export type WorldCarry = {
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
};

export type WorldChannel = {
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
};

export type WorldCollider = WorldColliderSphere | WorldColliderCapsule | WorldColliderBox | WorldColliderFromCreation | null;

export type WorldColliderBox = {
  $type?: "box";
  /**
   * The positive half-extents.
   */
  halfExtents: DocumentVector3;
  /**
   * The body-local orientation.
   */
  rotation: DocumentQuaternion;
};

export type WorldColliderCapsule = {
  $type?: "capsule";
  /**
   * The body-local vector from the lower sphere center to the upper sphere center.
   */
  endpoint: DocumentVector3;
  /**
   * The capsule radius.
   */
  radius: number;
};

export type WorldColliderFromCreation = {
  $type?: "fromCreation";
  /**
   * The referenced Id.
   */
  prototypeId: string;
};

export type WorldColliderSphere = {
  $type?: "sphere";
  /**
   * The sphere radius.
   */
  radius: number;
};

export type WorldCollision = {
  /**
   * The contact qualities the world requires. An empty list permits analytic primitive contact; any declared requirement selects the SDF field.
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
};

export type WorldCollisionEvents = {
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
};

export type WorldContactRequirement = "SmoothUnionContact" | "GradientDerivedUp";

/**
 * How long a filled WorldPlacementContribution slot keeps the piece a federation partner put in it.
 */
export type WorldContributionTenure = "Presence" | "Endowed";

export type WorldControllerStateSlots = {
  /**
   * The row containing the machine id.
   */
  machineState: CellName;
  /**
   * The row containing the device id.
   */
  deviceState: CellName;
};

export type WorldCurveKnot = {
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
};

export type WorldCurveRow = {
  /**
   * The row's stable name, unique within the section — the spelling every consumer's own curve reference resolves against.
   */
  name: string;
  /**
   * The authored knots, in curve order. An open curve needs at least two; a closed curve at least three; at most MaxKnots.
   */
  knots: (WorldCurveKnot | null)[];
  /**
   * Whether the last knot connects back to the first. Defaults to false (open).
   */
  closed?: boolean;
};

export type WorldDecision = {
  /**
   * One to 32 uniquely named options, in deterministic tie-break order.
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
  scoreKind?: CellKindNonNullable;
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
  onNoChoice?: (ActionEffect | null)[] | null;
};

export type WorldDecisionMode = "HighestScore" | "Weighted";

export type WorldDecisionNeighbors = {
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
};

export type WorldDecisionOption = {
  /**
   * The stable, non-empty option name.
   */
  name: CellName;
  /**
   * A numeric expression in the decision's score kind. Arithmetic failure makes this option ineligible for that reconsideration.
   */
  score: ExpressionProgramNonNullable2;
  /**
   * Effects fired when entering this option; may be empty for an inspection-only choice.
   */
  effects: (ActionEffect | null)[];
  /**
   * Eligibility predicate, or null for always eligible.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * Optional bounded nearby-body expansion. Requires rule forEach; each and left identify the observer, right the candidate, only inside this option's gate, score, and effects. A different individual is a selection transition.
   */
  neighbors?: WorldDecisionNeighbors | null;
};

export type WorldDestination = {
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
};

export type WorldDestinationDurability = "ephemeral" | "persisted";

export type WorldDestinationScope = "user" | "group" | "global";

export type WorldDisclosureTier = "Frames" | "Presentation" | "Replica";

/**
 * A spatial region composed with the deterministic sequence that fills it.
 */
export type WorldDistribution = {
  /**
   * The region to fill.
   */
  region: WorldDistributionRegion;
  /**
   * The per-index fill sequence.
   */
  fill: WorldSequence;
};

/**
 * A closed spatial region vocabulary consumed by WorldDistribution.
 */
export type WorldDistributionRegion = WorldDistributionRegionDisc | WorldDistributionRegionPoints | WorldDistributionRegionLattice | WorldDistributionRegionNoise | WorldDistributionRegionScatter | null;

/**
 * A planar disc centered on the consumer's origin.
 */
export type WorldDistributionRegionDisc = {
  $type?: "disc";
  /**
   * The disc radius.
   */
  radius: number;
  /**
   * The radial fill count, or null to use the consumer's requested count.
   */
  sampleCount?: number | null;
};

/**
 * A finite two-axis lattice local to a placement.
 */
export type WorldDistributionRegionLattice = {
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
};

/**
 * Deterministic hash-lattice fBm patch admission over a placement-local grid, centered on the placement — one instance at the center of every admitted cell. The placement twin of Noise: same fixed-point fBm, same seed fold against generation.worldSeed, same threshold semantics; here admission stamps a creation copy instead of writing a field value.
 */
export type WorldDistributionRegionNoise = {
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
};

/**
 * A cycle of authored spawn poses, each expanded by a planar square.
 */
export type WorldDistributionRegionPoints = {
  $type?: "points";
  /**
   * The spawn-point names.
   */
  names: (string | null)[];
  /**
   * The square half-extent on X and Z.
   */
  halfExtent: number;
};

/**
 * One jittered instance per Spacing-cell block over a placement-local grid, centered on the placement. The placement twin of Scatter: same integer PCG3D block jitter, same seed fold — every block materializes exactly the one jittered point (never a filled neighborhood), so the instance count is exact and seed-independent: ceil(Width/Spacing) x ceil(Depth/Spacing).
 */
export type WorldDistributionRegionScatter = {
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
};

/**
 * Applies the same instantaneous world-space rigid impulse body.impulse fires (Δv = impulse / mass, through the server's rigid-body solver — never a second impulse mechanism) to a Key-addressed body, along HeadingKey's own body's forward facing, scaled by a live kind=Fixed state cell's magnitude. The cue gesture's one honest path: billiards charges MagnitudeState by hold duration and fires this on release. Refused by name when the struck body resolves to no active body or carries no 'rigid' kit facet, the heading body resolves to no active body, or the resulting impulse is not representable or exceeds the world's declared rigid speed ceiling.
 */
export type WorldEffectApplyRigidImpulse = {
  $type?: "applyRigidImpulse";
  /**
   * The struck body reference — body:<n>, argmax:<row>/ argmin:<row>, or placement:<id> (see BodyRefVocabulary).
   */
  key: string;
  /**
   * The body reference whose forward facing supplies the impulse direction — the same reference grammar as Key.
   */
  headingKey: string;
  /**
   * The declared kind=Fixed row naming the impulse's magnitude.
   */
  magnitudeState: StateChannelRefNonNullable;
  /**
   * The row's cell key, or null for an unkeyed row.
   */
  magnitudeKey?: StateChannelRefNonNullable;
};

/**
 * Designates a subject into a body's named target register, or clears the register. A kit's action designates into its own body the participant that last affected it; a world rule names the body with Key and the subject with TargetKey.
 */
export type WorldEffectDesignate = {
  $type?: "designate";
  /**
   * The authored target-register name.
   */
  register: string;
  /**
   * Whether the register is filled or cleared; only a world rule clears.
   */
  kind?: WorldBodyDesignationKind;
  /**
   * The body a world rule names — see Key.
   */
  key?: StateChannelRefNonNullable;
  /**
   * The body a world rule designates, required when Kind is Body and refused when it is Clear. A kit's action names none.
   */
  targetKey?: StateChannelRefNonNullable;
};

/**
 * Emits a deterministic presentation-neutral cue (WorldGameplayCue).
 */
export type WorldEffectEmitCue = {
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
  key?: StateChannelRefNonNullable;
};

/**
 * Paints one lattice field cell, or the cube of Radius around it.
 */
export type WorldEffectPaintField = {
  $type?: "paintField";
  field: string;
  x: number;
  y: number;
  z: number;
  value: number;
  operation?: WorldFieldWriteOp;
  radius?: number;
};

/**
 * A timed planar velocity overlay (the dash): BodyDirection is rotated by the body's attitude at fire time and ridden as authored, never normalized, so it must be unit length, at Speed for DurationSeconds, an exact whole number of engine ticks.
 */
export type WorldEffectPlanarImpulse = {
  $type?: "planarImpulse";
  /**
   * The unit direction in the body's own frame.
   */
  bodyDirection: DocumentVector3;
  /**
   * The overlay's speed, in world units per second.
   */
  speed: number;
  /**
   * How long the overlay rides.
   */
  durationSeconds: number;
  /**
   * The body a world rule names — see Key.
   */
  key?: StateChannelRefNonNullable;
  /**
   * The participant a kit's action addresses — see Target.
   */
  target?: ActionTarget;
};

/**
 * Teleports a world-addressed body to a spawn point or a literal position.
 */
export type WorldEffectPose = {
  $type?: "pose";
  /**
   * A row name or literal key as plain text, or an object identifying a reserved channel, lexical pool field, or static pool field.
   */
  key: StateChannelRefNonNullable;
  spawnPoint?: string | null;
  position?: DocumentVector3;
  yawDegrees?: number;
  pitchDegrees?: number;
  rollDegrees?: number;
};

/**
 * Places a body upright at a live cell of an anchored topology, plus an authored offset.
 */
export type WorldEffectPoseCell = {
  $type?: "poseCell";
  /**
   * A body reference, including placement:$each.
   */
  key: string;
  /**
   * The declared topology.
   */
  topology: string;
  /**
   * The integer cell ordinal evaluated when the effect fires.
   */
  expression: ExpressionProgramNonNullable2;
  /**
   * The displacement from the cell centre, in world units.
   */
  offset: DocumentVector3;
};

/**
 * Removes a HUD panel document row by id.
 */
export type WorldEffectRemoveHudPanel = {
  $type?: "removeHudPanel";
  id: string;
};

/**
 * Removes a placement document row by id; inside a transaction it must sit in the closing suffix.
 */
export type WorldEffectRemovePlacement = {
  $type?: "removePlacement";
  id: string;
};

/**
 * Saves the world through the host's save tap.
 */
export type WorldEffectSave = {
  $type?: "save";
};

/**
 * Multiplies a body's vertical velocity (the jump cut; gate on Rising).
 */
export type WorldEffectScaleVerticalVelocity = {
  $type?: "scaleVerticalVelocity";
  /**
   * The multiplier.
   */
  factor: number;
  /**
   * The body a world rule names — see Key.
   */
  key?: StateChannelRefNonNullable;
  /**
   * The participant a kit's action addresses — see Target.
   */
  target?: ActionTarget;
};

/**
 * Writes one fact on the identity a world-addressed body drives under: the body's cell in the world's reserved WorldIdentityFactLane row and the identity's own persisted facts row, together. Exactly one of Value and Expression is authored; a body driving under no owned identity refuses the write rather than minting one.
 */
export type WorldEffectSetIdentityFact = {
  $type?: "setIdentityFact";
  /**
   * The body — an index, a $cell: indirection, or a bound key.
   */
  key: StateChannelRefNonNullable;
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
  expression?: ExpressionProgramNonNullable2;
};

/**
 * Writes a body's vertical-velocity channel (the jump launch / the surge).
 */
export type WorldEffectSetVerticalVelocity = {
  $type?: "setVerticalVelocity";
  /**
   * The vertical velocity, in world units per second.
   */
  velocity: number;
  /**
   * The body a world rule names — an index, a $cell: indirection, or a bound key. A kit's action names none: it acts on its own body, or on Target.
   */
  key?: StateChannelRefNonNullable;
  /**
   * The participant a kit's action addresses; a world rule refuses any but the default.
   */
  target?: ActionTarget;
};

/**
 * Starts a named timer slot with an authored duration.
 */
export type WorldEffectStartTimer = {
  $type?: "startTimer";
  state: string;
  seconds: number;
  target?: ActionTarget;
};

/**
 * Upserts a HUD panel document row.
 */
export type WorldEffectUpsertHudPanel = {
  $type?: "upsertHudPanel";
  panel: WorldHudPanel;
};

/**
 * Upserts a placement document row; inside a transaction it must sit in the closing suffix.
 */
export type WorldEffectUpsertPlacement = {
  $type?: "upsertPlacement";
  placement: WorldPlacement;
};

/**
 * An emission facet — a synth voice a world row itself makes (phenomena sound like themselves; a creek is not a speaker). Nullable on WorldPlacement — a facet edit is the row's existing whole-row upsert. Under a repeat facet the emission binds to the placement root only (an 8×8 lattice must not become 64 voices; a per-copy flag is a future facet field, not a schema fork).
 */
export type WorldEmission = {
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
};

export type WorldExports = {
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
};

export type WorldFieldCondition = {
  /**
   * The field read at the cell.
   */
  field: string;
  /**
   * The comparison.
   */
  comparison: ExpressionComparison;
  /**
   * The scalar compared against (literal or state-row reference).
   */
  value: WorldLatticeScalar | null;
};

export type WorldFieldTopology = {
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
};

export type WorldFieldWrite = {
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
  value: WorldLatticeScalar | null;
};

export type WorldFieldWriteOp = "Set" | "Add";

export type WorldFlockProfile = {
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
  cohesionAffinity?: ExpressionProgramNonNullable2;
  /**
   * Independent Fixed expression weighting each retained neighbor's heading, under the same contract as CohesionAffinity. Affinities select relative influence; the outer Alignment and Cohesion weights set term strength.
   */
  alignmentAffinity?: ExpressionProgramNonNullable2;
};

export type WorldFlockSpace = "Tangent" | "Volume";

/**
 * The WorldScreenSource arms that produce a sampled frame — Producer, View and Probe — carrying its own JsonPolymorphicAttribute over the same three discriminators (producer/view/probe) their parent union uses, so a member declared as this narrower type round-trips the identical JSON a full WorldScreenSource screen row does. A WorldProbe socket is the driving consumer: it plugs one of these into each named input rather than the wider union, which would legally admit a decal, machine, text or none source no probe kernel can sample.
 */
export type WorldFrameSource = WorldScreenSourceProducer | WorldScreenSourceView | WorldScreenSourceProbe | null;

/**
 * The WorldScreenSource arms that produce a sampled frame — Producer, View and Probe — carrying its own JsonPolymorphicAttribute over the same three discriminators (producer/view/probe) their parent union uses, so a member declared as this narrower type round-trips the identical JSON a full WorldScreenSource screen row does. A WorldProbe socket is the driving consumer: it plugs one of these into each named input rather than the wider union, which would legally admit a decal, machine, text or none source no probe kernel can sample.
 */
export type WorldFrameSourceNonNullable = WorldScreenSourceProducer | WorldScreenSourceView | WorldScreenSourceProbe | null;

export type WorldGenerationDefaults = {
  /**
   * Folded into every site's Pcg32XshRr starting state. Defaults to 0.
   */
  worldSeed?: number;
};

export type WorldGrant = {
  /**
   * What holds the grant — an actor, a group, or another document.
   */
  grantee?: Grantee;
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
   * The MutationKindMask a row admits — legal only on a Mutate row over a concrete Section, Creation, or Placement subject, or an Edit row over a concrete State subject (never the wildcard — "which kinds" presupposes one bounded target — and never any other capability). The grant door refuses a bit outside the target's own declared kind set (WorldMutationKindCatalog.KindsOf(section), where a row-scoped subject resolves to the section that owns it, or KindsOf(WorldSection.State) for an Edit row) and refuses an effective mask of zero (an admitted-but-inert bit set is a grant that lies — the identical "grant nothing instead" rule Budget's 0 and Ceiling's 0 already enforce). On an Edit row this is what separates bumping a state row from redefining it: verbs:UpsertStateCell,RemoveStateCell admits the per-cell writes while denying the whole-row UpsertStateRow/RemoveStateRow that could re-author the row's envelope. An unmasked Edit row keeps full reach over its subject, so deny-by-default plus opt-in narrowing holds and no seeded row changes meaning. A null mask on a re-grant of the same (Grantee, Capability, Subject) row clears a previously-recorded mask — unlike Budget/Reach, which only ever write when the incoming grant carries one and otherwise leave the prior value untouched; a mask a re-grant does not repeat is a mask the operator meant to take back, not one this door defaults into surviving silently. Revoking the row clears it outright. When a principal holds both a concrete row and the (trusted-only) wildcard row, the deciding row from Rule governs which mask applies — ConcreteHold beats WildcardHold, exactly as it does for the bare allow/deny check.
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
};

export type WorldGravity = {
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
};

export type WorldGravityArea = {
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
};

export type WorldGravityAreaAcceleration = WorldGravityAreaAccelerationDirectional | WorldGravityAreaAccelerationRadial | null;

export type WorldGravityAreaAccelerationDirectional = {
  $type?: "directional";
  /**
   * The acceleration vector in world units per second squared. Zero is admitted so Replace can author a zero-gravity pocket.
   */
  value: DocumentVector3;
};

export type WorldGravityAreaAccelerationRadial = {
  $type?: "radial";
  /**
   * The positive acceleration magnitude in world units per second squared.
   */
  magnitude: number;
};

export type WorldGravityAreaBounds = WorldGravityAreaBoundsSphereBounds | WorldGravityAreaBoundsBoxBounds | null;

export type WorldGravityAreaBoundsBoxBounds = {
  $type?: "box";
  /**
   * The positive placement-local half extents, multiplied by the placement's scale.
   */
  halfExtents: DocumentVector3;
};

export type WorldGravityAreaBoundsSphereBounds = {
  $type?: "sphere";
  /**
   * The positive placement-local radius, multiplied by the placement's scale.
   */
  radius: number;
};

export type WorldGravityAreaMode = "Combine" | "Replace";

export type WorldGravityAttractor = {
  /**
   * The placements row whose position the source sits at. The row need not be solid or visible; only its transform is read.
   */
  placementId: string;
  /**
   * The source's non-negative gravitational mass.
   */
  mass: number;
};

export type WorldGravityPoint = {
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
};

export type WorldGravitySolver = "Pairwise" | "FastMonopole" | "AdaptiveFmm";

export type WorldGroup = {
  /**
   * The group's stable id, unique within Groups — the token Group carries as a grant principal. SafeName-typed (the For precedent: a scoped session's process-local instance name is composed from this id, spelled by ToFile, in WorldSessionResolver.MintInstanceName; a document refuses an id carrying ~, which keeps that spelling injective).
   */
  id: SafeName;
  /**
   * The owning kind's name — validated to reference a declared WorldGroupKind (unknown-by-name).
   */
  kindName: string;
  /**
   * The current flat membership rows. Each row is a local actor or verified external identity, with role, stable join ordinal, and optional tags. Group refs are refused; the roster is bounded by the kind's own Capacity.
   */
  members: (WorldGroupMember | null)[];
  /**
   * The row's own tags — what a Tagged destination selector matches against: a traveler resolving a tagged destination is expected to hold exactly one membership among rows carrying the selector's tag, never zero or several. Validated non-empty and distinct when present; null/absent means this row carries none, which is not the same as an authored empty list (refused — omit the member instead).
   */
  tags?: (string | null)[] | null;
  /**
   * The monotonic revision, incremented by each accepted membership mutation.
   */
  revision?: number;
  /**
   * The next never-reused join ordinal; it remains advanced after a member leaves and is not derived from Revision.
   */
  nextJoinOrdinal?: number;
};

export type WorldGroupEvictionPolicy = "Remove" | "Disband";

export type WorldGroupKind = {
  /**
   * The kind's stable name, unique within Kinds — the vocabulary a KindName reference is validated against (unknown-by-name, the same shape as the state-row cell-existence refusal).
   */
  name: string;
  /**
   * The role→capability map (see WorldGroupRole). May be empty — a kind with no roles reaches no capability at all through its group grantee, so any grant naming it is refused as unreachable.
   */
  roles: (WorldGroupRole | null)[];
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
};

export type WorldGroupLifetime = "Ephemeral" | "Persistent";

export type WorldGroupMember = {
  /**
   * The local or verified member identity.
   */
  ref: WorldMemberRef;
  /**
   * The declared role name, or null for no role-scoped capability; validators must not infer a role.
   */
  role: string | null;
  /**
   * The monotonic succession ordinal assigned when this member joined the group.
   */
  joinOrdinal: number;
  /**
   * Optional non-empty tags attached to this member.
   */
  tags?: (string | null)[] | null;
};

export type WorldGroupRole = {
  /**
   * The role's stable name, unique within its kind.
   */
  name: string;
  /**
   * The capabilities a member acting under this role may be granted through the group principal. Never empty — a role reaching nothing is a role that could not exist without lying about what it is for.
   */
  capabilities: WorldCapability[];
};

export type WorldGroupSelector = WorldGroupSelectorNamed | WorldGroupSelectorTagged | null;

export type WorldGroupSelectorNamed = {
  $type?: "named";
  /**
   * The Id this destination is bound to. Must resolve to a declared group row — an undeclared id refuses by name.
   */
  group: string;
};

export type WorldGroupSelectorTagged = {
  $type?: "tagged";
  /**
   * The tag every candidate membership is matched against. Must be non-empty.
   */
  tag: string;
};

export type WorldGroupsSection = {
  /**
   * The declared kind catalog.
   */
  kinds: (WorldGroupKind | null)[];
  /**
   * The group roster — authored and runtime rows in one list (see WorldGroup).
   */
  groups: (WorldGroup | null)[];
  /**
   * The ownership bindings (see WorldOwnership). May be empty.
   */
  ownership: (WorldOwnership | null)[];
};

export type WorldHold = {
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
};

export type WorldHoldEnvelope = {
  /**
   * The terminal upward speed (u/s). Required for a Medium bond; refused (the arc never clamps a rise) for Gravity/Lift.
   */
  riseSpeed?: number | null;
  /**
   * The terminal downward speed (u/s) — a Gravity/Lift row's own terminal fall speed, or a Medium row's terminal sink speed.
   */
  sinkSpeed?: number;
};

export type WorldHoldGravity = {
  /**
   * The downward acceleration while rising (u/s²) — the floaty top of the arc.
   */
  rise: number;
  /**
   * The downward acceleration while falling (u/s²) — the snappy descent (heavier than the rise). The world's own solved gravity field, where one is authored, overrides the MAGNITUDE but keeps this row's rise-to-fall ratio as the arc's asymmetry.
   */
  fall: number;
};

export type WorldHoldMedium = {
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
};

export type WorldHoldSpend = {
  /**
   * The declared state.body slot name.
   */
  state: string;
  /**
   * The positive rate the slot drains at, per second.
   */
  ratePerSecond: number;
};

export type WorldHostDefaults = {
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
   * The external-clock election policy, consumed at boot by the clock registry (which tolerates an unknown source id): null for the launcher's automatic election, or a non-whitespace source id / off. Shape-only validation (null or non-whitespace); the registry, not the validator, interprets the id.
   */
  genlock?: string | null;
  /**
   * The QUIC listen endpoint (host:port) the authoritative host binds for remote peer admission, or null to stay loopback-only (no socket ever opens). Durable configuration per the unification contract — the --listen CLI flag reflects it for a single run without editing the document. Shape-only validation (null or a non-whitespace host:port pair with a port 0..65535, where 0 binds any free port and the [world.listen: bound …] narration names the one bound); Server.WorldPeerHost is what actually parses and binds it.
   */
  listen?: string | null;
  /**
   * The QUIC endpoint at which this world's authority is reached when another world resolves it as a destination, or null when the authority is colocated with the resolver. Colocation short-circuits the authority transport; it does not select a separate transfer path.
   */
  authority?: string | null;
  /**
   * The preferred graphics backend (Auto is OS-portable), or null when BackendRow reads it from a row — omitting both reads as Auto.
   */
  backend?: WorldBackendPreference | null;
  /**
   * A scalar kind=Text state row whose slot names the backend token, read at boot after state first-fills. Declaring this beside a literal Backend is refused. This boot-only site (HostBackend) writes the settled preference into Backend, clears this facet, and narrates the settlement on stderr. The source row retains its value and draw bookkeeping. Its natural spelling is a weighted text source over the backend tokens (auto/directx/ vulkan — a one-context Markov table with bound 1, the degenerate flat weighted draw), parsed through ParseBackend at settle. A token naming no backend refuses by name. Drawing the name rather than an ordinal is deliberate: an ordinal draw over an enum silently re-points itself the day a member is inserted, and reads at the authoring site as a number nothing explains. Declared together with Backend it is refused by name — this record is a class, so presence is honestly observable here, unlike bodies.capacityRow's struct-typed site.
   */
  backendRow?: string | null;
  /**
   * The undo horizon, in journal entries: world.undo can never reach past this many trailing entries. 0 is unbounded — every world authored before this field existed keeps growing its journal for the life of the process, exactly as before. A positive depth bounds it: once the journal holds more than this many entries, the oldest ones fold forward into the base the journal already keeps (the same document-level replay WorldServer.ApplyUndo performs, run forward), so a checkpoint restore and a replay from the new base plus the retained tail still reproduce the live definition bit-identically. world.status echoes the authored value; world.undo names it when a requested count reaches past what the horizon has kept.
   */
  journalDepth?: number;
  /**
   * The window title the OS shows in the caption bar, the taskbar, and Alt+Tab, or null for the engine's own. Shape-only validation (null or non-whitespace).
   */
  title?: string | null;
  /**
   * The window and taskbar icon: a .ico path resolved against the world document's own directory, or an absolute path taken as-is, so an author's icon travels beside their world file. null — and equally a path naming a file this OS cannot read as an icon — wears the host executable's own icon resource instead, which no world has to ship. Shape-only validation (null or non-whitespace); the platform window backend is what reads the file.
   */
  icon?: string | null;
};

export type WorldHostPresentation = "Windowed" | "None" | "Offscreen";

export type WorldHudCursor = {
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
};

export type WorldHudCursorRole = "TextPrimary" | "TextDim" | "Accent" | "Phosphor";

export type WorldHudDefaults = {
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
};

/**
 * One HUD element row inside a WorldHudPanel — a stable id (unique within the owning panel), its kind, its local rect, its color role, an authored literal string (meaningful for Text), and an optional binding into the closed HudBindingVocabulary (meaningful for Text and Gauge — a bound text element's live value replaces the authored literal; a bound gauge element's live value drives its fill; an unbound gauge draws empty).
 */
export type WorldHudElement = {
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
  sources?: (WorldHudFrameCandidate | null)[] | null;
  /**
   * How long a Frame element cross-fades when its winning candidate changes; 0 cuts. Finite and non-negative.
   */
  fadeSeconds?: number;
};

/**
 * A WorldHudElement's rendered kind — the schema→render expansion cost differs per kind (see WorldHudCapacity).
 */
export type WorldHudElementKind = "Rect" | "Text" | "Gauge" | "Frame";

export type WorldHudElementList = (WorldHudElement | null)[];

/**
 * One ranked source candidate of a Frame element: the frame shown while When holds. Candidates are walked in authored order every frame and the first holding one wins; a null predicate always holds, so the last row is the default.
 */
export type WorldHudFrameCandidate = {
  /**
   * The sampled frame while this candidate wins.
   */
  source: WorldFrameSourceNonNullable;
  /**
   * The condition, evaluated for the panel's seat.
   */
  when?: OverlayPredicateNullable | null;
};

/**
 * How a Frame element maps its sampled frame's aspect ratio onto its own rect — the same uv-mapping choice a screen material or a UI image element makes.
 */
export type WorldHudFrameFit = "Cover" | "Contain" | "Stretch";

export type WorldHudLayer = "Under" | "Over" | "Replace";

/**
 * One HUD panel row — a stable id (unique within the section), a normalized viewport rect in screen space, which band it draws in, its chrome style, and its child elements. WorldMutation.UpsertHudPanel carries the whole row (elements included) as one cross-row transaction boundary; WorldMutation.UpsertHudElement/ WorldMutation.RemoveHudElement read-modify-write a single element within an already-declared panel.
 */
export type WorldHudPanel = {
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
};

/**
 * A WorldHudPanel's chrome recipe — the authored twin of Puck.Overlays.OverlayPanelStyle (Puck.World.Schema must not reference Puck.Overlays; the renderer maps this token to the concrete style).
 */
export type WorldHudPanelStyle = "Panel" | "Strip" | "Chip";

/**
 * A normalized rect (origin top-left, Y down) — a WorldHudPanel's rect is in screen space [0, 1] × [0, 1]; a WorldHudElement's rect is in its owning panel's local [0, 1] × [0, 1] space.
 */
export type WorldHudRect = {
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
};

export type WorldHudSection = {
  /**
   * The section defaults.
   */
  defaults: WorldHudDefaults;
  /**
   * Gets the authored world-scope panels. The absence-coalesce lives in the accessor for the same reason Elements's does.
   */
  panels: (WorldHudPanel | null)[];
};

/**
 * A WorldHudElement's color role — a curated authored subset of Puck.Overlays.OverlayColorRole (Puck.World.Schema must not reference Puck.Overlays; the renderer maps this token to the concrete role).
 */
export type WorldHudStyleToken = "Primary" | "Dim" | "Accent" | "Positive" | "Warning" | "Danger";

export type WorldIconBadgeOverride = {
  /**
   * The controller family name.
   */
  family: string;
  /**
   * The icon name this family shows instead of the row's default.
   */
  icon: string;
};

export type WorldIconBadgeRow = {
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
};

export type WorldIconGlyphRef = {
  /**
   * The font id (WorldIconFontCatalog).
   */
  font: string;
  /**
   * The glyph spelling: one literal character, or U+XXXX.
   */
  glyph: string;
};

export type WorldIconRow = {
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
};

export type WorldIconographySection = {
  /**
   * The icon rows.
   */
  rows?: (WorldIconRow | null)[] | null;
  /**
   * The badge-mapping rows.
   */
  badges?: (WorldIconBadgeRow | null)[] | null;
};

export type WorldIdentityDefinition = {
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
  /**
   * Gets the immutable selection of identity-owned record pools.
   */
  records?: CellName[] | null;
};

export type WorldIdentityFacts = {
  /**
   * The keyed Int row on the identity's document holding the facts.
   */
  state: CellName;
  /**
   * How many distinct facts the row holds; a write past it refuses by name.
   */
  capacity: number;
};

export type WorldIdentitySeed = {
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
};

export type WorldImport = {
  /**
   * The fragment's file path, resolved against the importing document's own directory exactly like basis.
   */
  document: string;
  /**
   * The alias, or null to compose the fragment's names unchanged. An alias is a bare identifier — a letter or underscore, then letters, digits, and underscores — so it heads the generated name alias$name each of the fragment's declarations composes as, and a source reads it as alias.name (TryValidateAlias).
   */
  as?: string | null;
};

export type WorldInputHoldAuthoring = {
  ceilingSeconds: number;
  lowerAfterSeconds: number;
  defaultSeconds: number;
  equalizeByDefault: boolean;
  participants: (WorldInputHoldParticipantAuthoring | null)[];
};

export type WorldInputHoldParticipantAuthoring = {
  bodyIndex: number;
  seconds: number;
  equalized: boolean;
};

export type WorldInteraction = {
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
  effects: (ActionEffect | null)[];
  /**
   * Whether the interaction fires every tick the co-occurrence holds For a distance interaction, at most this many right carriers per left carrier — the nearest first, ties broken by the lower body index — or null for every carrier in range. The work sheet prices the pair count at this budget. (Level) or once per crossing (Edge, the default — an interaction that transforms/spawns/despawns almost always wants Edge, for the same reason a rule that writes a row does: level-firing a spawn is a journal entry every tick the co-occurrence holds).
   */
  mode?: ActionTriggerMode;
  neighbours?: number | null;
};

export type WorldInteractionCoOccurrence = "Distance" | "Region";

export type WorldInteractionsSection = {
  /**
   * The declared interaction rows, in authoring/evaluation order.
   */
  interactions: (WorldInteraction | null)[];
};

export type WorldKit = {
  name: string;
  bodyMotionProgram: string;
  motion: WorldMotion;
  producers?: {
    [k: string]: BodyProgramParameters | null;
  } | null;
  actions?: {
    [k: string]: ActionSpec | null;
  } | null;
  collider?: WorldCollider | null;
  bodyContact?: WorldBodyContactMode;
  mass?: number;
  rigid?: WorldRigid | null;
  carry?: WorldCarry | null;
  tether?: WorldTether | null;
  pad?: {
    [k: string]: WorldPadElement;
  } | null;
  autonomy?: WorldAutonomyCadence | null;
};

export type WorldKitsSection = {
  /**
   * The declared kits, in order.
   */
  rows?: (WorldKit | null)[] | null;
  /**
   * The kit→entity assignment policy — ABSENT resolves to Default.
   */
  assignment?: WorldRowAssignment | null;
};

export type WorldLatticeFill = WorldLatticeFillRect | WorldLatticeFillNoise | WorldLatticeFillScatter | WorldLatticeFillDraw | null;

export type WorldLatticeFillDraw = {
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
};

export type WorldLatticeFillNoise = {
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
};

export type WorldLatticeFillRect = {
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
};

export type WorldLatticeFillScatter = {
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
};

export type WorldLatticeMedium = Record<string, never>;

export type WorldLatticeScalar = unknown;

export type WorldLook = {
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
};

export type WorldLookCue = {
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
};

export type WorldLookMotion = {
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
  /**
   * A creation frame name to state.<row>[.<key>] map (a key that is exactly $body is the wearing body's index): the frame holds while its cell's stored value is nonzero, and the first holding frame in declaration order overrides the cue and replay cursor — a blink, a wince, a mouth shape the simulation schedules. Legitimate only on a creation source.
   */
  poses?: {
    [k: string]: string | null;
  } | null;
  /**
   * Up to four optional expressions, in x/y/z/w order. Missing entries read zero. Expressions read eased state; each consumer defines its own numeric range.
   */
  lanes?: ExpressionProgramNonNullable2[] | null;
};

export type WorldLookSource = WorldLookSourceCatalog | WorldLookSourceCreation | null;

export type WorldLookSourceCatalog = {
  $type?: "catalog";
  /**
   * The procedural renderer catalog rig to pin, or null for the occupant-owned pick. A fresh occupant seeds that pick from its first local slot and carries it across authority transfers, so ordinary admission does not restyle it.
   */
  index: number | null;
};

export type WorldLookSourceCreation = {
  $type?: "creation";
  /**
   * The referenced Id, authored literally or through a Text state cell; it must resolve at validation.
   */
  prototypeId: DocumentIdentifier;
};

export type WorldLooksSection = {
  /**
   * The declared looks, in order.
   */
  rows?: (WorldLook | null)[] | null;
  /**
   * The look→entity assignment policy — ABSENT resolves to Default.
   */
  assignment?: WorldRowAssignment | null;
};

export type WorldMachine = {
  /**
   * The instance identity used by displays, controls, and hardware operations.
   */
  name: string;
  /**
   * The registered provider identifier.
   */
  engine: string;
  /**
   * The provider's versioned configuration, validated through its descriptor.
   */
  configuration: unknown;
  /**
   * Whether the host advances this instance. A stopped instance retains its hardware state.
   */
  running?: boolean;
  /**
   * Ordered hardware bindings, independent of any display.
   */
  memory?: (WorldMachineMemory | null)[] | null;
  /**
   * The optional standing cable endpoint owned by this machine instance.
   */
  cable?: WorldMachineCable | null;
};

export type WorldMachineCable = {
  /**
   * The cable's stable kebab-case name, shared by every plugged port.
   */
  name: string;
  /**
   * This machine's 0-based place in cable order — contiguous across the cable's ports (validated), and what decides the linking engine's deterministic player order.
   */
  position: number;
};

export type WorldMachineMemory = {
  /**
   * The binding identity within its machine.
   */
  name: string;
  /**
   * Whether values enter or leave world state.
   */
  direction: WorldMachineMemoryDirection;
  /**
   * The provider's hardware address space.
   */
  space: string;
  /**
   * The signed or unsigned scalar format: i8/u8, i16/u16, i32/u32, or i64/u64.
   */
  format: string;
  /**
   * The world-state row.
   */
  row: string;
  /**
   * An unsigned raw address; mutually exclusive with Symbol.
   */
  address?: number | null;
  /**
   * An exported content symbol; mutually exclusive with Address.
   */
  symbol?: string | null;
  /**
   * The key of a table-shaped state row, or null for a slot.
   */
  key?: string | null;
  /**
   * inspect for reads; patch or bus for writes.
   */
  access?: string;
  /**
   * onChange or everyTick. Repeating a hardware-visible write requires explicit everyTick authoring.
   */
  update?: string;
  /**
   * checked by default; truncate explicitly admits narrowing and bit reinterpretation.
   */
  conversion?: string;
};

export type WorldMachineMemoryDirection = "Read" | "Write";

export type WorldMarkerRing = {
  /**
   * The source row field the ring radius reads. The only field Speakers admits is radius (Radius); a row that is not a bed draws no ring under this policy without refusing (v1's one closed field name — a future source kind names its own).
   */
  field: string;
};

export type WorldMarkerRow = {
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
};

export type WorldMarkerSource = WorldMarkerSourceSpeakers | WorldMarkerSourcePoint | null;

export type WorldMarkerSourcePoint = {
  $type?: "point";
  /**
   * The marker's world position.
   */
  position: DocumentVector3;
};

export type WorldMarkerSourceSpeakers = {
  $type?: "speakers";
};

export type WorldMarkerStyle = {
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
};

export type WorldMemberIdentity = {
  /**
   * The identity issuer. It is non-empty and free of control characters.
   */
  issuer?: string;
  /**
   * The issuer's subject claim. It is non-empty and free of control characters.
   */
  subject?: string;
  /**
   * The optional validated world name for which the claim was attested.
   */
  world?: SafeName | null;
};

export type WorldMemberRef = {
  /**
   * The populated payload arm.
   */
  kind?: MemberRefKind;
  /**
   * The local principal, only for Local.
   */
  principal?: Principal | null;
  /**
   * The verified identity, only for Verified.
   */
  verified?: WorldMemberIdentity | null;
};

export type WorldMetadataAuthor = {
  /**
   * The author's display name.
   */
  name: string;
  /**
   * The author's object id, when the author chooses to attach one — see WorldObjectId. Authored, not authenticated: nothing here proves the name behind the id.
   */
  oid?: string | null;
};

export type WorldMetadataSection = {
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
    [k: string]: unknown;
  } | null;
};

export type WorldMotion = {
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
};

export type WorldMotionDefaults = {
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
};

export type WorldMusicRow = {
  /**
   * The row's stable name — its mutation address.
   */
  name: string;
  /**
   * The referenced document's file path, relative to the authoring document's directory or absolute.
   */
  source: string;
  /**
   * The SHA-256 hex64 of the referenced document's canonical bytes.
   */
  hash: string;
};

export type WorldNavigationConnectivity = "Axis" | "FacesAndEdges" | "Full";

export type WorldNavigationDomain = {
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
};

export type WorldNavigationKind = "Surface" | "Volume" | "Medium";

export type WorldNavigationSection = {
  /**
   * Finite rectangular domains, each compiled from the world's deterministic solid field.
   */
  domains?: (WorldNavigationDomain | null)[] | null;
};

export type WorldNavigationSharing = {
  /**
   * Resident destination-cell trees. A full cache with pending work refuses another destination as capacity-limited; it never launches an unbudgeted independent search.
   */
  goalCapacity: number;
  /**
   * Total reverse-Dijkstra expansions per simulation tick, shared fairly between pending resident goals. Each expansion inspects at most 26 edges. A tree can eventually settle every domain cell; the independent A* MaxExpandedNodes bound does not truncate a shared tree.
   */
  expandedNodesPerTick: number;
};

export type WorldObserverDisclosure = {
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
};

export type WorldObserverDisclosureMode = "All" | "Radius" | "SelfOnly";

export type WorldObstructionLatch = {
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
};

export type WorldOwnership = {
  /**
   * The owned thing.
   */
  subject: OwnershipSubject;
  /**
   * Who owns it.
   */
  owner: OwnershipOwner;
};

export type WorldPadElement = "LeftStickX" | "LeftStickY" | "RightStickX" | "RightStickY" | "LeftTrigger" | "RightTrigger" | "South" | "East" | "West" | "North" | "DpadUp" | "DpadDown" | "DpadLeft" | "DpadRight" | "LeftShoulder" | "RightShoulder" | "Start" | "Back";

export type WorldPatch = {
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
};

/**
 * One placement instance row — a creation asset stamped into the world by reference: transform + facets as data, addressed by its stable Id. A placement whose creation carries timeline frames is animated: it replays client-side on the render clock through the reserved dynamic-transform pool (distribution/mirror facets are static-stamp-only and reject on an animated row). A placement carrying an Inhabit facet is a live population body rather than furniture (see WorldPlacementInhabit); its declared creation eyes derive WorldCamera feeds and its declared faces derive screens (both at the delivery boundary, never written to the document).
 */
export type WorldPlacement = {
  /**
   * The row's stable string id (its mutation address). It may not carry : or be exactly $each (TryValidateId).
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
   * The placement's response facet (see WorldPlacementResponse) — the ordered prototype swaps a lattice-field or state-cell condition fires, or null for an ordinary placement that always shows PrototypeId. Omitted from the wire when null. Refused together with Attach, Inhabit, and FaceSources.
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
  /**
   * Which of Respond's entries held at the last response sweep, one bit per entry in authored order (bit i for entry i); 0 while none holds, and always 0 on a row without Respond. The response sweep writes it; the row shows the lowest set bit's entry, else its own authored PrototypeId (ShownPrototypeId). Refused on a row without Respond and with a bit at or past its entry count. A reader whose disclosure withholds a cell an entry reads is handed the mask without that entry's bit (Disclosed). Omitted from the wire when 0.
   */
  holding?: number;
};

/**
 * A placement's attach facet — binds the row's stamp to a live population body's transform, so the resolved world pose follows that body every tick (an avatar's hat, held item, nameplate, or aura) instead of sitting at the row's own authored Position/YawDegrees. The offset rides the body's own local frame — rotated by the body's orientation before adding, the Puck.SdfVm.Views.OrientedFollowRig/FirstPersonRig convention for a moving anchor, never the world-axis FollowRig shape a fixed subject would use. The resolved pose is never written back into the document, and it is derived twice, at two clocks, from the one authored facet: the authoritative answer is fixed point — the body's fixed-point pose composed with this facet's authored (float, quantized at resolution like every other placement field) offset, by Puck.World.Server.WorldPlacementAttachment.TryResolve, on demand by world.attachments and once per tick by attached local gravity areas;the rendered pose is presentation float — the same composition over the client's interpolated body pose, packed every frame by Client.WorldStampPool, which is what makes an attached row visibly ride its body as smoothly as the body itself. An attached row draws through that reserved stamp pool and not as a static stamp (Client.WorldPlacementStamper.IsStaticStamp), and it charges MaxStampRegistrations like an animated row does. Region, solid (under the analytic contact provider), and emission were once refused on the same row as this one because each read the row's own static transform — all three now read the same resolved dynamic pose instead (Server.WorldEventFeed.CollectRegions, Server.WorldColliderSet.RefreshAttached, Server.WorldGravityField.RefreshAttachedAreas, Client.WorldStampPool.TryShapePosition/RootPose), so a region's aura, an analytic collider's hitbox, and an emission's voice all track the carrier: an equipped item's sensing sphere, hitbox, or source point rides the body it is attached to. What stays refused: distribution/mirror (static-stamp-only, the same rule an animated or inhabited row already enforces), inhabit (a row cannot both spawn its own driven bodies and ride another's), and solid specifically under the field contact provider (it compiles every solid row's geometry once into one SDF program and never rebuilds it per tick) — refused by name rather than defining a blend (see WorldDefinitionValidator).
 */
export type WorldPlacementAttach = {
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
};

/**
 * A placement's board facet — the tabletop primitive. Anchors a state.lattices Grid topology (which already carries its own world-space origin/cellSize frame) to this placement, so a chess set, a checkers board, or a card table is one placement/body carrying one topology — carriable as a unit once an attachment primitive picks it up. A topology is carried by at most one placement (validated). Occupancy is the only row the engine reads; Turn/Verdict/ Move/Plan are author-named convenience bindings world.tabletop echoes together — ordinary declared rows, never engine-interpreted, so this facet stays a reusable primitive rather than a chess-specific feature.
 */
export type WorldPlacementBoard = {
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
};

/**
 * A placement's contribution facet: the row is a slot whose frame (id, pose, scale, and these lifecycle terms) is host-authored and whose creation a federation partner supplies through ordinary mutations. Null is an ordinary placement.
 */
export type WorldPlacementContribution = {
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
  contributor?: Principal | null;
  /**
   * Server-stamped. The simulation tick at or after which the presence sweep retracts this slot; null while the link is reachable.
   */
  retractDeadlineTick?: number | null;
};

/**
 * A placement's deal facet: the row is a template whose instances are dealt from a keyed state row, one child placement per cell, laid out over the template's own Distribution region in cell order. The template itself renders nothing and collides with nothing; its prototype and its Solid/Grip/Region/ Emission facets are what every child is stamped with. A child is an ordinary placement row named <template>/<cellKey> (ChildId) with Parent naming the template and the region's dealt offset as its local position, written and removed through ordinary UpsertPlacement/RemovePlacement mutations under Principal.World by the per-tick sweep (Server.WorldTick.SweepPlacementDeals), so a dealt instance journals, undoes, replays, and rebuilds colliders through the one placement door. Every template a tick re-deals lands in that tick's one mutation.
 */
export type WorldPlacementDeal = {
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
};

/**
 * Instance-owned properties which reconciliation seeds at creation and subsequently preserves. Membership and the allocated deal slot remain owned by the source row.
 */
export type WorldPlacementDealPreserve = {
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
};

/**
 * A deal facet's variant selection: a keyed row read at the dealt cell's key, and the map from that cell's text to the prototype the child is dealt with.
 */
export type WorldPlacementDealVariants = {
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
};

/**
 * A per-instance override of one declared creation face's feed — the face twin of the emission facet's per-instance override channel.
 */
export type WorldPlacementFace = {
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
};

export type WorldPlacementFaceList = (WorldPlacementFace | null)[];

/**
 * A placement's grip facet — overrides the world's DefaultHold hold policy for every collider this row compiles, composing as the tighter authoring layer: present, it decides; absent, the row's colliders fall back to the world default. Requires Solid (nothing else compiles a collider a grip trait could apply to). Every collider a distribution/mirror expands from one row shares the row's single grip decision — a lattice of holdable handholds is authored as one placement, not one per copy.
 */
export type WorldPlacementGrip = {
  /**
   * Whether a body's surface hold may take this row's compiled surface(s), overriding the world default.
   */
  holdable: boolean;
};

/**
 * A placement's inhabit facet — the row's binding to live population bodies. An inhabited placement is a normal entry in the entity table: it holds a Puck.World.Server.WorldBody, integrates under the named kit, and is addressable as Entity like any avatar. Its stamp rides the body's pose instead of the row's static transform; the row's position/yaw become its spawn pose. Absent (null) = decoration, the unchanged furniture behaviour.
 */
export type WorldPlacementInhabit = {
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
   * How many bodies: an authored literal, or a live cell reference naming an Int state.world row whose value the population admits and retires bodies to track (WorldPopulation.ReconcileInhabitCounts) — the spawner primitive a crawl's mob generator rides. Either way, bounded by the world's authored peer capacity; a cell reference is additionally bounded by Distribution's own sample count, the tighter of the two winning. Absent is an authored literal of 1 — see ResolvedCount.
   */
  count?: WorldPlacementInhabitCount;
  /**
   * The region and deterministic fill sequence that place the bodies relative to the placement root.
   */
  distribution?: WorldDistribution | null;
};

export type WorldPlacementInhabitCount = number | {
  [k: string]: unknown;
};

/**
 * A reflection plane in a placement's local frame.
 */
export type WorldPlacementMirror = {
  /**
   * The plane normal.
   */
  normal: DocumentVector3;
  /**
   * The signed plane offset along the normalized Normal.
   */
  offset: number;
};

export type WorldPlacementPolicyDefaults = {
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
};

/**
 * A WorldPlacementFace's portal facet — the authored decision that a face is a door: which WorldDestination row it leads to, and under what travel scope. Absent (the default) means the face is not a door — nothing here fires anything by itself; turning the decision into a diegetic step-into trigger is WorldInstanceHost.TriggerPortal's job, never this facet's. Durability, scope, and process-local instance selection live on the named WorldDestination row this facet points at, not here — a facet composes one destination selection with a travel scope, never re-authors how that destination is minted. Extensible deliberately (an optional-member record, the same widen-without-moving-existing-members shape WorldLatticeMedium's own remarks describe): a future fact-gate field (an authored predicate a traveler must satisfy to pass) adds cleanly as a new trailing member.
 */
export type WorldPlacementPortal = {
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
};

/**
 * Author-selected work and payment policy for rearranging a deal template's children.
 */
export type WorldPlacementReflow = {
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
};

/**
 * A placement's region facet — a named volume row, not a trigger system: any placement may carry one, turning its stamp into a sensing volume the world-events feed watches for body enter/exit edges (see Server.WorldEventFeed and the observe region:<name> grant subject). The region's name is the carrying placement's Id — one identity, never a second string kept in sync by hand. The volume is a sphere centered on the placement's Position (the placement's own Scale/YawDegrees do not affect it — a region's size is its own authored radius, never derived from the creation's visual bounds). Presentation-only in itself (drawing no geometry); sensing reads the same document-authored center every tick, converted to fixed-point at the same boundary WorldSolid facets already cross through — unless the row also carries Attach, in which case the center is the resolved live body pose instead (Server.WorldEventFeed.CollectRegions, the same resolve world.attachments answers): the sensing sphere follows the carrier, and an inactive carrier senses nobody rather than sensing at a stale point.
 */
export type WorldPlacementRegion = {
  /**
   * The sensing radius, world units. Must be finite and positive (validated).
   */
  radius: number;
};

/**
 * One entry of a placement's response trait: while When holds, the row draws and collides as PrototypeId instead of its authored PrototypeId — the bridge that lets a placement react to live simulation state (a burning tree shows a charred stump; a filled account swaps a granary's face). Absent Respond, the placement always shows its authored prototype.
 */
export type WorldPlacementResponse = {
  /**
   * The condition tested every sweep — a lattice-field read or a state-cell read.
   */
  when: WorldPlacementResponseCondition;
  /**
   * The creation the placement shows/collides as while When holds and no earlier entry does. Must resolve to a declared, non-animated creation row.
   */
  prototypeId: string;
};

/**
 * The closed union a response entry's When speaks. Both arms evaluate against the SAME per-tick document the rule frame folds a rule's writes into before this sweep runs (Server.WorldTick.SweepPlacementResponses runs after EvaluateWorldRules' own end-of-tick fold), so a cell a rule wrote this tick already reads through StateCondition on the same tick's sweep.
 */
export type WorldPlacementResponseCondition = WorldPlacementResponseConditionFieldCondition | WorldPlacementResponseConditionStateCondition | null;

/**
 * The original lattice-field condition, unchanged from before this union existed: the named field read at the placement's own coupled cell, compared against a literal or another row's slot cell.
 */
export type WorldPlacementResponseConditionFieldCondition = {
  $type?: "field";
  /**
   * The field read at the cell.
   */
  field: string;
  /**
   * The comparison.
   */
  comparison: ExpressionComparison;
  /**
   * The scalar compared against (literal or state-row reference) — the same WorldLatticeScalar grammar a fields.reactions Transform/Expose condition already uses.
   */
  value: WorldLatticeScalar | null;
};

/**
 * A state-cell condition: compares State's cell (its slot cell when Key is absent, else the cell at Key) against Value, or — when ComparandState is authored instead — against another declared row's cell, read live at the same evaluation. Exactly one of Value and ComparandState may be present, the same one-comparand rule CompareState already enforces for a rule's own gate — this facet reuses that field convention rather than inventing a second reading of "compare a state cell." Independent of the placement's position, and of whether the document declares a fields section at all.
 */
export type WorldPlacementResponseConditionStateCondition = {
  $type?: "state";
  /**
   * The state row compared — any declared state.world row of a numeric kind (never Text).
   */
  state: string;
  /**
   * The comparison.
   */
  comparison: ExpressionComparison;
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
};

export type WorldPlacementResponseConditionStateConditionBare = {
  /**
   * The state row compared — any declared state.world row of a numeric kind (never Text).
   */
  state: string;
  /**
   * The comparison.
   */
  comparison: ExpressionComparison;
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
};

export type WorldPlacementResponseList = (WorldPlacementResponse | null)[];

/**
 * The contract a named placement-local spatial volume contributes to queries.
 */
export type WorldPlacementSpatialRole = "Occupation" | "Clearance" | "Influence";

/**
 * A named spatial volume authored on one placement. The name is placement-local metadata; an influence channel is opaque to the engine and is never interpreted as water, power, or another game noun.
 */
export type WorldPlacementSpatialVolume = {
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
};

export type WorldPlacementSpatialVolumeList = (WorldPlacementSpatialVolume | null)[];

export type WorldPlacementsSection = {
  /**
   * The placements, in order.
   */
  rows?: (WorldPlacement | null)[] | null;
  /**
   * The live-placement policy — ABSENT derives from Rows (DeriveFrom: no live authoring, the scale envelope the rows span).
   */
  policy?: WorldPlacementPolicyDefaults | null;
};

export type WorldPlayerDefaults = {
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
};

export type WorldPoolBodyBinding = {
  member: CellName;
  placement?: string | null;
  seat?: number | null;
};

export type WorldPoolBodyCarrier = {
  pool: CellName;
  field: CellName;
  /**
   * Gets the immutable mapping, with exactly one entry for each enum member.
   */
  bindings: (WorldPoolBodyBinding | null)[];
};

/**
 * The three independently authored sequences that seed a body's producer state.
 */
export type WorldPopulationVariation = {
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
};

/**
 * Where a traveler lands at a WorldPlacementPortal's destination — the positional-continuity decision a portal facet authors on top of which destination it names.
 */
export type WorldPortalArrival = "spawn" | "mapped";

export type WorldPortalDefaults = {
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
};

export type WorldPortalTravel = "party" | "body";

export type WorldPortalsSection = {
  /**
   * The travel default every portal facet in this document falls back to when it authors no Travel of its own.
   */
  portalDefaults: WorldPortalDefaults;
};

/**
 * The named composition channel's own live read is at or above its declared threshold. Legitimate only inside a kit's shaping-row gate, where the world's channel table resolves Channel to an ordinal at kit-compile time.
 */
export type WorldPredicateHeld = {
  $type?: "held";
  /**
   * The declared composition channel name.
   */
  channel: string;
};

/**
 * The fact holds this tick.
 */
export type WorldPredicateNow = {
  $type?: "now";
  /**
   * The body fact.
   */
  fact: ActionFact;
};

/**
 * The fact held within the last WindowSeconds — a per-instance recency clock, refreshed while the fact holds and decaying otherwise (coyote time is Recently(Grounded, w)).
 */
export type WorldPredicateRecently = {
  $type?: "recently";
  /**
   * The body fact.
   */
  fact: ActionFact;
  /**
   * The recency window.
   */
  windowSeconds: number;
};

/**
 * Whether a named timer slot has drained.
 */
export type WorldPredicateTimerElapsed = {
  $type?: "timerElapsed";
  /**
   * The timer slot.
   */
  state: string;
};

export type WorldProbe = {
  /**
   * The probe's own name — SafeName-shaped, unique among the rows; probe.status and probe.record address it.
   */
  id: string;
  /**
   * The registered probe kind id (a puck.probe.manifest.v1 manifest's file stem) — checked against the shipped vocabulary at document load (IsRegisteredProbeKind), never interpreted here. The document never states where the kind runs; the kind's own registration decides kernel-on-device versus out-of-process model.
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
    [k: string]: WorldFrameSource | null;
  } | null;
  /**
   * A recorded puck.probe.track.v1 document path, resolved against the world document's own directory, played back in place of every socket at once — probe.record's own output shape, the hardware-free leg every probe admits. Mutually exclusive with Inputs. Omitted from the wire when null.
   */
  track?: string | null;
  /**
   * The kind's config values, or null when the kind declares none or every field has a default. Not validated at document load — the kind's own manifest config schema validates it at boot, matching Config's shallow-then-deep precedent.
   */
  config?: unknown;
  /**
   * What this probe's channels drive, or null for a probe that is only read back.
   */
  bindings?: (WorldProbeBinding | null)[] | null;
};

export type WorldProbeBinding = WorldProbeBindingAxis | WorldProbeBindingParameter | WorldProbeBindingControl | null;

export type WorldProbeBindingAxis = {
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
};

export type WorldProbeBindingControl = {
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
};

export type WorldProbeBindingParameter = {
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
};

export type WorldProbeParameterTarget = WorldProbeParameterTargetExtension | WorldProbeParameterTargetProbe | null;

export type WorldProbeParameterTargetExtension = {
  $type?: "extension";
  /**
   * The render.extensions[].id entry this targets — must name an entry the document itself composes.
   */
  id: string;
  /**
   * The extension's config field name — checked against its manifest at boot, never here (the same shallow-then-deep precedent every kind-vocabulary field follows).
   */
  field: string;
};

export type WorldProbeParameterTargetProbe = {
  $type?: "probe";
  /**
   * The probes[].id this targets — a row of this document other than the binding's own.
   */
  id: string;
  /**
   * That probe's kind config field name — checked against its manifest at boot, never here.
   */
  field: string;
};

export type WorldPropertyRegistrySection = {
  /**
   * The declared property vocabulary — unique, non-empty, each naming a declared keyed int state row of the same name.
   */
  names: (string | null)[];
  /**
   * Gets the immutable dynamic pool-to-body bindings.
   */
  carriers?: (WorldPoolBodyCarrier | null)[] | null;
};

export type WorldPrototype = {
  /**
   * The row's stable string id — its mutation address and the handle placements reference — authored literally or through a Text state cell.
   */
  id: DocumentIdentifier;
  /**
   * The canonical (validated + normalized) creation document.
   */
  document: {
    /**
     * The document version tag (puck.creation.v1).
     */
    schema?: string | null;
    /**
     * The creation's handle, authored literally or through a containing world's Text state cell; normalization narrows a literal value to letters, digits, dashes, and underscores.
     */
    name?: unknown;
    /**
     * The material palette (null = the default sweep).
     */
    palette?: ({
      /**
       * The base color as #RRGGBB (see HexColor).
       */
      color: string;
      /**
       * The emissive strength (null = 0 — optional members are nullable and normalized at load).
       */
      emissive?: number | null;
      /**
       * The specular strength (null = the material default).
       */
      specular?: number | null;
      /**
       * The GGX roughness in [0, 1] (null = DefaultRoughness) — see SdfMaterial for the roughness-floor formula. A document still spelling shininess is an unmapped member and is refused.
       */
      roughness?: number | null;
      /**
       * The fresnel edge-lift strength in [0, 1] (null = 0 — no lift) — see SdfMaterial.
       */
      sheen?: number | null;
      /**
       * The metalness in [0, 1] (null = 0 — dielectric) — see SdfMaterial.
       */
      metal?: number | null;
      /**
       * The clearcoat strength in [0, 1] (null = 0 — no coat) — see SdfMaterial.
       */
      coat?: number | null;
      /**
       * Optional generic coverage and reveal surfaces.
       */
      weathering?: {
        edge?: number;
        lines?: number;
        settle?: number;
        reach?: number;
        seed?: number;
        scale?: number;
        floor?: number;
        lane?: number;
        under?: ({
          threshold: number;
          surface: {
            color: string;
            roughness: number;
            metal: number;
          };
        } | null)[] | null;
        deposit?: {
          color: string;
          roughness: number;
          metal: number;
        } | null;
      } | null;
      /**
       * The wrap-lighting share in [0, 1] (null = 0 — the plain Lambert term) — see Wrap.
       */
      wrap?: number | null;
      /**
       * The shading-normal broadening in [0, 1] (null = 0 — no broadening) — see Soften.
       */
      soften?: number | null;
      /**
       * The bounce tint as #RRGGBB or a state binding (null = black — no bounce) — see Bounce.
       */
      bounce?: string | null;
      /**
       * Optional refractive paint layer.
       */
      inset?: {
        origin: unknown;
        rotation: unknown;
        depth: number;
        ior: number;
        paint: {
          stops: ({
            radius: number;
            color: string;
          } | null)[];
          softness?: number;
          modulationAmplitude?: number;
          modulationFrequency?: number;
          seed?: number;
        };
      } | null;
    } | null)[] | null;
    /**
     * The authored shapes (null = empty).
     */
    shapes?: ({
      /**
       * The shape's stable id.
       */
      id: number;
      /**
       * The optional player-given name, authored literally or through a containing world's Text state cell.
       */
      name?: unknown;
      /**
       * The primitive.
       */
      type: "Sphere" | "Box" | "Torus" | "Cylinder" | "Capsule" | "Ellipsoid" | "RoundCone" | "Plane" | "Cone" | "Prism" | "Superellipsoid" | "Sweep";
      /**
       * The shape's position (the author frame's workbench space).
       */
      position: unknown;
      /**
       * The orientation.
       */
      rotation: unknown;
      /**
       * The per-axis scale — the primitive's size (see CreationGeometry).
       */
      scale: unknown;
      /**
       * The palette slot (null = 0).
       */
      material?: number | null;
      /**
       * The blend op name (null = Union).
       */
      blend?: "Union" | "SmoothUnion" | "Subtraction" | "Intersection" | "Xor" | "SmoothIntersection" | "SmoothSubtraction" | "ChamferUnion" | "ChamferIntersection" | "ChamferSubtraction" | "GrooveUnion" | "PipeUnion" | "Morph" | "GrooveSubtraction" | "PipeSubtraction" | "StairsUnion" | "StairsSubtraction" | null;
      /**
       * The smooth-blend radius (null = 0).
       */
      smooth?: number | null;
      /**
       * The composition group (null = ungrouped).
       */
      group?: number | null;
      /**
       * The shape's local twist rate (null = 0).
       */
      twist?: number | null;
      /**
       * The shape's shell thickness (null = 0, solid).
       */
      onion?: number | null;
      /**
       * The shape's local bend rate about Y (null = 0).
       */
      bend?: number | null;
      /**
       * The shape's inflation radius (null = 0).
       */
      dilate?: number | null;
      /**
       * The ordered SDF VM domain operators (ShapeDomainOp) applied to the shape's point in creation space, before its own translate/rotate/scale (null = none). Every op the copy expansion covers reaches contact as well as render; wallpaper does not expand, and a solid placement carrying one is refused at validation.
       */
      domain?: ({
        $type?: "symmetry";
        /**
         * The plane normal, in the shape's creation-space frame (normalized at canonicalization; a zero/non-finite normal falls back to UnitX — the retired Mirror: true flag's exact fold).
         */
        normal: unknown;
        /**
         * The plane's signed offset along Normal (null = 0, through the creation origin).
         */
        offset?: number | null;
      } | {
        $type?: "repeat";
        /**
         * The per-axis cell spacing, creation units (clamped to >= 0.001 per axis, matching the builder's own floor).
         */
        spacing: unknown;
        /**
         * The per-axis repeat-cell limit — the lattice spans cell indices -limit..+limit (null = UnboundedLimit per axis, far past any authored reach).
         */
        limit?: unknown;
        /**
         * The point the lattice folds around, creation units (null = the creation origin, the fold this op has always used). Cell selection centres on this point instead of the creation root — the lattice of physical copies is unchanged, so a null origin is byte-identical to today's fold.
         */
        origin?: unknown;
      } | {
        $type?: "polar";
        /**
         * The sector count around the axis (clamped >= 1).
         */
        count: number;
        /**
         * The rotation axis (null = Y, the XZ ground plane).
         */
        axis?: "X" | "Y" | "Z" | null;
        /**
         * Whether adjacent sectors mirror across their shared bisector (null = false).
         */
        mirror?: boolean | null;
        /**
         * The per-sector palette stride (null = 0, geometric only).
         */
        materialStride?: number | null;
        /**
         * The point the sector fold pivots around, creation units (null = the creation origin, the pivot this op has always used). A null origin is byte-identical to today's fold.
         */
        origin?: unknown;
      } | {
        $type?: "wallpaper";
        /**
         * The wallpaper group.
         */
        group: "P1" | "P2" | "Pm" | "Pg" | "Cm" | "Pmm" | "Pmg" | "Pgg" | "Cmm" | "P4" | "P4M" | "P4G" | "P3" | "P3M1" | "P31M" | "P6" | "P6M";
        /**
         * The lattice cell extents in the fold plane, creation units.
         */
        cell: unknown;
        /**
         * The repeat-cell limit per plane axis (null = UnboundedLimit per axis).
         */
        limit?: unknown;
        /**
         * The fold plane (null = XZ).
         */
        plane?: "XZ" | "XY" | "YZ" | null;
        /**
         * The parity-material stride (null = 0, geometric only).
         */
        materialStride?: number | null;
        /**
         * The symmetry-LOD distance threshold (null = 0, off).
         */
        lodDistance?: number | null;
      } | null)[] | null;
      /**
       * The driver-fed rotations this shape rides (ShapeSwingDocument), at most MaxSwings (null = none).
       */
      swings?: ({
        /**
         * The Name supplying (φ, w). An unresolvable name is refused at canonicalization.
         */
        driver: string;
        /**
         * The joint the shape turns about, in the creation's author frame (see CreationFrame) — a shoulder or a hip, not the shape's own centre.
         */
        pivot: unknown;
        /**
         * The rotation axis, in the creation's author frame; normalized at canonicalization. A positive angle turns right-handed about it.
         */
        axis: unknown;
        /**
         * The peak angle in radians, magnitude at most MaxAmplitude. A negative amplitude mirrors the swing, which is how a limb on the body's other side reads.
         */
        amplitude: unknown;
        /**
         * The phase offset added to the driver's phase, radians (null = 0) — how two limbs on one driver are put out of step (contralateral arms differ by π).
         */
        phase?: unknown;
        /**
         * The waveform name (null = Sine); see CreationWave.
         */
        wave?: string | null;
      } | null)[] | null;
      /**
       * The driver-fed translations this shape rides (ShapeSlideDocument), at most MaxSlides (null = none). Applied after every swing, along the authored axis. Both facets are presentation-only — composed onto the shape's per-frame dynamic transform and read nowhere else, so the SDF program, the colliders, the solid field, and simulation state are all blind to them. A shape carrying Domain rides its own slot packed with its Parent chain's rigid delta (the identity with no parent), leaving nowhere for its OWN swing/slide to compose, so they are refused alongside a domain list at validation.
       */
      slides?: ({
        /**
         * The Name supplying (φ, w). An unresolvable name is refused at canonicalization.
         */
        driver: string;
        /**
         * The slide direction, in the creation's author frame; normalized at canonicalization.
         */
        axis: unknown;
        /**
         * The peak offset in creation units, magnitude at most MaxAmplitude.
         */
        amplitude: unknown;
        /**
         * The phase offset added to the driver's phase, radians (null = 0).
         */
        phase?: unknown;
        /**
         * The waveform name (null = Sine); see CreationWave.
         */
        wave?: string | null;
      } | null)[] | null;
      /**
       * The Name of the shape this one hangs from (null = the creation root): the parent's animated motion carries this shape and its own pivots with it, so a forearm swung at the elbow also rides the upper arm's swing at the shoulder. The parent must be declared earlier in shapes (so a chain resolves in one pass and can never cycle); presentation-only on the same terms as the swings it composes with.
       */
      parent?: string | null;
      /**
       * The point this shape hinges about when it acts as a bone of a CreationEffectorDocument chain and declares no Swings (null = the pivot of the first swing, or the shape's own position when it has neither). Author-frame, like every other point here. Read only by the effector solve, so it changes no pose on its own.
       */
      joint?: unknown;
      /**
       * Only for Prism: top width divided by bottom width, finite in [0, 1]. Null uses 0.5; zero makes a triangular profile and one a rectangular profile. Scale holds bottom half-width, half-height and extrusion half-depth. Rotate the prism to reverse its taper.
       */
      taper?: number | null;
      /**
       * Optional Prism cross-section controls. Null uses a trapezoid; other profiles expose rounded rectangles, polygons, and ellipses with flat extruded caps.
       */
      profile?: {
        /**
         * The profile family.
         */
        kind: "Trapezoid" | "RoundedRectangle" | "Polygon" | "Ellipse" | "ChamferedRectangle" | "Convex" | "Path";
        /**
         * A fraction of the profile's smaller half-extent, in [0, 1]: RoundedRectangle's corner radius, ChamferedRectangle's chamfer radius, or Convex's uniform corner rounding.
         */
        cornerRadius?: number;
        /**
         * Polygon side count, from 3 through 32.
         */
        sides?: number;
        /**
         * Convex only: 3 to MaxConvexVertices local XY points, clockwise (X right, Y up — each turn's 2D cross product of consecutive edges strictly negative), no coincident or collinear vertices, and convex. Ignored by every other kind.
         */
        vertices?: unknown[] | null;
        /**
         * Required only for Path; its points are normalized profile coordinates.
         */
        path?: {
          contours: ({
            start: [number, number];
            segments: ({
              end: [number, number];
              control?: unknown;
              control2?: unknown;
              arcCenter?: unknown;
              clockwise?: boolean;
            } | null)[];
            closed?: boolean;
          } | null)[];
          tolerance?: number;
          stroke?: {
            radiusStart: number;
            radiusEnd: number;
            smooth?: boolean;
            from?: number;
            to?: number;
          } | null;
          shear?: {
            linear: number;
            quadratic?: number;
            cubic?: number;
            offset?: number;
            from?: number;
            to?: number;
            target?: number;
          } | null;
        } | null;
      } | null;
      /**
       * Only for Prism: how the XY profile becomes a solid. Null or extrude sweeps it along local Z to scale.z. revolve sweeps it about the local Y axis with scale.z as the radial offset — zero gives a solid of revolution (a cowl, a boot cuff, a pauldron dome), a positive offset a ring. Renderable for every profile; a solid placement refuses it, because both contact compilers read a per-axis-scaled local box and a revolve reads scale.z as an offset rather than an extent.
       */
      lift?: "Revolve" | "Extrude" | null;
      /**
       * Only for Prism, Cylinder and Cone: the edge-rounding radius in the creation's own units, finite and non-negative. The profile (and, for an extruded prism, its half-depth) insets by it and the field offsets back out, so the shape keeps its authored extent and its edges fillet. Refused by name past MaxRounding.
       */
      rounding?: number | null;
      /**
       * The shape's second-material inset face region (null = none) — see ShapePanelDocument. Render-only; refused by name on a Plane, on a shape carrying Domain ops or a non-zero Group, and on a creation that otherwise needs its own field scope.
       */
      panel?: {
        /**
         * How far the panel copy shrinks on every local axis before it is placed — the copy's own scale is shape scale − Inset per axis (floored at MinimumScale), a sharp-cornered shrink rather than a rounded Minkowski erosion: MaxFieldScopeDepth is 1, so nothing is free to isolate a Dilate field op to the copy alone inside the shared scope this and the plate ride. Creation units. Finite and non-negative; refused by name past the shape's smallest local half-extent (the smallest of HalfExtent over the X, Y, and Z local axes) — past that the eroded copy is empty everywhere.
         */
        inset: number;
        /**
         * How far the panel's own face sits from the plate's face along Face, in creation units, exact whatever the Inset. Positive recesses it: the eroded copy is composed with Subtraction so its near face becomes the recess floor exactly Depth below the plate's face — the floor and walls shade with Material, the subtraction-shades-with-the-subtrahend convention the Moth's hood opening rides. Negative raises the panel proud by |Depth| instead: the copy is composed with Union, its far face standing |Depth| past the plate's own and its back end buried inside the plate. Finite; refused by name past ±2·h′, the eroded copy's own full extent along Face — deeper, a recess carves an enclosed void and a raise floats detached, both silent no-ops on the surface. See Resolve for the offset each case translates the copy by.
         */
        depth: number;
        /**
         * The panel's own palette slot — see Material. Clamped into [0, CreationDocument.PaletteSize) at normalization.
         */
        material: number;
        /**
         * The local direction the panel erodes and offsets against, read in the primitive's own local frame — before its placement transform, the same frame Domain ops read. Null defaults to +Z (the Prism extrude axis / a Box's front); a non-finite or zero-length authored value is refused by name, and a finite non-unit one is normalized at canonicalization, mirroring a domain op's own normal. Any primitive may author a curved face (a Cylinder's +X rim, say) — the erode/translate/blend recipe reads only HalfExtent along the axis, never the primitive's curvature.
         */
        face?: unknown;
      } | null;
      /**
       * Only for Box, Cylinder and Prism (an extruded prism only — a revolve has no cap seam to bevel): the 45-degree edge-chamfer radius in the creation's own units, finite and non-negative. Refused by name past MaxChamfer, and refused by name alongside a nonzero Rounding on the same shape.
       */
      chamfer?: number | null;
      /**
       * The shape's second-material surface bands against other, earlier-declared shapes (null = none), at most MaxTrims — see ShapeTrimDocument. Render-only, like Panel; refused by name on a shape carrying Domain ops, a non-zero Group, or on a creation that otherwise needs its own field scope.
       */
      trims?: ({
        /**
         * The Name of another shape in the same creation, declared EARLIER in shapes (so a chain resolves in one pass and can never cycle — mirrors Parent's own rule). A Subtraction cutter riding the host's own Group is the common case; a plain plane-like Box with blend Union works too. Only the reference's own primitive geometry (type, scale, taper, profile, lift, rounding, chamfer) and pose are re-read; its own blend, domain, panel, and trims play no part in the band.
         */
        shape: string;
        /**
         * How far outward from the reference shape's own surface the band reaches, in creation units. Finite and positive; refused by name otherwise. Baked into the reference copy's own scale (a sharp-cornered growth — the mirror image of, and the same imprecision, Inset's own erosion already accepts) rather than a field-op dilation: it is the only way the reference's own offset survives composing through the macro's Intersection blend inside the one field scope both copies share — a field op applied after that blend would dilate the ALREADY-INTERSECTED result (eroding the host's own copy a second time by Width too), not the reference alone.
         */
        width: number;
        /**
         * The trim's own palette slot — see Material. Clamped into [0, CreationDocument.PaletteSize) at normalization. Used for both copies the macro composes, so whichever term the blend resolves to at a given point still paints the same color.
         */
        material: number;
        /**
         * The margin the trim's own scope must beat the host's already-present, unmodified surface by to render at all — a real Dilate field op at a POSITIVE radius on the host's own copy (exact and isolated: the copy is alone in the trim's scope at that point, so nothing else is affected). The scope pops via Union into the accumulator the host's own plain shape already folded into, and max(a, b) >= a makes a genuine erosion (a negative radius) provably always lose to that plain surface — the trim would never render — so the copy is instead nudged narrowly CLOSER than the plain surface by Inset, imperceptibly proud rather than inward: it loses everywhere by default (small) and wins only where the reference's own dilated copy does not additionally push the Intersection candidate past it, i.e. near the reference. Creation units, finite, in [0, MaxInset]; refused by name otherwise. Default 0.003 — just enough to clear the plain surface without z-fighting, the pipeline's own shin margin.
         */
        inset?: number;
      } | null)[] | null;
      /**
       * Whether this shape is SHADING-ONLY (null = false): skipped by the beam and fine march (so it never carves the silhouette, the contact field, or the collider) and included only in the shading evaluation at an already-found hit, where its own material and its perturbation of the surface normal paint its footprint — a seam (a thin subtraction) or a rivet (a small union, its own material) that stays a crisp mark at any distance instead of dotting out. Refused by name alongside Panel or Trims on the same shape.
       */
      detail?: boolean | null;
      /**
       * The shape's radial flare warp (null = none) — see ShapeFlareDocument. Admitted on every primitive; applies to the shape's own local point after Twist/Bend, in that order (twist, then bend, then flare), before the primitive's own scale. Render-only, like Twist/Bend.
       */
      flare?: {
        /**
         * The linear flare rate at the far end of the span (dimensionless; s(1) = StartScale + Amount). Clamped to [MinAmount, MaxAmount].
         */
        amount: number;
        /**
         * The mid-span sinusoidal bulge amplitude (dimensionless, peaks at the span's midpoint). Clamped to [-MaxBulge, MaxBulge].
         */
        bulge: number;
        /**
         * The axial distance the profile runs over, in creation units; finite and strictly greater than zero, or the shape is refused at validation.
         */
        span: number;
        /**
         * The axial coordinate where the profile begins, in creation units (null = 0).
         */
        top?: number | null;
        /**
         * Profile axis in [0, 2].
         */
        axis?: number;
        /**
         * Positive radial scale at the span origin.
         */
        startScale?: number;
      } | null;
      /**
       * Only for Superellipsoid: the generalizing exponent, finite in [MinSuperellipsoidExponent, MaxSuperellipsoidExponent] (null = MinSuperellipsoidExponent, the ellipsoid limit — an unauthored Superellipsoid is a plain ellipsoid).
       */
      exponent?: number | null;
      /**
       * Whether this shape casts soft shadows and contributes ambient occlusion (null = true). False excludes it from ONLY the soft-shadow and ambient-occlusion field walks — it still carves the silhouette, the contact field, and the collider like any ordinary shape, unlike Detail (which excludes from every march). For a small part whose own penumbra/AO cost outweighs its visual contribution (an eyelid, a rivet, a groove) — see Secondary.
       */
      secondary?: boolean | null;
      /**
       * The shape's Gaussian domain-push bumps (null = none), at most MaxBumps — see ShapeBumpDocument. Applied to the shape's own local point after Shear, before the primitive's own scale. Render-only, like Twist/Bend/Flare.
       */
      bumps?: ({
        /**
         * The bump's local center, in creation units.
         */
        center: unknown;
        /**
         * The per-axis Gaussian falloff radii, in creation units — clamped away from zero at normalization (GaussianPushMinRadius) so the exponent's divisor is never zero. Refused by name if any authored component is negative or non-finite (a magnitude is what the field reads; a negative radius states nothing a positive one does not).
         */
        radii: unknown;
        /**
         * The peak displacement at the center, in creation units (zero = an exact identity, still admitted — it still charges the per-copy instance count).
         */
        push: unknown;
      } | null)[] | null;
      /**
       * A polynomial point shear with explicit target and driver axes.
       */
      shear?: {
        linear: number;
        quadratic?: number;
        cubic?: number;
        target?: number;
        driver?: number;
      } | null;
      /**
       * The shape's swept-curve geometry (null = none) — see ShapeCurveDocument. Admitted only, and required, on Sweep (refused elsewhere, by name; a Sweep with no curve is refused, by name); refused alongside Panel, Trims, Flare, Shear, Bumps, or Domain on the same shape.
       */
      curve?: {
        /**
         * The curve's first control point, in the shape's local (workbench) frame — creation units, or a state.<row>[.<key>] reference.
         */
        a: unknown;
        /**
         * The curve's middle control point.
         */
        b: unknown;
        /**
         * The curve's last control point.
         */
        c: unknown;
        /**
         * The sweep radius at t = 0; finite and strictly positive.
         */
        radiusStart: number;
        /**
         * The sweep radius at t = 1; finite and strictly positive.
         */
        radiusEnd: number;
        /**
         * The mid-span radius bulge amplitude (null = 0), added by bulge·sin(π·t)^0.65; refused past MaxSweepBulgeRatio times max(RadiusStart, RadiusEnd).
         */
        bulge?: number | null;
        /**
         * The helical strand count (null = 1), in [MinSweepStrands, MaxSweepStrands]. A count above 1 is render-only — refused for deterministic field contact by name, matching Sweep's own status.
         */
        strands?: number | null;
        /**
         * The strand orbit rate, in turns along the curve (null = 0); finite.
         */
        twist?: number | null;
        /**
         * The strand orbit radius, in creation units (null = 0); non-negative, finite, refused past MaxSweepStrandOffsetRatio times max(RadiusStart, RadiusEnd).
         */
        strandOffset?: number | null;
      } | null;
      /**
       * The shape's per-instance lane-driven erosion (null = none) — see ShapeErodeDocument. Admitted on every primitive; its reach is this shape's own Reach, so it needs no separate authored magnitude.
       */
      erode?: {
        /**
         * Which dynamic-slot lane drives the erosion.
         */
        lane: number;
        /**
         * The lane value where erosion begins (t = 0).
         */
        from: number;
        /**
         * The lane value where the shape is fully eroded (t = 1); may be less than From to run the fold in reverse (the shape grows IN as the lane rises — the torn-fabric use). Must differ from From.
         */
        to: number;
        /**
         * The erosion front's noise lattice frequency, cells per world unit (null = 1).
         */
        noise?: number | null;
      } | null;
      /**
       * Shape-local cellular relief, evaluated after geometry within its own field scope.
       */
      cells?: {
        frequency: number;
        amplitude: number;
        seed: number;
        mode: "F1" | "F2MinusF1";
        randomness: number;
      } | null;
    } | null)[] | null;
    /**
     * The animation timeline frames (null = none).
     */
    frames?: ({
      /**
       * The frame's name (rest is the live pose).
       */
      name: string;
      /**
       * The per-shape transform snapshots, keyed by shape id.
       */
      transforms: ({
        /**
         * The shape id the snapshot belongs to.
         */
        id: number;
        /**
         * The pose position.
         */
        position: unknown;
        /**
         * The pose orientation.
         */
        rotation: unknown;
        /**
         * The pose scale.
         */
        scale: unknown;
      } | null)[];
    } | null)[] | null;
    /**
     * The IK rig's chains (null = none). Shapes stay flat; a chain only references shape ids.
     */
    chains?: ({
      /**
       * The chain's stable id.
       */
      id: number;
      /**
       * The player-given name (null = unnamed).
       */
      name?: string | null;
      /**
       * The member shape ids, root→tip order.
       */
      shapes: number[];
      /**
       * KindLimb or KindSpine (null = limb when exactly 3 shapes, else spine).
       */
      kind?: string | null;
      /**
       * The live goal position (null = the rest tip — re-seeded at load).
       */
      goal?: unknown;
      /**
       * The bend-direction hint (null = above the root — re-seeded at load).
       */
      pole?: unknown;
    } | null)[] | null;
    /**
     * The creation's anchored camera eyes (null = none). Each rides a shape and produces a named feed — the lantern-fish's lure lens is one entry here.
     */
    cameras?: ({
      /**
       * The eye's stable id within the creation.
       */
      id: number;
      /**
       * The anchored shape id (a Id). A camera naming a missing shape is dropped at load (its offset frame has no anchor).
       */
      shapeId: number;
      /**
       * The eye offset from the anchored shape's frame origin, in the creation's author frame (see CreationFrame).
       */
      position: unknown;
      /**
       * The eye heading offset, degrees (null = 0).
       */
      yaw?: number | null;
      /**
       * The eye tilt offset, degrees (null = 0).
       */
      pitch?: number | null;
      /**
       * The vertical field of view, degrees (null = the engine default).
       */
      fov?: number | null;
      /**
       * The look-at target distance ahead (null = 1).
       */
      focus?: number | null;
      /**
       * The named feed this eye publishes (null = the eye's id as a name). A screen face wired to this name shows this eye's live render — pure data, no creature-specific channel.
       */
      feed?: string | null;
    } | null)[] | null;
    /**
     * The behavior manifest (null = the defaults: walks, no face). Records how the creation moves and any screen faces it declares, so consumers stop re-supplying those facts by hand.
     */
    behavior?: {
      /**
       * How the creation moves, as a free-text token a consuming world resolves as a kit name: a creation declaring swim inhabits the world's kit row named swim when a placement's inhabit facet omits an explicit kit (a world declaring no such kit rejects the placement loudly, naming every kit it declares). It is not a closed enum — the runtime answer to "how does it move" is the resolved WorldKit.Model, never this string parsed per frame. Null = walk.
       */
      locomotion?: string | null;
      /**
       * The declared screen faces (null = none). A creation with a face shows a feed on its body; the face's default source is pure data, wirable to any camera feed.
       */
      faces?: ({
        /**
         * The face's name (a wiring handle — face by default).
         */
        name: string;
        /**
         * The shape whose surface is the screen (a Id; -1/null = the whole creation's canonical face surface, resolved by the consumer).
         */
        shapeId?: number | null;
        /**
         * The feed this face shows when nothing else is wired, as a source token a consuming world resolves through a closed four-token map — none (no signal), test (the test pattern), and camera:<name> / feed:<name> (a View of the named camera, resolved against the placement's derived creation-eye feeds then the world's own camera rows). An unrecognized token (including a bare named:emotes, which named a host registry no world provides) lights the no-signal card. Null = the no-signal card until a world's face override wires a feed.
         */
        defaultSource?: string | null;
      } | null)[] | null;
      /**
       * The declared sounds (null = none). Omitted from the wire when null, so a creation authored without this member serializes to unchanged bytes.
       */
      sounds?: ({
        /**
         * The sound's name (a wiring handle — sound by default; unique within the creation).
         */
        name: string;
        /**
         * The shape the voice emits from (a Id; null = the creation's root). A sound naming a missing shape is dropped at load (the post-edit-deletion self-heal, mirroring faces).
         */
        shapeId?: number | null;
        /**
         * The voice's puck.synthesizer-patch.v1 patch, inline (validated through the synth family's own canonicalizer as part of creation validation).
         */
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
        /**
         * The emitter level (null = 1 — unity).
         */
        level?: number | null;
        /**
         * The audible support radius in world units (null = the consuming world's default speaker radius).
         */
        radius?: number | null;
      } | null)[] | null;
    } | null;
    /**
     * The engraved/embossed text runs the creation carries (null/empty = none). Each is a string laid onto a surface, expanded at emission into Glyph shapes — see TextRunDocument. Omitted from the wire when null, so a creation authored without this member serializes to unchanged bytes.
     */
    textRuns?: ({
      /**
       * The run's text (whitespace / unmapped code points advance the pen without a glyph).
       */
      text: string;
      /**
       * The run's anchor centre on the host surface (workbench space).
       */
      position: unknown;
      /**
       * The run plane's orientation (local +X advance, +Y ascent, +Z the relief normal).
       */
      rotation: unknown;
      /**
       * The world height of one em, in the creation's own (pre-placement) units.
       */
      emHeight: number;
      /**
       * The glyph extrude half-depth — the relief the slab straddles the surface by (null = a thin default).
       */
      depth?: number | null;
      /**
       * engrave (Subtraction — a carved recess) or emboss (Union — proud relief); null = emboss.
       */
      mode?: string | null;
      /**
       * The palette slot the letters shade with (null = 0).
       */
      material?: number | null;
      /**
       * The world text catalog font name; null selects that catalog's default font.
       */
      font?: string | null;
      /**
       * The greedy glyph-level wrap width, in the creation's own units (null = only explicit line feeds break lines). Must be positive when present.
       */
      maxWidth?: number | null;
      /**
       * left, center, or right — how lines position against the block's widest line (null = left).
       */
      align?: string | null;
      /**
       * Extra advance after every glyph, in em units (null = 0; negative tightens).
       */
      tracking?: number | null;
      /**
       * A multiplier on the font's line height for the baseline step between lines (null = 1).
       */
      lineSpacing?: number | null;
      /**
       * The shape the run rides, or null for a run placed in creation space. A riding run's Position/Rotation are in that shape's unit-local frame: scaled by the shape's scale, then rotated and translated with it, so the run stays on the surface as the shape resizes.
       */
      shapeId?: number | null;
    } | null)[] | null;
    /**
     * The authored part identities this creation publishes when used as an entity look (null = none). Each maps a stable identifier to one shape's dynamic transform.
     */
    parts?: ({
      /**
       * The ordinal, case-sensitive identifier an entity-part anchor names.
       */
      id: string;
      /**
       * The stable Id whose dynamic pose the part publishes.
       */
      shapeId: number;
    } | null)[] | null;
    /**
     * The creation-level noise-relief facet (null = none) — see CreationNoiseDocument. Omitted from the wire when null, so a creation authored without it serializes to unchanged bytes.
     */
    noise?: {
      /**
       * The base lattice frequency, cells per creation unit (clamped to (0, MaxFrequency]).
       */
      frequency: number;
      /**
       * The peak outward displacement, world units (clamped to (0, MaxAmplitude]; zero drops the facet).
       */
      amplitude: number;
      /**
       * The fBm octave count (null = 4; clamped to 1..MaxNoiseOctaves).
       */
      octaves?: number | null;
      /**
       * The per-octave amplitude factor (null = 0.5; clamped to [MinGain, MaxGain]).
       */
      gain?: number | null;
      /**
       * The per-octave frequency factor (null = 2; clamped to [MinLacunarity, MaxLacunarity]).
       */
      lacunarity?: number | null;
      /**
       * The hash seed folded into every lattice corner (null = 0).
       */
      seed?: number | null;
    } | null;
    /**
     * The named animation drivers (null = none), at most MaxDrivers — see CreationDriverDocument. A shape's Swings/Slides name one of these. Presentation-only, like the facets that read them.
     */
    drivers?: ({
      /**
       * The driver's name, unique within the creation — the spelling a facet's driver member resolves against.
       */
      name: string;
      /**
       * One of SignalPlanarTravel, SignalTravel, SignalTime, SignalSpeed, SignalVerticalSpeed, or SignalTurnRate. The first three integrate: φ accumulates Cadence × the signal's per-frame delta while w > 0, and wraps modulo 2π so a wheel spins forever without losing precision. The last three are instantaneous: φ is set to Cadence × the current value, so a facet reading them tracks rather than cycles.
       */
      signal: string;
      /**
       * The signal-to-phase gain: radians per metre for the travel signals, radians per second for SignalTime, radians per metre-per-second for SignalSpeed/SignalVerticalSpeed, and radians per radian-per-second for SignalTurnRate. Magnitude at most MaxCadence.
       */
      cadence: unknown;
      /**
       * The gate: every token must hold for the driver's weight to ease toward 1 (it eases toward 0 otherwise, using the authored blend times or WeightSeconds by default, so a limb returns to rest instead of freezing mid-stride). Authored as one token or an array of them — a bare string reads as a one-token gate and canonicalizes to the array form. A token is a Puck.Physics.Motion.BodyFacts member name, WhenAlways, TokenMoving, or TokenStill; null is ungated. At most MaxGateTokens. A token no consumer can resolve gates the driver permanently off.
       */
      when?: (string | null)[] | null;
      /**
       * Gate activation's exponential time constant in seconds; null uses WeightSeconds. Finite and non-negative; zero applies immediately on the next positive frame delta.
       */
      blendInSeconds?: number | null;
      /**
       * Gate release's exponential time constant in seconds, on the same terms as BlendInSeconds. Independent release timing lets a mechanism settle after a quick body response.
       */
      blendOutSeconds?: number | null;
    } | null)[] | null;
    /**
     * The inverse-kinematics effectors (null = none), at most MaxEffectors — see CreationEffectorDocument. Each corrects a chain of this creation's own shapes so its tip reaches a target, composing after the drivers and the parent chain. Presentation-only, like the drivers.
     */
    effectors?: ({
      /**
       * The effector's name, unique within the creation — what a read-back or a refusal spells.
       */
      name: string;
      /**
       * The bones, root→tip, each the Name of a shape that DESCENDS from the one before it through Parent. A bone's joint is the pivot of its first Swings entry, or its authored Joint when it swings nothing. Two bones (MinChainBones) solve analytically; three or more — a tail, a tentacle, a spider leg with a coxa — solve by cyclic coordinate descent over Iterations sweeps. At most MaxChainBones.
       */
      chain: (string | null)[];
      /**
       * The Name of the shape whose posed origin IS the end effector — the last bone itself, or a shape descending from it (a boot under a shin, a claw under a tarsus).
       */
      tip: string;
      /**
       * Where the tip is asked to be.
       */
      target: {
        /**
         * One of KindSurface, KindBody, or KindState.
         */
        kind: string;
        /**
         * KindSurface: the probe direction in the creation's author frame (see CreationFrame), so it turns with the body — [0, -1, 0] is "below the body", which on a wall-held body points at the wall's own down. Normalized at canonicalization; zero names no direction and is refused.
         */
        direction?: unknown;
        /**
         * KindSurface: how far along Direction the probe searches, world units. Beyond MaxReach, or at zero or less, is refused. A probe that finds nothing eases the correction out rather than reaching at nothing.
         */
        reach?: unknown;
        /**
         * KindSurface: how far off the hit surface, along its normal, the tip is placed (null = 0 — the tip lands on the surface). The thickness of a boot's sole or the gap a claw keeps.
         */
        standoff?: unknown;
        /**
         * KindBody: the population entity index whose root pose is the target.
         */
        index?: number | null;
        /**
         * KindBody: the offset from that body's root, in the creation's author frame (null = its root exactly).
         */
        offset?: unknown;
        /**
         * KindState: a state.<row>[.<key>] reference to a text cell spelling a world-space [x, y, z], read at the frame's tick — a target a rule, a console write, or another system publishes. The containing world refuses a row it does not declare.
         */
        reference?: string | null;
      };
      /**
       * The gate, in the same vocabulary and with the same easing a driver's When uses: the correction blends in while every token holds and back out otherwise, so a released effector returns the limb to its driver-posed pose instead of dropping it.
       */
      when?: (string | null)[] | null;
      /**
       * A constant ceiling on the correction, in [0, 1] (null = 1 — full correction). Multiplied by the gate's eased weight, so a half-weight effector suggests rather than commands.
       */
      weight?: unknown;
      /**
       * The contact latch (null = none) — see CreationPlantDocument.
       */
      plant?: {
        /**
         * The Name whose wrapped phase the window is read against.
         */
        driver: string;
        /**
         * The phase interval [from, to] in radians, each in [0, 2π). A window whose from exceeds its to wraps through 0 — the half-cycle straddling the phase origin is as authorable as any other.
         */
        window: unknown;
        /**
         * The target influence outside the plant window while the driver is active, in [0, 1]. Null means 1 (continue following the target); 0 releases the tip to its authored swing. As the driver eases to rest, target influence returns to 1 so an idle limb can settle onto its surface.
         */
        swingWeight?: number | null;
      } | null;
    } | null)[] | null;
    /**
     * The bounded participating volumes (null = none), at most MaxVolumes — see VolumeDocument. Each rides the creation root's or a named shape's dynamic slot; a static placement bakes its frame in. Presentation-only: no distance field, no collider.
     */
    volumes?: ({
      kind: string;
      position: unknown;
      rotation: unknown;
      halfExtent: unknown;
      ramp: ({
        density: number;
        color: string;
      } | null)[];
      parent?: string | null;
      axis?: number | null;
      width?: number | null;
      speed?: number | null;
      seed?: number | null;
      steps?: number | null;
      intensity?: number | null;
      extinction?: number | null;
      pulseAmplitude?: number | null;
      pulseFrequency?: number | null;
      intensityLane?: number | null;
      enabled?: boolean;
      coverage?: number | null;
      softness?: number | null;
      /**
       * Whether the family is supported.
       */
      isKnownKind?: boolean;
    } | null)[] | null;
  } | null;
  /**
   * The SHA-256 hex64 of the document's canonical bytes (Hash on the canonical result the compose boundary produces). ABSENT resolves to the hash computed from Document at load — an author never writes a content hash by hand; see Hash.
   */
  hash?: string | null;
};

export type WorldQualityPreset = {
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
};

export type WorldReaction = WorldReactionDiffuse | WorldReactionDecay | WorldReactionTransform | WorldReactionEmit | WorldReactionExpose | WorldReactionFlow | null;

export type WorldReactionDecay = {
  $type?: "decay";
  /**
   * The field decayed.
   */
  field: string;
  /**
   * The fraction per step, in [0, 1].
   */
  rate: WorldLatticeScalar | null;
};

export type WorldReactionDiffuse = {
  $type?: "diffuse";
  /**
   * The field diffused.
   */
  field: string;
  /**
   * The fraction per step, in [0, 1].
   */
  rate: WorldLatticeScalar | null;
};

export type WorldReactionEmit = {
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
  amount: WorldLatticeScalar | null;
};

export type WorldReactionExpose = {
  $type?: "expose";
  /**
   * The field sampled at the body.
   */
  field: string;
  /**
   * The comparison.
   */
  comparison: ExpressionComparison;
  /**
   * The constant compared against.
   */
  value: WorldLatticeScalar | null;
  /**
   * The keyed int state row written, keyed by body index.
   */
  row: string;
};

export type WorldReactionFlow = {
  $type?: "flow";
  /**
   * The field transported.
   */
  field: string;
  /**
   * The fraction of a cell's per-direction share that actually moves each step, in [0, 1].
   */
  rate: WorldLatticeScalar | null;
  /**
   * The other lattice rows forming the terrain basis a downhill direction is measured against; empty or omitted means the field flows over its own height alone.
   */
  over?: (string | null)[] | null;
  /**
   * The scalar fixed-kind state row an edge cell's outward share accumulates into each step (a clamped add), or null to treat every lattice edge as a wall.
   */
  spillRow?: string | null;
};

export type WorldReactionTransform = {
  $type?: "transform";
  /**
   * The conditions, all of which must hold.
   */
  when: (WorldFieldCondition | null)[];
  /**
   * The writes, applied in order.
   */
  then: (WorldFieldWrite | null)[];
};

export type WorldReference = {
  /**
   * The reference's own name — SafeName-shaped, unique within the section.
   */
  name: SafeName;
  /**
   * The referenced world's document name (e.g. "modules/dive", see WorldDocumentName), authored verbatim and relative to the referring document. Mutually exclusive with Owner/World.
   */
  document?: string | null;
  /**
   * The remote world's owning platform user id (a UUID) — worlds ARE users, so naming the owner names the world's account. Required together with World; refused alone.
   */
  owner?: string | null;
  /**
   * The remote world's own SafeName-shaped id within its owner's account. Required together with Owner; refused alone.
   */
  world?: SafeName | null;
};

/**
 * The stylized curvature enrichment, keyed on the level-set mean curvature the lit path already measures at each hit. Every field is optional individually — absent resolves to the engine's pinned default. The three gains share one runtime gate: while all of them are zero the renderer skips the extra field tap the curvature normal needs, so an unauthored world pays nothing.
 */
export type WorldRenderCurvature = {
  /**
   * How far a concave crease darkens, in [0, 1] at a cavity whose curvature reaches InkLow. Zero darkens none.
   */
  cavity?: number | null;
  /**
   * How far a convex ridge brightens, in [0, 1] at a ridge whose curvature reaches InkLow. Zero brightens none.
   */
  rim?: number | null;
  /**
   * The ink outline's strength where the curvature magnitude spikes. Zero draws none.
   */
  ink?: number | null;
  /**
   * The curvature magnitude (1 / fillet radius, in world units) at which the outline starts and the ridge and cavity terms saturate.
   */
  inkLow?: number | null;
  /**
   * The curvature magnitude at which the outline saturates.
   */
  inkHigh?: number | null;
  /**
   * BindableColor's grammar: the outline colour.
   */
  inkColor?: BindableColor | null;
};

export type WorldRenderCycle = {
  /**
   * The state row read (its slot cell; Fixed or Int).
   */
  state: string;
  /**
   * At least two keys, strictly ascending At in [0, 1).
   */
  keys: (WorldRenderCycleKey | null)[];
};

export type WorldRenderCycleKey = {
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
};

export type WorldRenderDefaults = {
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
   * The post-render extension chain, composed over the world's rendered output in list order — e.g. [{ "id": "sdf-film-grain", "config": { "intensity": 0.08 } }]. Optional; an absent or empty list is the byte-identical default path (no extension composed). Every id must name a shipped shader set — a puck.shader.manifest.v1 manifest's file stem (checked at document load); each entry's own config is validated against that manifest's declared config schema at boot and by puck schema.
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
   * The analytic studio-reflection softboxes and horizon gradient a GGX specular lobe reflects. Optional; absent (no softboxes, a black horizon) contributes nothing to the shaded color.
   */
  environment?: WorldRenderEnvironment | null;
  /**
   * The tonemap applied to the frame's final color. Optional; absent is None — the stylized shaded color, unchanged.
   */
  tonemap?: WorldTonemap | null;
  /**
   * The far distance in world units: the depth at which every camera march ends — the far plane the renderer's fine march exits at, the reach of the beam's cone proofs, and the depth the fog and depth ramps are measured against. Geometry beyond it is never marched, so an infinite plane ends on a visible horizon curve at this depth unless the sky fog has absorbed it (render.sky.fogDensity). Optional; absent resolves to the engine's pinned 40 — exactly the value every world marched to before this field existed. Must lie within [MinFarDistance, MaxFarDistance]. Re-read on every definition revision (a world.row.set render lands on the next frame); world.budget echoes it with its derived costs.
   */
  farDistance?: number | null;
};

export type WorldRenderEnvironment = {
  /**
   * The reflection softboxes, at most SdfEnvironment.MaxSoftboxes. Absent or empty contributes nothing.
   */
  softboxes?: (WorldRenderSoftbox | null)[] | null;
  /**
   * The reflection horizon gradient. Absent is black — contributes nothing.
   */
  horizon?: WorldRenderHorizon | null;
};

export type WorldRenderExtensionEntry = {
  /**
   * The shader set id (its puck.shader.manifest.v1 manifest's file stem) — checked against the shipped vocabulary at document load (IsRegisteredPostRenderExtension), never interpreted here.
   */
  id: "sdf-film-grain";
  /**
   * The set's config values, or null when the manifest declares none or every field has a default. Not validated at document load — the manifest's declared config schema validates it at boot (matching Machine's Options, the identical shallow-then-deep precedent), refusing boot with the set id and reason on a malformed value.
   */
  config?: unknown;
};

export type WorldRenderHorizon = {
  /**
   * The ground-ward (direction.y = −1) colour. Absent is black.
   */
  low?: BindableColor | null;
  /**
   * The sky-ward (direction.y = 1) colour. Absent is black.
   */
  high?: BindableColor | null;
};

/**
 * One light. The $type string is the JSON discriminator; a new kind is a new derived record, its JsonDerivedTypeAttribute line, and its lane semantics in SdfEnvironment.
 */
export type WorldRenderLight = WorldRenderLightDirectional | WorldRenderLightHemisphere | WorldRenderLightRim | WorldRenderLightPoint | WorldRenderLightOccluder | null;

/**
 * A Lambert directional light.
 */
export type WorldRenderLightDirectional = {
  $type?: "directional";
  /**
   * The direction from a lit surface toward the light, any nonzero length (normalized host-side before upload). Absent is the pinned sun direction.
   */
  direction?: DocumentVector3;
  /**
   * The light's linear colour.
   */
  color?: BindableColor | null;
  /**
   * The diffuse weight. Absent is the pinned sun weight.
   */
  weight?: number | null;
  /**
   * The light's angular radius in radians, in [0, atan 0.3]: the penumbra half-slope is its tangent, so 0 casts a hard shadow. Read only when the light shadows. Absent is the pinned penumbra.
   */
  angularRadius?: number | null;
  /**
   * Whether this light drives the soft-shadow march (at most one light per world). Absent is false. An unshadowed directional is scaled by ambient occlusion instead.
   */
  shadows?: boolean | null;
};

/**
 * A hemisphere ambient: a floor plus a gradient on the surface normal's Y (sky above, darker below), scaled by ambient occlusion.
 */
export type WorldRenderLightHemisphere = {
  $type?: "hemisphere";
  /**
   * The ambient's linear colour.
   */
  color?: BindableColor | null;
  /**
   * The floor. Absent is the pinned ambient floor.
   */
  base?: number | null;
  /**
   * The hemisphere gradient. Absent is the pinned gradient.
   */
  gradient?: number | null;
};

export type WorldRenderLightList = (WorldRenderLight | null)[];

/**
 * A smooth attenuation field. Position is world space, or an offset in an anchored entity/part/placement frame. Missing anchors disable it. Radius is positive; Weight is in [0, 1]. It shares the eight-light capacity.
 */
export type WorldRenderLightOccluder = {
  $type?: "occluder";
  position?: DocumentVector3;
  radius?: number | null;
  anchor?: WorldAnchor | null;
  weight?: number | null;
};

/**
 * A point light with inverse-square falloff and a soft core: intensity = weight / (1 + (distance / radius)^2). No shadow march in v1 — a point light never occludes and is never occluded.
 */
export type WorldRenderLightPoint = {
  $type?: "point";
  /**
   * The world-space position for a static (unanchored) light. Absent is the world origin. An offset in the anchor frame when an anchor is authored.
   */
  position?: DocumentVector3;
  /**
   * The falloff radius. Absent is the engine default.
   */
  radius?: number | null;
  /**
   * An entity, entity part, or placement frame. A missing live target disables the light.
   */
  anchor?: WorldAnchor | null;
  /**
   * The light's linear colour.
   */
  color?: BindableColor | null;
  /**
   * The strength. Absent is the engine default.
   */
  weight?: number | null;
};

/**
 * A view-dependent silhouette brighten: weight · color · pow(1 − saturate(dot(normal, −rayDirection)), power), added after the material shade.
 */
export type WorldRenderLightRim = {
  $type?: "rim";
  /**
   * The rim's linear colour.
   */
  color?: BindableColor | null;
  /**
   * The strength. Absent is zero, which adds nothing.
   */
  weight?: number | null;
  /**
   * The falloff exponent — larger confines the highlight nearer the silhouette. Absent is the engine default.
   */
  power?: number | null;
};

/**
 * The lit path's lights and stylization as world data. Absent renders the pinned sun and hemisphere an unauthored world always had; present, the list IS the lights — an authored list without a hemisphere has no ambient. Every field of every light is optional individually and resolves to the engine's pinned default for its kind.
 */
export type WorldRenderLighting = {
  /**
   * The lights, at most SdfEnvironment.MaxLights, in slot order (a render.cycle key moves a light by its slot). At most one directional may shadow: the soft-shadow march runs once per lit pixel.
   */
  lights?: WorldRenderLightList | null;
  /**
   * The stylized curvature enrichment — cavity darkening, curvature rim light, and an ink outline. Optional; absent (and all-zero) shades exactly as a world that declares none.
   */
  curvature?: WorldRenderCurvature | null;
};

/**
 * The SAFE, enumerated world render-scale tiers a run pins for the settled REVEALED room view — the demo/user-facing quality option layered over the engine's CONTINUOUS render-scale knob (SdfViewSnapshot.RenderScale, quantized to SdfWorldEngine.RenderScaleQ). The player picks one of these KNOWN-GOOD steps, never a free numeric value; each tier is pinned to an exact quantized numerator so the reduced render extent (worldRenderDims) is a stable integer at any window size and the bilinear upsample stays artifact-free. The continuous knob stays reachable PROGRAMMATICALLY (layout eases, dev tooling) — the enumerated policy lives only here at the user surface. Names + scales are the ONE definition (WorldRenderScaleTiers); the world document's quality presets, the console world.render-scale verb, and the boot resolution all read them there.
 */
export type WorldRenderScaleTier = "Native" | "ThreeQuarter" | "Half" | "Quarter" | "Eighth";

/**
 * The procedural sky as an ordered stack of layers. Absent is a hard gate: the world renders the pinned two-stop gradient and fog density, as before this section existed. The layers composite in a fixed order — gradient, stars, sun disc, clouds — whatever order they are authored in; fog is read every frame on its own. A layer kind appears at most once.
 */
export type WorldRenderSky = {
  /**
   * The layers.
   */
  layers?: WorldRenderSkyLayerList | null;
};

/**
 * One sky layer. The $type string is the JSON discriminator.
 */
export type WorldRenderSkyLayer = WorldRenderSkyLayerGradient | WorldRenderSkyLayerFog | WorldRenderSkyLayerSunDisc | WorldRenderSkyLayerStars | WorldRenderSkyLayerClouds | null;

/**
 * The procedural cloud layer: a deterministic hashed-lattice noise on a plane above the camera, thresholded by coverage, drawn over the gradient, stars and sun disc and fading into the horizon.
 */
export type WorldRenderSkyLayerClouds = {
  $type?: "clouds";
  /**
   * The fraction of the sky the layer covers, in [0, 1]. Absent is zero.
   */
  coverage?: number | null;
  /**
   * The width of a cloud's edge, in (0, 1]. Absent is the engine default.
   */
  softness?: number | null;
  /**
   * The size of one cloud cell in layer units (the layer sits at unit height). Absent is the engine default.
   */
  scale?: number | null;
  /**
   * The hash seed folded into the lattice.
   */
  seed?: number | null;
  /**
   * BindableColor's grammar: the cloud colour. Absent is white.
   */
  color?: BindableColor | null;
  /**
   * The layer's wind, in layer units per second along world X and Z, integrated on the tick clock. Absent holds still.
   */
  drift?: DocumentVector2;
  /**
   * The layer's rotation about the zenith in radians per second; positive is counter-clockwise seen from below. Absent is none.
   */
  spin?: number | null;
  /**
   * The Coriolis twist in radians at 45° elevation, falling off toward the horizon and the zenith. Positive winds counter-clockwise. Absent is none.
   */
  curl?: number | null;
  /**
   * The wind of the shaping field relative to the cloud field, in layer units per second. Absent holds the shapes.
   */
  shear?: DocumentVector2;
};

/**
 * The exponential distance fog fading toward the sky gradient.
 */
export type WorldRenderSkyLayerFog = {
  $type?: "fog";
  /**
   * The density per world unit. Absent is the pinned density.
   */
  density?: number | null;
};

/**
 * The colour gradient over elevation: piecewise-linear between stops, clamped to the end stops.
 */
export type WorldRenderSkyLayerGradient = {
  $type?: "gradient";
  /**
   * Two to SdfEnvironment.MaxSkyStops stops, strictly ascending in elevation. A render.cycle key moves a stop by its index and may not add or remove one.
   */
  stops?: (WorldRenderSkyStop | null)[] | null;
};

export type WorldRenderSkyLayerList = (WorldRenderSkyLayer | null)[];

/**
 * The procedural star field: a deterministic per-cell hash over an octahedral sky projection.
 */
export type WorldRenderSkyLayerStars = {
  $type?: "stars";
  /**
   * The star grid's cell count per octahedral axis. Absent is the engine default.
   */
  density?: number | null;
  /**
   * The peak per-star brightness. Absent is zero, which draws nothing.
   */
  brightness?: number | null;
  /**
   * The hash seed folded into every cell.
   */
  seed?: number | null;
  /**
   * Scintillation for a share of the stars. Optional; absent twinkles none.
   */
  twinkle?: WorldRenderSkyTwinkle | null;
};

/**
 * The visible sun disc — an additive highlight about one directional light's direction.
 */
export type WorldRenderSkyLayerSunDisc = {
  $type?: "sunDisc";
  /**
   * The Lights slot of a directional light. Absent is the shadow light, or the first directional when none shadows.
   */
  light?: number | null;
  /**
   * The disc's angular half-radius in radians, in (0, π/2]. Absent is the engine default.
   */
  radius?: number | null;
  /**
   * The peak additive brightness. Absent is zero, which draws nothing.
   */
  intensity?: number | null;
};

/**
 * One gradient stop.
 */
export type WorldRenderSkyStop = {
  /**
   * The direction's Y component this stop sits at, in [−1, 1].
   */
  elevation?: number | null;
  /**
   * BindableColor's grammar: the colour at this elevation. Absent (in a cycle key) keeps the previous key's colour.
   */
  color?: BindableColor | null;
};

/**
 * Scintillation: a hash-chosen share of the stars dip and recover on the simulation clock, each at its own harmonic and phase of one authored rate, so no two twinkle in step. Presentation-only, keyed on the tick.
 */
export type WorldRenderSkyTwinkle = {
  /**
   * The fraction of stars that twinkle, in [0, 1]. Zero twinkles none.
   */
  share?: number | null;
  /**
   * How far a twinkling star dips below its steady brightness, in [0, 1].
   */
  depth?: number | null;
  /**
   * The fundamental scintillation rate in hertz.
   */
  rate?: number | null;
};

export type WorldRenderSoftbox = {
  /**
   * From a reflecting surface toward the softbox, any nonzero length (normalized before upload).
   */
  direction: DocumentVector3;
  /**
   * The angular half-extent (width, height) the falloff widens by, both strictly positive.
   */
  size: DocumentVector2;
  /**
   * BindableColor's grammar: the softbox's linear colour. Absent is white.
   */
  color?: BindableColor | null;
  /**
   * The strength. Absent is 1.
   */
  weight?: number | null;
  /**
   * Additional falloff softening, in the same units as Size. Absent is 0.
   */
  blur?: number | null;
};

export type WorldRigid = {
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
};

/**
 * The row-to-entity assignment declaration — nothing about Sequence/Rows is kit-specific, so the same primitive distributes the kit table (a way of moving) and the look table (a way of looking) across the population. Resolved once at construction into each entry's fixed row index (precompute; zero steady-state cost). The kit assignment affects the simulation (it selects the compiled tuning/action bindings); the look assignment is presentation-only (it selects the appearance row).
 */
export type WorldRowAssignment = {
  /**
   * The sequence that selects a row.
   */
  sequence: WorldSequence;
  /**
   * Gets the authored row-name view. The absence-coalesce lives in the accessor for the same reason Elements's does.
   */
  rows: DocumentIdentifierList;
};

export type WorldRule = {
  /**
   * The rule's stable name — unique within the section. A CellName, the same validated-identifier type a state row and a cell key ride: dot-free and free of the reserved character set, refused by name at the JSON converter. The reserved $ prefix is refused on top of that, by RuleCompiler.CompileAll — exactly as it is for a state row name, and for the same reason: $ marks what the engine mints, and the engine mints no rule. A rule a .puck scope generates carries the $ inside its name instead (GeneratedName: turn$east).
   */
  name: CellName;
  /**
   * The effects applied in order when the rule fires.
   */
  effects: (ActionEffect | null)[];
  /**
   * The predicate that must hold, or null for always.
   */
  gate?: ActionPredicateNullable2 | null;
  /**
   * Whether the rule fires every tick the gate holds (Level, the default) or once per crossing (Edge). A rule that writes a row almost always wants Edge: level-firing an addState writes one journal entry per tick.
   */
  mode?: ActionTriggerMode;
  /**
   * A keyed state row to iterate, $zones to iterate the rule's own Zones table (its non-empty indices, each bound to $each), or null for one evaluation per tick. With a row named, the gate and effects evaluate once per cell the row holds at the top of the tick, with $each bound to that cell's key — the quantifier that lets one rule tick a status for every participant carrying it, or one rule judge every piece or card of a keyed row. An integer key also binds the each participant reference; a non-integer key binds $each alone. The latch is kept per key, by the key's interned CellKey ordinal, so a key keeps its latch wherever it sits in the row.
   */
  forEach?: StateChannelRefNonNullable;
  /**
   * The optional choice policy; common effects run only when entering a selected option.
   */
  decision?: WorldDecision | null;
  /**
   * The values computed once per evaluation, in declared order, read as $local:<name>.
   */
  locals?: (RuleLocal | null)[] | null;
  /**
   * The rule's zone table, or null: ordered zones over one token domain, in index order, an empty entry holding an index no zone answers. Every row position in the rule — a compareState's state, a $reduce:/$match: row, a $zone: endpoint's zone, a transfer's from/to, an expression's row — may spell $zones[<index>] to select an entry live, the index being an infix cell key (game[from], $each, $local:<name>, or any expression), so one rule serves every pile of a game. An evaluation applies only when every live index selects an entry — an index outside the table, or at an empty entry, reads the gate closed, so a table's gaps are the rule's own statement of which piles it is for. forEach: "$zones" iterates the table's own non-empty indices with $each bound to each.
   */
  zones?: (string | null)[] | null;
  /**
   * The declared pool and typed lexical binding to snapshot and visit once per live instance, or null for ordinary row/no iteration. It cannot appear beside ForEach.
   */
  poolForEach?: RulePoolIteration | null;
};

export type WorldScheduleExpectation = "Submitted" | "Refused";

export type WorldScheduleInstance = {
  /**
   * The instance's name — a single safe path segment, the name a row's World addresses and the name its export file carries. boot is reserved for the world this process booted with.
   */
  name: string;
  /**
   * The sibling world document's name, resolved beside the document that declares it (WorldDocumentPaths).
   */
  document: string;
};

export type WorldScheduleRow = {
  /**
   * The completed-tick coordinate — the same coordinate Ticks arms at — this command is submitted at. Submission is what the coordinate pins: an Immediate-routed verb runs inline in that submission; a Simulation-routed verb folds into the next tick's snapshot and applies during Tick + 1, as a live console line does. Tick 0 is refused — no step has completed there.
   */
  tick: number;
  /**
   * The acting identity, spelled as the engine's own principal label (Describe): seat1..seat4, parsed by TryParse so the seat ceiling here is the one the wire codec enforces. Every other label is refused, console included: the console principal is trusted at every gate (Puck.World.Server.WorldGrants.IsTrusted), so a step acting as it proves nothing about authority. The power a scheduled step needs is authored in the document's own grants.
   */
  principal: string;
  /**
   * The command line, verbatim: non-empty, one line, not a # comment, and opening with a verb WorldScheduleCommands admits. Argument shapes are not checked here — this project holds no command registry — so a malformed argument becomes a recorded refusal at its tick rather than a boot failure.
   */
  command: string;
  /**
   * What the ingress must answer for this row. Any other recorded outcome fails the world by name: a step whose outcome nobody declared is a step nobody measured.
   */
  expect?: WorldScheduleExpectation;
  /**
   * Text the recorded refusal detail must contain, or null to accept any refusal. Legitimate only beside Refused.
   */
  refusal?: string | null;
  /**
   * The armed instance this row is submitted into, named by its Name, or null for the world this process booted with. A name no Instances entry declares is refused at validation.
   */
  world?: string | null;
};

export type WorldScheduleSection = {
  /**
   * How many ticks the world keeps stepping past the last scheduled command's tick before the export is written: the settle margin a Simulation-routed command's apply, the mutation drain, and the rules reading its result need. At least 1 — a zero margin would export at the same tick the last command was submitted, before it could have applied — and at most MaxSettleTicks.
   */
  settleTicks: number;
  /**
   * The scheduled commands, ascending by tick. Rows may share a tick; they are then submitted in declaration order, which is the whole ordering contract. Capacity MaxRows. May be empty: a world whose expectations are decided by its rules alone still wants the export.
   */
  rows: (WorldScheduleRow | null)[];
  /**
   * The sibling worlds this run starts beside the booted one, in declaration order. Empty for a run over one world. Capacity MaxInstances.
   */
  instances?: (WorldScheduleInstance | null)[] | null;
};

export type WorldScreen = {
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
  /**
   * The screen's live byte-window bindings between its booted machine's bus and ordinary state.world Int cells (see WorldScreenMemory), or null for a screen with none. Omitted from the wire when null.
   */
  memory?: (WorldScreenMemory | null)[] | null;
};

export type WorldScreenMagazine = {
  /**
   * The ordered source list (at least one entry).
   */
  entries: (WorldScreenSource | null)[];
  /**
   * The 0-based entry the selector starts on (what screen.select advances from), not what the screen boots showing — a screen always wakes on its declared Source (the one-live-console ceiling depends on this). Live selection drifts from this and is folded back by world.save (see Puck.World.Server.WorldSessionCapture).
   */
  selected?: number;
  /**
   * Whether advancing past the last entry returns to the first (the arcade cabinet's wrapping cycle); when false the selector clamps at both ends.
   */
  wrap?: boolean;
};

export type WorldScreenMemory = {
  /**
   * The machine bus address the window starts at. Validated within 0..(MaxAddress - Width + 1) — outside the engine's addressable memory refuses by name at validation, never at runtime (a machine's own IMachineMemoryPeek silently reads/no-ops out of its own smaller readable/writable range instead, exactly as it does for any other peek/poke).
   */
  address: number;
  /**
   * How many bytes the window spans, little-endian (the low byte at Address): 1 or 2.
   */
  width: number;
  /**
   * The declared state.world row this binding mirrors to/from — must resolve to a kind=Int row.
   */
  row: string;
  /**
   * The cell inside Row, or null for its slot cell. Refused when Row is keyed and this is absent, or unkeyed and this is present — the same (row, key) pair rule every other named-cell reference in this document follows. Omitted from the wire when null.
   */
  key?: string | null;
  /**
   * Which way the binding moves a value.
   */
  direction?: WorldScreenMemoryDirection;
};

export type WorldScreenMemoryDirection = "Read" | "Write";

/**
 * How a Session face's destination render projects onto the face — an ordinary head-on camera image, or a WINDOW whose image shears with the viewer's own eye so the destination scene parallaxes against the aperture the way a real opening would.
 */
export type WorldScreenProjection = "Camera" | "Window";

export type WorldScreenResolution = unknown;

export type WorldScreenRoute = {
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
  /**
   * Where a pointer hit on the screen's source goes: Presentation for hover and highlight, or Simulation for a pointer ray mapped in fixed point from this row, such as a light gun (WorldScreenMappings). Passthrough is refused by name: host passthrough exists only for a source the local user opened on their own machine, and a world document can never create one or send it input. null (the default) is Presentation. Omitted from the wire when null.
   */
  input?: SourceDestination | null;
};

/**
 * The signal carried by a WorldScreen's lit face. A source declares which provider feeds a slot; the engine resolves and samples it. The $type string is the JSON discriminator; a new source kind is a new derived record plus its JsonDerivedTypeAttribute line.
 */
export type WorldScreenSource = WorldScreenSourceNone | WorldScreenSourceMachine | WorldScreenSourceProducer | WorldScreenSourceView | WorldScreenSourceSession | WorldScreenSourceText | WorldScreenSourceProbe | null;

/**
 * A named machine output. The source is a consumer reference only: the named machine is prepared, advanced, and retired by the world's machine host independently of every display that samples it.
 */
export type WorldScreenSourceMachine = {
  $type?: "machine";
  /**
   * The declared Name instance.
   */
  instance: string;
  /**
   * The provider video output name exposed by that instance.
   */
  output: string;
};

/**
 * No provider is bound — the engine lights the slot with its procedural no-signal fallback (an animated test-card / striped no-signal look, never black).
 */
export type WorldScreenSourceNone = {
  $type?: "none";
};

/**
 * A declared probe's texture output — the frame a kind that declares an output writes each cycle (a relit camera frame, a mask), published like a camera feed. The probe must be a probes row of this document; whether its kind writes a texture is checked at boot against the kind's manifest.
 */
export type WorldScreenSourceProbe = {
  $type?: "probe";
  /**
   * The probes[].id whose output this slot shows.
   */
  id: string;
};

/**
 * An image a registered producer makes, such as the test pattern, a QR code, a camera or a desktop capture, named by the producer's id rather than by a document kind, so a new producer needs no change here. The producer binds Settings through its own settings shape and refuses a member it does not declare, by name; WorldImageProducerSettings holds the shapes of the producers the engine ships, and WorldImageProducerVocabulary holds every registered producer's shape. Two producer sources are equal when their ids and settings are, member for member.
 */
export type WorldScreenSourceProducer = {
  $type?: "producer";
  /**
   * The registered producer's id.
   */
  id: string;
  /**
   * The producer's settings object, or null for its defaults. Omitted from the wire when null.
   */
  settings?: IReadOnlyDictionaryStringJsonElement | null;
};

/**
 * A live rendered view of another world, resolved through a destinations row (docs/architecture/worlds.md, "Observation and display"). The face/screen resolves the same resolver-owned identity a portal crossing at the same door would land in (WorldSessionResolver), attaches an observation lease to the resolved instance's server, and mirrors just enough of its delivered definition/snapshots to render its static authored geometry through CameraName (or the destination's default projection). It never re-derives durability/scope/generation itself — those are the destination row's own facts.
 */
export type WorldScreenSourceSession = {
  $type?: "session";
  /**
   * The Name this face/screen observes. Must resolve to a declared destinations row — an undeclared name refuses at boot (validated, like a portal facet's own destination).
   */
  destination: string;
  /**
   * The destination's own placeable-camera name to render through, or null for its default projection (its first declared camera, else a fixed overview derived from its spawn points). Wire name camera. Validated only as non-empty when present at author time — the destination's own definition is not joined at boot (references assert naming intent, not reachability), so an unknown camera name is refused loudly at bind time instead, once the destination is actually resolved, falling back to the default projection rather than refusing the whole bind. Ignored under Window (see Projection).
   */
  camera?: string | null;
  /**
   * How the destination render projects onto this face (see WorldScreenProjection). Default Camera — unauthored worlds and every session facet authored before this member existed render byte-identically. Optional and trailing (the same widen-without-moving-existing-members shape CameraName itself already follows). Window requires this same face's Portal to author Mapped with a Counterpart — refused by name otherwise (see WorldDefinitionValidator); a top-level screens row or magazine entry carries no face to pair with, so window is refused there unconditionally.
   */
  projection?: WorldScreenProjection;
  /**
   * The offscreen target's [width, height] in pixels, or null for the engine default (Puck.SdfVm.Views.WorldSessionView.DefaultWidth x DefaultHeight — today's 160x144 panel, unchanged for an unauthored facet). Each axis is validated within 1..WorldDefinitionValidator.MaxSurfaceDimension. Omitted from the wire when null.
   */
  resolution?: WorldScreenResolution | null;
};

/**
 * Authored reading text on the screen face, rendered through the engine's glyph-decal tier (Puck.SdfVm.SdfWorldEngine.SetScreenDecal): a fixed monospace cell grid sampled from the world's packed font atlas at shade time — the dense-text sibling of a creation's textRuns, which stamp marched Glyph geometry. Signs, plaques, books, and monitors author this; short sculptural lettering stays a text run. Requires the world to declare a text font catalog (Text); the decal bypasses the CRT image pipeline, so no image source competes with it on the slot.
 */
export type WorldScreenSourceText = {
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
};

/**
 * A named view from the presentation view stack, such as a monitor showing another camera's output.
 */
export type WorldScreenSourceView = {
  $type?: "view";
  /**
   * The registered view name this slot samples.
   */
  cameraName: string;
};

export type WorldSearchChance = {
  /**
   * The keyed integer row a chosen outcome writes.
   */
  row: string;
  /**
   * Negamax: the absolute ply (0 = root) whose move choice is replaced, 0..depth-1. Tree: the 1-based playout ply the chance draw replaces.
   */
  atDepth: number;
};

export type WorldSearchRow = {
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
   * A value expression, stored as its program like every other expression a world document holds, evaluated over the frame after a ply from the perspective of the side that made it; iterative-deepening negamax with alpha-beta compares it across plies — the two-sided, zero-sum reading of what a ply is worth. Exactly one of this and Scores is authored when a score is needed; required when Depth exceeds one, or Best is authored, and refused with MonteCarlo unauthored alongside it.
   */
  score?: ExpressionProgramNonNullable2;
  /**
   * A keyed integer row receiving the deepest completed depth's answer: token (the mover's ordinal in Tokens), to (its destination cell), and score (the negamax value, or, with Scores authored, the root mover's own seat's value).
   */
  best?: string | null;
  /**
   * How plies are compared by the score: Negamax to the depth cap, or MonteCarlo, which reads the score where no candidate is accepted or at the cap.
   */
  method?: SearchMethod;
  /**
   * How many tree iterations a MonteCarlo job runs before it lands.
   */
  iterations?: number;
  /**
   * The job's chance node, or null for a job with none.
   */
  chance?: WorldSearchChance | null;
  /**
   * A keyed integer row, one cell per seat in Turn's own ordinal order, holding each seat's own current score — the n-seat reading of what a ply is worth: a level maximizes the mover seat's own entry rather than negating the reply, so no seat's gain is assumed to be another's loss (max-n). Exactly one of this and Score is authored when a score is needed; refused with MonteCarlo, whose outcome backprop alternates sign along the path.
   */
  scores?: string | null;
  /**
   * Optional integer slot; zero suspends candidate work.
   */
  enabled?: string | null;
  /**
   * Optional integer slot copied to best.revision with the completed answer.
   */
  revision?: string | null;
};

export type WorldSearchSection = {
  /**
   * The declared jobs.
   */
  jobs?: (WorldSearchRow | null)[] | null;
};

export type WorldSearchShape = WorldSearchShapeRelocate | WorldSearchShapeDrop | WorldSearchShapeJump | WorldSearchShapeTandem | WorldSearchShapePromote | WorldSearchShapeTransferred | null;

export type WorldSearchShapeDrop = {
  $type?: "drop";
};

export type WorldSearchShapeJump = {
  $type?: "jump";
  /**
   * The directions tried, in the topology's own vocabulary (CompiledTopology.Direction). The single-element list ["any"] tries every direction the topology declares.
   */
  over: (string | null)[];
  /**
   * How many hops one candidate may chain.
   */
  maxHops?: number;
};

export type WorldSearchShapePromote = {
  $type?: "promote";
  /**
   * An integer row keyed by the tokens holding each token's code.
   */
  codes: string;
  /**
   * The codes a token may take, at most MaxPromotions.
   */
  to: number[];
};

export type WorldSearchShapeRelocate = {
  $type?: "relocate";
  /**
   * Whether the token standing on the target leaves the board.
   */
  displace?: boolean;
};

export type WorldSearchShapeTandem = {
  $type?: "tandem";
  /**
   * The companion token's cell key in Tokens.
   */
  with: string;
};

export type WorldSearchShapeTransferred = {
  $type?: "transfer";
  /**
   * Which end of its zone a token must stand at to move: Last (default, the top of the pile) or First.
   */
  selector?: ZoneSelector;
  /**
   * Whether the token lands first in the destination rather than last.
   */
  insertFirst?: boolean;
};

export type WorldSeatCameraFeel = {
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
};

export type WorldSeatFollow = {
  /**
   * The exponential rate (per second) the camera yaw closes on the heading — about 63% of the remaining angle per 1/rate seconds; larger is a stiffer follow.
   */
  rate: number;
  /**
   * Whether the follow also runs while the body has no movement input. false (the default) is the classic feel: after a free-look the camera stays where you left it until you move.
   */
  whileIdle?: boolean;
};

export type WorldSeatGyro = {
  /**
   * The dimensionless multiplier applied after projection.
   */
  scale?: number;
  /**
   * Independent X/Y/Z dead zones in radians per second.
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
   */
  yaw?: [number, number, number];
  /**
   * The X/Y/Z projection weights producing semantic look-up angular velocity.
   */
  pitch?: [number, number, number];
};

export type WorldSeatModeFamily = {
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
   */
  states: (WorldSeatModeState | null)[];
};

export type WorldSeatModeState = {
  /**
   * The state's stable name — the token player.mode <family> <state> takes and the value Client.WorldContextFamilies context rows key on.
   */
  name: string;
  /**
   * The control application this state drives, or null for an ordinary state (the seat drives its own body normally). "camera" is the only admitted value: entering the state possesses the seat's declared camera body (see CameraPlacementIdPrefix) through the ordinary Engage door (PlayerCommandModule.Mode.cs), diverting the seat's own body intent to Idle and resolving its view through views.cameraRig instead.
   */
  target?: string | null;
};

export type WorldSeatViewControl = {
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
};

export type WorldSeatYawReference = "World" | "Body";

/**
 * A deterministic index-to-sample declaration shared by distributions, row assignment, color, and population variation. The document selects the sequence and its phase; the engine owns its exact arithmetic.
 */
export type WorldSequence = {
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
};

/**
 * A deterministic index-to-sample declaration shared by distributions, row assignment, color, and population variation. The document selects the sequence and its phase; the engine owns its exact arithmetic.
 */
export type WorldSequenceNullable = {
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
};

export type WorldShaping = {
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
};

export type WorldShapingAcross = {
  /**
   * The lateral convergence rate (u/s²) toward zero slip while this row governs, or null to remove slip immediately.
   */
  lateral?: number | null;
};

export type WorldShapingAlong = {
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
};

export type WorldSimulationDefaults = {
  /**
   * The simulation rate in Hz. Zero is a legal, distinct rate: a resident, non-stepping world — a static diorama the authoritative server never advances a fixed step for, though it still applies ordered submissions (mutations, session requests, connects/disconnects) through the administrative drain, so a rate-0 world can accept the very write that revives it. At rate 0, a simulation-tick duration authored as a positive value means never — not zero and not "already expired" — since there is no tick mapping for a world that never advances (see CompiledTickDuration, PopulationReconnectGraceTicks). A positive rate must be a divisor of TicksPerSecond (50400) exactly, so Puck.Hosting.EngineTicks.PerRate always derives a whole engine-tick step width — never truncated, never remainder-carried (WorldDefinitionValidator refuses a non-divisor, naming the nearest valid rates; a negative rate is refused outright, at any magnitude). 45 and 90 Hz — Steam Deck OLED's two refresh rates — both divide 50400 exactly (1120 and 560 engine ticks per step). The engine holds no rate of its own: an authored section states its rate, and a world authoring no simulation section runs at UnauthoredSimulationRateHz — the distinct rate-0 resident world is reached only by authoring rateHz 0 by name. The derived-floor seam. This record is deliberately the one place a follow-on validation pass adds the physics floor (from body size/speed), the interactivity floor (from input latency), the substep-derived contact clamp (contactHertz <= RateHz * n / 8 at substep count n — it coincides with RateHz / 4 only at n = 2), and the representable band — none of which is built yet. The clamp's n is a solver parameter, so its validator arrives with the solver landing that introduces it. A derived floor belongs here, beside the rate it constrains, never as a second section.
   */
  rateHz: number;
};

/**
 * A row's solidity facet — it participates in contact resolution using its own declared shape. Presence is the whole switch; null means decoration — the row is drawn but bodies pass through it.
 */
export type WorldSolid = {
  /**
   * The signed skin added to the shape for contact purposes. Positive fattens the collider past the drawn surface; negative lets a body sink in. Compensates the smooth-union blend.
   */
  margin: number;
};

/**
 * A placement-local bounded shape. Box yaw is measured about +Y; Y extents are part of the contract.
 */
export type WorldSpatialShape = {
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
};

export type WorldSpatialShapeKind = "Box" | "Sphere";

export type WorldSpawnPoint = {
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
};

export type WorldSpeaker = WorldSpeakerFixed | WorldSpeakerAnchored | WorldSpeakerBed | null;

export type WorldSpeakerAnchored = {
  $type?: "anchored";
  /**
   * What the speaker rides (see WorldAnchor).
   */
  anchor: WorldAnchorNonNullable;
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
};

/**
 * A point speaker's distance-attenuation policy, or null on the row to coalesce to the WorldAudioDefaults section (DefaultSpeakerRadius/DefaultCurve).
 */
export type WorldSpeakerAttenuation = {
  /**
   * The finite audible support radius in world units — at or beyond it the emitter is culled (finite support is the cull).
   */
  radius: number;
  /**
   * The falloff curve token (CurveSmoothstep or CurveLinear), or null for the audio-defaults curve.
   */
  curve: string | null;
};

export type WorldSpeakerBed = {
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
};

/**
 * A speaker's feed — what it plays: a shared source identity, a stereo channel selector, and a gain. Stereo separation is two independent speaker rows sharing one source with left/right selectors and different geometry — no group/attachment construct. Mono sources (the synth) degenerate every selector to ChannelMix.
 */
export type WorldSpeakerFeed = {
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
};

export type WorldSpeakerFixed = {
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
};

/**
 * The signal a WorldSpeaker's feed taps — a shared source identity, never an inline payload: the runtime drains each distinct source once per mix block and every feed tapping it shares that one pull, so "stereo = two rows sharing a source" costs one drain. The $type string is the JSON discriminator, matching WorldScreenSource's convention; a new source kind is a new derived record plus its JsonDerivedTypeAttribute line.
 */
export type WorldSpeakerSource = WorldSpeakerSourceNone | WorldSpeakerSourceMachine | WorldSpeakerSourceTune | WorldSpeakerSourceSynth | null;

/**
 * A named machine audio output. The source is a consumer reference only: the named machine host owns preparation, advancement, and the single drain shared by every speaker feed that names this output.
 */
export type WorldSpeakerSourceMachine = {
  $type?: "machine";
  /**
   * The declared Name instance.
   */
  instance: string;
  /**
   * The provider audio output name exposed by that instance.
   */
  output: string;
};

/**
 * No signal is bound — honest silence (the emitter holds its place; audio.emitters reads the state).
 */
export type WorldSpeakerSourceNone = {
  $type?: "none";
};

/**
 * The world voice synth playing a patch asset (WorldPatch). Patches are mono by construction: the feed's channel selectors degenerate to mix — documented, never rejected.
 */
export type WorldSpeakerSourceSynth = {
  $type?: "synth";
  /**
   * The referenced Name (must resolve).
   */
  patchId: string;
};

/**
 * A tune asset (WorldTune) played through a headless machine host — acquired while any speaker references it, released when orphaned (a runtime derivation, never a data concept).
 */
export type WorldSpeakerSourceTune = {
  $type?: "tune";
  /**
   * The referenced Name (must resolve).
   */
  tuneId: string;
};

export type WorldSpeed = {
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
};

export type WorldSpeedHeld = {
  /**
   * The declared composition channel name read while held.
   */
  channel: string;
  /**
   * The speed multiplier while the channel reads held. Required positive.
   */
  multiplier: number;
};

export type WorldStateRow = {
  /**
   * A validated state-section row name or cell key — the base SafeName rule plus no dot anywhere, which is what makes the state.<row>.<key> HUD binding grammar unambiguous by construction: splitting a bound token on '.' can never mistake part of a row or cell name for a grammar separator, because neither can hold one. It holds no backquote either, the expression language's quote for a name, so every row and cell key can be written in an expression. A row's reserved slot key ("$value") is unaffected — '$' is neither a reserved character nor a dot, so it is already a legal CellName like any other author-chosen key, exactly the one reserved exception the substrate mints rather than authors. A generated name (GeneratedName, turn$east) is a legal CellName for the same reason; the doors an author writes through refuse it, not this type.
   */
  name: string;
  /**
   * The closed set of cell value kinds a state row declares, shared by every cell the row carries. Carries no float kind: simulation state is float-free by the determinism contract (see Fixed for how a fractional value still rides here). A counter is represented as Fixed; a timer is Int declaring min zero.
   */
  kind: "Int" | "Fixed" | "Bool" | "Text" | "Vector";
  value?: ShapeNonNullable42;
  /**
   * The row's current cells (default empty). Refused past its effective capacity, and on a duplicate key, by name — unless Evicts is set, in which case a write that would grow past capacity evicts the oldest cell instead of refusing (see Evicts). A slot-shaped row (see IsSlot) holds exactly one cell keyed SlotKey; a keyed row may hold any author-chosen keys except SlotKey itself, which is reserved for the value sugar and refused as an authored cell key.
   */
  cells?: {
    key: string;
    value: ShapeNonNullable42;
    advance?: StateAdvance;
    dynamics?: StateDynamics;
    cycle?: StateCycle;
    /**
     * Whether a cell's effective value-over-time behavior is its carrying row's own default, or an explicit opt-out — see Behavior and Resolve.
     */
    behavior?: "Inherit" | "None";
    clock?: ShapeNonNullable43;
    visibility?: StateVisibility;
    /**
     * When a stored token property was last seen and whether the latest observation still sees it.
     */
    observation?: {
      /**
       * The last observation tick.
       */
      tick: number;
      /**
       * Whether the latest explicit refresh sees this token.
       */
      visible: boolean;
    };
    provenance?: string;
  }[];
  /**
   * The row-wide declared lower bound every cell's Value must satisfy, raw-encoded per Kind (raw FixedQ4816 bits for Fixed), or null for none. Independent of Max — a one-sided range (a floor with no ceiling, or the reverse) is legal; when both are present Min must be less than Max. Legitimate only for Int/Fixed. Omitted from the wire when null. A timer is represented as Int with this at zero.
   */
  min?: number | string;
  /**
   * The row-wide declared upper bound, raw-encoded per Kind, or null for none. Independent of Min; see its remarks. Omitted from the wire when null.
   */
  max?: number | string;
  /**
   * The row's own cell-count ceiling (1..MaxCellsPerRow), or null to fall back to the implicit ceiling. A row declaring Capacity can never be a slot (IsSlot), even if it happens to carry exactly one cell — declaring a capacity is declaring table intent. Omitted from the wire when null.
   */
  capacity?: number;
  /**
   * What Overflow does with a write TryAdmitWrite finds outside the row's declared envelope, or that overflows raw 64-bit arithmetic.
   */
  overflow?: "Refuse" | "Saturate";
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
    secret?: ClosedBitset256 | null;
    /**
     * An authored seek, non-negative, default 0: a rebuild advances the generator by (skip + cursor) * cost rather than cursor * cost. Authored data, the same class as the seed ladder's own rungs — it never writes the persisted DrawCursor, which keeps counting samples from zero exactly as an unskipped site's does.
     */
    skip?: number;
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
   * A persisted token-keyed knowledge layer refreshed explicitly by the authority.
   */
  knowledge?: {
    /**
     * The integer/boolean property row keyed by stable token identity.
     */
    source: string;
    /**
     * A boolean board; true cells are currently observed.
     */
    mask: string;
    /**
     * The integer row, keyed by the same tokens, whose values are cells of the mask's topology. Several tokens may occupy one cell. Null selects the direct board projection where source, mask, and knowledge share a topology.
     */
    positions?: string | null;
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
   * A StateRow's declared cell domain — the closed answer to "which keys does this row's storage admit" that IsKeyed/IsSlot/CellCeiling switch over, replacing the five hand-kept discriminators (a null Board, Tokens, Zone, KeysFrom, or History facet) inference used to read the same shape off of. Orthogonal traits — a row's Advance/Dynamics/Cycle/ Draw/Visibility/Knowledge/ Phase/PhaseOf/ Evicts/Min/Max/ Overflow — are unaffected by which case a row declares; every combination the validator already refused (a lattice row carrying advance, a phase row carrying capacity) is refused the identical way with the case substituted for the old field.
   */
  domain?: StateDomainSlot | StateDomainKeys | StateDomainKeysOf | StateDomainCellsOf | StateDomainRing | null;
  /**
   * A CellsOf row's declared inverse: the board's cells are not authored directly but derived from a keyed Tokens row naming cells of the same topology and a Codes row keyed the same way, giving each token's code. Legitimate only on a Int or Bool row (TryProveDerivedDomain) whose EffectiveDomain is CellsOf and that carries no field trait — a document project's validator enforces both.
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
  dynamics?: StateDynamics;
  cycle?: StateCycle;
  clock?: ShapeNonNullable43;
  /**
   * The vector space this row belongs to; required for Vector, refused for every other kind.
   */
  space?: string;
  /**
   * The StateEnum this row's integer values name, or null for a row whose values carry no symbolic domain. Legitimate only for Int cells, and only naming an enum the section declares.
   */
  enum?: CellName;
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
  /**
   * The verdict trait on a state row: the row is a test expectation's answer. The row's own name is the verdict's name, Gate is the expectation in the author's words, the cell Status names carries NotEvaluated/Pass/ Fail, and every other cell of the row is a value the gate saw, written by the same rule effect that decided the status. A verdict row is an Int row, so a value the gate saw of a Fixed or a Bool row is held by a witness: a row of that kind naming this one in Witness, written by the same firing, refused at every door this row is refused at, and frozen when this row settles.
   */
  verdict?: {
    /**
     * The expectation, in the author's words — what a failing verdict names. Non-empty, at most MaxGateLength characters. A .puckexpect clause lowers its own source text here.
     */
    gate: string;
    /**
     * The cell key carrying the status code. Must be a declared cell of the same row.
     */
    status: CellName;
  };
  /**
   * A validated state-section row name or cell key — the base SafeName rule plus no dot anywhere, which is what makes the state.<row>.<key> HUD binding grammar unambiguous by construction: splitting a bound token on '.' can never mistake part of a row or cell name for a grammar separator, because neither can hold one. It holds no backquote either, the expression language's quote for a name, so every row and cell key can be written in an expression. A row's reserved slot key ("$value") is unaffected — '$' is neither a reserved character nor a dot, so it is already a legal CellName like any other author-chosen key, exactly the one reserved exception the substrate mints rather than authors. A generated name (GeneratedName, turn$east) is a legal CellName for the same reason; the doors an author writes through refuse it, not this type.
   */
  witness?: string;
};

export type WorldStateSection = {
  /**
   * Document-owned cell rows. These remain mutation-addressable through state:<name>.
   */
  world?: WorldStateRow[] | null;
  /**
   * Per-body ephemeral counters and timers, compiled into each body's bounded ordinal arrays.
   */
  body?: (ActionStateSlot | null)[] | null;
  /**
   * Per-body counters and timers synchronized through the durable identity-document seam.
   */
  identity?: (ActionStateSlot | null)[] | null;
  /**
   * The lattice topologies the section's lattice-shaped rows lie over (see LatticeTopology; the document adds the physical WorldFieldTopology case).
   */
  lattices?: (LatticeTopology | null)[] | null;
  /**
   * The declared vector embedding spaces, or null for none.
   */
  spaces?: (StateSpace | null)[] | null;
  /**
   * The declared symbolic value domains a row may name, or null for none.
   */
  enums?: (StateEnum | null)[] | null;
  /**
   * The declared row families, or null for none.
   */
  families?: (StateFamily | null)[] | null;
  /**
   * The immutable record shapes used by pools.
   */
  records?: (StateRecord | null)[] | null;
  /**
   * The bounded instance pools and their initial or captured state.
   */
  pools?: (StatePool | null)[] | null;
  /**
   * Gets the bounded pools of endpoint pairs.
   */
  pairPools?: (StatePairPool | null)[] | null;
};

export type WorldStorageDefaults = {
  /**
   * The per-user blob endpoint (a URI, e.g. https://blob.byteterrace.com), or null for none. Validated as an absolute URI when present. Feeds WorldStorageSyncHandle's target construction; a URI here is edge-shaped (platform-managed containers), a connection-string override (CLI-only — see the validator) is raw-shaped.
   */
  endpoint?: string | null;
  /**
   * An explicit user-id override (a UUID string for a dev box or agent), or null to decline identity (local-only). Fed to the identity resolver's explicit-override source.
   */
  userId?: string | null;
  /**
   * The direct-to-account connection container listing uses when Endpoint resolves to an edge-shaped target — the platform edge cannot serve List at all (see the storage extension documentation), so an edge-shaped target with this null refuses discovery by name instead of a request the edge cannot answer. Validated as an absolute URI when present; a connection-string override (CLI-only — see the validator) is for the dev/emulator shape. Ignored when Endpoint is raw-shaped (a raw target lists directly, like it reads and writes).
   */
  discoveryEndpoint?: string | null;
};

export type WorldTargetRegister = {
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
};

export type WorldTether = {
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
};

/**
 * One elevation bloom hue's lit ring + outer halo pair (see Puck.Overlays.DesignTokens.Elevation for the composite rule). Each color carries its own baked alpha (#RRGGBBAA).
 */
export type WorldThemeBloomHue = {
  /**
   * The 1px lit ring color.
   */
  ring: BindableColor;
  /**
   * The outer distance-falloff halo color.
   */
  halo: BindableColor;
};

export type WorldThemeChrome = {
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
};

export type WorldThemeColor = {
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
};

/**
 * One of the two settled cubic-bezier easings a theme's motion section authors.
 */
export type WorldThemeCubicBezier = {
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
};

export type WorldThemeDiegetic = {
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
};

export type WorldThemeElevation = {
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
};

export type WorldThemeIcon = {
  /**
   * The hairline stroke half-width every procedural glyph/icon draws with, in glyph-local units.
   */
  strokeHalfWidth: number;
};

export type WorldThemeMotion = {
  caretBlink: number;
  durFast: number;
  durMed: number;
  durPanel: number;
  easeStd: WorldThemeCubicBezier;
  easeOut: WorldThemeCubicBezier;
};

export type WorldThemeRadius = {
  radius1: number;
  radius2: number;
  radius3: number;
};

/**
 * One scrim's fill color plus its own alpha, split apart so a world can retheme opacity independent of hue — the two knobs a scrim (a translucent panel/strip/chip backing) actually varies. Alpha is clamped to ScrimMinAlpha at resolve time when it is a state binding (see WorldDefinitionValidator's theme validation for the literal-authoring floor).
 */
export type WorldThemeScrim = {
  /**
   * The scrim's opaque fill color.
   */
  color: BindableColor;
  /**
   * The scrim's opacity, in [0, 1].
   */
  alpha: BindableScalar;
};

export type WorldThemeSection = {
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
};

export type WorldThemeSpace = {
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
};

export type WorldThemeType = {
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
};

export type WorldTonemap = "None" | "Filmic";

export type WorldTransferFullPolicy = "Retry" | "Refuse";

export type WorldTune = {
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
};

export type WorldTurn = {
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
};

export type WorldUpTurnRates = {
  /**
   * The ceiling on how fast a solved gravity field may turn the up axis. Crossing this rate lets an attractor's pull replace the world's own as one continuous roll rather than a single-tick inversion; every ordinary reorientation turns far slower and is untouched.
   */
  field?: number;
  /**
   * The ceiling on how fast a measured ground-contact normal may turn the up axis — a discontinuity filter, not a smoothing rate: it should sit an order of magnitude above the fastest curvature this kit's own top speed can walk, so ordinary running adopts the surface exactly and only a collider crease is spread across a few ticks. Both rates must remain positive after Q48.16 compilation.
   */
  contact?: number;
};

export type WorldUpdateDefaults = {
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
};

export type WorldViewDefaults = {
  /**
   * The authored chase framing, or null for a document that seats no body; SeatRig is what a reader resolves through.
   */
  seatRig?: WorldCameraProgram | null;
  /**
   * The authored constraints for live seat camera input, or null for a document that seats no body; SeatControl is what a reader resolves through.
   */
  seatControl?: WorldSeatViewControl | null;
  /**
   * The program a seat's view resolves through while its published mode state targets CameraTarget — null for a world that authors no camera-targeting mode state. Resolved through the ordinary Puck.World.Client.WorldCameraRigCompiler pipeline against whichever body the seat currently perceives from (the possessed camera body — see Puck.World.Server.WorldEngagement), exactly like SeatRig resolves against the seat's own avatar; no bespoke per-frame integrator reads this field.
   */
  cameraRig?: WorldCameraProgram | null;
  /**
   * The directory holding the pipeline compiler's dxc, or null to resolve it by bare name through the ordinary executable search path — never an environment variable.
   */
  shaderToolchain?: string | null;
  /**
   * The authored views.graphs frame-graph instances (empty = none declared).
   */
  graphs?: (WorldViewGraph | null)[] | null;
  /**
   * The graph scheduler's price ceiling, or null for none.
   */
  graphBudget?: WorldViewGraphBudget | null;
  /**
   * Gets the authored named layouts. The absence-coalesce lives in the accessor for the same reason Elements's does.
   */
  layouts?: (WorldViewLayout | null)[] | null;
  /**
   * Gets the authored views.pipelines rows. The absence-coalesce lives in the accessor for the same reason Elements's does.
   */
  pipelines?: (WorldViewPipeline | null)[] | null;
};

export type WorldViewGraph = {
  /**
   * The instance's stable name (a SafeName, unique within the section), which another row's input names.
   */
  name: string;
  /**
   * The graph document, resolved relative to the world document's own directory as a Source is.
   */
  source: string;
  /**
   * The authored camera the instance renders from, or null for a graph that reads no camera.
   */
  camera?: string | null;
  /**
   * How often it refreshes, or null for every frame something visible reads it.
   */
  refresh?: WorldViewGraphRefresh | null;
  /**
   * The external versions of its graph bound to other instances' outputs, or null for none.
   */
  inputs?: (WorldViewGraphInput | null)[] | null;
};

export type WorldViewGraphBudget = {
  /**
   * The pass-pixels (passes times rendered pixels) those instances may spend in one presented frame; the stalest due instance is admitted first and the rest read their latest completed output. 0 sets no ceiling.
   */
  passPixelsPerFrame?: number;
};

export type WorldViewGraphInput = {
  /**
   * The external version of the instance's graph document the binding fills.
   */
  resource: string;
  /**
   * The views.graphs row whose output fills it. Naming the row's own instance reads its previous frame.
   */
  instance: string;
  /**
   * Whether the read takes the producer's previous completed frame, which lets two instances show each other. Default false: the producer renders first in the same frame.
   */
  previousFrame?: boolean;
};

export type WorldViewGraphRefresh = {
  /**
   * The instance renders at most once every this many presented frames, or null when Hertz states the rate.
   */
  divisor?: number | null;
  /**
   * The most renders a second, or null when Divisor states the rate.
   */
  hertz?: number | null;
};

export type WorldViewLayout = {
  /**
   * The layout's stable name (the view.override layout override handle; unique within the section).
   */
  name: string;
  /**
   * The joined-seat count this layout composes for, or 0 for the catch-all. Default 0.
   */
  seatCount?: number;
  /**
   * How long the ease into this composition takes when it becomes active. Default 0, a cut.
   */
  transitionSeconds?: number;
  /**
   * The render scale (0, 1] applied to every slot mid-transition (a soft dip that sharpens on settle). Default 1, no dip.
   */
  transitionRenderScale?: number;
  /**
   * Gets the slots, in order. The absence-coalesce lives in the accessor for the same reason Elements's does.
   */
  slots: WorldViewSlot[];
};

export type WorldViewPipeline = {
  /**
   * The pipeline's stable name (a SafeName, unique within the section) — what a Pipeline names and a pipeline.* console verb addresses.
   */
  name: string;
  /**
   * Where the pipeline comes from: a pipeline document, a one-off shader source file, or a puck.shader.package.v1 package directory, which loads with its source tree gone. It resolves beside the document that authors it (WorldDocumentPaths), like every relative path a document authors.
   */
  source: string;
  /**
   * The authored camera feeding the pipeline frame block's cameraPosition, cameraTarget, cameraUp and cameraFov, or null, which leaves cameraFov zero so a pipeline keeps its own pointer orbit.
   */
  camera?: string | null;
  /**
   * The pipeline clock's rate multiplier — presentation only, never simulation state. Default 1; 0 freezes the clock.
   */
  timeScale?: number;
  /**
   * The image version the instance shows, or null for the source's first declared output.
   */
  output?: string | null;
  /**
   * This instance's parameter overrides, keyed by pass name; each value is that pass's config object keyed by field. A field absent here keeps the default the shared source declares, so two instances of one source differ only in what they override. The source's config schema binds them when a boot, a world.load or world.reload, a commit, or an upsert names them (the server reads the source, and refuses a value by name), and again when a graph installs (the host binds them into its passes). null overrides nothing.
   */
  overrides?: {
    [k: string]: unknown;
  } | null;
};

export type WorldViewSlot = {
  /**
   * The rect's left edge, normalized [0, 1]. Default 0.
   */
  x?: number;
  /**
   * The rect's top edge, normalized [0, 1]. Default 0.
   */
  y?: number;
  /**
   * The rect's width, normalized (0, 1]. Default 1, so a slot that authors no rect fills the window.
   */
  width?: number;
  /**
   * The rect's height, normalized (0, 1]. Default 1.
   */
  height?: number;
  /**
   * The authored camera name filling this slot, or null for the seat that owns it (or the pipeline named by Pipeline).
   */
  camera?: string | null;
  /**
   * The authored views.pipelines row name filling this slot with a compiled shader pipeline, or null for an ordinary seat/camera slot. Mutually exclusive with Camera.
   */
  pipeline?: string | null;
};

export type WorldVoiceProfile = {
  /**
   * The referenced Name (must resolve when declared).
   */
  patchId?: string | null;
  /**
   * The base inter-syllable tick spacing (must be positive when declared).
   */
  cadenceTicks?: number | null;
};

/**
 * Selection of a single token from a zone.
 */
export type ZoneSelector = "Key" | "First" | "Last" | "Random" | "Slice";
