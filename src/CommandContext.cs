using System;

using Microsoft.Win32.SafeHandles;

using NenTools.ImGui.Hooks.DirectX;

using Vortice.Direct3D12;
using Windows.Win32;

#pragma warning disable CA1416 // This call site is reachable on all platforms. (method) is only supported on: 'windows' 5.1.2600 and later.

namespace NenTools.ImGui.Hooks.DirectX12;

internal class CommandContext
{
    public ID3D12CommandAllocator CommandAllocator { get; set; }
    public ID3D12GraphicsCommandList CommandList { get; set; }
    public ID3D12Fence Fence { get; set; }
    private SafeFileHandle? FenceEvent { get; set; }
    public ulong FenceValue { get; set; }
    public bool WaitingForFence { get; set; }
    public Lock Lock { get; private set; } = new();
    public bool HasCommands = false;

    public bool Setup(DX12BackendHook hook)
    {
        Reset();

        var device = hook.Device;

        try
        {
            CommandAllocator = device.CreateCommandAllocator(CommandListType.Direct);
            CommandAllocator.Name = $"[{nameof(DX12BackendHook)}] ImGui Command Allocator";

            CommandList = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, CommandAllocator);
            CommandList.Name = $"[{nameof(DX12BackendHook)}] ImGui CommandList";

            Fence = device.CreateFence(FenceValue, FenceFlags.None);
            Fence.Name = $"[{nameof(DX12BackendHook)}] ImGui Command Context Fence";

            FenceEvent = PInvoke.CreateEvent(null, false, false, (string)null);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"[{nameof(DX12BackendHook)}] Failed to create command context: {ex.Message}");
            Reset();
            return false;
        }
    }

    public void Wait(uint timeoutMilliseconds = 5000)
    {
        lock (Lock)
        {
            if (FenceEvent is not null && WaitingForFence)
            {
                if (Fence.CompletedValue < FenceValue)
                {
                    var result = PInvoke.WaitForSingleObject(FenceEvent, timeoutMilliseconds);
                    if ((uint)result != 0)
                        throw new TimeoutException("Timed out waiting for the D3D12 ImGui command list.");
                }

                PInvoke.ResetEvent(FenceEvent);
                WaitingForFence = false;
                CommandAllocator.Reset();
                CommandList.Reset(CommandAllocator, null);
                HasCommands = false;
            }
        }
    }

    public void Execute(ID3D12CommandQueue queue)
    {
        lock (Lock)
        {
            if (HasCommands)
            {
                CommandList.Close();
                queue.ExecuteCommandList(CommandList);
                queue.Signal(Fence, ++FenceValue);
                Fence.SetEventOnCompletion(FenceValue, FenceEvent.DangerousGetHandle());
                WaitingForFence = true;
                HasCommands = false;
            }
        }
    }

    public void Reset()
    {
        lock (Lock)
        {
            CommandAllocator?.Dispose();
            CommandList?.Dispose();
            Fence?.Dispose();
            FenceValue = 0;
            FenceEvent?.Dispose();
            FenceEvent = null;
            WaitingForFence = false;
            HasCommands = false;
        }
    }
}
