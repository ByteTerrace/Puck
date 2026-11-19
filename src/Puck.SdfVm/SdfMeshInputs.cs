using Puck.Abstractions.Counting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The frame inputs a bake group's geometry reads besides its program, which move without a program rebuild: a sampled
/// field's snapshot (<see cref="SdfSampledSet"/>) and an operand's pose (<see cref="SdfInstanceRange.Operands"/>).
/// <see cref="SdfMeshPathBake"/> visits only the groups that read one, plans a group again when its input moved
/// (a snapshot it no longer holds, or an operand a step from where its geometry snapped it), and keeps one bake of a group
/// in flight with the newest plan waiting as its successor. These are the counts of that door, over every bake holding
/// one library.
/// </summary>
public static class SdfMeshInputs {
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-inputs";

    /// <summary>Counts each group planned again because a sampled field it reads published another snapshot.</summary>
    public static readonly WorkKind Resampled = new(name: "sdf.mesh-inputs.resampled", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each group planned again because an operand it reads moved a step.</summary>
    public static readonly WorkKind Operand = new(name: "sdf.mesh-inputs.operand", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each newer plan that replaced a successor still waiting on its group's bake: an input moving faster
    /// than its group's bakes land, whose mesh then jumps further when it does.</summary>
    public static readonly WorkKind Coalesced = new(name: "sdf.mesh-inputs.coalesced", unit: "plans", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Resampled, Operand, Coalesced];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// library exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Creates the counters one library's bakes count into.</summary>
    /// <returns>A set over <see cref="Kinds"/>, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);
}
