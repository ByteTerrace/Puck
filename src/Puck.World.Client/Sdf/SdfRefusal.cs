namespace Puck.World.Client.Sdf;

/// <summary>The <c>sdf.decode</c> door's whole refusal vocabulary — every reason <see cref="SdfDocumentException"/>
/// can be constructed with. <see cref="SdfDocumentDecoder"/>'s throw sites each name exactly one of these; there is
/// no other way to construct an <see cref="SdfDocumentException"/>, so <c>world.refusals</c>' catalog (which reads
/// this enum's <see cref="RefusalAttribute"/> tags) is exhaustive over what this door can refuse with, by
/// construction rather than by convention.</summary>
public enum SdfRefusal {
    /// <summary>The document's raw bytes are not well-formed JSON.</summary>
    [Refusal(door: "sdf.decode", condition: "the document's raw bytes are not well-formed JSON", kind: RefusalKind.ProtocolFault)]
    MalformedJson,

    /// <summary>The JSON root is not an object.</summary>
    [Refusal(door: "sdf.decode", condition: "the JSON root is not an object", kind: RefusalKind.ProtocolFault)]
    RootNotObject,

    /// <summary>An object repeats the same member name.</summary>
    [Refusal(door: "sdf.decode", condition: "an object repeats the same member name", kind: RefusalKind.ProtocolFault)]
    DuplicateKey,

    /// <summary>An object carries a member name outside this context's allowed set.</summary>
    [Refusal(door: "sdf.decode", condition: "an object carries a member name outside this context's allowed set", kind: RefusalKind.ProtocolFault)]
    UnknownMember,

    /// <summary>The root omits <c>materials</c> (an explicit <c>[]</c> is required to author none).</summary>
    [Refusal(door: "sdf.decode", condition: "the root omits 'materials' (an explicit [] is required to author none)", kind: RefusalKind.ProtocolFault)]
    MaterialsRequired,

    /// <summary><c>materials</c> is present but is not a JSON array.</summary>
    [Refusal(door: "sdf.decode", condition: "'materials' is present but is not a JSON array", kind: RefusalKind.ProtocolFault)]
    MaterialsNotArray,

    /// <summary>A <c>materials</c> entry is not a JSON object.</summary>
    [Refusal(door: "sdf.decode", condition: "a 'materials' entry is not a JSON object", kind: RefusalKind.ProtocolFault)]
    MaterialEntryNotObject,

    /// <summary>The root omits <c>ops</c> (an explicit <c>[]</c> is required to author none, and is also how
    /// <c>world.sdf.load</c> clears a previously loaded document).</summary>
    [Refusal(door: "sdf.decode", condition: "the root omits 'ops' (an explicit [] is required to author none)", kind: RefusalKind.ProtocolFault)]
    OpsRequired,

    /// <summary><c>ops</c> is present but is not a JSON array.</summary>
    [Refusal(door: "sdf.decode", condition: "'ops' is present but is not a JSON array", kind: RefusalKind.ProtocolFault)]
    OpsNotArray,

    /// <summary>An <c>ops</c> entry is not a JSON object.</summary>
    [Refusal(door: "sdf.decode", condition: "an 'ops' entry is not a JSON object", kind: RefusalKind.ProtocolFault)]
    OpEntryNotObject,

    /// <summary>An <c>ops</c> entry omits the <c>op</c> string naming which op it is.</summary>
    [Refusal(door: "sdf.decode", condition: "an 'ops' entry omits the 'op' string naming which op it is", kind: RefusalKind.ProtocolFault)]
    OpNameRequired,

    /// <summary>An <c>ops</c> entry names an op this decoder does not recognize.</summary>
    [Refusal(door: "sdf.decode", condition: "an 'ops' entry names an op this decoder does not recognize", kind: RefusalKind.ProtocolFault)]
    UnknownOpName,

    /// <summary>A required member (a number, a 3-vector, or a material index) is absent from its object.</summary>
    [Refusal(door: "sdf.decode", condition: "a required member (a number, a 3-vector, or a material index) is absent from its object", kind: RefusalKind.ProtocolFault)]
    RequiredMemberMissing,

    /// <summary>A member expected to be a JSON number is a different kind.</summary>
    [Refusal(door: "sdf.decode", condition: "a member expected to be a JSON number is a different kind", kind: RefusalKind.ProtocolFault)]
    NotANumber,

    /// <summary>A member expected to be a 3-element number array is a different shape.</summary>
    [Refusal(door: "sdf.decode", condition: "a member expected to be a 3-element number array is a different shape", kind: RefusalKind.ProtocolFault)]
    NotAVector3,

    /// <summary>A <c>blend</c> member names a string this decoder does not recognize.</summary>
    [Refusal(door: "sdf.decode", condition: "a 'blend' member names a string this decoder does not recognize", kind: RefusalKind.ProtocolFault)]
    UnknownBlendName,

    /// <summary>The root's <c>schema</c> member is not exactly <c>puck.sdf.v1</c>.</summary>
    [Refusal(door: "sdf.decode", condition: "the root's 'schema' member is not exactly 'puck.sdf.v1'", kind: RefusalKind.Verdict)]
    SchemaMismatch,

    /// <summary><c>materials</c> declares more entries than this prototype's fixed reservation
    /// (<see cref="SdfDocumentDecoder.MaxMaterials"/>).</summary>
    [Refusal(door: "sdf.decode", condition: "'materials' declares more entries than this prototype's fixed reservation", kind: RefusalKind.Verdict)]
    MaterialsTooMany,

    /// <summary><c>ops</c> declares more entries than this prototype's fixed reservation
    /// (<see cref="SdfDocumentDecoder.MaxOps"/>).</summary>
    [Refusal(door: "sdf.decode", condition: "'ops' declares more entries than this prototype's fixed reservation", kind: RefusalKind.Verdict)]
    OpsTooMany,

    /// <summary>A decoded number is NaN or infinite.</summary>
    [Refusal(door: "sdf.decode", condition: "a decoded number is NaN or infinite", kind: RefusalKind.Verdict)]
    NumberNotFinite,

    /// <summary>A decoded number that must be non-negative (a shape's radius/half-extent/round, or a material
    /// channel) is negative — the sign half of <see cref="SdfDocumentDecoder"/>'s mirror of
    /// <see cref="Puck.SignedDistance.SdfProgramBuilder"/>'s <c>RequireNonNegative</c> call sites (sphere/capsule/cylinder
    /// radii, cylinder half-height, torus radii, box half-extents and round, and all four material channels).</summary>
    [Refusal(door: "sdf.decode", condition: "a decoded number that must be non-negative (a shape's radius/half-extent/round, or a material channel) is negative", kind: RefusalKind.Verdict)]
    NumberNegative,

    /// <summary>An op's <c>material</c> index is outside the document's own materials palette.</summary>
    [Refusal(door: "sdf.decode", condition: "an op's 'material' index is outside the document's own materials palette", kind: RefusalKind.Verdict)]
    MaterialOutOfPalette,

    /// <summary>A <c>scale</c> op's component is not strictly positive.</summary>
    [Refusal(door: "sdf.decode", condition: "a 'scale' op's component is not strictly positive", kind: RefusalKind.Verdict)]
    ScaleNonPositive,

    /// <summary>A <c>smooth</c> value is negative.</summary>
    [Refusal(door: "sdf.decode", condition: "a 'smooth' value is negative", kind: RefusalKind.Verdict)]
    SmoothNegative,

    /// <summary>A <c>rotate</c> op's axis does not normalize to a finite unit vector.</summary>
    [Refusal(door: "sdf.decode", condition: "a 'rotate' op's axis does not normalize to a finite unit vector", kind: RefusalKind.Verdict)]
    AxisNotNormalizable,

    /// <summary>A top-level (or a top-level <c>push</c>'s own) blend is outside the union family.</summary>
    [Refusal(door: "sdf.decode", condition: "a top-level (or a top-level push's own) blend is outside the union family", kind: RefusalKind.Verdict)]
    BlendNotTopLevelAllowed,

    /// <summary>A <c>push</c> would nest a field scope past this build's depth cap
    /// (<see cref="Puck.SignedDistance.SdfProgramBuilder.MaxFieldScopeDepth"/>).</summary>
    [Refusal(door: "sdf.decode", condition: "a 'push' would nest a field scope past this build's depth cap", kind: RefusalKind.Verdict)]
    PushTooDeep,

    /// <summary>A <c>pop</c> appears with no matching open <c>push</c>.</summary>
    [Refusal(door: "sdf.decode", condition: "a 'pop' appears with no matching open 'push'", kind: RefusalKind.Verdict)]
    PopUnmatched,

    /// <summary>The document ends with one or more <c>push</c> ops never closed by <c>pop</c>.</summary>
    [Refusal(door: "sdf.decode", condition: "the document ends with one or more 'push' ops never closed by 'pop'", kind: RefusalKind.Verdict)]
    UnclosedPush,

    /// <summary>The program builder itself refused a decoded material for a reason this decoder does not pre-check
    /// (the inherited half of this door's validation — see <see cref="SdfDocumentDecoder.Replay"/>). NOT dead: a
    /// negative material channel is refused earlier now, as <see cref="NumberNegative"/>, but <c>AddMaterial</c>'s
    /// composed-palette/<c>ScreenMaterialId</c> collision gate depends on cross-contributor state (how many
    /// materials the SHARED builder already holds from other emitters) this decoder cannot see or pre-check, and
    /// remains live through this path.</summary>
    [Refusal(door: "sdf.decode", condition: "the program builder itself refused a decoded material for a reason this decoder does not pre-check (e.g. the composed palette's ScreenMaterialId collision gate — cross-contributor state this decoder cannot see; NOT sign, which is refused earlier as NumberNegative)", kind: RefusalKind.Verdict)]
    BuilderRejectedMaterial,

    /// <summary>The program builder itself refused a decoded op for a reason this decoder does not pre-check (the
    /// inherited half of this door's validation — see <see cref="SdfDocumentDecoder.Replay"/>). NOT dead: a negative
    /// radius/half-extent/round is refused earlier now, as <see cref="NumberNegative"/>, but the builder's
    /// DERIVED-overflow checks (e.g. a torus major/minor radius pair that is individually finite and non-negative
    /// but whose SUM overflows; the same shape for a cylinder's radius/half-height bound, a capsule endpoint's dot
    /// product, or a box's half-extent length) are not replicated at decode and remain live through this
    /// path.</summary>
    [Refusal(door: "sdf.decode", condition: "the program builder itself refused a decoded op for a reason this decoder does not pre-check (e.g. a derived-quantity overflow from individually finite, non-negative inputs — a torus radius sum, a cylinder/box/capsule derived bound; NOT sign, which is refused earlier as NumberNegative)", kind: RefusalKind.Verdict)]
    BuilderRejectedOp,

    /// <summary>An internal op-kind switch has no case for a decoded op. Defensive: <see cref="SdfDocumentDecoder"/>'s
    /// own op-name table only ever produces a defined <see cref="SdfDocumentOpKind"/>, so this is not reachable
    /// through the decoder's public entry points today.</summary>
    [Refusal(door: "sdf.decode", condition: "an internal op-kind switch has no case for a decoded op (defensive; not reachable through this decoder's own op-name table)", kind: RefusalKind.Verdict)]
    UnhandledOpKind,

    /// <summary>The document, composed with the CURRENT live world definition, would exceed the probed render
    /// envelope's program-word ceiling — the document validates alone but not beside what the scene currently
    /// spends. See <see cref="Puck.World.Client.WorldSdfDocumentEmitter.Load"/>.</summary>
    [Refusal(door: "sdf.decode", condition: "the document, composed with the live world scene, would exceed the probed program-word envelope", kind: RefusalKind.Verdict)]
    ComposedProgramWordsExceeded,

    /// <summary>The document, composed with the CURRENT live world definition, would exceed the probed render
    /// envelope's instance ceiling. See <see cref="Puck.World.Client.WorldSdfDocumentEmitter.Load"/>.</summary>
    [Refusal(door: "sdf.decode", condition: "the document, composed with the live world scene, would exceed the probed instance envelope", kind: RefusalKind.Verdict)]
    ComposedInstancesExceeded,
}
