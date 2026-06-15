using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Ipc;

namespace QuickSymbols;

public sealed unsafe partial class Plugin
{
    // IPC names | Other plugins can use these to open Quick Symbols and receive the selected symbol themselves.
    private const int IpcVersion = 3;
    private const string IpcGetVersion = "QuickSymbols.GetVersion";
    private const string IpcOpenPicker = "QuickSymbols.OpenPicker";
    private const string IpcClosePicker = "QuickSymbols.ClosePicker";
    private const string IpcGetHotkey = "QuickSymbols.GetHotkey";
    private const string IpcWatchInput = "QuickSymbols.WatchInput";
    private const string IpcSymbolSelected = "QuickSymbols.SymbolSelected";
    private const int IpcWatchFrames = 18;

    // IPC providers | Keeps third-party integrations opt-in and lets the other plugin own its own input/caret state.
    private readonly ICallGateProvider<int> ipcGetVersion;
    private readonly ICallGateProvider<string, bool> ipcOpenPicker;
    private readonly ICallGateProvider<string, bool> ipcClosePicker;
    private readonly ICallGateProvider<int[]> ipcGetHotkey;
    private readonly ICallGateProvider<string, bool> ipcWatchInput;
    private readonly ICallGateProvider<string, string, object?> ipcSymbolSelected;

    // IPC popup | Used when another plugin asks Quick Symbols to show the picker and handle the selected symbol itself.
    private bool ipcPopupOpen;
    private bool ipcHotkeyWasDown;
    private string? ipcPopupOwner;
    private string? ipcActiveOwner;
    private int ipcActiveFrames;
    private Vector2 ipcPopupAnchorPos;
    private Vector2 ipcPopupAnchorSize;
    private int ipcPopupRaiseFrames;
    // #

    private void RegisterIpc()
    {
        this.ipcGetVersion.RegisterFunc(() => IpcVersion);
        this.ipcOpenPicker.RegisterFunc(this.OpenPickerFromIpc);
        this.ipcClosePicker.RegisterFunc(this.ClosePickerFromIpc);
        this.ipcGetHotkey.RegisterFunc(this.GetHotkeyFromIpc);
        this.ipcWatchInput.RegisterFunc(this.WatchInputFromIpc);
    }

    private void UnregisterIpc()
    {
        this.ipcGetVersion.UnregisterFunc();
        this.ipcOpenPicker.UnregisterFunc();
        this.ipcClosePicker.UnregisterFunc();
        this.ipcGetHotkey.UnregisterFunc();
        this.ipcWatchInput.UnregisterFunc();
    }

    private int[] GetHotkeyFromIpc()
    {
        this.ConfigChanged();
        return this.Config.ToggleHotkey.Select(key => (int)key).ToArray();
    }

    private bool WatchInputFromIpc(string owner)
    {
        owner = owner.Trim();
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        this.ipcActiveOwner = owner;
        this.ipcActiveFrames = IpcWatchFrames;
        return true;
    }

    private bool TryOpenWatchedInputPopup()
    {
        if (this.ipcActiveFrames > 0)
        {
            this.ipcActiveFrames--;
        }
        else
        {
            this.ipcActiveOwner = null;
            this.ipcHotkeyWasDown = false;
        }

        var owner = this.ipcActiveOwner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        if (!this.CheckIpcHotkey(this.Config.ToggleHotkey))
        {
            return false;
        }

        if (this.ipcPopupOpen && this.ipcPopupOwner == owner)
        {
            this.ipcPopupOpen = false;
            this.ipcPopupOwner = null;
            this.ipcPopupRaiseFrames = 0;
            return true;
        }

        return this.OpenPickerFromIpc(owner);
    }

    private bool CheckIpcHotkey(VirtualKey[] keys)
    {
        this.CheckHotkeyEditorSafety();
        if (this.hotkeyRecording || keys.Length == 0)
        {
            this.ipcHotkeyWasDown = false;
            return false;
        }

        foreach (var key in keys)
        {
            if (!IsIpcKeyDown(key))
            {
                this.ipcHotkeyWasDown = false;
                return false;
            }
        }

        if (this.ipcHotkeyWasDown)
        {
            return false;
        }

        this.ipcHotkeyWasDown = true;
        foreach (var key in keys)
        {
            TryClearKey(key);
        }

        return true;
    }

    private static bool IsIpcKeyDown(VirtualKey key)
    {
        var value = (int)key;
        return value switch
        {
            0x10 => AnyKeyDown(0x10, 0xA0, 0xA1),
            0x11 => AnyKeyDown(0x11, 0xA2, 0xA3),
            0x12 => AnyKeyDown(0x12, 0xA4, 0xA5),
            _ => IsKeyDown(value),
        };
    }

    private static bool AnyKeyDown(params int[] keys)
    {
        foreach (var key in keys)
        {
            if (IsKeyDown(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKeyDown(int key)
    {
        if (IsDalamudKeyDown(key))
        {
            return true;
        }

        try
        {
            return (GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDalamudKeyDown(int key)
    {
        try
        {
            var virtualKey = (VirtualKey)key;
            return KeyState.IsVirtualKeyValid(virtualKey) && KeyState[virtualKey];
        }
        catch
        {
            return false;
        }
    }

    private static void TryClearKey(VirtualKey key)
    {
        try
        {
            if (KeyState.IsVirtualKeyValid(key))
            {
                KeyState[key] = false;
            }
        }
        catch
        {
            // QuickSymbols may be reading raw key state for plugin-owned ImGui inputs.
        }
    }

    private bool OpenPickerFromIpc(string owner)
    {
        owner = owner.Trim();
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var mousePos = ImGui.GetIO().MousePos;
        var displaySize = ImGui.GetIO().DisplaySize;
        if (mousePos.X < 0f || mousePos.Y < 0f || mousePos.X > displaySize.X || mousePos.Y > displaySize.Y)
        {
            mousePos = displaySize * 0.5f;
        }

        this.CloseAllPopups(clearKeybindTarget: true);
        this.selectedPopupTab = PopupTab.Symbols;
        this.ipcPopupOwner = owner;
        this.ipcPopupOpen = true;
        this.ipcPopupAnchorSize = new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));
        this.ipcPopupAnchorPos = ClampPositionToScreen(mousePos + new Vector2(12f * scale, 12f * scale), this.ipcPopupAnchorSize);
        this.ipcPopupRaiseFrames = 12;
        this.popupClickGuardFrames = 2;
        return true;
    }

    private bool ClosePickerFromIpc(string owner)
    {
        owner = owner.Trim();
        if (string.IsNullOrWhiteSpace(owner) || this.ipcPopupOwner != owner)
        {
            return false;
        }

        this.ipcPopupOpen = false;
        this.ipcPopupOwner = null;
        this.ipcPopupRaiseFrames = 0;
        return true;
    }

    private void SendSymbolToIpcOwner(string symbol)
    {
        var owner = this.ipcPopupOwner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            this.ipcPopupOpen = false;
            this.ipcPopupOwner = null;
            this.ipcPopupRaiseFrames = 0;
            return;
        }

        try
        {
            this.ipcSymbolSelected.SendMessage(owner, symbol);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to send Quick Symbols IPC selection for {owner}.");
            this.ipcPopupOpen = false;
            this.ipcPopupOwner = null;
            this.ipcPopupRaiseFrames = 0;
        }
    }
}
