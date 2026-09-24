using System.Text;

namespace Puck.Vulkan;

/// <summary>
/// Owns a native array of NUL-terminated UTF-8 strings (a <c>const char* const*</c>), the shape Vulkan takes for
/// extension and layer names. The array and every string come from one <see cref="IAllocator"/>, and disposing the
/// owner frees them all.
/// </summary>
public sealed unsafe class Utf8StringArray : IDisposable {
    private readonly IAllocator m_allocator;

    private int m_count;
    private nint m_pointer;

    private Utf8StringArray(IAllocator allocator) {
        m_allocator = allocator;
    }

    /// <summary>Gets the number of strings in the array.</summary>
    public int Count => m_count;
    /// <summary>Gets the pointer to the first string pointer, or zero when the array is empty or disposed.</summary>
    public nint Pointer => m_pointer;

    /// <summary>Marshals <paramref name="values"/> into a native array of UTF-8 strings. When an allocation fails partway,
    /// every string already allocated and the array itself are freed before the exception propagates.</summary>
    /// <param name="allocator">The unmanaged allocator that provides the array and every string.</param>
    /// <param name="values">The strings to marshal, in order.</param>
    /// <returns>A new owner of the marshalled array.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/>, <paramref name="values"/>, or one of its
    /// elements is <see langword="null"/>.</exception>
    /// <exception cref="OutOfMemoryException">The allocator could not provide the array or one of the strings.</exception>
    public static Utf8StringArray Create(IAllocator allocator, IReadOnlyList<string> values) {
        ArgumentNullException.ThrowIfNull(argument: allocator);
        ArgumentNullException.ThrowIfNull(argument: values);

        for (var index = 0; (index < values.Count); index++) {
            ArgumentNullException.ThrowIfNull(
                argument: values[index],
                paramName: nameof(values)
            );
        }

        var array = new Utf8StringArray(allocator: allocator);

        if (0 == values.Count) {
            return array;
        }

        try {
            array.m_pointer = ((nint)VulkanMarshalHelpers.Allocate(
                allocator: allocator,
                sizeBytes: checked((((nuint)sizeof(nint)) * ((nuint)values.Count)))
            ));

            var strings = ((byte**)array.m_pointer);

            for (var index = 0; (index < values.Count); index++) {
                var value = values[index];
                var byteCount = Encoding.UTF8.GetByteCount(s: value);
                var text = ((byte*)VulkanMarshalHelpers.Allocate(
                    allocator: allocator,
                    sizeBytes: (((nuint)byteCount) + 1)
                ));

                fixed (char* characters = value) {
                    _ = Encoding.UTF8.GetBytes(
                        byteCount: byteCount,
                        bytes: text,
                        charCount: value.Length,
                        chars: characters
                    );
                }

                text[byteCount] = 0;
                strings[index] = text;
                array.m_count = (index + 1);
            }

            return array;
        } catch {
            array.Dispose();
            throw;
        }
    }
    /// <summary>Frees every string and the array. Safe to call more than once.</summary>
    public void Dispose() {
        if (0 == m_pointer) {
            return;
        }

        var strings = ((byte**)m_pointer);

        for (var index = 0; (index < m_count); index++) {
            m_allocator.Free(ptr: strings[index]);
        }

        m_allocator.Free(ptr: ((void*)m_pointer));
        m_count = 0;
        m_pointer = 0;
    }
}
