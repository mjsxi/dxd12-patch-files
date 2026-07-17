# FFXVI Faith Framework DX12 compatibility patch

This repository contains a Proton-focused replacement for Faith Framework's
`NenTools.ImGui.Hooks.DirectX12.dll`. It keeps Faith's public ImGui APIs and UI
components intact while replacing only the DirectX 12 renderer backend.

The source is based on
[`Nenkai/NenTools.ImGui`](https://github.com/Nenkai/NenTools.ImGui) commit
`507e3f32331c83b8d630cf0b684ec33a966b67e4` and remains under its MIT license.

## Repository layout

- `src/` contains the two modified NenTools.ImGui source files.
- `release/NenTools.ImGui.Hooks.DirectX12.dll` is the release-ready build.
- `LICENSE` contains the upstream MIT license.

## Publishing a patch

Create a GitHub release and attach this file as a release asset, without
renaming it:

```text
release/NenTools.ImGui.Hooks.DirectX12.dll
```

Unloaded-II reads the latest release from `mjsxi/dxd12-patch-files`, downloads
the asset named exactly `NenTools.ImGui.Hooks.DirectX12.dll`, caches it locally,
and reapplies it after Faith Framework installs or updates. The DLL is not
included in the universal Unloaded-II package.

## Compatibility changes

- avoids replacing the live swapchain object's complete virtual-function table;
- learns both native and Proton wrapper queue layouts using guarded memory reads;
- validates the direct command queue against observed game submissions and the
  swapchain's D3D12 device;
- owns borrowed COM references;
- waits for GPU fences before recycling or releasing render resources;
- rebuilds renderer resources after resize or swapchain replacement; and
- contains managed exceptions before they can cross native hook callbacks.

No other Faith Framework file is replaced.
