using System.Text.Json.Serialization.Metadata;

namespace Puck.State;

/// <summary>Where an operand is spelled, for a refusal that quotes the author's own field names: every caller is
/// refused by the same shapes under different-sounding names (<c>state</c>/<c>key</c> vs <c>comparandState</c>/
/// <c>comparandKey</c> vs <c>fromState</c>/<c>fromKey</c>), so a refusal that quoted one caller's spelling at
/// another's author would name a field they never wrote.</summary>
/// <param name="RuleName">The rule being compiled.</param>
/// <param name="Verb">The authored effect or predicate verb.</param>
/// <param name="FieldLabel">The field the operand name was spelled in.</param>
/// <param name="KeyFieldLabel">The field its key was spelled in.</param>
/// <param name="AllowText">Whether a <see cref="CellKind.Text"/> row is admissible here.</param>
public readonly record struct OperandSite(string RuleName, string Verb, string FieldLabel, string KeyFieldLabel, bool AllowText = false);

/// <summary>One family of reserved operand spellings — a document project registers one per prefix it answers
/// (a participant's distance, a screen's memory, a region's occupancy), and the compiler consults the registered
/// families before its own.</summary>
public abstract class OperandFamily {
    /// <summary>Gets the spellings this family answers, as an author would read them in a refusal
    /// (<c>$region:&lt;placementId&gt;</c>), for the refusal that lists every reserved channel.</summary>
    public abstract IReadOnlyList<string> Spellings { get; }

    /// <summary>Compiles an operand when this family claims its name; a name the family does not claim leaves
    /// <paramref name="fact"/> null and returns <see langword="false"/> so the next family may answer it. A claimed
    /// name that is malformed throws a <see cref="RuleException"/>.</summary>
    /// <param name="name">The authored operand name.</param>
    /// <param name="key">The authored cell key, or <see langword="null"/>.</param>
    /// <param name="site">Where the operand is spelled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="fact">The compiled operand, when claimed.</param>
    public abstract bool TryCompile(string name, string? key, in OperandSite site, RuleCompileContext context, out OperandFact? fact);
}

/// <summary>One dynamic cell-key spelling a document project answers beside the library's <c>$cell:</c> and bound
/// tokens.</summary>
public abstract class KeyFamily {
    /// <summary>Compiles a key when this family claims its spelling; a spelling the family does not claim returns
    /// <see langword="false"/>.</summary>
    /// <param name="key">The authored key.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in, for refusal text.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="cell">The compiled indirection, when claimed.</param>
    public abstract bool TryCompile(string key, string ruleName, string verb, string keyFieldLabel, RuleCompileContext context, out CompiledCellRef cell);
}

/// <summary>One authored effect arm a document project owns: the record type and its <c>$type</c> discriminator
/// (registered onto <see cref="ActionEffect"/>'s polymorphism at serializer resolution), the transaction step that
/// mirrors it (if any), and its compile.</summary>
public abstract class EffectFamily {
    /// <summary>Gets the <see cref="ActionEffect"/>-derived record type.</summary>
    public abstract Type EffectType { get; }
    /// <summary>Gets the <c>$type</c> discriminator the record is spelled under.</summary>
    public abstract string Discriminator { get; }
    /// <summary>Gets the <see cref="TransactionStep"/>-derived record type mirroring the effect inside a transaction,
    /// or <see langword="null"/> when the effect has no atomic step.</summary>
    public virtual Type? StepType => null;
    /// <summary>Gets the step's <c>$type</c> discriminator, or <see langword="null"/>.</summary>
    public virtual string? StepDiscriminator => null;
    /// <summary>Lifts a transaction step of <see cref="StepType"/> to the effect it mirrors.</summary>
    /// <param name="step">The step.</param>
    public virtual ActionEffect Lift(TransactionStep step) => throw new NotSupportedException(message: $"'{Discriminator}' has no transaction step");
    /// <summary>Compiles an authored effect of <see cref="EffectType"/>.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    public abstract EffectFact Compile(ActionEffect effect, string ruleName, RuleCompileContext context);
}

/// <summary>One authored predicate arm a document project owns: the record type and its discriminator, and its
/// compile — which may refuse, for an arm that is authorable only inside another program the same JSON shape serves.</summary>
public abstract class PredicateFamily {
    /// <summary>Gets the <see cref="ActionPredicate"/>-derived record type.</summary>
    public abstract Type PredicateType { get; }
    /// <summary>Gets the <c>$type</c> discriminator the record is spelled under.</summary>
    public abstract string Discriminator { get; }
    /// <summary>Compiles an authored predicate of <see cref="PredicateType"/> to one gate token.</summary>
    /// <param name="predicate">The predicate.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    public abstract GateToken Compile(ActionPredicate predicate, string ruleName, RuleCompileContext context);
}

/// <summary>The families a rule's names resolve through: the registered families a document project supplies,
/// consulted first, then the library's own. One instance is built per document project and shared by every context
/// it compiles with.</summary>
public sealed class RuleVocabulary {
    /// <summary>The library's own vocabulary with nothing registered.</summary>
    public static RuleVocabulary Core { get; } = new(operands: [], effects: [], predicates: [], keys: []);

    /// <summary>Initializes a vocabulary over the registered families.</summary>
    /// <param name="operands">The operand families, in the order they are consulted.</param>
    /// <param name="effects">The effect arms.</param>
    /// <param name="predicates">The predicate arms.</param>
    /// <param name="keys">The key families, in the order they are consulted.</param>
    public RuleVocabulary(IReadOnlyList<OperandFamily> operands, IReadOnlyList<EffectFamily> effects, IReadOnlyList<PredicateFamily> predicates, IReadOnlyList<KeyFamily> keys) {
        ArgumentNullException.ThrowIfNull(argument: operands);
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: predicates);
        ArgumentNullException.ThrowIfNull(argument: keys);

        Operands = operands;
        Effects = effects;
        Predicates = predicates;
        Keys = keys;
    }

    /// <summary>Gets the registered operand families, in consultation order.</summary>
    public IReadOnlyList<OperandFamily> Operands { get; }
    /// <summary>Gets the registered effect arms.</summary>
    public IReadOnlyList<EffectFamily> Effects { get; }
    /// <summary>Gets the registered predicate arms.</summary>
    public IReadOnlyList<PredicateFamily> Predicates { get; }
    /// <summary>Gets the registered key families, in consultation order.</summary>
    public IReadOnlyList<KeyFamily> Keys { get; }

    /// <summary>Finds the effect family owning an authored effect's type, or <see langword="null"/>.</summary>
    /// <param name="effect">The effect.</param>
    public EffectFamily? EffectOf(ActionEffect effect) {
        var type = effect.GetType();

        foreach (var family in Effects) {
            if (family.EffectType == type) {
                return family;
            }
        }

        return null;
    }

    /// <summary>Finds the effect family whose transaction step an authored step's type is, or <see langword="null"/>.</summary>
    /// <param name="step">The step.</param>
    public EffectFamily? StepOf(TransactionStep step) {
        var type = step.GetType();

        foreach (var family in Effects) {
            if (family.StepType == type) {
                return family;
            }
        }

        return null;
    }

    /// <summary>Finds the predicate family owning an authored predicate's type, or <see langword="null"/>.</summary>
    /// <param name="predicate">The predicate.</param>
    public PredicateFamily? PredicateOf(ActionPredicate predicate) {
        var type = predicate.GetType();

        foreach (var family in Predicates) {
            if (family.PredicateType == type) {
                return family;
            }
        }

        return null;
    }

    /// <summary>Appends the registered arms to a resolved polymorphic base's derived-type list — the
    /// <see cref="JsonTypeInfo"/> modifier a document project's serializer options install so
    /// <see cref="ActionEffect"/>, <see cref="ActionPredicate"/>, and <see cref="TransactionStep"/> read and write
    /// the registered arms under their discriminators.</summary>
    /// <param name="typeInfo">The type info being resolved.</param>
    public void ExtendJson(JsonTypeInfo typeInfo) {
        ArgumentNullException.ThrowIfNull(argument: typeInfo);

        if (typeInfo.PolymorphismOptions is not { } polymorphism) {
            return;
        }
        if (typeInfo.Type == typeof(ActionEffect)) {
            foreach (var family in Effects) {
                polymorphism.DerivedTypes.Add(item: new JsonDerivedType(derivedType: family.EffectType, typeDiscriminator: family.Discriminator));
            }
        } else if (typeInfo.Type == typeof(TransactionStep)) {
            foreach (var family in Effects) {
                if ((family.StepType is { } stepType) && (family.StepDiscriminator is { } stepDiscriminator)) {
                    polymorphism.DerivedTypes.Add(item: new JsonDerivedType(derivedType: stepType, typeDiscriminator: stepDiscriminator));
                }
            }
        } else if (typeInfo.Type == typeof(ActionPredicate)) {
            foreach (var family in Predicates) {
                polymorphism.DerivedTypes.Add(item: new JsonDerivedType(derivedType: family.PredicateType, typeDiscriminator: family.Discriminator));
            }
        }
    }
}
