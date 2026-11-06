using Puck.Abstractions.Counting;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>
/// A presentation-side CPU query of a static field (a march, a sweep) whose answer its owner keeps for reuse through
/// <see cref="WorldFieldAnswer{TQuery, TResult}"/>. The query decides its own reuse rule: <see cref="Covers"/> says
/// whether the answer this query was marched for stands for another query within the bound it was marched under.
/// </summary>
/// <typeparam name="TQuery">The query type itself.</typeparam>
/// <typeparam name="TResult">The answer type.</typeparam>
public interface IWorldFieldQuery<TQuery, TResult> where TQuery : struct, IWorldFieldQuery<TQuery, TResult> {
    /// <summary>Gets the counters the query's family counts into.</summary>
    static abstract WorldFieldQueryFamily Family { get; }
    /// <summary>Gets the field the query reads. An answer is reused only over the same instance, which the query
    /// layer never changes after construction.</summary>
    SdfFieldEvaluator Field { get; }

    /// <summary>Returns whether the answer this query was marched for stands for <paramref name="asked"/>: never
    /// further from the answer a march of <paramref name="asked"/> would give than the bound this query's family
    /// states. A query that cannot prove that answers <see langword="false"/>, and the asked query marches.</summary>
    /// <param name="asked">The query asked now, over the same field.</param>
    /// <returns>Whether the held answer may be returned for <paramref name="asked"/>.</returns>
    bool Covers(in TQuery asked);
    /// <summary>Marches the query against its field.</summary>
    /// <returns>The answer.</returns>
    TResult March();
}
/// <summary>The counters one family of <see cref="IWorldFieldQuery{TQuery, TResult}"/> counts into:
/// requests asked, requests answered without a field or with nothing to march (skipped), answers reused and
/// answers marched. Skipped, reused and marched partition the requests.</summary>
/// <param name="Requests">Every request, skipped ones included.</param>
/// <param name="Skipped">Requests answered without marching because there was no field or nothing to ask.</param>
/// <param name="Reused">Requests answered by the owner's held answer.</param>
/// <param name="Marched">Requests that marched the field.</param>
public sealed record WorldFieldQueryFamily(WorkKind Requests, WorkKind Skipped, WorkKind Reused, WorkKind Marched) {
    /// <summary>Counts a request that marches nothing.</summary>
    public void Skip() {
        WorldFieldQueryWork.Process.Count(kind: Requests);
        WorldFieldQueryWork.Process.Count(kind: Skipped);
    }
}
/// <summary>The presentation-side field queries' process totals (<c>world.counters world.field-queries</c>): the
/// effectors' surface probes and the chase camera's clearance sweeps.</summary>
public static class WorldFieldQueryWork {
    /// <summary>The effector surface probes (<see cref="WorldEffectorSolver.TryProbeSurface"/>).</summary>
    public static WorldFieldQueryFamily Surface { get; } = new(
        Marched: new(name: "world.field-queries.surface.marched", unit: "count", workClass: WorkClass.Pacing),
        Requests: new(name: "world.field-queries.surface.requests", unit: "count", workClass: WorkClass.Pacing),
        Reused: new(name: "world.field-queries.surface.reused", unit: "count", workClass: WorkClass.Pacing),
        Skipped: new(name: "world.field-queries.surface.skipped", unit: "count", workClass: WorkClass.Pacing)
    );
    /// <summary>The chase camera's clearance sweeps (<see cref="WorldCameraClearance"/>).</summary>
    public static WorldFieldQueryFamily Clearance { get; } = new(
        Marched: new(name: "world.field-queries.clearance.marched", unit: "count", workClass: WorkClass.Pacing),
        Requests: new(name: "world.field-queries.clearance.requests", unit: "count", workClass: WorkClass.Pacing),
        Reused: new(name: "world.field-queries.clearance.reused", unit: "count", workClass: WorkClass.Pacing),
        Skipped: new(name: "world.field-queries.clearance.skipped", unit: "count", workClass: WorkClass.Pacing)
    );
    /// <summary>Process totals; read twice to measure a window.</summary>
    public static WorkCounterSet Process { get; } = new(name: "world.field-queries", kinds: [
        Surface.Requests, Surface.Skipped, Surface.Reused, Surface.Marched,
        Clearance.Requests, Clearance.Skipped, Clearance.Reused, Clearance.Marched,
    ]);
}
/// <summary>
/// <para>One owner's held answer to one field query, kept beside the state it serves (an effector's arrays, a camera's
/// arm), never in shared or thread-static storage, so packing on any thread reuses it.</para>
/// <para>The one door to a held answer is <see cref="Ask"/>: the answer is private, and returned only where the held
/// query <see cref="IWorldFieldQuery{TQuery, TResult}.Covers"/> the asked one over the same field; every other request
/// marches and is held in its place.</para>
/// </summary>
/// <typeparam name="TQuery">The query.</typeparam>
/// <typeparam name="TResult">The answer.</typeparam>
public struct WorldFieldAnswer<TQuery, TResult> where TQuery : struct, IWorldFieldQuery<TQuery, TResult> {
    private TQuery m_query;
    private TResult m_result;
    private bool m_held;

    /// <summary>Answers a query: the held answer where it covers <paramref name="asked"/>, otherwise a fresh march,
    /// which is then held.</summary>
    /// <param name="asked">The query.</param>
    /// <returns>The answer.</returns>
    public TResult Ask(in TQuery asked) {
        var family = TQuery.Family;

        WorldFieldQueryWork.Process.Count(kind: family.Requests);

        if (m_held && ReferenceEquals(objA: m_query.Field, objB: asked.Field) && m_query.Covers(asked: in asked)) {
            WorldFieldQueryWork.Process.Count(kind: family.Reused);

            return m_result;
        }

        WorldFieldQueryWork.Process.Count(kind: family.Marched);
        m_result = asked.March();
        m_query = asked;
        m_held = true;

        return m_result;
    }
}
