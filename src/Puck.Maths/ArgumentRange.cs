using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Validates an argument that selects one entry of a table, throwing the platform's own out-of-range diagnoses.</summary>
internal static class ArgumentRange {
    /// <summary>Throws unless <paramref name="value"/> is an index into <paramref name="count"/> entries.</summary>
    /// <typeparam name="T">The integer type of the index.</typeparam>
    /// <param name="value">The index to validate.</param>
    /// <param name="count">The number of entries.</param>
    /// <param name="paramName">The name the diagnosis names, which is the caller's argument expression.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative, or not below <paramref name="count"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfNotIndex<T>(T value, T count, [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : INumberBase<T>, IComparable<T> {
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: paramName,
            value: value
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: count,
            paramName: paramName,
            value: value
        );
    }
    /// <summary>Throws unless <paramref name="value"/> lies in the closed range from zero through <paramref name="maximum"/>.</summary>
    /// <typeparam name="T">The integer type of the value.</typeparam>
    /// <param name="value">The value to validate.</param>
    /// <param name="maximum">The largest admitted value.</param>
    /// <param name="paramName">The name the diagnosis names, which is the caller's argument expression.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative, or above <paramref name="maximum"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ThrowIfNotThrough<T>(T value, T maximum, [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : INumberBase<T>, IComparable<T> {
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: paramName,
            value: value
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: maximum,
            paramName: paramName,
            value: value
        );
    }
    /// <summary>Throws unless <paramref name="row"/> and <paramref name="column"/> name a cell of a row-major table,
    /// and returns that cell's flat index.</summary>
    /// <param name="row">The row to validate.</param>
    /// <param name="rowCount">The number of rows.</param>
    /// <param name="column">The column to validate.</param>
    /// <param name="columnCount">The number of columns, which is the row stride.</param>
    /// <param name="rowName">The name the row diagnosis names, which is the caller's argument expression.</param>
    /// <param name="columnName">The name the column diagnosis names, which is the caller's argument expression.</param>
    /// <returns><c>row · columnCount + column</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> or <paramref name="column"/> is negative, or not below its count.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ThrowIfNotCell(int row, int rowCount, int column, int columnCount, [CallerArgumentExpression(nameof(row))] string? rowName = null, [CallerArgumentExpression(nameof(column))] string? columnName = null) {
        ThrowIfNotIndex(
            count: rowCount,
            paramName: rowName,
            value: row
        );
        ThrowIfNotIndex(
            count: columnCount,
            paramName: columnName,
            value: column
        );

        return ((row * columnCount) + column);
    }
}
