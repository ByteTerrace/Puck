using Puck.Abstractions.Counting;

using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for registering <see cref="SdfMovedTransforms"/>'s counters before any moved set exists: a forwarder built from
/// the static <see cref="SdfMovedTransforms.Kinds"/> reads zero until a moved set stands behind it, then reads what that
/// moved set counted, because the static kinds are the very instances every moved set counts. A view that composes a
/// frame source of its own attaches its moved set beside the presenter's, so the counters sum every frame source.
/// </summary>
public sealed class SdfMovedTransformsWorkLawTests {
    [Fact]
    public void AForwarderRegisteredBeforeTheMovedSetReadsItOnceRetargeted() {
        var forwarder = new ForwardingWorkCounterSource(
            kinds: SdfMovedTransforms.Kinds,
            name: SdfMovedTransforms.SourceName
        );

        Assert.True(condition: forwarder.TryRead(
            kind: SdfMovedTransforms.OwedRows,
            value: out var before
        ));
        Assert.Equal(
            actual: before,
            expected: 0L
        );

        var moved = new SdfMovedTransforms();

        Assert.Equal(
            actual: moved.WorkKinds.ToArray(),
            expected: SdfMovedTransforms.Kinds.ToArray()
        );

        moved.Begin(
            everything: true,
            tableRows: 12
        );
        forwarder.Retarget(target: moved);
        Assert.True(condition: forwarder.TryRead(
            kind: SdfMovedTransforms.OwedRows,
            value: out var after
        ));
        Assert.Equal(
            actual: after,
            expected: 12L
        );
    }
    [Fact]
    public void AViewsOwnMovedSetAddsToThePresentersUntilTheViewIsReleased() {
        var forwarder = new ForwardingWorkCounterSource(
            kinds: SdfMovedTransforms.Kinds,
            name: SdfMovedTransforms.SourceName
        );
        var presenter = new SdfMovedTransforms();
        var session = new SdfMovedTransforms();

        forwarder.Retarget(target: presenter);
        forwarder.Attach(instance: session);
        presenter.Begin(
            everything: true,
            tableRows: 12
        );
        session.Begin(
            everything: true,
            tableRows: 5
        );
        Assert.True(condition: forwarder.TryRead(
            kind: SdfMovedTransforms.OwedRows,
            value: out var both
        ));
        Assert.Equal(
            actual: both,
            expected: 17L
        );

        // A released view's frames stay counted, and its moved set is no longer read.
        forwarder.Detach(instance: session);
        session.Begin(
            everything: true,
            tableRows: 5
        );
        Assert.True(condition: forwarder.TryRead(
            kind: SdfMovedTransforms.OwedRows,
            value: out var released
        ));
        Assert.Equal(
            actual: released,
            expected: 17L
        );
    }
}
