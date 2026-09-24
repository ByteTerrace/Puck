namespace Puck.Memory;

public static class Allocator {
    private static IAllocator CurrentField = new MimallocAllocator();

    public static IAllocator Current {
        get => CurrentField;
        set => CurrentField = (value ?? throw new ArgumentNullException(paramName: nameof(value)));
    }

}
