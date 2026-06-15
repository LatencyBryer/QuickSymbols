using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Ipc;

namespace QuickSymbols;

public sealed unsafe partial class Plugin
{
    // IPC names | Other plugins can use these to open Quick Symbols and receive the selected symbol themselves.
    private const int IpcVersion = 2;
    private const string IpcGetVersion = "QuickSymbols.GetVersion";
    private const string IpcOpenPicker = "QuickSymbols.OpenPicker";
    private const string IpcClosePicker = "QuickSymbols.ClosePicker";
    private const string IpcGetHotkey = "QuickSymbols.GetHotkey";
    private const string IpcSymbolSelected = "QuickSymbols.SymbolSelected";

    // IPC providers | Keeps third-party integrations opt-in and lets the other plugin own its own input/caret state.
    private readonly ICallGateProvider<int> ipcGetVersion;
    private readonly ICallGateProvider<string, bool> ipcOpenPicker;
    private readonly ICallGateProvider<string, bool> ipcClosePicker;
    private readonly ICallGateProvider<int[]> ipcGetHotkey;
    private readonly ICallGateProvider<string, string, object?> ipcSymbolSelected;

    // IPC popup | Used when another plugin asks Quick Symbols to show the picker and handle the selected symbol itself.
    private bool ipcPopupOpen;
    private string? ipcPopupOwner;
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
    }

    private void UnregisterIpc()
    {
        this.ipcGetVersion.UnregisterFunc();
        this.ipcOpenPicker.UnregisterFunc();
        this.ipcClosePicker.UnregisterFunc();
        this.ipcGetHotkey.UnregisterFunc();
    }


    private int[] GetHotkeyFromIpc()
    {
        this.ConfigChanged();
        return this.Config.ToggleHotkey.Select(key => (int)key).ToArray();
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
