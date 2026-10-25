using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <see cref="WorldCodeWarmer"/>: it prepares the methods with IL of their own that the engine's
/// assemblies declare, follows the engine's own references only, reports what it did, and stops rather than throws.</summary>
public sealed class WorldCodeWarmerLawTests {
    [Fact]
    public void TheClosureHoldsTheEntryAndEachPuckReferenceOnce() {
        var closure = WorldCodeWarmer.PuckClosure(entry: typeof(WorldCodeWarmer).Assembly);
        var names = closure.Select(selector: static assembly => assembly.GetName().Name).ToArray();

        Assert.Equal(expected: typeof(WorldCodeWarmer).Assembly, actual: closure[0]);
        Assert.Contains(expected: "Puck.World.Server", collection: names);
        Assert.All(collection: names, action: static name => Assert.StartsWith(expectedStartString: "Puck", actualString: name));
        Assert.Equal(expected: names.Length, actual: names.Distinct().Count());
    }
    [Fact]
    public void PreparingCountsOnlyTheMethodsWithIlOfTheirOwn() {
        const BindingFlags Declared = (BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var prepared = WorldCodeWarmer.Prepare(assemblies: [typeof(Specimens.Plain).Assembly]);
        var declared = typeof(Specimens).Assembly.GetTypes().Sum(selector: static type => (type.GetMethods(bindingAttr: Declared).Length + type.GetConstructors(bindingAttr: Declared).Length));
        var withoutIl = new[] {
            typeof(Specimens.Shape).GetMethod(name: nameof(Specimens.Shape.Sides))!,
            typeof(Specimens).GetMethod(name: nameof(Specimens.Echo))!,
            typeof(Specimens).GetMethod(name: nameof(Specimens.GetTickCount))!,
            typeof(Specimens.Measure).GetMethod(name: nameof(Specimens.Measure.Invoke))!,
        };

        Assert.True(condition: (prepared > 0));
        Assert.True(condition: (prepared <= (declared - withoutIl.Length)));
        // A plain type's method and constructor; of the rest only the abstract type's constructor has IL of its own.
        Assert.Equal(expected: 2, actual: WorldCodeWarmer.Prepare(assemblies: [new SpecimenAssembly(types: [typeof(Specimens.Plain)])]));
        Assert.Equal(expected: 1, actual: WorldCodeWarmer.Prepare(assemblies: [new SpecimenAssembly(types: [typeof(Specimens), typeof(Specimens.Shape), typeof(Specimens.Holder<>), typeof(Specimens.Measure)])]));
    }
    [Fact]
    public void ARunReportsWhatItPreparedAndAStopRatherThanThrowing() {
        using var prepared = new StringWriter();
        using var stopped = new StringWriter();

        Assert.True(condition: (WorldCodeWarmer.Run(assemblies: static () => [typeof(Specimens.Plain).Assembly], error: prepared) > 0));
        Assert.StartsWith(expectedStartString: "[world.warm] prepared ", actualString: prepared.ToString());
        Assert.Equal(expected: -1, actual: WorldCodeWarmer.Run(assemblies: static () => throw new FileNotFoundException(message: "absent"), error: stopped));
        Assert.Contains(expectedSubstring: "[world.warm] stopped after", actualString: stopped.ToString());
        Assert.Contains(expectedSubstring: "FileNotFoundException: absent", actualString: stopped.ToString());
    }
    [Fact]
    public void AStartedWarmerFinishesOnABackgroundThread() {
        using var error = new StringWriter();
        var thread = WorldCodeWarmer.Start(entry: typeof(Specimens.Plain).Assembly, error: TextWriter.Synchronized(writer: error));

        Assert.True(condition: thread.IsBackground);
        Assert.Equal(expected: ThreadPriority.Lowest, actual: thread.Priority);
        Assert.True(condition: thread.Join(timeout: TimeSpan.FromMinutes(5)));
        Assert.StartsWith(expectedStartString: "[world.warm] prepared ", actualString: error.ToString());
        Assert.Throws<ArgumentNullException>(testCode: () => WorldCodeWarmer.Start(entry: null!, error: error));
        Assert.Throws<ArgumentNullException>(testCode: () => WorldCodeWarmer.Start(entry: typeof(Specimens).Assembly, error: null!));
        Assert.Throws<ArgumentNullException>(testCode: () => WorldCodeWarmer.Run(assemblies: null!, error: error));
        Assert.Throws<ArgumentNullException>(testCode: () => WorldCodeWarmer.Run(assemblies: static () => [], error: null!));
        Assert.Throws<ArgumentNullException>(testCode: () => WorldCodeWarmer.PuckClosure(entry: null!));
        Assert.Throws<ArgumentNullException>(testCode: () => WorldCodeWarmer.Prepare(assemblies: null!));
    }

    // An assembly that declares exactly the types it is handed.
    private sealed class SpecimenAssembly(Type[] types) : Assembly {
        public override Type[] GetTypes() => types;
    }
}

/// <summary>Methods of every kind the warmer meets: plain, abstract, generic, in a generic type, platform-invoked and
/// runtime-implemented (a delegate's).</summary>
public static class Specimens {
    public sealed class Plain {
        public int Twice(int value) => (2 * value);
    }
    public abstract class Shape {
        public abstract int Sides();
    }
    public sealed class Holder<T> {
        public T? Value { get; set; }
    }
    public delegate int Measure(int value);

    public static T Echo<T>(T value) => value;

    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();
}
