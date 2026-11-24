# Memory budgets on Puck's target devices (brief s1-research-budgets)

Research only: no source edited, no build, no World. Written for the streaming
and memory campaign's device readings (`docs/plans/streaming-and-memory.md`,
decisions 1 and 2).

Step numbers in this report are the campaign outline's as it stood when the round ran: 2 one byte authority, 3 attribution, 4 duplicate-copy removal, 5 priority eviction, 6 mapped portal demand, 7 shared lighting demand, 8 areas, terrain and moons, 9 managed churn. [The plan](../streaming-and-memory.md#the-build-order) builds the same work as its steps 1 and 3, 2, 6, 5, 4, 9, 10 and 11, in that order.

How to read the citations. Every external claim carries the sentence it rests
on and its URL. Microsoft Learn and .NET pages came back as the page's own
markdown, so those quotes are exact. Other pages were read through a fetch tool
that returns extracted quotes; those quotes are the tool's verbatim extracts, and
where it only paraphrased I say so. "Search summary" marks a claim seen only in
a search engine's summary, never opened. "Inference" and "hypothesis" mark my
own reasoning. Puck code is cited `file:line` from Read and Grep output in the
lane's worktree when the round ran; line numbers drift, symbols do not.

## 1. Direct3D 12 and DXGI

**The reading.** `IDXGIAdapter3::QueryVideoMemoryInfo(NodeIndex, MemorySegmentGroup, &info)`
"informs the process of the current budget and process usage", and its remarks
say: "Applications must explicitly manage their usage of physical memory
explicitly and keep usage within the budget assigned to the application process.
Processes that cannot kept their usage within their assigned budgets will likely
experience stuttering, as they are intermittently frozen and paged-out to allow
other processes to run."
(https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo)

`DXGI_QUERY_VIDEO_MEMORY_INFO` has four fields
(https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info):
- `Budget`: "Specifies the OS-provided video memory budget, in bytes, that the
  application should target. If *CurrentUsage* is greater than *Budget*, the
  application may incur stuttering or performance penalties due to background
  activity by the OS to provide other applications with a fair usage of video memory."
- `CurrentUsage`: "Specifies the application's current video memory usage, in bytes."
- `AvailableForReservation` and `CurrentReservation`: "The OS uses the
  reservation as a hint to determine the application's minimum working set.
  Applications should attempt to ensure that their video memory usage can be
  trimmed to meet this requirement."

**The reservation.** `SetVideoMemoryReservation` "sends the minimum required
physical memory for an application, to the OS", and "Applications are encouraged
to set a video reservation to denote the amount of physical memory they cannot
go without. This value helps the OS quickly minimize the impact of large memory
pressure situations."
(https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-setvideomemoryreservation)
The residency page adds: "Ideally, the user-specified "low" graphics settings, or
something even lower, is the right value for such a reservation. Setting a
reservation won't ever give an application a higher budget than it would
normally receive. ... Even the reservation is not guaranteed to be available to
the application when the application isn't the foreground application."
(https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency)

**The notification.** `RegisterVideoMemoryBudgetChangeNotificationEvent(hEvent, &cookie)`
takes "A handle to the event object that the operating system sets when memory
budgets change", and "Instead of calling QueryVideoMemoryInfo regularly,
applications can use CPU synchronization objects to efficiently wake threads
when budget changes occur."
(https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-registervideomemorybudgetchangenotificationevent)
DXVK's reimplementation records that Windows signals once at registration:
"// This method seems to fire the" / "// event immediately on Windows" followed
by `SetEvent(hEvent);` (https://raw.githubusercontent.com/doitsujin/dxvk/master/src/dxgi/dxgi_adapter.cpp).

**Segment groups.** `DXGI_MEMORY_SEGMENT_GROUP_LOCAL` is "The grouping of
segments which is considered local to the video adapter, and represents the
fastest available memory to the GPU. Applications should target the local
segment group as the target size for their working set." `NON_LOCAL` "may have
slower performance than the local segment group."
(https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ne-dxgi1_4-dxgi_memory_segment_group)
The mapping onto heaps (https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ne-d3d12-d3d12_memory_pool):
"When the adapter is UMA, D3D12_MEMORY_POOL_L0 and DXGI_MEMORY_SEGMENT_GROUP_LOCAL
refer to the same memory." and "When the adapter is not UMA: D3D12_MEMORY_POOL_L0
and DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL refer to the same memory.
D3D12_MEMORY_POOL_L1 and DXGI_MEMORY_SEGMENT_GROUP_LOCAL refer to the same memory."
So on the RTX 2060 and 4070, LOCAL is VRAM (L1) and NON_LOCAL is the system
memory that upload, readback and staging heaps live in (L0); a UMA adapter has
one group.

**Under pressure.** "The budget can fluctuate noticeably as background processes
wake-up and sleep; and fluctuate dramatically when the user switches away to
another application. ... If an application doesn't stay within its budget, the
process will be intermittently frozen to allow other applications to run and/or
the creation APIs will return failure." And: "UMA adapters have one pool, while
discrete adapters have two pools. While it is true that kernel can shift some
heaps on discrete adapters from video memory to system memory, it does so only
as an extreme last resort. Applications should not rely on the over-budget
behavior of the kernel, and should focus on good budget management instead."
And: "even simple applications are strongly recommended to watch for negative
budget changes and destroy unused API objects roughly once a frame."
(https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency)

**Priorities.** The same page: default priorities put render targets,
depth-stencil and UAV heaps in a high bucket ("assigned priority values in the
range starting at D3D12_RESIDENCY_PRIORITY_HIGH"), everything else at
`D3D12_RESIDENCY_PRIORITY_NORMAL`, and an application built for D3D12 may
"Override nearly all heap priorities with the application's own bucketization
scheme" through `ID3D12Device1::SetResidencyPriority`. It also says "destruction
of heaps is a more reliable technique to free up residency across all adapter
architectures" than `Evict`.

**Integrated GPUs.** D3D12MA documents the UMA case: on an integrated GPU,
`pLocalBudget` "returns the budget of the shared memory available for all D3D12
resources" and `pNonLocalBudget` "is not applicable and returns zeros."
(https://gpuopen-librariesandsdks.github.io/D3D12MemoryAllocator/html/class_d3_d12_m_a_1_1_allocator.html)

## 2. Vulkan

**VK_EXT_memory_budget.** From the specification source
(https://raw.githubusercontent.com/KhronosGroup/Vulkan-Docs/main/chapters/memory.adoc;
refpage https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceMemoryBudgetPropertiesEXT.html):
"A heap's budget provides a guideline for how much total memory the current
process can use at any given time before allocations may start failing or
causing performance degradation. The values may change based on other activity
in the system that is outside the scope and control of the Vulkan
implementation." (assembled from the tool's extracts of those two sentences);
"A heap's usage is an estimate of how much memory the current process has
allocated from that heap."; "The values returned in this structure are not
invariant."; and heapBudget "must be less than or equal to" `VkMemoryHeap::size`.
It is read per heap by chaining `VkPhysicalDeviceMemoryBudgetPropertiesEXT` into
`vkGetPhysicalDeviceMemoryProperties2`. Vulkan has no budget notification and
no reservation.

**VK_EXT_memory_priority and VK_EXT_pageable_device_local_memory.**
`VkMemoryPriorityAllocateInfoEXT::priority` "is a floating-point value between 0
and 1, indicating the priority of the allocation relative to other memory
allocations. Larger values are higher priority."
(https://docs.vulkan.org/refpages/latest/refpages/source/VkMemoryPriorityAllocateInfoEXT.html)
The pageable extension "indicates to the application that the operating system
implements pageable device-local memory", lets it "dynamically adjust the
priority of existing memory allocations", and with it "the Vulkan
implementation will also ensure that host-local memory allocations will never be
promoted to device-local memory"
(https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_pageable_device_local_memory.html);
the spec: "the application should set the appropriate memory priority with
VkMemoryPriorityAllocateInfoEXT and adjust it as necessary with
vkSetDeviceMemoryPriorityEXT. Higher priority allocations will be moved to
device-local memory first." (memory.adoc, tool extract). Whether RADV implements
the pageable extension: a Mesa issue titled "radv: implement
VK_EXT_pageable_device_local_memory" exists
(https://gitlab.freedesktop.org/mesa/mesa/-/issues/9224; search summary only,
gitlab refused every fetch), and per the same summary vkd3d-proton uses it
optionally since v2.9. Unverified.

**Linux has no overcommit manager.** NVIDIA: "When memory is over-committed on
Windows," the OS may move allocations to system memory or pause the process, but
"There is no OS memory manager on Linux that mitigates over-commitment"; and
"Use VK_EXT_pageable_device_local_memory to avoid demotion of critical resources
by assigning memory priority." (https://developer.nvidia.com/blog/vulkan-dos-donts/)
A user thread reports no system-memory fallback when VRAM fills: "The NVIDIA
Linux driver doesn't handle the VRAM sharing with the system RAM", with no
NVIDIA reply (https://forums.developer.nvidia.com/t/non-existent-shared-vram-on-nvidia-linux-drivers/260304,
July 2023; a user claim, not a vendor statement). Inference: on the RTX cards
under Linux the device budget behaves as a hard ceiling (allocation fails),
not a soft one (stutter), so a Linux build must reserve before it allocates.

**How RADV reports it** (Mesa main, read from the mirror
https://raw.githubusercontent.com/chaotic-cx/mesa-mirror/main/src/amd/vulkan/radv_physical_device.c;
gitlab.freedesktop.org refused every fetch behind its bot wall; the Android
mirror https://android.googlesource.com/platform/external/mesa3d/+/refs/heads/main/src/amd/vulkan/radv_physical_device.c
shows the same heap code):
- Heap layout on an APU (`if (!pdev->info.has_dedicated_vram) {`):
  `const uint64_t total_size = gtt_size + visible_vram_size;`, then by default
  "/* On APUs, the carveout is usually too small for games that request a
  minimum VRAM size greater than it. To workaround this, we compute the total
  available memory size (GTT + visible VRAM size) and report 2/3 as VRAM and 1/3
  as GTT. */" with `visible_vram_size = align64((total_size * 2) / 3, pdev->info.gart_page_size);`
  and `gtt_size = total_size - visible_vram_size;`. A per-application drirc
  switch, `pdev->drirc.performance.enable_unified_heap_on_apu`, instead exposes
  one heap ("Some applications seem better when the driver exposes only one heap
  of VRAM on APUs.").
- Heap flags: the GTT heap is `.flags = 0,`; the VRAM and visible-VRAM heaps are
  `.flags = VK_MEMORY_HEAP_DEVICE_LOCAL_BIT,`. GTT memory types are
  `VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,`
  (and a `HOST_CACHED` variant).
- Budget: `radv_query_heap_info` reads the driver's own allocations
  (`allocated_vram`, `allocated_vram_vis`, `allocated_gtt`) and the kernel's
  system-wide usage (`vram_usage`, `vram_vis_usage`, `gtt_usage`); the comment's
  formula is "heap_budget = heap_size - global_heap_usage + app_heap_usage"; on
  an APU with the unified option `heapBudget[vram_vis_heap_idx] = total_free_space + total_internal_usage;`,
  otherwise free space is split about two thirds and one third across the fake
  heaps ("On APUs, the driver exposes fake heaps to the application because
  usually the carveout"; the tool paraphrased the rest); finally each budget is
  clamped, `memoryBudget->heapBudget[i] = MIN2(memory_properties->memoryHeaps[i].size, memoryBudget->heapBudget[i]);`.
  Inference: on the Deck the two RADV heaps are not two physical pools; only
  their sum means anything, and it counts GPU buffer usage (carve-out plus GTT)
  by every process, not the CPU-side memory those processes hold.

## 3. The Steam Deck

**What Valve publishes.** The Steamworks FAQ (https://partner.steamgames.com/doc/steamdeck/faq?l=english):
"Steam Deck has 16 gigabyte of unified memory." "One gigabyte that's dedicated
for the GPU, but depending on workload the GPU can access up to 8 GB." "Up to 8GB
can be shared right now and it's an amount that can be changed on a game-by-game
basis." It gives no budget guidance for developers. The tech page: "16 GB LPDDR5
on-board RAM (6400 MT/s quad 32-bit channels)", "GPU: 8 RDNA 2 CUs, 1.6GHz (1.6
TFlops FP32)", "CPU: Zen 2 4c/8t, 2.4-3.5GHz" (https://www.steamdeck.com/en/tech).

**Where the numbers come from (kernel).** The 1 GB is the BIOS UMA frame buffer
carve-out (third-party guides say the Deck reserves 1 GB by default and the BIOS
caps it at 4 GB, https://pimylifeup.com/steam-deck-increase-vram; search summary
only). The GPU's reach into system memory is GTT. In Linux 6.12, `amdgpu_ttm_init`
sizes it with "/* Compute GTT size, either based on TTM limit" / "* or whatever
the user passed on module init." and `gtt_size = ttm_tt_pages_limit() << PAGE_SHIFT;`
(https://sources.debian.org/src/linux/6.12.8-1/drivers/gpu/drm/amd/amdgpu/amdgpu_ttm.c),
and TTM's limit is set in `ttm_global_init`: "/* Limit the number of pages in
the pool to about 50% of the total system memory." with `num_pages /= 2;`
(https://sources.debian.org/src/linux/6.12.8-1/drivers/gpu/drm/ttm/ttm_device.c).
The amdgpu `gttsize` parameter will "Restrict the size of GTT domain (for
userspace use) in MiB for testing" and "is deprecated"
(https://docs.kernel.org/gpu/amdgpu/module-parameters.html).

Inference, arithmetic on the cited rules and not a Deck reading: Linux sees
about 15 GB (16 GB less the 1 GB carve-out and firmware), GTT is about half of
that, so the GPU reaches about 1 GB + 7.x GB, which matches Valve's "up to
8 GB"; RADV then reports a device-local heap of about two thirds of that (about
5.5 GB) and a host heap of about a third. SteamOS ships Valve's own kernel,
which may size GTT differently, and Valve's "changed on a game-by-game basis"
suggests a per-game knob (drirc or kernel); unverified.

**What a game should treat as its budget there (inference from the sources
above).** Two ceilings bind at once: the GPU ceiling (carve-out plus GTT limit,
which RADV's heap budgets track system-wide) and the physical ceiling (16 GB
shared by CPU, GPU, SteamOS and the Steam client, which Linux reports as
`MemAvailable`). Neither reading sees the other side: RADV's budget does not
subtract the CPU memory Puck holds, and `MemAvailable` is the only reading that
does. Whether GTT pages count against `MemAvailable` is not documented in what I
read; it is my hypothesis (TTM allocates ordinary pages), and a Deck reading
settles it. Work by a Valve contractor on VRAM priority notes "as the Steam Deck
uses the same memory for both the CPU and GPU, meaning the GPU is always using
GTT memory" (https://steamdeckhq.com/news/valve-developer-optimizes-linux-vram-usage/,
2026-04-10).

**Proton is evidence, not Puck's path.** Puck runs natively on Linux
(`src/Puck.World/Puck.World.csproj:18`: `<RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers>`)
with its Vulkan backend, so on the Deck it reads RADV directly. How DXVK's DXGI
(which VKD3D-Proton games use) answers on the Deck still shows what shipped games
see (https://raw.githubusercontent.com/doitsujin/dxvk/master/src/dxgi/dxgi_adapter.cpp):
on a unified adapter it sums the device-local heaps' VK_EXT_memory_budget for
both segment groups ("// Apparently, Unreal Engine decided that not having a
non-local heap equates" / "// running on a 15-year old Intel IGP and disables a
bunch of effects as a" / "// result. Simply report the VRAM budgets as a
workaround." with `if (MemorySegmentGroup == DXGI_MEMORY_SEGMENT_GROUP_LOCAL || m_adapter->isUnifiedMemoryArchitecture())`),
reports `pVideoMemoryInfo->AvailableForReservation += memInfo.heaps[i].heapSize / 2;`,
and for `DedicatedVideoMemory` reports "memory normally to not unnecessarily
confuse games on Deck". Its adapter takes the driver's budget and corrects usage
by its own counters, `info.heaps[i].memoryAllocated = std::max(memBudget.heapUsage[i], allocated) - allocated + used;`,
and without the extension falls back to the heap size as budget
(`info.heaps[i].memoryBudget = memProps.memoryProperties.memoryHeaps[i].size;`)
(https://raw.githubusercontent.com/doitsujin/dxvk/master/src/dxvk/dxvk_adapter.cpp).

## 4. Shipped engines and middleware

**D3D12MA** (https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/D3D12MemoryAllocator/master/src/D3D12MemAlloc.cpp):
`CurrentBudgetData::UpdateBudget` calls
`adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &infoLocal);`
and the NON_LOCAL twin, stores `CurrentUsage` and `Budget`, and snapshots its own
block bytes (`m_BlockBytesAtD3D12Fetch[0] = m_BlockBytes[0];`). Between fetches
it estimates usage as the fetched usage plus its own block bytes since
(`*outLocalUsage = D3D12Usage + blockBytes > blockBytesAtD3D12Fetch ?`), and
refetches after 30 allocator operations:
`bool ShouldUpdateBudget() const { return m_OperationsSinceBudgetFetch >= 30; }`.
Its documentation calls `GetBudget` "very fast, suitable to be called every
frame or every allocation."

**VMA** (https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/staying_within_budget.html;
the tool paraphrased this page with short extracts): with
`VMA_ALLOCATOR_CREATE_EXT_MEMORY_BUDGET_BIT` the budget is fetched inside
`vmaSetCurrentFrameIndex()` once per frame; `vmaGetHeapBudgets()` "can be called
every frame or even before every allocation"; `VMA_ALLOCATION_CREATE_WITHIN_BUDGET_BIT`
refuses an allocation over budget and is meant for non-essential resources, not
render targets; without the extension VMA estimates from its own information and
heap sizes ("may be less precise"). Against buggy drivers VMA substitutes
`m_Budget.m_VulkanBudget[heapIndex] = m_MemProps.memoryHeaps[heapIndex].size * 8 / 10; // 80% heuristics.`
for a zero budget and clamps one over the heap size
(https://skia.googlesource.com/external/github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/+/6a93b8aa5fa0ca5c6cd56f85eadfc0711c5a9e5d%5E%21/).
Whether VMA uses the same 80% when the extension is absent: not confirmed.

**AMD's RDNA guide** (https://gpuopen.com/learn/rdna-performance-guide/):
"Use QueryVideoMemoryInfo and VK_EXT_memory_budget to query for the current
memory budget." "It is recommended to only use 80% of the total VRAM to reduce
the probability of eviction." "Don't call memory query functions per frame."
"Instead use the RegisterVideoMemoryBudgetChangeNotificationEvent function to
have the OS signal the app".

**Unreal Engine.** Texture streaming: `r.Streaming.PoolSize` is "The pool size in
MB available for textures in the engine." and `r.Streaming.LimitPoolSizeToVRAM`
"If enabled, the texture pool size will be limited to how much GPU memory is
available." (https://dev.epicgames.com/documentation/en-us/unreal-engine/texture-streaming-configuration-in-unreal-engine).
How the default is derived is not on that page. A licensee quoting UE 5.6
source (`WindowsD3D12Device.cpp`) shows the D3D12 RHI takes 90% of the DXGI
local budget as total graphics memory: "const int64 TargetBudget =
LocalVideoMemoryInfo.Budget * 0.90f;" / "// Target using 90% of our budget to
account for some fragmentation." while D3D11 uses the whole budget. The reply
(Mihnea Balta, 2025-06-24; his role is not shown on the page): "that hardcoded
0.9 value came from Microsoft 9 years ago", it "affects the
PoolSizeVRAMPercentage value from DefaultEngine.ini", "the default value of 70
makes the engine use 63% of the available VRAM for the streaming pool" unless
`D3D12.AdjustTexturePoolSizeBasedOnBudget` overwrites it, and "VRAM is
virtualized and the budgets are approximate so it's probably safe to go up to
the limit" (https://forums.unrealengine.com/t/dx12-vram-budget/2616586). A
community cvar wiki quotes `r.Streaming.PoolSize.VRAMPercentageClamp` (default
1024 MB): "When using PoolSizeVRAMPercentage, a maximum amout of memory to
reserve in MB." / "This avoids reserving too much space for systems with a lot
of VRAM. (default=1024)" (https://indxzero.github.io/ue544cvarwiki/articles/r.streaming.poolsize.vrampercentageclamp).
Nanite's geometry pool is fixed at start, not budget-driven:
"r.Nanite.Streaming.StreamingPoolSize not being marked with ECVF_Scalability is
intentional." "Changing this value requires reallocating streaming pages, which
is expected to happen during engine initialization." "the streaming pool should
be large enough to accommodate all geometry required for the current view."
(Henry Liu, 2026-03-10, staff role not labelled;
https://forums.unrealengine.com/t/scaling-nanite-streamingpoolsize-vram-usage-via-scalability-settings-or-at-runtime/2712963).
A search summary (not opened) says Nanite raises pixels per edge through
`r.Nanite.Streaming.QualityScale` when the pool is too small; the 512 MB
default I remember is unverified.

**Console practice.** Consoles hand a title a fixed, published allotment rather
than a varying budget. Xbox Series X: "16 GB GDDR6 w/ 320b bus", "10GB @ 560 GB/s,
6GB @ 336 GB/s" (https://news.xbox.com/en-us/2020/03/16/xbox-series-x-tech/).
How much a title gets is in NDA GDK pages: the public memory index names
`XboxAnacondaTitleMemory` and `XboxLockhartTitleMemory` elements of
MicrosoftGame.config (https://devdocs.xbox.com/build/console-features/memory.md),
and the overview redirects to an access-restricted page. The platform raises the
allotment by SDK release: GDK notes quoted by the press, "Hundreds of additional
MB of memory are now available to Xbox Series S developers", with Series S
having "8GB of memory for games to use" (https://www.thesixthaxis.com/?p=399702,
2022-08-05). The 13.5 GB Series X title figure appears only in search summaries
I could not open. The lesson is the shape, not the numbers: on unified memory a
console title is handed one fixed figure for CPU and GPU together and spends it
by its own split. The Deck is unified like a console but shared with a desktop
OS and a variable GPU reach, so its figure must be read, not assumed.

## 5. .NET

From https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector:
- GC settings "are only read by the runtime when the GC is initialized".
- Heap hard limit: "the maximum commit size, in bytes, for the GC heap and GC
  bookkeeping"; in a memory-constrained environment the default is "either 20 MB
  or the heap hard limit percent of the container limit, whichever is larger",
  and the percent default there is 75%.
- Regions: "Starting in .NET 7, the GC heap switched its physical representation
  from segments to regions for 64-bit Windows and Linux." The GC reserves
  address space ("by default 256 GB is reserved"), and "this is only reserving
  memory, not committing". "By default, each region is 4 MB for SOH. For UOH
  (LOH and POH), it's eight times the SOH region size."
- High memory percent: "By default, when the physical memory load reaches 90%,
  garbage collection becomes more aggressive about doing full, compacting
  garbage collections to avoid paging."
- Conserve memory: "Configures the garbage collector to conserve memory at the
  expense of more frequent garbage collections and possibly longer pause times."
  and "If the value is non-zero, the large object heap will be compacted
  automatically if it has too much fragmentation."
- DATAS: "DATAS adapts to application memory requirements, meaning the app heap
  size should be roughly proportional to the long-lived data size." "Enabled by
  default starting in .NET 9." It is listed under the server GC settings.

Server versus workstation (https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc):
workstation "is designed for client apps"; server provides "A heap and a
dedicated thread to perform garbage collection ... for each logical CPU", "often
has larger size segments", and "can be resource-intensive".

At run time: `GC.RefreshMemoryLimit()` "Instructs the Garbage Collector to
reconfigure itself by detecting the various memory limits on the system", re-reads
`GCHeapHardLimit` and the other hard-limit settings from `AppContext` ("you can
use `AppContext.SetData("GCHeapHardLimit", (ulong) 100 * 1024 * 1024)` to
override the GCHeapHardLimit to 100M"), and throws `InvalidOperationException`
when "the heap hard limit that the refresh will set ... is lower than what's
already committed" (https://learn.microsoft.com/en-us/dotnet/api/system.gc.refreshmemorylimit,
.NET 8 and later). `GC.AddMemoryPressure` exists for finalizer-owned native
memory: "The garbage collector only knows about managed memory and schedules
collections based on this knowledge." and "It's not necessary to use these
methods in types that follow the dispose pattern"
(https://learn.microsoft.com/en-us/dotnet/api/system.gc.addmemorypressure).

Measuring native allocations. Puck already routes mesh payloads through its own
allocator with statistics (`src/Puck.Platform/Memory/MimallocAllocator.cs:14`:
`public sealed partial class MimallocAllocator : IAllocator, IAllocatorStatistics {`),
and `host.memory` prints `allocator-committed` beside `managed-committed`. What
escapes it: `NativeMemory` calls (`src/Puck.Abstractions/Gpu/Residency/GpuRegion.cs:394`:
`m_contents = ((byte*)NativeMemory.AllocZeroed(byteCount: ((nuint)byteCount)));`),
driver allocations and the CLR's own heaps. For those the readings are the OS
ones: on Linux `/proc/self/smaps_rollup` and the DRM client stats in
`/proc/self/fdinfo` (`drm-resident-<region>`: "The total size of buffers that
are resident (i.e., have their backing store present or instantiated)" in the
region; `drm-memory-<region>` "is deprecated and is only printed by amdgpu; it
is an alias for drm-resident-<region>", https://docs.kernel.org/gpu/drm-usage-stats.html;
amdgpu's regions are vram, gtt and cpu in its `amdgpu_fdinfo.c`, search summary
only), and on Windows the page walk and the `GPU Process Memory` counters the
open items already name. fdinfo is the independent second reading of GPU bytes
on the Deck that VK_EXT_memory_budget's usage can be checked against.

## 6. Where the sources disagree

- **How often to read.** AMD's guide: "Don't call memory query functions per
  frame", use the notification. AMD's own allocators: D3D12MA refetches every 30
  operations and calls `GetBudget` fast enough for "every frame or every
  allocation"; VMA fetches once per frame. Microsoft: watch for negative changes
  "roughly once a frame". Vulkan has no notification at all. They agree on the
  substance: cache the reading, refresh it on an event or at a low rate, and
  correct usage between refreshes by your own counters.
- **How much of the budget to spend.** AMD: 80% of total VRAM. Unreal's D3D12
  RHI: 90% of the DXGI budget (a figure its licensee says came from Microsoft
  around 2016), D3D11 100%. The same licensee: "probably safe to go up to the
  limit". VMA uses 80% only as a guard against a broken driver.
- **The UMA non-local group.** Native D3D12 (per D3D12MA): non-local is zero on
  UMA. DXVK on a unified adapter answers NON_LOCAL with the local budget, as a
  workaround for Unreal. A reader of DXGI on a UMA adapter must never add the two.
- **What GPU memory the Deck has.** Valve: 1 GB dedicated, "up to 8 GB",
  per-game. RADV: two fake heaps split two thirds and one third of carve-out
  plus GTT, or one heap per drirc. Third-party guides: 1 GB default, 4 GB BIOS
  maximum, and conflicting claims about whether the carve-out matters on SteamOS
  (search summaries only).
- **Overcommit.** Windows pages and freezes a process over budget (Microsoft,
  NVIDIA). Linux has no such manager (NVIDIA); NVIDIA Linux users report outright
  failure; amdgpu can evict VRAM to GTT on a discrete card, but on the Deck
  everything is already GTT (the Valve contractor article), so over the GTT limit
  is failure (inference).

## 7. What Puck has today

- **No device budget is read anywhere.** Grep over `src` for
  `QueryVideoMemoryInfo`, `RegisterVideoMemoryBudgetChangeNotification`,
  `SetVideoMemoryReservation`, `SetResidencyPriority`, `VK_EXT_memory_budget`,
  `VK_EXT_memory_priority`, `pageable_device_local` and `MakeResident` returned
  only unrelated `Evict(` methods (`WorldHistory.cs`, `SdfMeshLibrary.cs`,
  `WorldDeferredVerbEchoes.cs`). DXGI is opened only as `IDXGIAdapter1` through
  `IDXGIFactory4` (`src/Puck.DirectX/NativeMethods.txt:3` `IDXGIFactory4`, `:5`
  `IDXGIAdapter1`), and the adapter is reopened per read with a fresh factory
  (`src/Puck.DirectX/Apis/DirectXNativeDeviceApi.cs:170`
  `private T ReadAdapter<T>(nint deviceHandle, delegate*<IDXGIAdapter1*, ID3D12Device*, T> read) {`,
  `:172` `var factory = DxgiInterop.CreateFactory();`): fine at device creation,
  wrong for a budget read on an event or a timer, which needs an `IDXGIAdapter3`
  held for the device's life. Vulkan's optional extension lists
  (`src/Puck.Vulkan/Factories/VulkanLogicalDeviceFactory.cs:55`-`72`: external
  memory, external semaphore, present timing) have no memory-budget entry.
- **The device's memory is a size read once.** `GpuMemoryProfile`
  (`src/Puck.Abstractions/Gpu/Residency/GpuMemoryProfile.cs:27`) holds
  `DeviceLocalBytes` from `DXGI_ADAPTER_DESC1`
  (`:73` `public static GpuMemoryProfile FromDirectX(bool unifiedMemory, bool cacheCoherentUnifiedMemory, ulong dedicatedVideoMemory, ulong sharedSystemMemory, bool gpuUploadHeapSupported) {`)
  or the sum of device-local Vulkan heaps
  (`:124` `if (!IsDeviceLocalHeap(heap: heap, memoryHeaps: memoryHeaps)) {`).
  Inference from section 2: on the Deck that sum is RADV's two-thirds
  visible-VRAM heap, neither the GPU's reach nor the machine's memory.
- **Two independent quarter shares of one pool.** The mesh region:
  `src/Puck.SdfVm/SdfWorldTables.MeshRegion.cs:217`
  `public const ulong MeshRegionDeviceLocalShare = 4UL;`, applied at `:228`
  `value: profile.DeviceLocalShare(divisor: MeshRegionDeviceLocalShare, unreportedBytes: MostMeshRegionBytes)`
  and fixed at construction (`src/Puck.SdfVm/SdfWorldTables.cs:205`
  `m_meshArena = new SdfMeshRegionArena(limitBytes: MeshRegionLimitOf(profile: device.MemoryProfile));`).
  The shader pipeline's budget file held `public const ulong DeviceLocalShare = 4UL;`
  (since replaced by a reservation in the account), read at
  `src/Puck.Shaders/ShaderPipelineRenderNode.Budget.cs:67`
  `public ulong DeviceBudgetBytes => ShaderPipelineMemoryBudget.For(profile: m_device.MemoryProfile);`.
  Neither knows of the other, of view targets, of the lighting scene or of other
  processes. This is the duplication step 2 removes: two holders computing their
  own share of one pool.
- **The account sees host bytes only.** `src/Puck.Hosting/Jobs/MemoryAccount.cs:58`
  `public long DefaultBudget { get; } = Math.Max(val1: 0, val2: (((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) * 3) / 4));`,
  `:60` headroom of installed / 32 clamped to 16-512 MiB, `:63`
  `public long CacheBudget => Math.Clamp((DefaultBudget / 12), (16L << 20), (512L << 20));`.
  Admission (`:159`
  `var spare = Math.Max(val1: 0, val2: ((((m_account.m_budget ?? m_account.DefaultBudget) - m_account.Headroom) - m_account.m_bytes.Sum()) - m_account.m_reserved));`,
  `:161` pacing by the caller's `available`) is sound and is the door to extend.
  Its owners today are mesh payloads, bake workspaces and store decodes
  (`CreateOwner` at `src/Puck.SdfVm/SdfMesh.Storage.cs:190`,
  `SdfMeshPathBake.cs:2396`, `SdfMeshBakeGpuWork.cs:149`, `SdfMeshStore.cs:188`,
  `SdfMeshStoreCodec.cs:93`). Trim listeners exist for the mesh region
  (`SdfWorldTables.MeshRegion.cs:307`), the mesh library (`SdfMeshLibrary.cs:322`)
  and the irradiance scene (`SdfMeshIrradiance.cs:1359`), so a lowered budget
  already reaches them.
- **The GPU ledger exists and maps onto the segment groups.**
  `src/Puck.Abstractions/Gpu/Counters/GpuDeviceMemoryWork.cs:32` counts
  device-local bytes (aperture as a subset) and disjoint host-visible bytes at
  the driver's allocation size; "Swapchain images, which the presentation engine
  allocates, never reach here." (`:18`). Its device-local row corresponds to DXGI
  LOCAL and the Vulkan device-local heaps on a discrete card, its host-visible
  row to NON_LOCAL and the host heaps; on UMA both are one pool, which
  `host.memory` says it cannot total (`src/Puck.World/HostMemoryCommandModule.cs:48`:
  "unified physical total is unavailable").
- **Host readings.** `src/Puck.Hosting/HostMemory.cs:49`
  `public static long? AvailablePhysicalBytes() {` (Windows memory status, Linux
  `MemAvailable:`), `:63` installed, `:80` commit charge and limit.
- **GC configuration.** The World runs workstation GC with background
  collections: `src/Puck.World/Puck.World.csproj` sets no
  `ServerGarbageCollection`, and `Directory.Build.targets:77`-`79` set server GC
  only for `PuckKind` Test. It sets
  `src/Puck.World/Puck.World.csproj:26`
  `<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5" />`
  and no heap hard limit. Workstation is the right flavour for a client on a
  4c/8t handheld; DATAS, a server-GC feature, does not apply.

## 8. What MemoryAccount should be

### 8.1 What it reads

One reading, refreshed together, that admission works from:

1. **Device budget and usage per physical pool**, from the backend: Direct3D 12
   `QueryVideoMemoryInfo` for LOCAL and NON_LOCAL on an `IDXGIAdapter3` held for
   the device's life; Vulkan `VK_EXT_memory_budget` per heap, enabled whenever
   the device lists it, folded into pools by heap flags and the profile
   (device-local heaps into the device pool on a discrete card; every heap into
   the one pool on a unified device, because RADV's split is not physical).
   Without the extension: heap size as budget and Puck's ledger as usage, as
   DXVK and VMA fall back, flagged in the reading so a report shows it.
2. **Puck's own ledger** (`GpuDeviceMemoryWork`): usage between refreshes is the
   fetched usage plus the ledger's change since, as D3D12MA and DXVK do, so an
   allocation never waits on a driver query.
3. **Host memory**: `HostMemory.AvailablePhysicalBytes`, installed bytes, commit
   charge, the committed managed heap (`GCMemoryInfo.TotalCommittedBytes`) and
   the native allocator's committed bytes; on Linux the process total from
   `/proc/self/smaps_rollup` (Windows already reads private bytes in `host.memory`).
4. **An independent device reading for attribution and checks, not
   admission**: Windows `GPU Process Memory` counters; Linux DRM fdinfo
   (`drm-resident-vram`, `drm-resident-gtt`) on amdgpu; something equivalent on
   NVIDIA Linux (open question 6). The plan's "two independent sources each" is
   met by the API's usage against the OS counter, with Puck's ledger as a third.

### 8.2 The split

Fixed fractions are what Puck has now, and they are the wrong shape: two quarter
shares that ignore each other and everything else. Shipped practice splits the
same way across sources: a floor for what must be resident (Microsoft's
reservation advice; Nanite's fixed pool sized for "all geometry required for
the current view") and an elastic pool for what streams (Unreal's texture pool
derived from the budget). Recommendation:

- **Pools by physical memory.** A discrete card has a device pool (LOCAL, VRAM)
  and a host pool (installed memory, which NON_LOCAL staging also spends). A
  unified device has one pool. `GpuMemoryProfile.UnifiedMemory` decides which
  pool a device byte charges. The managed heap and native allocations always
  charge the host pool.
- **Owners reserve from the account; nobody computes a share.** Classes, in
  admission order:
  1. *Fixed by the views* (view targets, cascades, screens, the shader
     pipeline's graph, swapchain-sized targets): charged Live at actual size; the
     shader pipeline's replacement peak becomes a `TryReserve` against the device
     pool in place of `DeviceLocalShare = 4UL`. A refused reservation keeps the
     installed graph running, as today.
  2. *Bounded transfer* (upload rings and staging, the ledger's host-visible
     row): a reservation sized by the landing rate, charged to the host pool on a
     discrete card and to the one pool on UMA.
  3. *Elastic presentation* (mesh region, lighting trace scene and probe cache):
     each holds a floor, its coarsest admitted state (the coarsest level within
     the far distance, a probe window at the least share), and grows into what
     classes 1 and 2 and the floors leave, by the selector's priorities. The mesh
     region's limit stops being a quarter of `DeviceLocalBytes` fixed at
     construction and becomes this remainder, still capped by
     `GpuRegion.MaxByteCount`; `SdfMeshTier.BudgetEighths` keeps working inside it.
  4. *Caches* (`Cached` category): spend what is left and are trimmed first, as
     `TrimCaches` already does.
  5. *Managed heap*: measured, never reserved. Its committed bytes are an owner
     row read from `GCMemoryInfo`, so the elastic classes shrink when it grows.
     No `GCHeapHardLimit` in normal runs: a hard limit turns pressure into an
     out-of-memory exception where the rest of the account turns it into coarser
     presentation (inference from "the maximum commit size"). A hypothesis worth
     one Deck measurement: a generous hard limit set through `AppContext.SetData`
     and `GC.RefreshMemoryLimit()` as a backstop when the pool is small.
- **The floors are the DXGI reservation.** Class 1, class 2 and the class 3
  floors sum to the minimum working set: pass it to
  `SetVideoMemoryReservation(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, floor)`
  (Microsoft: the "low" settings "or something even lower"). Vulkan has no
  reservation; where `VK_EXT_memory_priority` exists, class 1 allocates high and
  classes 3 and 4 low.
- **Spend margin.** Admit against `budget - max(10% of budget, headroom)`. The
  sources disagree (80% of VRAM, 90% of budget, "probably safe up to the
  limit"); 90% of the OS budget is the better starting rule because it is a
  share of what the OS grants rather than of raw VRAM, and step 3's fixed-stop
  readings move it.

### 8.3 Reacting to a change

- **Windows:** register the budget-change event at device creation and wait on
  it on a host thread, a real wait as "Puck plays nice with its host" requires;
  the event fires once at registration, which gives the first reading. On a
  signal, re-query and publish a new reading.
- **Vulkan:** no notification exists. Refresh at a low rate on a host timer wait
  (AMD advises against per-frame queries), immediately on any refused allocation
  or reservation, and between refreshes correct usage by the ledger.
- **On a drop:** the account lowers its budget through the existing `SetBudget`
  door (which already trims caches), then elastic owners shrink in priority
  order: caches, then the mesh region's limit (the region already coarsens under
  its budget through `SdfMeshTier`'s bias), then the lighting bounds, then
  eviction by priority (campaign step 5). A refused device allocation is the same
  signal (lower the limit to what is held), never a World-ending throw; that
  closes the open item on `GpuRegion.Grow` from
  `SdfMeshRegionStore.EnsureCapacity`.
- **On a rise:** restore after a quiet period with hysteresis (the tier bias
  already has `SdfMeshTier.BiasQuietFrames`), so an alt-tab does not churn bakes.
- Every change of reading is counted and printed by `host.memory`, so one window
  shows budget, usage and the response together.

### 8.4 What must differ on the Deck

- **One pool, two ceilings.** The pool's budget is the smaller of (a) the sum of
  every RADV heap's `heapBudget` (GPU reach: carve-out plus GTT, system-wide) and
  (b) `MemAvailable` plus what Puck itself holds, CPU and GPU, less headroom.
  (b) assumes GTT pages count against `MemAvailable` (hypothesis); the first Deck
  reading checks it with fdinfo `drm-resident-gtt` against `MemAvailable` before
  and after a large allocation.
- **Never read the device-local heaps alone.** `GpuMemoryProfile.DeviceLocalBytes`
  on RADV is the fake two-thirds heap (inference from section 2), so every share
  derived from it (the mesh region and the shader pipeline today) is wrong on
  the Deck; the account's pool replaces those shares.
- **No overcommit.** Over the GTT limit is failure, not stutter (inference from
  section 6), so reserving before allocating is mandatory there and the floors
  must fit.
- **Managed and native bytes compete with the GPU directly.** The managed-heap
  row matters most here; the GC's own 90% memory-load trigger reacts to GPU
  pressure too (inference: GTT pages are physical load).
- **No staging copy.** `GpuMemoryProfile`'s own documentation says the profile
  is what `GpuResidency.Select` chooses a region's policy from and that on
  unified memory `GpuResidency.RingMemory` places a ring in host memory
  (`GpuMemoryProfile.cs:9`-`11`, `:24`-`26`); the transfer class should then be
  near zero on the Deck, which is a check, not an assumption.
- **The 10 GB target stays a qualification, not the budget.** The budget comes
  from the readings; the debug world is qualified against 10 GB and against the
  reading, as the plan says.

### 8.5 The invariant and what enforces it

Invariant: no presentation byte is allocated on a pool without an admitted
charge in the one account, and the admitted total plus the measured unadmitted
rows (managed heap, driver, swapchain) stays under the pool's spend limit.
Enforcement today: none for device bytes. The door that can enforce it is the
allocation path both backends already route counted allocations through (where
`GpuDeviceMemoryWork.CountAllocated` is called): an allocation there takes an
owner's reservation handle, so a caller without one cannot allocate. Until that
door exists the invariant is reporting only, and the build lane should say so.
Where it stops: driver memory, pipelines, swapchain images and other processes
are measured, never admitted.

### 8.6 Mechanical checks for the build step

- `host.memory` prints per pool: budget, usage (API), usage (ledger), usage (OS
  counter), spend limit, reservation. On the lead machine API usage and the OS
  counter agree with the ledger up to driver and swapchain memory, on both
  backends.
- Direct3D 12 and Vulkan on the same RTX card report the LOCAL budget and the
  device-local heap budget within a stated tolerance (an independent pair).
- A budget change is observed: start a second GPU-heavy process, read the
  budget-change count and the new budget.
- `host.memory budget <bytes>` and a synthetic lowered device budget make the
  mesh region coarsen (`detail-bias`, `region-held` falls) without a refusal or
  a throw.
- The shader pipeline's refusal (`SHADERPIPE_BUDGET`) still fires when its
  reservation fails, now from the account.

## 9. The five decisions the build step must make

1. **Where the device reading lives and what it is.** Recommend a backend-neutral
   reading type in `Puck.Abstractions` beside `GpuMemoryProfile` (budget and usage
   per physical pool, plus a flag for "estimated without the extension"),
   produced by the device: `IDXGIAdapter3` held for the device's life,
   `VK_EXT_memory_budget` enabled when listed. Not a second account.
2. **Pools.** Recommend `MemoryAccount` gains pools keyed by physical memory:
   device and host on a discrete card, one on unified memory, chosen by
   `GpuMemoryProfile.UnifiedMemory`; every owner charges the pool its bytes live
   in. The account stays the single authority (the residency decision's single
   physical byte-accounting owner).
3. **The spend limit.** Recommend `min(reported budget × 0.9, budget − headroom)`
   per device pool; the host pool keeps today's installed-memory rule paced by
   available memory; on the Deck the smaller of the summed RADV heap budgets and
   available-plus-own. 0.9 is a starting value that step 3's readings move.
4. **The split.** Recommend reservations with floors and priority classes
   (fixed by views, transfer, elastic presentation, caches, measured managed
   heap) in place of every fixed share: delete `MeshRegionDeviceLocalShare`,
   `ShaderPipelineMemoryBudget.DeviceLocalShare` and the
   device-local share helper on `GpuMemoryProfile` they share, and pass the floor sum
   to `SetVideoMemoryReservation`.
5. **The reaction.** Recommend: on Windows the budget event on a host wait
   thread; on Vulkan a low-rate host-timer refresh plus a refresh on refusal; a
   drop goes through `SetBudget` to trims, region coarsening, lighting bounds and
   then eviction; a refused allocation is a budget signal, never a throw; a rise
   restores after a quiet period.

## 10. Open questions, each with an owner

1. **The Deck's numbers** (RADV heap sizes and budgets, GTT size on SteamOS's
   kernel, `MemAvailable` with the Steam client running, whether GTT counts
   against `MemAvailable`): a Deck reading by a lane with the hardware; decides
   the pool formula in 8.4. No Deck reading exists yet.
2. **DXGI against Vulkan on one RTX card** (does NVIDIA's Windows Vulkan budget
   track the WDDM budget?): a bounded measurement the build lane can run on the
   lead machine with a GPU grant; decides whether one margin fits both backends.
3. **NVIDIA Linux over budget** (failure, eviction or the desktop crash users
   report): a Linux boot of an RTX machine; decides whether Linux discrete needs
   reserve-before-allocate as strictly as the Deck.
4. **Priority extensions on the target drivers** (`VK_EXT_memory_priority`,
   `VK_EXT_pageable_device_local_memory` on NVIDIA Windows, NVIDIA Linux and RADV):
   `vulkaninfo` on each; decides whether priority classes are passed down.
5. **The margin** (0.8, 0.9 or the full budget): step 3's fixed-stop readings
   with the budget-change counter; decides the constant in decision 3.
6. **Per-process GPU usage on NVIDIA Linux** for the second reading (NVML, or
   DRM fdinfo from the open kernel module): not researched here; the build lane
   checks what the open module prints.
7. **Swapchain and driver memory**, in DXGI `CurrentUsage` but not in the
   ledger: step 3's attribution names the gap per backend.
