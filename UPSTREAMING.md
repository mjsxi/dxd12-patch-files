# Upstreaming DX12 Proton Compatibility to Reloaded.ImGui.Hooks

Notes from patching Faith Framework's `NenTools.ImGui.Hooks.DirectX12.dll` for
FFXVI under Proton. Goal: bring these fixes into
[`Reloaded.Hooks.ImGui`](https://github.com/Sewer56/Reloaded.Hooks.ImGui) (or
whichever repo sewer56 designates).

Source based on
[`Nenkai/NenTools.ImGui`](https://github.com/Nenkai/NenTools.ImGui) commit
`507e3f3`.

---

## Problem statement

The stock DX12 ImGui backend renders nothing (or crashes) under Proton/Wine.
It works on native Windows. The failures come from assumptions that hold on
Windows but break under Wine's DX12 translation layer, and from a few
pre-existing bugs that are masked on Windows by driver leniency.

---

## Root causes and fixes

### 1. Swapchain vtable replacement is fragile under Proton

**Problem.** The original code replaced the live swapchain object's entire
vtable. Under Proton, the swapchain pointer the game sees is a Wine wrapper,
not the native D3D12 swapchain. Replacing the wrapper's vtable breaks Wine's
internal dispatch. Frame-generation middleware (FSR FG, Streamline) inserts
another wrapper layer, making it worse.

**Fix.** Hook the shared vtable *entry points* (the function pointers
themselves) via `FunctionPointerHook`, rather than replacing any object's
vtable. This intercepts all swapchain/queue instances regardless of wrapper
depth, and doesn't fight Wine's object model.

```
Before: swapchain->vtable[Present] = &OurPresent   // per-object, breaks wrappers
After:  hook the address stored in vtable[Present]  // global, wrapper-agnostic
```

**Upstream impact.** This is a design-level change. sewer56 should weigh in on
whether entry-point hooking fits Reloaded.Hooks' architecture, or whether a
hybrid (per-object on Windows, entry-point under Proton) is preferable.

### 2. Command queue discovery fails under Proton

**Problem.** The stock code reads the command queue from a hardcoded offset
inside the swapchain object. Wine's swapchain wrapper has a different internal
layout, so the offset is wrong and the queue pointer is garbage.

**Fix.** At init time, create a dummy device + swapchain + queue, then scan
the swapchain's memory for the queue pointer:

- **Direct scan:** walk pointer-sized slots (up to 512) looking for the queue
  pointer directly inside the swapchain object.
- **Indirect scan:** if not found, follow each pointer-sized slot one hop
  (to handle Wine's wrapper → native swapchain indirection), then scan the
  inner object for the queue pointer.

All reads are guarded with `VirtualQuery` — we check that the page is
committed and not `PAGE_NOACCESS` / `PAGE_GUARD` before dereferencing. This
prevents access violations when scanning past the end of the object.

The result is a `QueueLocation(IsIndirect, ContainerOffset, QueueOffset)`
record learned from the dummy objects, then applied to the live swapchain at
runtime.

At Present time, the resolved queue is validated:
- Must be a `D3D12_COMMAND_LIST_TYPE_DIRECT` queue.
- The queue's `ID3D12Device` and the swapchain's `ID3D12Device` must be the
  same COM identity (checked via `IUnknown` pointer equality through
  `QueryInterface`).
- The queue pointer must have been previously observed in
  `ExecuteCommandLists` calls from the game (prevents hooking editor or
  overlay queues).

**Upstream impact.** This is the core novel contribution. The scan + validation
logic is self-contained and could be extracted into a reusable
`QueueDiscovery` class.

### 3. GPU use-after-free in texture upload path

**Problem.** `UpdateTexture` disposed the upload buffer and reset the command
allocator immediately after `ExecuteCommandList`, without waiting for the GPU
fence. The GPU could still be reading the upload buffer or executing commands
from the allocator. This is invalid per D3D12 spec and can cause device
removal under load.

The `LoadTexture` path did this correctly (created an event, called
`SetEventOnCompletion` + `WaitForSingleObject`), but `UpdateTexture` called
`SetEventOnCompletion` without an event handle, which is a no-op.

**Fix.** Added a persistent `SafeFileHandle` event to the texture upload path.
`UpdateTexture` now signals the fence, waits on the event, then resets the
event before disposing the upload buffer or resetting the allocator.

**Upstream impact.** Any DX12 texture upload/update path in Reloaded should be
audited for the same pattern. The rule: never dispose a resource or reset a
command allocator until the fence confirms the GPU is done with it.

### 4. COM reference discipline

**Problem.** The original code wrapped native pointers in Vortice COM objects
without calling `AddRef`, then `Dispose`d them (which calls `Release`). This
steals a reference the caller still owns, leading to use-after-free when the
game later releases the same object.

**Fix.** `WrapWithReference<T>(nint)` calls `Marshal.AddRef` before
constructing the Vortice wrapper, so `Dispose` balances correctly. All
borrowed pointers (swapchain from Present, queue from discovery) go through
this helper.

**Upstream impact.** Every place Reloaded wraps a native COM pointer it
doesn't own needs the same AddRef discipline.

### 5. Descriptor heap mismatch (Faith-specific, but instructive)

**Problem.** Faith allocated ImGui's font texture descriptor from a separate
2048-entry heap, but bound an 11-entry rendering heap on the command list.
Every ImGui draw call sampled an unbound descriptor → invisible UI.

**Fix.** Use a single shader-visible heap for both ImGui's allocation
callbacks and command-list binding.

**Upstream impact.** Probably not relevant to Reloaded directly, but worth
documenting as a "common DX12 ImGui integration mistake."

### 6. Integer overflow in frame context indexing

**Problem.** `_commandContextIndex++ % count` overflows to negative after
~2.1 billion frames (~10 hours at 60fps). C# `%` on negative ints returns
negative → `IndexOutOfRangeException`.

**Fix.** Cast through `uint` before modulo:
```csharp
(int)((uint)_commandContextIndex++ % (uint)_commandContexts.Count)
```

### 7. Minor fixes

| Fix | Detail |
|-----|--------|
| `LoadTexture` Map failure | Empty `if` block → null-pointer write. Now throws with cleanup. |
| SRV callback null guard | `_textureHeapAllocator` can be null after teardown; native callbacks would crash. Added `?.`. |
| `CommandContext.Setup` partial leak | Failed creation steps leaked earlier resources. Now calls `Reset()` on failure. |
| `CommandContext.Reset` stale state | `HasCommands` wasn't cleared. |
| Dummy window leak | `CreateHandleWithDummyWindow` never called `DestroyWindow`. Added `finally`. |
| `_textureHeapAllocator` was `static` | Made instance field; static state breaks teardown ordering. |
| Typo `ComamndQueueVTable` | → `CommandQueueVTable` (public API). |
| Pitch alignment parentheses | Correct but misleading precedence; added explicit parens. |
| Unused variable / usings | Removed `props` in `InitD3D12`, dead usings in `CommandContext.cs`. |
| Logging consistency | `Console.WriteLine` → `DebugLog.WriteLine` in `CommandContext`. |

---

## Suggested upstream PR sequence

1. **Queue discovery module** — extract `FindQueueLocation`,
   `TryResolveCommandQueue`, `TryReadPointer`, `IsReadable`,
   `LooksLikeComObject`, `QueueLocation` into a standalone class. This is the
   highest-value, easiest-to-review piece. No design decisions required.

2. **COM reference discipline** — `WrapWithReference`, `SameComIdentity`,
   AddRef/Release audit. Small, mechanical, obviously correct.

3. **Fence synchronization audit** — fix any upload/update paths that dispose
   resources before the fence completes. Small, obviously correct.

4. **Entry-point hooking** — the design-level change. Open an issue first,
   get sewer56's take on whether this replaces or supplements per-object
   vtable hooks.

5. **Frame context overflow + minor fixes** — batch into a cleanup PR.

---

## Open questions for sewer56

- Does Reloaded.Hooks already have guarded memory read utilities, or should
  the `VirtualQuery`-based `IsReadable` / `TryReadPointer` be contributed?
- Is there an existing abstraction for "learn an object layout at runtime by
  scanning a dummy instance"? The queue discovery pattern generalizes beyond
  DX12 (e.g., finding the device pointer inside a swapchain wrapper).
- Should Proton detection be explicit (check for `winevulkan.dll` /
  `ntdll` wine markers) or implicit (try direct scan, fall back to indirect)?
  We went implicit — it works on both Windows and Proton without branching.
- Reloaded.Hooks.ImGui targets a specific ImGui version. The descriptor
  callback wiring (`ImGui_ImplDX12_InitInfo_t`) changed in ImGui 1.92. Which
  version is upstream tracking?

---

## Files changed in this patch

- `src/Dx12BackendHook.cs` — all fixes above
- `src/CommandContext.cs` — setup/teardown robustness, logging, unused usings
