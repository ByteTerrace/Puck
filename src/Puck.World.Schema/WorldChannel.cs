using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The declared channel ordinals resolved for the engine motion roles. An unclaimed role is <c>-1</c>.</summary>
public readonly record struct RoleChannelOrdinals(int MoveAdvance, int MoveStrafe, int Turn, int MoveUp, int Pitch, int Roll, int FaceX = -1, int FaceY = -1, int FaceZ = -1, int MoveX = -1, int MoveY = -1, int MoveZ = -1) {
    /// <summary>Gets a value indicating whether the world declares the full world-frame movement-direction triple.</summary>
    public bool HasMoveDirection => (((MoveX >= 0) && (MoveY >= 0)) && (MoveZ >= 0));

    /// <summary>Gets the authored ordinal claiming <paramref name="role"/>, or <c>-1</c> when unclaimed.</summary>
    public int this[ChannelRole role] => role switch {
        ChannelRole.MoveAdvance => MoveAdvance,
        ChannelRole.MoveStrafe => MoveStrafe,
        ChannelRole.Turn => Turn,
        ChannelRole.MoveUp => MoveUp,
        ChannelRole.Pitch => Pitch,
        ChannelRole.Roll => Roll,
        ChannelRole.FaceX => FaceX,
        ChannelRole.FaceY => FaceY,
        ChannelRole.FaceZ => FaceZ,
        ChannelRole.MoveX => MoveX,
        ChannelRole.MoveY => MoveY,
        ChannelRole.MoveZ => MoveZ,
        _ => -1,
    };

    /// <summary>Builds an intent by writing values to the declared role ordinals.</summary>
    public PlayerIntent Intent(FixedQ4816 moveAdvance = default, FixedQ4816 moveStrafe = default, FixedQ4816 turn = default,
        FixedQ4816 moveUp = default, FixedQ4816 pitch = default, FixedQ4816 roll = default) {
        var intent = default(PlayerIntent);

        intent = Write(
            intent: intent,
            role: ChannelRole.MoveAdvance,
            value: moveAdvance
        );
        intent = Write(
            intent: intent,
            role: ChannelRole.MoveStrafe,
            value: moveStrafe
        );
        intent = Write(
            intent: intent,
            role: ChannelRole.Turn,
            value: turn
        );
        intent = Write(
            intent: intent,
            role: ChannelRole.MoveUp,
            value: moveUp
        );
        intent = Write(
            intent: intent,
            role: ChannelRole.Pitch,
            value: pitch
        );
        intent = Write(
            intent: intent,
            role: ChannelRole.Roll,
            value: roll
        );

        return intent;
    }
    /// <summary>Reads a resolved role from <paramref name="intent"/>.</summary>
    public FixedQ4816 Read(in PlayerIntent intent, ChannelRole role) {
        var ordinal = this[role];

        return ((ordinal >= 0)
            ? intent[ordinal]
            : FixedQ4816.Zero
        );
    }
    /// <summary>Returns <paramref name="intent"/> with one claimed role replaced.</summary>
    public PlayerIntent Write(PlayerIntent intent, ChannelRole role, FixedQ4816 value) {
        var ordinal = this[role];

        return ((ordinal >= 0)
            ? intent.WithChannel(
                ordinal: ordinal,
                value: value
            )
            : intent
        );
    }
}
/// <summary>The world's channel table compiled once before simulation: name→ordinal resolution and per-ordinal shape
/// and threshold — the vocabulary <c>Puck.World.Server.WorldBody</c>'s edge derivation, the binding/press
/// surfaces, and the addon wire resolve declared channel names against. Validation
/// (<see cref="WorldDefinitionValidator"/>) has already run by the time this is built. Channels receive document-order slots within their own bank; role claims populate a resolved lookup
/// and per-address role mask.</summary>
public sealed class WorldChannelTable {
    /// <summary>Gets the document's compiled default tuple. Live consumers use their published owner mapping.</summary>
    public static WorldChannelTable For(WorldDefinition definition) => WorldInputCatalog.For(definition).DefaultMapping.Channels;

    /// <summary>The default binary threshold — <c>One/2</c>, the one threshold at which the flip bound
    /// <c>c ≤ min(T − 1, One − T)</c> collapses to the symmetric <c>c &lt; ½</c> (see
    /// <see cref="FixedContributionFold"/>'s remarks). A world declaring any other threshold is legal and gets the
    /// general bound, not this special case.</summary>
    public static readonly FixedQ4816 DefaultBinaryThreshold = (FixedQ4816.One / FixedQ4816.FromInteger(value: 2L));
    /// <summary>Gets the empty table — every world/kit compile call site that has not been threaded a real one yet falls
    /// back to this rather than null-checking.</summary>
    public static WorldChannelTable Empty { get; } = new WorldChannelTable(ordinals: OrdinalTable.Empty);

    private readonly WorldRelativeChannel?[] m_relative = new WorldRelativeChannel?[ChannelLimits.MaxAxes];
    private readonly FixedQ3232[] m_relativeLimits = new FixedQ3232[ChannelLimits.MaxAxes];
    private readonly ChannelShape[] m_shapes = new ChannelShape[ChannelLimits.MaxChannels];
    private readonly ChannelFrame[] m_frames = new ChannelFrame[ChannelLimits.MaxChannels];
    private readonly FixedQ4816[] m_thresholds = new FixedQ4816[ChannelLimits.MaxChannels];
    private readonly bool[] m_declared = new bool[ChannelLimits.MaxChannels];
    private readonly bool[] m_roles = new bool[ChannelLimits.MaxChannels];
    private readonly int[] m_roleOrdinals = new int[Enum.GetValues<ChannelRole>().Length];
    private readonly bool[] m_declaredRoles = new bool[Enum.GetValues<ChannelRole>().Length];
    // The reverse of m_ordinals — an ordinal's declared name, for a read-back that must name a channel rather
    // than just its ordinal (player.channels). Null at an undeclared address — unlike m_ordinals,
    // sized to MaxChannels so any in-range ordinal indexes safely without a bounds check of its own.
    private readonly string?[] m_names = new string?[ChannelLimits.MaxChannels];

    private readonly OrdinalTable m_ordinals;

    private int[] m_addresses = [];
    private ChannelShape[] m_logicalShapes = [];

    private WorldChannelTable(OrdinalTable ordinals) {
        m_ordinals = ordinals;
        Array.Fill(
            array: m_roleOrdinals,
            value: -1
        );
    }

    /// <summary>The numeric slots carrying displacement in this layout.</summary>
    public uint RelativeMask { get; private set; }
    /// <summary>Gets the declared channel count.</summary>
    public int ChannelCount { get; private init; }
    /// <summary>Whether a logical declaration owns a role, including roles inactive in this mapping.</summary>
    public bool DeclaresRole(ChannelRole role) => (uint)role < (uint)m_declaredRoles.Length && m_declaredRoles[(int)role];
    /// <summary>Gets the frame the MoveAdvance/MoveStrafe pair is authored in — the two declare the same frame by
    /// validator rule, so this reads either; <see cref="ChannelFrame.World"/> when neither role is claimed.</summary>
    public ChannelFrame MoveFrame => ((RoleOrdinals.MoveAdvance >= 0)
        ? m_frames[RoleOrdinals.MoveAdvance]
        : ((RoleOrdinals.MoveStrafe >= 0)
            ? m_frames[RoleOrdinals.MoveStrafe]
            : ChannelFrame.World
    ));
    /// <summary>Gets the resolved role ordinal set.</summary>
    public RoleChannelOrdinals RoleOrdinals => new(
        MoveAdvance: m_roleOrdinals[((int)ChannelRole.MoveAdvance)],
        MoveStrafe: m_roleOrdinals[((int)ChannelRole.MoveStrafe)],
        Turn: m_roleOrdinals[((int)ChannelRole.Turn)],
        MoveUp: m_roleOrdinals[((int)ChannelRole.MoveUp)],
        Pitch: m_roleOrdinals[((int)ChannelRole.Pitch)],
        Roll: m_roleOrdinals[((int)ChannelRole.Roll)],
        FaceX: m_roleOrdinals[((int)ChannelRole.FaceX)],
        FaceY: m_roleOrdinals[((int)ChannelRole.FaceY)],
        FaceZ: m_roleOrdinals[((int)ChannelRole.FaceZ)],
        MoveX: m_roleOrdinals[((int)ChannelRole.MoveX)],
        MoveY: m_roleOrdinals[((int)ChannelRole.MoveY)],
        MoveZ: m_roleOrdinals[((int)ChannelRole.MoveZ)]
    );

    /// <summary>Compiles a world's declared channel table.</summary>
    /// <param name="channels">The world document's declared channel rows, already validated.</param>
    public static WorldChannelTable Compile(IReadOnlyList<WorldChannel> channels) {
        channels = channels.OrderBy(static row => row.Name, StringComparer.Ordinal).ToArray();
        var addresses = new int[channels.Count];
        var axis = 0;
        var button = 0;
        for (var index = 0; index < channels.Count; index++) {
            addresses[index] = channels[index].Shape == ChannelShape.Text ? -2 : channels[index].Shape == ChannelShape.Binary ? ChannelLimits.MaxAxes + button++ : axis++;
        }
        if ((axis > ChannelLimits.MaxAxes) || (button > ChannelLimits.MaxButtons)) {
            throw new ArgumentException("The live input mapping exceeds its numeric or button bank.", nameof(channels));
        }
        return CompileMapping(channels, addresses);
    }
    /// <summary>Compiles one allowed tuple's storage. Negative addresses are inactive logical declarations.</summary>
    public static WorldChannelTable CompileMapping(IReadOnlyList<WorldChannel> channels, IReadOnlyList<int> addresses) {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(addresses);
        if (channels.Count != addresses.Count) { throw new ArgumentException("Every logical declaration needs an address or an inactive marker.", nameof(addresses)); }
        var table = new WorldChannelTable(ordinals: OrdinalTable.Build(
            names: channels.Select(selector: static channel => channel.Name).ToArray(),
            comparer: StringComparer.Ordinal
        )) {
            ChannelCount = addresses.Count(static address => address >= 0),
            m_addresses = addresses.ToArray(),
            m_logicalShapes = channels.Select(static channel => channel.Shape).ToArray(),
        };

        for (var index = 0; (index < channels.Count); index++) {
            var channel = channels[index];
            if (channel.Role is { } declaredRole) { table.m_declaredRoles[(int)declaredRole] = true; }
            var ordinal = addresses[index];
            if (ordinal < 0) { continue; }
            if ((ordinal >= ChannelLimits.MaxChannels) || table.m_declared[ordinal] ||
                ((channel.Shape == ChannelShape.Binary) != (ordinal >= ChannelLimits.MaxAxes))) {
                throw new ArgumentException($"Channel '{channel.Name}' has an invalid or occupied bank address {ordinal}.", nameof(addresses));
            }

            table.m_shapes[ordinal] = channel.Shape;
            if (channel.Shape == ChannelShape.Relative) {
                table.RelativeMask |= (1U << ordinal);
                table.m_relative[ordinal] = channel.Relative!;
                table.m_relativeLimits[ordinal] = FixedQ3232.FromDouble(value: channel.Relative!.MaximumPerStep);
            }
            table.m_frames[ordinal] = channel.Frame;
            table.m_declared[ordinal] = true;
            table.m_names[ordinal] = channel.Name;
            if (channel.Role is { } role) {
                table.m_roles[ordinal] = true;
                table.m_roleOrdinals[((int)role)] = ordinal;
            }
            table.m_thresholds[ordinal] = ((channel.Shape == ChannelShape.Binary)
                ? ((channel.Threshold is { } threshold)
                    ? FixedQ4816.FromDouble(value: threshold)
                    : DefaultBinaryThreshold)
                : FixedQ4816.Zero
            );
        }

        return table;
    }
    /// <summary>Compiles a declared channel shape to the exact range and optional terminal threshold consumed by
    /// <see cref="FixedContributionFold.Evaluate"/>: bipolar is <c>(-One, One, null)</c>, unipolar is
    /// <c>(Zero, One, null)</c>, and binary is <c>(Zero, One, threshold)</c>. Binary's continuous pool/range domain is
    /// therefore the same as unipolar; only the last threshold step snaps it to a bit.</summary>
    /// <param name="shape">The declared channel shape.</param>
    /// <param name="threshold">The channel table's compiled fixed-point threshold (read only for binary).</param>
    public static (FixedQ4816 Minimum, FixedQ4816 Maximum, FixedQ4816? Threshold) CompileFoldShape(ChannelShape shape, FixedQ4816 threshold) {
        return (shape switch {
            ChannelShape.Bipolar => (-FixedQ4816.One, FixedQ4816.One, null),
            ChannelShape.Unipolar => (FixedQ4816.Zero, FixedQ4816.One, null),
            ChannelShape.Binary => (FixedQ4816.Zero, FixedQ4816.One, threshold),
            _ => (FixedQ4816.Zero, FixedQ4816.One, null),
        });
    }
    /// <summary>Composes exactly two simultaneous held-image values. Unipolar/binary take the maximum of two
    /// already-ranged operands (an OR). Bipolar instead sums and clamps once to
    /// <c>[-One, One]</c>, making zero an additive identity that cannot overwrite a genuinely negative value.</summary>
    /// <remarks>Pairwise clamping is safe here only because both callers combine exactly two already-settled operands:
    /// the owning seat with the tick's completed contributor accumulator in
    /// <c>Server.WorldServer.FoldChannelContributions</c>, or the resolved movement tier with the live-held image in
    /// <c>WorldBody.NextIntent</c>. An unordered growing contribution set must accumulate raw instead; clamping
    /// per arrival would make a bipolar result order-dependent.</remarks>
    /// <param name="a">One side's raw Q48.16 value.</param>
    /// <param name="b">The other side's raw Q48.16 value.</param>
    /// <param name="shape">The channel's declared shape.</param>
    public static long ComposeHeld(long a, long b, ChannelShape shape) {
        if (shape != ChannelShape.Bipolar) {
            return Math.Max(
                val1: a,
                val2: b
            );
        }

        var sum = (a + b);

        return ((sum < -FixedQ4816.One.Value)
            ? -FixedQ4816.One.Value
            : ((sum > FixedQ4816.One.Value)
                ? FixedQ4816.One.Value
                : sum
        ));
    }
    /// <summary>Formats a sample in its declared numeric representation, with its exact raw bits.</summary>
    public string DescribeValue(in PlayerIntent intent, int ordinal) => ((Shape(ordinal: ordinal) == ChannelShape.Relative)
        ? $"{intent.Channels.Relative(ordinal: ordinal)}({intent.Channels.Relative(ordinal: ordinal).Value})"
        : $"{ReadLevel(intent: intent, ordinal: ordinal)}({ReadLevel(intent: intent, ordinal: ordinal).Value})");
    /// <summary>Gets a displacement unit from its compiled descriptor.</summary>
    public string RelativeUnit(int ordinal) { RequireRelative(ordinal: ordinal); return m_relative[ordinal]!.Unit; }
    /// <summary>Gets a relative channel's compiled limit, refusing a level handle.</summary>
    public FixedQ3232 RelativeLimit(int ordinal) {
        RequireRelative(ordinal: ordinal);
        return m_relativeLimits[ordinal];
    }
    /// <summary>Quantizes one host displacement after authored sensitivity. No time factor participates.</summary>
    public FixedQ3232 QuantizeRelative(int ordinal, double sample) {
        RequireRelative(ordinal: ordinal);
        return FixedQ3232.FromDouble(value: (sample * m_relative[ordinal]!.Sensitivity));
    }
    /// <summary>Reads a normalized level, refusing a displacement descriptor.</summary>
    public FixedQ4816 ReadLevel(in PlayerIntent intent, int ordinal) {
        if (!IsDeclared(ordinal: ordinal) || (Shape(ordinal: ordinal) == ChannelShape.Relative)) {
            throw new ArgumentException(message: "The channel is not a held level.", paramName: nameof(ordinal));
        }
        return intent[ordinal];
    }
    /// <summary>Reads displacement through its checked descriptor.</summary>
    public FixedQ3232 ReadRelative(in PlayerIntent intent, int ordinal) {
        RequireRelative(ordinal: ordinal);
        return intent.Channels.Relative(ordinal: ordinal);
    }
    /// <summary>Saturates a completed widened displacement sum once.</summary>
    public FixedQ3232 ClampRelative(int ordinal, Int128 raw) {
        var bound = RelativeLimit(ordinal: ordinal).Value;

        return FixedQ3232.FromRawBits(value: ((long)Int128.Clamp(max: bound, min: -((Int128)bound), value: raw)));
    }

    private void RequireRelative(int ordinal) {
        if (!IsDeclared(ordinal: ordinal) || (Shape(ordinal: ordinal) != ChannelShape.Relative)) {
            throw new ArgumentException(message: "The channel is not a relative displacement.", paramName: nameof(ordinal));
        }
    }

    /// <summary>Gets a declared channel's movement frame (<see cref="ChannelFrame.World"/> for every channel that
    /// declares none — and for every channel but the MoveAdvance/MoveStrafe pair, where it is meaningless).</summary>
    public ChannelFrame Frame(int ordinal) => m_frames[ordinal];
    /// <summary>Determines whether a channel is declared at this ordinal.</summary>
    public bool IsDeclared(int ordinal) => ((ordinal >= 0) && (ordinal < ChannelLimits.MaxChannels) && m_declared[ordinal]);
    /// <summary>Determines whether the declared channel at <paramref name="ordinal"/> claims an engine motion role.</summary>
    public bool IsRole(int ordinal) => ((ordinal >= 0) && (ordinal < ChannelLimits.MaxChannels) && m_roles[ordinal]);
    /// <summary>Returns the declared channel name at this ordinal, or <see langword="null"/> when <see cref="IsDeclared"/> is
    /// <see langword="false"/> for it — the reverse of name-to-ordinal resolution, for a read-back that must name a
    /// channel (<c>body.channels</c>) rather than address it.</summary>
    public string? Name(int ordinal) => m_names[ordinal];
    /// <summary>Returns the declared shape at this ordinal (meaningful only when <see cref="IsDeclared"/>).</summary>
    public ChannelShape Shape(int ordinal) => m_shapes[ordinal];
    /// <summary>Returns the binary crossing threshold at this ordinal (meaningful only for a <see cref="ChannelShape.Binary"/> channel).</summary>
    public FixedQ4816 Threshold(int ordinal) => m_thresholds[ordinal];
    /// <summary>Resolves a declared channel name to its ordinal.</summary>
    public bool TryGetOrdinal(string name, out int ordinal) {
        if (m_ordinals.TryGetOrdinal(name: name, ordinal: out var index)) {
            ordinal = m_addresses[index];
            return ordinal >= 0;
        }
        ordinal = -1;
        return false;
    }
    /// <summary>Resolves a semantic name even when its action is inactive in this mapping.</summary>
    public bool TryGetLogicalId(string name, out int logical) => m_ordinals.TryGetOrdinal(name, out logical);
    /// <summary>Gets the active storage address of a logical id, refusing dormant and event-only actions.</summary>
    public bool TryGetAddress(int logical, out int address) {
        address = (uint)logical < (uint)m_addresses.Length ? m_addresses[logical] : -1;
        return address >= 0;
    }
    /// <summary>Gets the compiled address of a logical declaration in ordinal name order.</summary>
    public int Address(int declaration) => m_addresses[declaration];
    /// <summary>Resolves declared vocabulary independently of eligibility in this mapping.</summary>
    public bool TryGetShape(ChannelRef reference, out ChannelShape shape) {
        if (reference is ChannelRef.Name name && m_ordinals.TryGetOrdinal(name.Value, out var logical)) {
            shape = m_logicalShapes[logical];
            return true;
        }
        shape = default;
        return false;
    }
    /// <summary>Quantizes a binary contribution after its source fold; numeric values keep their precision.</summary>
    public FixedQ4816 Quantize(int ordinal, FixedQ4816 value) => ((Shape(ordinal: ordinal) == ChannelShape.Binary)
        ? ((value >= Threshold(ordinal: ordinal)) ? FixedQ4816.One : FixedQ4816.Zero) : value);
    /// <summary>Resolves a binding channel reference to its authored ordinal. <see cref="ChannelRef"/> carries only
    /// the declared-name arm; see <c>ChannelRef.cs</c>'s remarks.</summary>
    public bool TryGetOrdinal(ChannelRef reference, out int ordinal) {
        switch (reference) {
            case ChannelRef.Name name:
                return TryGetOrdinal(
                    name: name.Value,
                    ordinal: out ordinal
                );
            default:
                ordinal = -1;

                return false;
        }
    }
}
/// <summary>One row of the world's channel table — the intent vector's declared vocabulary (see
/// <see cref="Puck.World.Protocol.PlayerIntent"/>). The consumer is exactly one of <see cref="Role"/> (an engine
/// motion channel, claimable by at most one channel) or <see cref="Composition"/> (a kit composition trigger, bound —
/// or left inert — per kit via <see cref="WorldKit.Actions"/>).</summary>
/// <param name="Name">The channel's unique, non-empty name — the vocabulary key every binding, <c>body.press</c>,
/// kit <c>Actions</c> entry, and the addon wire resolve against.</param>
/// <param name="Shape">The declared value shape: bipolar <c>[-1, 1]</c>, unipolar <c>[0, 1]</c>, or binary.</param>
/// <param name="Role">The engine motion role this channel claims, or <see langword="null"/> for a composition channel.</param>
/// <param name="Composition">Whether this channel is a kit-composition trigger. Exactly one of <paramref name="Role"/>
/// or this must be set.</param>
/// <param name="Threshold">The binary crossing threshold in <c>[0, 1]</c> raw units (binary channels only); <see langword="null"/>
/// takes <see cref="WorldChannelTable.DefaultBinaryThreshold"/> (<c>One/2</c>).</param>
/// <param name="Relative">Required units, sensitivity and limit for Relative channels; absent on levels.</param>
/// <param name="Frame">What the MoveAdvance/MoveStrafe pair is relative to when a binding row folds into it (see
/// <see cref="ChannelFrame"/>); the two roles must declare the same frame, and every other channel leaves it at
/// <see cref="ChannelFrame.World"/>. Omitted from a saved document at that default.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldChannel(
    string Name,
    ChannelShape Shape,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChannelRole? Role = null,
    bool Composition = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Threshold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ChannelFrame Frame = ChannelFrame.World,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRelativeChannel? Relative = null
);
/// <summary>Units and host sensitivity for a displacement channel. The maximum is applied once after accumulation.</summary>
/// <param name="Unit">An authored unit name shared by producers and consumers.</param>
/// <param name="Sensitivity">Units per device sample, applied before host quantization.</param>
/// <param name="MaximumPerStep">The positive absolute displacement limit in authored units.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRelativeChannel(string Unit, double Sensitivity, double MaximumPerStep);
/// <summary>The frame a movement contribution to the MoveAdvance/MoveStrafe pair is authored in — composed by the
/// seat's client into world axes before it reaches the wire, so the sim never reads a camera pose. A world declares
/// it once on the pair; the stick's <c>player.move</c> is camera-framed by its own definition, so a world (or a
/// player's overlay, where the world allows) mixes the two by choosing which rows fold into the channels and which
/// bind the stick verb: WASD in the body's heading with the arrows on Turn beside a camera-framed stick is one
/// document, not two modes.</summary>
[JsonConverter(typeof(StrictEnumConverter<ChannelFrame>))]
public enum ChannelFrame : byte {
    /// <summary>Raw world axes — what the sim reads; the kit's own <c>MoveFrame</c>/<c>FacingSnap</c> decide the rest.</summary>
    World,
    /// <summary>Relative to the seat's camera yaw — latched the tick movement begins, so the camera may orbit
    /// mid-run without steering — and the body's HEADING turns to the way it moves (the seat composes the direction
    /// into the FaceX/FaceZ roles) — the third-person scheme.</summary>
    Camera,
    /// <summary>Relative to the body's own heading, which the Turn role steers and movement never turns — the
    /// keyboard scheme: strafe sidesteps (the drawn attitude angling toward the travel under the kit's facing snap,
    /// the heading intact), turn turns, the camera is its own control.</summary>
    Heading,
}
