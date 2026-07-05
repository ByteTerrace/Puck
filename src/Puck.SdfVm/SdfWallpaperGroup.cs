namespace Puck.SdfVm;

// Values must match Assets/Shaders/Sdf/sdf-vm.hlsli (SDF_WPG_*); IUC order, ported from the old monolith's
// AvatarSdfWallpaperGroup so authored content re-ports value-for-value.
/// <summary>The 17 wallpaper symmetry groups, in IUC order. P1–P4G tile a square/rectangular lattice; P3 and up tile
/// the equilateral hexagonal lattice (the cell must be square — the pitch is the cell's x extent).</summary>
public enum SdfWallpaperGroup : uint {
    /// <summary>Pure lattice translation (bit-identical to a two-axis RepeatLimited).</summary>
    P1 = 0,
    /// <summary>Half-turns keyed on the cell parity.</summary>
    P2 = 1,
    /// <summary>A mirror across the cell's Y axis.</summary>
    Pm = 2,
    /// <summary>A glide reflection.</summary>
    Pg = 3,
    /// <summary>A mirror composed with the centered lattice.</summary>
    Cm = 4,
    /// <summary>Perpendicular mirrors (the rectangle kaleidoscope).</summary>
    Pmm = 5,
    /// <summary>A mirror crossed with a glide.</summary>
    Pmg = 6,
    /// <summary>Perpendicular glides.</summary>
    Pgg = 7,
    /// <summary>Perpendicular mirrors on the centered lattice (2-fold centers off the mirrors).</summary>
    Cmm = 8,
    /// <summary>Quarter-turns about the cell corners (square cells only).</summary>
    P4 = 9,
    /// <summary>P4 plus mirrors through the 4-fold centers (square cells only).</summary>
    P4M = 10,
    /// <summary>P4 plus mirrors off the 4-fold centers (square cells only).</summary>
    P4G = 11,
    /// <summary>3-fold rotations on the hex lattice.</summary>
    P3 = 12,
    /// <summary>P3 plus mirrors through the hex corners.</summary>
    P3M1 = 13,
    /// <summary>P3 plus mirrors through the hex edge midpoints.</summary>
    P31M = 14,
    /// <summary>6-fold rotations on the hex lattice.</summary>
    P6 = 15,
    /// <summary>The full hex kaleidoscope (6-fold rotations plus both mirror families).</summary>
    P6M = 16,
}

/// <summary>The plane a <see cref="SdfOp.WallpaperFold"/> folds, named by the two axes it acts on (the third axis is
/// untouched). Values match the old monolith's axis decode.</summary>
public enum SdfWallpaperPlane : uint {
    /// <summary>Fold X and Z (tile the ground).</summary>
    XZ = 0,
    /// <summary>Fold X and Y (tile a wall facing Z).</summary>
    XY = 1,
    /// <summary>Fold Y and Z (tile a wall facing X).</summary>
    YZ = 2,
}
