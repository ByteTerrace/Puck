namespace Puck.ShaderVm;

/// <summary>Identifies what one <see cref="ShaderExpression"/> node evaluates to.</summary>
internal enum ShaderExpressionKind : byte {
    /// <summary>An execution-context input.</summary>
    Input = 0,
    /// <summary>A caller-supplied parameter.</summary>
    Parameter = 1,
    /// <summary>A program constant.</summary>
    Constant = 2,
    /// <summary>An operation over child nodes.</summary>
    Operation = 3,
}
