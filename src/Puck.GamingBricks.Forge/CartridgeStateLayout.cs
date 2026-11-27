namespace Puck.GamingBricks.Forge;

/// <summary>Shared byte layout for native state, initialization and persistence: the variable window, the array run and
/// the battery payload, each relative to a base address its target chooses.</summary>
/// <remarks>Two-byte slots precede byte slots, preserving declaration order within each width. This keeps every
/// halfword aligned without spending padding from the variable window. Arrays pack contiguously in declaration order, so
/// one table seeds them all. Physical base addresses belong to each target.</remarks>
public sealed class CartridgeStateLayout {
    /// <summary>A state slot's relative location and representation.</summary>
    /// <param name="Offset">Byte offset from the target's variable base.</param>
    /// <param name="Width">One or two bytes, little-endian.</param>
    /// <param name="Initial">The declared initial value.</param>
    public readonly record struct Slot(int Offset, int Width, int Initial);
    /// <summary>One run of the battery payload: a persisted variable or array.</summary>
    /// <param name="Name">The declared variable or array name.</param>
    /// <param name="IsArray">Whether the run is an array rather than a variable.</param>
    /// <param name="Length">The run's length in bytes.</param>
    public readonly record struct SaveRun(string Name, bool IsArray, int Length);

    /// <summary>Gets the bytes occupied by the variables.</summary>
    public int ByteCount { get; }
    /// <summary>Gets the named variable slots.</summary>
    public IReadOnlyDictionary<string, Slot> Slots { get; }
    /// <summary>Gets each array's byte offset from the target's array base.</summary>
    public IReadOnlyDictionary<string, int> ArrayOffsets { get; }
    /// <summary>Gets each array's length in bytes.</summary>
    public IReadOnlyDictionary<string, int> ArrayLengths { get; }
    /// <summary>Gets every array's initial contents, packed in layout order; empty when the document declares none.</summary>
    public byte[] ArrayInitial { get; }
    /// <summary>Gets the battery payload's runs in payload order, the declared variables then the declared arrays; empty
    /// when the document saves nothing.</summary>
    public IReadOnlyList<SaveRun> SavePayload { get; }
    /// <summary>Gets the payload's authored initial bytes, which a load restores when the stored block is invalid.</summary>
    public byte[] SaveDefaults { get; }

    /// <summary>Builds the representation of a validated document's state.</summary>
    /// <param name="document">The validated document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public CartridgeStateLayout(CartridgeDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        var slots = new Dictionary<string, Slot>(comparer: StringComparer.Ordinal);

        foreach (var variable in document.Variables.OrderByDescending(keySelector: static variable => variable.Width)) {
            slots.Add(
                key: variable.Name,
                value: new Slot(
                    ByteCount,
                    variable.Width,
                    variable.Initial
                )
            );
            ByteCount += variable.Width;
        }
        Slots = slots;

        var offsets = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var lengths = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var initial = new List<byte>();

        foreach (var array in document.Arrays) {
            offsets[array.Name] = initial.Count;
            lengths[array.Name] = array.Initial.Length;
            initial.AddRange(collection: array.Initial.Select(selector: static value => ((byte)value)));
        }
        ArrayOffsets = offsets;
        ArrayLengths = lengths;
        ArrayInitial = [.. initial];

        var runs = new List<SaveRun>();
        var defaults = new List<byte>();

        if (document.Save is { } save) {
            foreach (var name in save.Variables) {
                runs.Add(item: new SaveRun(Name: name, IsArray: false, Length: slots[name].Width));
                defaults.AddRange(collection: InitialBytes(name: name));
            }
            foreach (var name in save.Arrays) {
                runs.Add(item: new SaveRun(Name: name, IsArray: true, Length: lengths[name]));
                defaults.AddRange(collection: ArrayInitial.AsSpan(start: offsets[name], length: lengths[name]).ToArray());
            }
        }
        SavePayload = runs;
        SaveDefaults = [.. defaults];
    }

    /// <summary>Gets a saved slot's initial bytes in payload order.</summary>
    /// <param name="name">The declared variable name.</param>
    /// <returns>The complete little-endian initial value.</returns>
    public byte[] InitialBytes(string name) {
        var slot = Slots[name];

        return ((slot.Width == 1)
            ? [((byte)slot.Initial)]
            : [((byte)slot.Initial), ((byte)(slot.Initial >> 8))]
        );
    }
}
