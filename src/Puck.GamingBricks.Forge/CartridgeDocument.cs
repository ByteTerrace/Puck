using System.Text.Json.Serialization;

namespace Puck.GamingBricks.Forge;

/// <summary>A complete authored cartridge. Game content and behavior live in this document; compilers supply only hardware machinery.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeDocument {
    /// <summary>The recognized document family.</summary>
    public const string SchemaId = "puck.cartridge.v1";
    /// <summary>Gets the declared schema; absence is invalid.</summary>
    public required string Schema { get; init; }
    /// <summary>Gets the target: cgb or agb.</summary>
    public required string Target { get; init; }
    /// <summary>Gets the ASCII cartridge title, at most twelve characters.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the four-character ASCII game code.</summary>
    public required string GameCode { get; init; }
    /// <summary>Gets the RGB555 palette, four colors for CGB or sixteen for AGB. Color zero is transparent on sprites.</summary>
    public required int[] Palette { get; init; }
    /// <summary>Gets the named 8 by 8 tiles, addressed by declaration index.</summary>
    public required CartridgeTile[] Tiles { get; init; }
    /// <summary>Gets the 32 by 32 background tile map in row-major order.</summary>
    public required int[] Map { get; init; }
    /// <summary>Gets the named unsigned byte state slots and their initial values.</summary>
    public required CartridgeVariable[] Variables { get; init; }
    /// <summary>Gets rules evaluated once per game frame, in declaration order. Later rules see earlier writes.</summary>
    public required CartridgeRule[] Rules { get; init; }
    /// <summary>Gets sprite bindings sampled after the rules, in hardware priority order.</summary>
    public required CartridgeSprite[] Sprites { get; init; }
    /// <summary>Gets the horizontal background scroll, in pixels modulo 256.</summary>
    public required CartridgeValue ScrollX { get; init; }
    /// <summary>Gets the vertical background scroll, in pixels modulo 256.</summary>
    public required CartridgeValue ScrollY { get; init; }
}

/// <summary>A named tile whose eight rows each contain eight hexadecimal palette indices.</summary>
/// <param name="Name">The tile's author-facing name.</param>
/// <param name="Pixels">Eight strings of eight hexadecimal digits.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeTile(string Name, string[] Pixels);

/// <summary>A mutable unsigned byte of cartridge state.</summary>
/// <param name="Name">The case-sensitive state name.</param>
/// <param name="Initial">The initial value, 0 through 255.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeVariable(string Name, int Initial);

/// <summary>Exactly one of a literal byte or a reference to an authored state slot.</summary>
/// <param name="Constant">A literal in 0 through 255, or null for a variable.</param>
/// <param name="Variable">A declared state name, or null for a constant.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeValue(int? Constant = null, string? Variable = null);

/// <summary>A rule's conjunction. A key condition tests held/pressed/released input; a compare condition compares two unsigned bytes.</summary>
/// <param name="Kind">key or compare.</param>
/// <param name="Key">For key: a, b, start, select, up, down, left or right.</param>
/// <param name="Mode">For key: held, pressed or released.</param>
/// <param name="Left">For compare: the left operand.</param>
/// <param name="Comparison">For compare: eq, ne, lt, le, gt or ge.</param>
/// <param name="Right">For compare: the right operand.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeCondition(string Kind, string? Key = null, string? Mode = null, CartridgeValue? Left = null, string? Comparison = null, CartridgeValue? Right = null);

/// <summary>A state write. Arithmetic wraps modulo 256 on both targets.</summary>
/// <param name="Variable">The destination state name.</param>
/// <param name="Operation">set, add, subtract, and, or or xor.</param>
/// <param name="Value">The source operand, evaluated when the action executes.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeAction(string Variable, string Operation, CartridgeValue Value);

/// <summary>An ordered, conditional group of state changes.</summary>
/// <param name="Name">The diagnostic name.</param>
/// <param name="When">All conditions must hold; empty means every frame.</param>
/// <param name="Actions">Writes executed in declaration order.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeRule(string Name, CartridgeCondition[] When, CartridgeAction[] Actions);

/// <summary>A native 8 by 8 sprite bound to cartridge state. Coordinates are screen pixels; zero visibility hides it.</summary>
/// <param name="Name">The sprite's author-facing name.</param>
/// <param name="Tile">Tile index; values outside the authored tile bank hide the sprite.</param>
/// <param name="X">Screen X coordinate.</param>
/// <param name="Y">Screen Y coordinate.</param>
/// <param name="Visible">Zero hides the sprite; any other value shows it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeSprite(string Name, CartridgeValue Tile, CartridgeValue X, CartridgeValue Y, CartridgeValue Visible);
