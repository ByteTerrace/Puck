namespace Puck.State.Rules;

/// <summary>What a host must serve for a rule's <c>transformState</c> effect to fire. A host that does not
/// implement it refuses every transform by name rather than applying part of one.</summary>
public interface IArenaTransformHost {
    /// <summary>Applies one resolved transform inside the firing's open journal scope.</summary>
    /// <param name="transform">The resolved transform.</param>
    /// <param name="binding">The live key and zone ends the firing resolved.</param>
    /// <param name="moved">Whether the transform changed the arena.</param>
    /// <param name="refusal">Why the transform did not apply, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the transform applied.</returns>
    /// <remarks>The implementation never opens or closes a journal scope of its own: the firing's scope is what a
    /// refusal rewinds.</remarks>
    bool TryTransform(ArenaTransform transform, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal);
}
/// <summary>An effect host for rules that read and write the arena and nothing else: the state-only game with no
/// document project's facets behind it. Every write goes through the arena's own admission door, so a row's
/// envelope, overflow policy and symbolic domain decide it.</summary>
/// <remarks>The host is the reader every operand answers from, so a caller advances it to the tick it wants
/// (<see cref="Advance"/>) before evaluating.</remarks>
public class ArenaEffectHost : IEffectHost, IArenaTransformHost {
    private readonly long[] m_locals = new long[RuleCapacity.MaxLocalsPerRule];

    private readonly IReadOnlyList<DynamicsRow>? m_dynamics;
    private readonly ulong m_documentSeed;
    private readonly IReadOnlyList<GeneratorRow>? m_generators;
    private readonly string m_instanceIdentity;

    private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];

    private readonly ArenaDrawSite m_site;
    private readonly int m_ticksPerSecond;


    /// <summary>Initializes a host over an arena.</summary>
    /// <param name="arena">The store every read and write addresses.</param>
    /// <param name="generators">The section's declared draw sources, which a <c>generate</c> effect's row may name
    /// instead of inlining one.</param>
    /// <param name="ticksPerSecond">The simulation rate a dynamics follower is stepped at; zero leaves a dynamics
    /// cell reading its stored target.</param>
    /// <param name="dynamics">The declared dynamics rows a <see cref="StateDynamics"/> trait resolves against.</param>
    /// <param name="documentSeed">The document's own reroll lever, folded into every draw site's seed.</param>
    /// <param name="instanceIdentity">The running instance's identity, folded into every draw site's seed.</param>
    public ArenaEffectHost(StateArena arena, IReadOnlyList<GeneratorRow>? generators = null, int ticksPerSecond = 0, IReadOnlyList<DynamicsRow>? dynamics = null, ulong documentSeed = 0UL, string instanceIdentity = "") {
        ArgumentNullException.ThrowIfNull(argument: arena);

        Arena = arena;
        m_documentSeed = documentSeed;
        m_dynamics = dynamics;
        m_generators = generators;
        m_instanceIdentity = instanceIdentity;
        m_site = (_, rowOrdinal) => DrawSite(rowOrdinal: rowOrdinal);
        m_ticksPerSecond = ticksPerSecond;
    }

    /// <inheritdoc/>
    public StateArena Arena { get; }
    /// <inheritdoc/>
    public Span<long> Locals => m_locals;
    /// <inheritdoc/>
    public CellKey BoundEachKey { get; set; }
    /// <inheritdoc/>
    public CellKey BoundPreviousKey { get; set; }
    /// <inheritdoc/>
    public CellKey BoundTokenKey { get; set; }
    /// <inheritdoc/>
    public ulong EngineTick { get; private set; }
    /// <inheritdoc/>
    public Span<long> PatternWord => m_patternWord;
    /// <inheritdoc/>
    public ulong Tick { get; private set; }
    /// <inheritdoc/>
    public ArenaTime Time => new(
        Dynamics: m_dynamics,
        EngineTick: EngineTick,
        Tick: Tick,
        TicksPerSecond: m_ticksPerSecond
    );

    /// <summary>Moves the tick pair every read answers as of.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="engineTick">The engine tick.</param>
    public void Advance(ulong tick, ulong engineTick) {
        EngineTick = engineTick;
        Tick = tick;
    }
    /// <inheritdoc/>
    public virtual bool Apply(in Mutation mutation, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        return (mutation.Kind switch {
            MutationKind.Write => ApplyWrite(
            mutation: in mutation,
            refusal: out refusal
        ),
            MutationKind.WriteText => ApplyText(
            mutation: in mutation,
            refusal: out refusal
        ),
            MutationKind.Remove => Moved(
            changed: Arena.TryRemove(
                key: mutation.Key,
                reason: out var removeReason,
                rowOrdinal: mutation.RowOrdinal
            ),
            reason: removeReason,
            refusal: out refusal
        ),
            MutationKind.Push => Moved(
            changed: Arena.TryPush(
                reason: out var pushReason,
                rowOrdinal: mutation.RowOrdinal,
                value: mutation.Operand
            ),
            reason: pushReason,
            refusal: out refusal
        ),
            MutationKind.Generate => ApplyGenerate(
            mutation: in mutation,
            refusal: out refusal
        ),
            _ => false,
        });
    }
    /// <inheritdoc/>
    public virtual bool TryTransform(ArenaTransform transform, in ArenaTransformBinding binding, out bool moved, out EffectRefusal refusal) {
        var context = new ArenaTransformContext(
            Arena: Arena,
            DocumentSeed: m_documentSeed,
            Generators: m_generators,
            InstanceIdentity: m_instanceIdentity,
            Site: m_site,
            Time: Time
        );

        return ArenaTransforms.TryApply(
            binding: in binding,
            context: in context,
            moved: out moved,
            refusal: out refusal,
            transform: transform
        );
    }
    /// <summary>Returns the descriptor a draw site's seed ladder and stream id fold.</summary>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <returns>The site descriptor.</returns>
    /// <remarks>A host that carries draw sites in more than one document section overrides this so a state row's
    /// site cannot collide with one of another kind.</remarks>
    public virtual string DrawSite(int rowOrdinal) => Arena.Catalog.Descriptors[rowOrdinal].Name;
    /// <inheritdoc/>
    public virtual void Committed(int scope) { }
    /// <inheritdoc/>
    public virtual void Preflighting() { }
    /// <inheritdoc/>
    public virtual bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = IEffectHost.Unbound(effect: effect);

        return false;
    }
    /// <inheritdoc/>
    public virtual int BoundIndex(BoundKey key) => -1;

    private static bool Moved(bool changed, string reason, out EffectRefusal refusal) {
        refusal = (changed
            ? EffectRefusal.None
            : EffectRefusal.Of(
                code: RuleEffectRefusal.MutationRejected,
                reason: reason
            )
        );

        return changed;
    }
    private bool ApplyGenerate(in Mutation mutation, out EffectRefusal refusal) {
        var ordinal = mutation.RowOrdinal;

        if (
            (((uint)ordinal) >= ((uint)Arena.Rows.Count)) ||
            (Arena.Rows[ordinal].Draw is not { } draw)
        ) {
            refusal = EffectRefusal.Of(
                code: RuleEffectRefusal.MutationRejected,
                reason: $"row ordinal {ordinal} declares no draw"
            );

            return false;
        }
        if (!GeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var generator,
            generators: m_generators,
            reason: out var resolveReason
        )) {
            refusal = EffectRefusal.Of(
                code: RuleEffectRefusal.MutationRejected,
                reason: resolveReason
            );

            return false;
        }
        if (!ArenaDraws.TryFire(
            arena: Arena,
            documentSeed: m_documentSeed,
            generator: generator,
            instanceIdentity: m_instanceIdentity,
            reason: out var fireReason,
            result: out var fired,
            rowOrdinal: ordinal,
            secret: draw.Secret,
            site: DrawSite(rowOrdinal: ordinal),
            skip: draw.Skip
        )) {
            refusal = EffectRefusal.Of(
                code: RuleEffectRefusal.MutationRejected,
                reason: fireReason
            );

            return false;
        }

        var key = Arena.Catalog.Keys.Intern(name: StateRow.SlotKey);
        var emitted = ((fired.Text is { } text)
            ? Arena.TryWriteText(
                key: key,
                reason: out var writeReason,
                rowOrdinal: ordinal,
                text: text
            )
            : Arena.TryWrite(
                key: key,
                operand: fired.Numeric!.Value,
                reason: out writeReason,
                rowOrdinal: ordinal,
                write: StateWriteKind.Set
            )
        );

        // A draw is never a no-op: the cursor moved even when the emission repeats the cell's value.
        return Moved(
            changed: emitted,
            reason: writeReason,
            refusal: out refusal
        );
    }
    private bool ApplyText(in Mutation mutation, out EffectRefusal refusal) {
        var key = mutation.Key;
        var ordinal = mutation.RowOrdinal;
        var present = Arena.TryRead(
            key: key,
            rowOrdinal: ordinal,
            value: out var before
        );

        if (!Arena.TryWriteText(
            key: key,
            reason: out var reason,
            rowOrdinal: ordinal,
            text: mutation.Text
        )) {
            refusal = EffectRefusal.Of(
                code: RuleEffectRefusal.MutationRejected,
                reason: reason
            );

            return false;
        }

        refusal = EffectRefusal.None;

        return (
            !present ||
            (before.Kind != CellKind.Text) ||
            !string.Equals(
            a: before.AsText,
            b: mutation.Text,
            comparisonType: StringComparison.Ordinal
        )
        );
    }
    // A write to a cell the row does not hold mints it where the row's shape admits one; every other write goes
    // through the live door, so an advancing or cycling cell rebases rather than overwriting its base.
    private bool ApplyWrite(in Mutation mutation, out EffectRefusal refusal) {
        var key = mutation.Key;
        var ordinal = mutation.RowOrdinal;
        var time = Time;

        if (!Arena.TryCellSlot(
            key: key,
            rowOrdinal: ordinal,
            slot: out _
        )) {
            return Moved(
                changed: Arena.TryMint(
                    key: out _,
                    name: Arena.Catalog.Keys[key],
                    reason: out var mintReason,
                    rowOrdinal: ordinal,
                    value: Carry(
                        raw: mutation.Operand,
                        rowOrdinal: ordinal
                    )
                ),
                reason: mintReason,
                refusal: out refusal
            );
        }

        var present = Arena.TryRead(
            key: key,
            rowOrdinal: ordinal,
            value: out var before
        );

        if (!Arena.TryWriteLive(
            key: key,
            operand: mutation.Operand,
            reason: out var reason,
            rowOrdinal: ordinal,
            time: in time,
            write: mutation.Write
        )) {
            refusal = EffectRefusal.Of(
                code: RuleEffectRefusal.MutationRejected,
                reason: reason
            );

            return false;
        }

        refusal = EffectRefusal.None;

        // An admitted write that left the cell exactly as it was is not a refusal and not a move: a level gate
        // re-fires every tick it holds, and the read-back says so rather than claiming a change.
        return (
            !present ||
            !Arena.TryRead(
            key: key,
            rowOrdinal: ordinal,
            value: out var after
        ) ||
            !before.Equals(other: after)
        );
    }
    private CellValue Carry(int rowOrdinal, long raw) => (Arena.Layout[rowOrdinal].Kind switch {
        CellKind.Bool => CellValue.Bool(value: (raw != 0L)),
        CellKind.Fixed => CellValue.Fixed(rawBits: raw),
        _ => CellValue.Int(value: raw),
    });
}
