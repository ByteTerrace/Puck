// The reused Puck.Vulkan bindings assume implicit usings (disabled in this freestanding
// build). Only `using System` is actually needed — VkQueueFlags uses [Flags] unqualified;
// every other binding imports System.Runtime.InteropServices explicitly.
global using System;
