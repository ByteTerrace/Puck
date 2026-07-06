namespace Puck.Memory;

public static class Allocator {
    private static IAllocator CurrentField;

    static Allocator() {
        var allocatorEnv = Environment.GetEnvironmentVariable(variable: "Puck_ALLOCATOR");

        if (string.Equals(
            a: allocatorEnv,
            b: "native",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            CurrentField = new NativeMemoryAllocator();
        } else {
            CurrentField = new MimallocAllocator();
        }
    }

    public static IAllocator Current {
        get => CurrentField;
        set => CurrentField = (value ?? throw new ArgumentNullException(paramName: nameof(value)));
    }

}
