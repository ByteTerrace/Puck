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

    public static unsafe nint Alloc(nint size) => (nint)CurrentField.Allocate(
        alignment: 0,
        size: (nuint)size
    );
    public static unsafe nint Alloc(nint size, nint alignment) => (nint)CurrentField.Allocate(
        alignment: (nuint)alignment,
        size: (nuint)size
    );
    public static unsafe void* Alloc(nuint size, nuint alignment = 0) => CurrentField.Allocate(
        alignment: alignment,
        size: size
    );
    public static unsafe void Free(nint ptr) => CurrentField.Free(ptr: (void*)ptr);
    public static unsafe void Free(void* ptr) => CurrentField.Free(ptr: ptr);
    public static unsafe nint Realloc(nint ptr, nint newSize) => (nint)CurrentField.Reallocate(
        alignment: 0,
        newSize: (nuint)newSize,
        ptr: (void*)ptr
    );
    public static unsafe nint Realloc(nint ptr, nint newSize, nint alignment) => (nint)CurrentField.Reallocate(
        alignment: (nuint)alignment,
        newSize: (nuint)newSize,
        ptr: (void*)ptr
    );
    public static unsafe void* Realloc(void* ptr, nuint newSize, nuint alignment = 0) => CurrentField.Reallocate(
        alignment: alignment,
        newSize: newSize,
        ptr: ptr
    );
}
