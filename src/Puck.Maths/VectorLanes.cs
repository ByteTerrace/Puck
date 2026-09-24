using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>The width-specific operations a byte-wide vector kernel needs, so one kernel body serves every vector width.</summary>
/// <typeparam name="TBytes">The byte vector at this width.</typeparam>
/// <remarks>
/// The runtime's own width abstraction is not public, and the vector types expose no generic-math operator interface, so
/// even the bitwise operators reach a generic kernel through this interface; a Galois-field affine transform and a
/// per-lane byte shuffle are instruction-set leaves no cross-platform abstraction would carry anyway. Each
/// member is one instruction or one reinterpretation, and every implementation is a struct, so the JIT specializes a
/// kernel per width and inlines every member; the specialized kernel compiles to the instructions a hand-written
/// per-width body would.
/// </remarks>
internal interface IByteVectorLanes<TBytes> where TBytes : struct {
    /// <summary>Gets the number of bytes in one vector.</summary>
    static abstract int ByteCount { get; }
    /// <summary>Gets the vector with every bit set.</summary>
    static abstract TBytes AllBitsSet { get; }
    /// <summary>Gets the vector with every bit clear.</summary>
    static abstract TBytes Zero { get; }

    /// <summary>Computes the bitwise and of two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The bitwise and.</returns>
    static abstract TBytes And(TBytes left, TBytes right);
    /// <summary>Computes the bitwise and of a vector and the complement of another.</summary>
    /// <param name="left">The vector to mask.</param>
    /// <param name="right">The vector whose complement is the mask.</param>
    /// <returns>The bitwise and of <paramref name="left"/> and the complement of <paramref name="right"/>.</returns>
    static abstract TBytes AndNot(TBytes left, TBytes right);
    /// <summary>Broadcasts a byte to every lane.</summary>
    /// <param name="value">The byte to broadcast.</param>
    /// <returns>The broadcast vector.</returns>
    static abstract TBytes Broadcast(byte value);
    /// <summary>Broadcasts a sixteen-bit value to every sixteen-bit lane, viewed as bytes.</summary>
    /// <param name="value">The value to broadcast.</param>
    /// <returns>The broadcast vector.</returns>
    static abstract TBytes Broadcast(ushort value);
    /// <summary>Broadcasts a sixty-four-bit value to every sixty-four-bit lane, viewed as bytes.</summary>
    /// <param name="value">The value to broadcast.</param>
    /// <returns>The broadcast vector.</returns>
    static abstract TBytes Broadcast(ulong value);
    /// <summary>Applies the Galois-field affine transform with a zero constant.</summary>
    /// <param name="value">The bytes to transform.</param>
    /// <param name="matrix">The bit matrix, one qword per eight-byte group.</param>
    /// <returns>The transformed bytes.</returns>
    static abstract TBytes GaloisFieldAffineTransform(TBytes value, TBytes matrix);
    /// <summary>Loads one vector of bytes.</summary>
    /// <param name="source">The first byte of the region.</param>
    /// <param name="elementOffset">The byte offset of the vector.</param>
    /// <returns>The loaded vector.</returns>
    static abstract TBytes Load(ref readonly byte source, nuint elementOffset);
    /// <summary>Loads one vector of sixteen-bit elements, viewed as bytes.</summary>
    /// <param name="source">The first element of the region.</param>
    /// <param name="elementOffset">The element offset of the vector.</param>
    /// <returns>The loaded vector.</returns>
    static abstract TBytes Load(ref readonly ushort source, nuint elementOffset);
    /// <summary>Computes the bitwise or of two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The bitwise or.</returns>
    static abstract TBytes Or(TBytes left, TBytes right);
    /// <summary>Replicates a pair of sixteen-byte tables into every 128-bit lane.</summary>
    /// <param name="high">The first table.</param>
    /// <param name="low">The second table.</param>
    /// <returns>The replicated tables.</returns>
    static abstract (TBytes High, TBytes Low) Replicate(Vector128<byte> high, Vector128<byte> low);
    /// <summary>Shifts every sixteen-bit lane right by four bits, filling with zeros.</summary>
    /// <param name="value">The vector to shift, viewed as sixteen-bit lanes.</param>
    /// <returns>The shifted vector.</returns>
    /// <remarks>The count is fixed rather than a parameter because an inlined parameter reaches the shift intrinsic too late to be encoded as an immediate.</remarks>
    static abstract TBytes ShiftRightLogicalUInt16ByNibble(TBytes value);
    /// <summary>Exchanges the two bytes of every sixteen-bit lane.</summary>
    /// <param name="value">The vector to rotate, viewed as sixteen-bit lanes.</param>
    /// <returns>The rotated vector.</returns>
    static abstract TBytes SwapUInt16Bytes(TBytes value);
    /// <summary>Selects a table byte for every lane, within each 128-bit lane.</summary>
    /// <param name="table">The table, replicated into every 128-bit lane.</param>
    /// <param name="indices">The indices, each below sixteen.</param>
    /// <returns>The selected bytes.</returns>
    static abstract TBytes ShuffleWithinLanes(TBytes table, TBytes indices);
    /// <summary>Stores one vector of bytes.</summary>
    /// <param name="value">The vector to store.</param>
    /// <param name="destination">The first byte of the region.</param>
    /// <param name="elementOffset">The byte offset of the vector.</param>
    static abstract void Store(TBytes value, ref byte destination, nuint elementOffset);
    /// <summary>Stores one vector of sixteen-bit elements given as bytes.</summary>
    /// <param name="value">The vector to store.</param>
    /// <param name="destination">The first element of the region.</param>
    /// <param name="elementOffset">The element offset of the vector.</param>
    static abstract void Store(TBytes value, ref ushort destination, nuint elementOffset);
    /// <summary>Computes the bitwise exclusive or of two vectors.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The bitwise exclusive or.</returns>
    static abstract TBytes Xor(TBytes left, TBytes right);
}
/// <summary>The width-specific operations the exact signed-byte dot product needs, so one kernel body serves every vector width.</summary>
/// <typeparam name="TSignedBytes">The eight-bit signed vector at this width.</typeparam>
/// <typeparam name="TShorts">The sixteen-bit signed vector at this width.</typeparam>
/// <typeparam name="TInts">The thirty-two-bit signed vector at this width.</typeparam>
/// <remarks>Widening changes the element type, and the vector types expose no generic-math operator interface, so the widths supply both.</remarks>
internal interface ISignedByteWideningLanes<TSignedBytes, TShorts, TInts>
    where TSignedBytes : struct
    where TShorts : struct
    where TInts : struct {
    /// <summary>Gets the number of signed bytes in one vector.</summary>
    static abstract int ByteCount { get; }

    /// <summary>Adds two vectors lane by lane, wrapping.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The wrapping lane sums.</returns>
    static abstract TInts Add(TInts left, TInts right);
    /// <summary>Loads one vector of signed bytes.</summary>
    /// <param name="source">The first element of the region.</param>
    /// <param name="elementOffset">The element offset of the vector.</param>
    /// <returns>The loaded vector.</returns>
    static abstract TSignedBytes Load(ref readonly sbyte source, nuint elementOffset);
    /// <summary>Multiplies two vectors lane by lane, keeping the low sixteen bits.</summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector.</param>
    /// <returns>The wrapping lane products.</returns>
    static abstract TShorts Multiply(TShorts left, TShorts right);
    /// <summary>Sums every lane.</summary>
    /// <param name="value">The vector to sum.</param>
    /// <returns>The wrapping thirty-two-bit sum.</returns>
    static abstract int Sum(TInts value);
    /// <summary>Sign-extends eight-bit lanes to sixteen bits.</summary>
    /// <param name="value">The vector to widen.</param>
    /// <returns>The lower and upper halves, widened.</returns>
    static abstract (TShorts Lower, TShorts Upper) Widen(TSignedBytes value);
    /// <summary>Sign-extends sixteen-bit lanes to thirty-two bits.</summary>
    /// <param name="value">The vector to widen.</param>
    /// <returns>The lower and upper halves, widened.</returns>
    static abstract (TInts Lower, TInts Upper) Widen(TShorts value);
}
/// <summary>The 128-bit vector width.</summary>
internal readonly struct VectorLanes128 : IByteVectorLanes<Vector128<byte>>, ISignedByteWideningLanes<Vector128<sbyte>, Vector128<short>, Vector128<int>> {
    /// <inheritdoc cref="IByteVectorLanes{TBytes}.ByteCount"/>
    public static int ByteCount => 16;
    /// <inheritdoc/>
    public static Vector128<byte> AllBitsSet {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128<byte>.AllBitsSet;
    }
    /// <inheritdoc/>
    public static Vector128<byte> Zero {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128<byte>.Zero;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Add(Vector128<int> left, Vector128<int> right) => (left + right);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> And(Vector128<byte> left, Vector128<byte> right) => left & right;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AndNot(Vector128<byte> left, Vector128<byte> right) =>
        Vector128.AndNot(
            left: left,
            right: right
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Broadcast(byte value) => Vector128.Create(value: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Broadcast(ushort value) => Vector128.Create(value: value).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Broadcast(ulong value) => Vector128.Create(value: value).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> GaloisFieldAffineTransform(Vector128<byte> value, Vector128<byte> matrix) =>
        Gfni.GaloisFieldAffineTransform(
            a: matrix,
            b: 0,
            x: value
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load(ref readonly byte source, nuint elementOffset) =>
        Vector128.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Load(ref readonly ushort source, nuint elementOffset) =>
        Vector128.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<sbyte> Load(ref readonly sbyte source, nuint elementOffset) =>
        Vector128.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> Multiply(Vector128<short> left, Vector128<short> right) => (left * right);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Or(Vector128<byte> left, Vector128<byte> right) => left | right;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<byte> High, Vector128<byte> Low) Replicate(Vector128<byte> high, Vector128<byte> low) => (High: high, Low: low);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightLogicalUInt16ByNibble(Vector128<byte> value) =>
        Vector128.ShiftRightLogical(
            shiftCount: 4,
            vector: value.AsUInt16()
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShuffleWithinLanes(Vector128<byte> table, Vector128<byte> indices) =>
        Ssse3.Shuffle(
            mask: indices,
            value: table
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SwapUInt16Bytes(Vector128<byte> value) =>
        Vector128.ShiftLeft(
            shiftCount: 8,
            vector: value.AsUInt16()
        ).AsByte() | Vector128.ShiftRightLogical(
            shiftCount: 8,
            vector: value.AsUInt16()
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Vector128<byte> value, ref byte destination, nuint elementOffset) =>
        value.StoreUnsafe(
            destination: ref destination,
            elementOffset: elementOffset
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Vector128<byte> value, ref ushort destination, nuint elementOffset) =>
        value.AsUInt16().StoreUnsafe(
            destination: ref destination,
            elementOffset: elementOffset
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sum(Vector128<int> value) => Vector128.Sum(vector: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<short> Lower, Vector128<short> Upper) Widen(Vector128<sbyte> value) => Vector128.Widen(source: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector128<int> Lower, Vector128<int> Upper) Widen(Vector128<short> value) => Vector128.Widen(source: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Xor(Vector128<byte> left, Vector128<byte> right) => left ^ right;
}
/// <summary>The 256-bit vector width.</summary>
internal readonly struct VectorLanes256 : IByteVectorLanes<Vector256<byte>>, ISignedByteWideningLanes<Vector256<sbyte>, Vector256<short>, Vector256<int>> {
    /// <inheritdoc cref="IByteVectorLanes{TBytes}.ByteCount"/>
    public static int ByteCount => 32;
    /// <inheritdoc/>
    public static Vector256<byte> AllBitsSet {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256<byte>.AllBitsSet;
    }
    /// <inheritdoc/>
    public static Vector256<byte> Zero {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256<byte>.Zero;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> Add(Vector256<int> left, Vector256<int> right) => (left + right);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> And(Vector256<byte> left, Vector256<byte> right) => left & right;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> AndNot(Vector256<byte> left, Vector256<byte> right) =>
        Vector256.AndNot(
            left: left,
            right: right
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Broadcast(byte value) => Vector256.Create(value: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Broadcast(ushort value) => Vector256.Create(value: value).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Broadcast(ulong value) => Vector256.Create(value: value).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> GaloisFieldAffineTransform(Vector256<byte> value, Vector256<byte> matrix) =>
        Gfni.V256.GaloisFieldAffineTransform(
            a: matrix,
            b: 0,
            x: value
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Load(ref readonly byte source, nuint elementOffset) =>
        Vector256.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Load(ref readonly ushort source, nuint elementOffset) =>
        Vector256.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<sbyte> Load(ref readonly sbyte source, nuint elementOffset) =>
        Vector256.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<short> Multiply(Vector256<short> left, Vector256<short> right) => (left * right);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Or(Vector256<byte> left, Vector256<byte> right) => left | right;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector256<byte> High, Vector256<byte> Low) Replicate(Vector128<byte> high, Vector128<byte> low) => (
        High: Vector256.Create(
            lower: high,
            upper: high
        ),
        Low: Vector256.Create(
            lower: low,
            upper: low
        )
    );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> ShiftRightLogicalUInt16ByNibble(Vector256<byte> value) =>
        Vector256.ShiftRightLogical(
            shiftCount: 4,
            vector: value.AsUInt16()
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> ShuffleWithinLanes(Vector256<byte> table, Vector256<byte> indices) =>
        Avx2.Shuffle(
            mask: indices,
            value: table
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> SwapUInt16Bytes(Vector256<byte> value) =>
        Vector256.ShiftLeft(
            shiftCount: 8,
            vector: value.AsUInt16()
        ).AsByte() | Vector256.ShiftRightLogical(
            shiftCount: 8,
            vector: value.AsUInt16()
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Vector256<byte> value, ref byte destination, nuint elementOffset) =>
        value.StoreUnsafe(
            destination: ref destination,
            elementOffset: elementOffset
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Vector256<byte> value, ref ushort destination, nuint elementOffset) =>
        value.AsUInt16().StoreUnsafe(
            destination: ref destination,
            elementOffset: elementOffset
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sum(Vector256<int> value) => Vector256.Sum(vector: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector256<short> Lower, Vector256<short> Upper) Widen(Vector256<sbyte> value) => Vector256.Widen(source: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector256<int> Lower, Vector256<int> Upper) Widen(Vector256<short> value) => Vector256.Widen(source: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> Xor(Vector256<byte> left, Vector256<byte> right) => left ^ right;
}
/// <summary>The 512-bit vector width.</summary>
internal readonly struct VectorLanes512 : IByteVectorLanes<Vector512<byte>>, ISignedByteWideningLanes<Vector512<sbyte>, Vector512<short>, Vector512<int>> {
    /// <inheritdoc cref="IByteVectorLanes{TBytes}.ByteCount"/>
    public static int ByteCount => 64;
    /// <inheritdoc/>
    public static Vector512<byte> AllBitsSet {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector512<byte>.AllBitsSet;
    }
    /// <inheritdoc/>
    public static Vector512<byte> Zero {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector512<byte>.Zero;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<int> Add(Vector512<int> left, Vector512<int> right) => (left + right);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> And(Vector512<byte> left, Vector512<byte> right) => left & right;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> AndNot(Vector512<byte> left, Vector512<byte> right) =>
        Vector512.AndNot(
            left: left,
            right: right
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Broadcast(byte value) => Vector512.Create(value: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Broadcast(ushort value) => Vector512.Create(value: value).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Broadcast(ulong value) => Vector512.Create(value: value).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> GaloisFieldAffineTransform(Vector512<byte> value, Vector512<byte> matrix) =>
        Gfni.V512.GaloisFieldAffineTransform(
            a: matrix,
            b: 0,
            x: value
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Load(ref readonly byte source, nuint elementOffset) =>
        Vector512.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Load(ref readonly ushort source, nuint elementOffset) =>
        Vector512.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<sbyte> Load(ref readonly sbyte source, nuint elementOffset) =>
        Vector512.LoadUnsafe(
            elementOffset: elementOffset,
            source: in source
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<short> Multiply(Vector512<short> left, Vector512<short> right) => (left * right);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Or(Vector512<byte> left, Vector512<byte> right) => left | right;
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector512<byte> High, Vector512<byte> Low) Replicate(Vector128<byte> high, Vector128<byte> low) {
        var (highHalf, lowHalf) = VectorLanes256.Replicate(
            high: high,
            low: low
        );

        return (
            High: Vector512.Create(
                lower: highHalf,
                upper: highHalf
            ),
            Low: Vector512.Create(
                lower: lowHalf,
                upper: lowHalf
            )
        );
    }
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> ShiftRightLogicalUInt16ByNibble(Vector512<byte> value) =>
        Vector512.ShiftRightLogical(
            shiftCount: 4,
            vector: value.AsUInt16()
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> ShuffleWithinLanes(Vector512<byte> table, Vector512<byte> indices) =>
        Avx512BW.Shuffle(
            mask: indices,
            value: table
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> SwapUInt16Bytes(Vector512<byte> value) =>
        Vector512.ShiftLeft(
            shiftCount: 8,
            vector: value.AsUInt16()
        ).AsByte() | Vector512.ShiftRightLogical(
            shiftCount: 8,
            vector: value.AsUInt16()
        ).AsByte();
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Vector512<byte> value, ref byte destination, nuint elementOffset) =>
        value.StoreUnsafe(
            destination: ref destination,
            elementOffset: elementOffset
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store(Vector512<byte> value, ref ushort destination, nuint elementOffset) =>
        value.AsUInt16().StoreUnsafe(
            destination: ref destination,
            elementOffset: elementOffset
        );
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sum(Vector512<int> value) => Vector512.Sum(vector: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector512<short> Lower, Vector512<short> Upper) Widen(Vector512<sbyte> value) => Vector512.Widen(source: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Vector512<int> Lower, Vector512<int> Upper) Widen(Vector512<short> value) => Vector512.Widen(source: value);
    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<byte> Xor(Vector512<byte> left, Vector512<byte> right) => left ^ right;
}
