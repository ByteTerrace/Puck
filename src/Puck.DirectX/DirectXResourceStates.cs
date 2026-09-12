using System.Collections.Concurrent;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

// Graphics and compute record into the same direct queue. A resource's state belongs to the resource,
// not to a command list; creation/disposal bound the entries so recycled native pointers cannot inherit state.
internal static class DirectXResourceStates {
    private static readonly ConcurrentDictionary<nint, D3D12_RESOURCE_STATES> States = new();
    internal const D3D12_RESOURCE_STATES ShaderRead = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    internal static D3D12_RESOURCE_STATES Get(nint resource, D3D12_RESOURCE_STATES fallback) => States.TryGetValue(resource, out var state) ? state : fallback;
    internal static void Register(nint resource, D3D12_RESOURCE_STATES state) => States[resource] = state;
    internal static void Set(nint resource, D3D12_RESOURCE_STATES state) {
        while (States.TryGetValue(resource, out var before) && !States.TryUpdate(resource, state, before)) { }
    }
    internal static void Forget(nint resource) => States.TryRemove(resource, out _);
}