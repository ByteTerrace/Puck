using Puck.Maths;
using Puck.Physics.Fields;

namespace Puck.Physics.Testing;

/// <summary>The test double for <see cref="IFieldLatticeHost"/>: every hook defaults to the same no-op/zero
/// <see cref="FieldLattice.Step"/> itself falls back to when a caller omits a delegate. A scalar add reports all of
/// <c>amount</c> accepted when a hook is supplied and none when it is not.</summary>
/// <param name="bodyPosition">Answers a body's position, or <see langword="null"/> for none.</param>
/// <param name="readTag">Answers a body's tag on a row.</param>
/// <param name="writeTag">Receives a tag write.</param>
/// <param name="readScalar">Answers a row's scalar.</param>
/// <param name="addScalar">Receives a scalar add.</param>
internal sealed class LambdaHost(
    Func<int, FixedVector3?>? bodyPosition = null,
    Func<Puck.State.StateHandle, int, ulong, long>? readTag = null,
    Action<Puck.State.StateHandle, int, long, ulong>? writeTag = null,
    Func<Puck.State.StateHandle, ulong, FixedQ4816>? readScalar = null,
    Action<Puck.State.StateHandle, FixedQ4816, ulong>? addScalar = null
) : IFieldLatticeHost {
    public FixedQ4816 AddScalar(Puck.State.StateHandle row, FixedQ4816 amount, ulong tick) {
        addScalar?.Invoke(row, amount, tick);

        return ((addScalar is null) ? FixedQ4816.Zero : amount);
    }
    public FixedVector3? BodyPosition(int body) => bodyPosition?.Invoke(body);
    public FixedQ4816 ReadScalar(Puck.State.StateHandle row, ulong tick) => (readScalar?.Invoke(
        row,
        tick
    ) ?? FixedQ4816.Zero);
    public long ReadTag(Puck.State.StateHandle row, int body, ulong tick) => (readTag?.Invoke(
        row,
        body,
        tick
    ) ?? 0L);
    public void WriteTag(Puck.State.StateHandle row, int body, long value, ulong tick) => writeTag?.Invoke(
        row,
        body,
        value,
        tick
    );
}
/// <summary>Steps field lattices in laws.</summary>
internal static class FieldLatticeSteps {
    /// <summary>Advances one <see cref="FieldLattice"/> a single tick with no bodies, against
    /// <paramref name="host"/> or an all-default <see cref="LambdaHost"/>.</summary>
    /// <param name="lattice">The lattice to step.</param>
    /// <param name="host">The host, or <see langword="null"/> for the all-default one.</param>
    public static void Step(FieldLattice lattice, IFieldLatticeHost? host = null) => lattice.Step(
        tick: 1,
        bodyCount: 0,
        host: (host ?? new LambdaHost())
    );
}
