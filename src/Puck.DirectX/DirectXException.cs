namespace Puck.DirectX;

/// <summary>
/// The exception thrown when a DirectX call returns a failing <c>HRESULT</c>. It carries the name of the
/// failing operation and the result code.
/// </summary>
public sealed class DirectXException : Exception {
    private static string CreateMessage(string operation, int result) {
        if (string.IsNullOrWhiteSpace(value: operation)) {
            throw new ArgumentException(
                message: "Operation name must be provided.",
                paramName: nameof(operation)
            );
        }

        return $"DirectX operation '{operation}' failed with HRESULT 0x{result:X8}.";
    }

    /// <summary>Gets the name of the DirectX operation that failed (for example, <c>CreateDXGIFactory2</c>).</summary>
    public string Operation { get; }
    /// <summary>Gets the <c>HRESULT</c> returned by the failing operation.</summary>
    public int Result { get; }

    /// <summary>Initializes a new instance of the <see cref="DirectXException"/> class for a failed operation.</summary>
    /// <param name="operation">The name of the DirectX operation that failed.</param>
    /// <param name="result">The <c>HRESULT</c> returned by the operation.</param>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is <see langword="null"/>, empty, or white space.</exception>
    public DirectXException(string operation, int result)
        : base(CreateMessage(
            operation: operation,
            result: result
        )) {
        Operation = operation;
        Result = result;
    }
}
