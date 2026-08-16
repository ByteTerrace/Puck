namespace Puck.Scripting;

/// <summary>Specifies the record value shape a physical engine input source carries — the open, engine-wide
/// vocabulary <c>Puck.World.AddonSourceVocabulary.TryResolve</c> derives from <c>Puck.Input.InputSources</c>' own
/// declared attributes. Consulted by binding-vocabulary validation (<c>Puck.World.WorldAffordances</c>); the addon
/// wasm ABI's own channel-name vocabulary is a distinct, closed set described by
/// <see cref="AddonChannelValueShape"/>, resolved through <see cref="IAddonChannelResolver"/> instead.</summary>
public enum AddonSourceShape {
    /// <summary>A pressed/released control. Its <c>A</c> lane is a pure BOOLEAN lane — literally <c>0</c> or
    /// <c>1</c>, never a fixed-point value — and its <c>B</c> lane is <c>0</c>.</summary>
    Digital = 0,

    /// <summary>A single-axis analog value in the <c>A</c> lane, magnitude at most <see cref="AddonAbi.One"/>;
    /// the <c>B</c> lane is <c>0</c>.</summary>
    Axis1D = 1,

    /// <summary>A two-axis analog value: the <c>A</c> and <c>B</c> lanes both carry fixed-point values with
    /// magnitude at most <see cref="AddonAbi.One"/>.</summary>
    Axis2D = 2,
}
