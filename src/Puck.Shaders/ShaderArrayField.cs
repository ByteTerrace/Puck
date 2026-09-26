namespace Puck.Shaders;

/// <summary>One array a pass declares in its <c>arrays</c>: an interface array in the World group, which a world binds to
/// a whole state row with a <c>parameters</c> entry and the pass reads through its generated accessor,
/// <c>worldGroup.&lt;name&gt;At(i)</c>. An array no row binds reads zeros.</summary>
/// <param name="Type">The element type: <c>float</c>, <c>int</c> or <c>uint</c>. A bound row must fill it: an integer
/// element takes a row whose declared bounds fit its range.</param>
/// <param name="Length">The element count, from one to <see cref="MaxLength"/>. A bound row's cell count must not exceed
/// it; element <c>i</c> holds the row's cell <c>i</c>, and elements past the row read zero.</param>
/// <param name="Description">The array's description.</param>
public sealed record ShaderArrayField(
    ShaderValueType Type,
    uint Length,
    string? Description = null
) {
    /// <summary>The most elements an array holds: a World block stores each element in a 16-byte row, and 4096 rows fill
    /// the 65,536-byte constant buffer every backend guarantees.</summary>
    public const uint MaxLength = 4096;

    /// <summary>Checks a pass's arrays: each a scalar element type and a length from one to <see cref="MaxLength"/>.</summary>
    /// <param name="ownerName">The pass the arrays belong to, named in a refusal.</param>
    /// <param name="arrays">The arrays by name, or <see langword="null"/> for none.</param>
    /// <exception cref="InvalidDataException">An array's type is not a scalar or its length is out of range.</exception>
    public static void Validate(string ownerName, IReadOnlyDictionary<string, ShaderArrayField>? arrays) {
        foreach (var (name, array) in (arrays ?? new Dictionary<string, ShaderArrayField>())) {
            if (array is null) {
                throw new InvalidDataException(message: $"Pass '{ownerName}' array '{name}' is null.");
            }
            if (array.Type.ComponentCount() != 1) {
                throw new InvalidDataException(message: $"Pass '{ownerName}' array '{name}' is {array.Type}; an array element is a float, int or uint.");
            }
            if (
                (array.Length == 0) ||
                (array.Length > MaxLength)
            ) {
                throw new InvalidDataException(message: $"Pass '{ownerName}' array '{name}' length {array.Length} must be from 1 to {MaxLength}.");
            }
        }
    }
}
