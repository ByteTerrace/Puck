using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Cameras;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the table of camera views of worlds shown through screens, as their residencies last filmed them
/// (<see cref="WorldFilmedViews{TLevel}"/>): it holds no level once nothing films or shows its view, and a dress that
/// films what it filmed before allocates nothing.</summary>
public sealed class WorldFilmedViewsLawTests {
    private static readonly CameraSnapshot Camera = CameraSnapshot.LookAt(
        fieldOfViewRadians: 1f,
        position: Vector3.Zero,
        target: -Vector3.UnitZ,
        viewportHeight: 72,
        viewportWidth: 96
    );

    // One level of nesting, which only the table may keep alive.
    private sealed class Level;

    private static void Film(WorldFilmedViews<Level> films, object target, Level? level) {
        films.Begin(target: target);

        if (level is not null) {
            films.Record(
                camera: in Camera,
                index: 1,
                level: level,
                name: "session$24$camera$lamp",
                target: target
            );
        }

        films.End(target: target);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Level> FilmOnce(WorldFilmedViews<Level> films, object target) {
        var level = new Level();

        Film(
            films: films,
            level: level,
            target: target
        );

        return new WeakReference<Level>(target: level);
    }
    private static bool Collected(WeakReference<Level> level) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return !level.TryGetTarget(target: out _);
    }

    // THE LAW: a residency's dress that no longer films a camera view drops it, so the table keeps no level of it. The
    // red leg is the residency that dresses no more: its view stays, holding its level, until the views no live level
    // shows are dropped (Retain), after which nothing holds it.
    [Fact]
    public void ATableHoldsNoLevelOnceNothingFilmsOrShowsItsView() {
        var films = new WorldFilmedViews<Level>();
        var dressing = new object();
        var refilmed = FilmOnce(
            films: films,
            target: dressing
        );

        Film(
            films: films,
            level: null,
            target: dressing
        );
        Assert.Equal(expected: 0, actual: films.Count);
        Assert.True(condition: Collected(level: refilmed));

        var retired = FilmOnce(
            films: films,
            target: new object()
        );

        Assert.False(condition: Collected(level: retired));
        Assert.Equal(expected: 1, actual: films.Count);

        films.Retain(shows: static _ => false);

        Assert.Equal(expected: 0, actual: films.Count);
        Assert.True(condition: Collected(level: retired));
        GC.KeepAlive(obj: films);
    }
    // THE LAW: a dress that films the views it filmed before allocates nothing, and the view keeps the index it landed at.
    [Fact]
    public void ASteadyDressAllocatesNothing() {
        var films = new WorldFilmedViews<Level>();
        var target = new object();
        var level = new Level();

        Film(
            films: films,
            level: level,
            target: target
        );

        var before = GC.GetAllocatedBytesForCurrentThread();

        Film(
            films: films,
            level: level,
            target: target
        );

        Assert.Equal(expected: before, actual: GC.GetAllocatedBytesForCurrentThread());
        Assert.Same(expected: level, actual: films.LevelAt(index: 1, target: target));
    }
}
