using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>Registers single bindings with a <see cref="WorldStateMirror"/> the way a seat or a document registers its
/// reads, for laws over rows no document's presentation manifest records.</summary>
internal static class MirrorBindings {
    /// <summary>Registers one binding under an owner of its own and returns the slot a lookup finds for it.</summary>
    /// <param name="mirror">The mirror.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="conversion">How the reader converts the cell.</param>
    /// <returns>The registered slot's index.</returns>
    public static int Bind(this WorldStateMirror mirror, in StateBinding binding, WorldStateConversion conversion) {
        mirror.Register(
            bindings: [new WorldPresentationBinding(
                Binding: binding,
                Conversion: conversion
            )],
            owner: new object()
        );

        return mirror.SlotOf(
            binding: in binding,
            conversion: conversion
        );
    }
    /// <summary>Registers the binding an authored token carries under an owner of its own and returns its slot.</summary>
    /// <param name="mirror">The mirror.</param>
    /// <param name="token">The authored <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> token.</param>
    /// <param name="conversion">How the reader converts the cell.</param>
    /// <returns>The registered slot's index.</returns>
    /// <exception cref="ArgumentException"><paramref name="token"/> is no state binding.</exception>
    public static int Bind(this WorldStateMirror mirror, string token, WorldStateConversion conversion) => Bind(
        binding: (StateBinding.Parse(token: token) ?? throw new ArgumentException(
            message: $"'{token}' is no state binding.",
            paramName: nameof(token)
        )),
        conversion: conversion,
        mirror: mirror
    );
}
