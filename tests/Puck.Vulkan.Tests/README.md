# Puck.Vulkan.Tests

This xUnit v3 suite checks the Vulkan backend's managed decisions that do not
need a device. Native marshalling takes every byte from the injected
allocator, so an allocator that refuses one allocation shows that a string
array failing partway frees everything it had already allocated. A recording
buffer API and a device context that refuses every member pin the usage and
memory each storage and vertex buffer is created with, the memory properties
and allocate flags those select, and the `VulkanBuffer` owner's map and destroy
order. Command tables built over a resolver that stands in for the driver show
that every handle kind reaches the one zero-handle guard, and that the
renderer's boot chain, failed at each link, destroys everything it built before
that link exactly once in reverse order and runs no later link. The group
planner turns the gate spike's two-group layouts into the set layouts and push
range they need, and refuses a description no backend may plan. On a command
table whose layout entry points record what they are handed, the pipeline
layout created from those plans holds the planned set layouts in set order,
their stage flags and the push range, and a failed set layout leaves nothing
alive. It creates
no real Vulkan instance or device, so it says nothing about driver behavior;
cross-backend rendering is checked by `puck parity`.

## Verification

```powershell
dotnet test tests/Puck.Vulkan.Tests/Puck.Vulkan.Tests.csproj -c Release
```

## Documentation

📚 [Vulkan](../../docs/rendering/vulkan.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
