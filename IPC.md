# QuickSymbols IPC Documentation

> Integration guide for plugins that want to use the QuickSymbols picker with their own ImGui text inputs.

## Overview

Exposes a small IPC surface so other plugins can open the symbol picker and receive the symbol selected by the user.

This is intended for plugin-owned ImGui inputs where QuickSymbols should **not** write into the input directly. The consumer plugin remains responsible for inserting the selected text and maintaining its own focus, selection, and caret state.

## IPC Version

| Field | Value |
| --- | --- |
| Current IPC version | `1` |

Use `QuickSymbols.GetVersion` if your plugin needs to check compatibility before using the IPC.

## Endpoint Summary

| Endpoint | Type | Purpose |
| --- | --- | --- |
| `QuickSymbols.GetVersion` | Function | Returns the current QuickSymbols IPC version. |
| `QuickSymbols.GetHotkey` | Function | Returns the current picker hotkey configured by the user. |
| `QuickSymbols.OpenPicker` | Function | Opens the QuickSymbols picker for a specific owner key. |
| `QuickSymbols.ClosePicker` | Function | Closes the picker for a specific owner key. |
| `QuickSymbols.SymbolSelected` | Event | Notifies subscribers when the user selects a symbol. |

---

## Endpoints

### `QuickSymbols.GetVersion`

Returns the current QuickSymbols IPC version.

```csharp
var getVersion = pluginInterface.GetIpcSubscriber<int>("QuickSymbols.GetVersion");
var version = getVersion.InvokeFunc();
```

---

### `QuickSymbols.GetHotkey`

Returns the current QuickSymbols picker hotkey configured by the user.

This lets your plugin detect the same keybind the user already configured in QuickSymbols instead of creating a separate plugin-specific keybind.

```csharp
var getHotkey = pluginInterface.GetIpcSubscriber<int[]>("QuickSymbols.GetHotkey");
var hotkey = getHotkey.InvokeFunc();
```

The returned values are the configured `VirtualKey` values as `int`.

```csharp
var keys = getHotkey.InvokeFunc();

if (keys.Length == 0)
    return;

// Compare the returned key values against your input/key state logic.
```

---

### `QuickSymbols.OpenPicker`

Opens the QuickSymbols picker for a specific owner key.

The owner key is used so the receiving plugin can identify whether a selected symbol belongs to its own request.

```csharp
var openPicker = pluginInterface.GetIpcSubscriber<string, bool>("QuickSymbols.OpenPicker");
var opened = openPicker.InvokeFunc("ExamplePlugin");
```

Returns `true` when QuickSymbols accepted the request.

The picker may stay open after a symbol is selected, allowing the user to insert multiple symbols in a row. The consumer plugin can call `QuickSymbols.ClosePicker` when it wants to close it.

---

### `QuickSymbols.ClosePicker`

Closes the picker if it is currently open for that owner key.

```csharp
var closePicker = pluginInterface.GetIpcSubscriber<string, bool>("QuickSymbols.ClosePicker");
var closed = closePicker.InvokeFunc("ExamplePlugin");
```

Returns `true` when the picker was closed.

---

### `QuickSymbols.SymbolSelected`

Subscribe to this event to receive the selected symbol.

| Argument | Type | Description |
| --- | --- | --- |
| `owner` | `string` | The owner key passed to `OpenPicker`. |
| `symbol` | `string` | The selected symbol/text. |

```csharp
private ICallGateSubscriber<string, string, object?>? quickSymbolsSelected;
private Action<string, string>? onQuickSymbolsSelected;

quickSymbolsSelected = pluginInterface.GetIpcSubscriber<string, string, object?>("QuickSymbols.SymbolSelected");

onQuickSymbolsSelected = (owner, symbol) =>
{
    if (owner != "ExamplePlugin")
        return;

    // Insert into your own input here.
    // Your plugin keeps ownership of focus, selection and caret state.
};

quickSymbolsSelected.Subscribe(onQuickSymbolsSelected);
```

Remember to unsubscribe on dispose:

```csharp
if (quickSymbolsSelected != null && onQuickSymbolsSelected != null)
{
    quickSymbolsSelected.Unsubscribe(onQuickSymbolsSelected);
    onQuickSymbolsSelected = null;
}
```

---

## Recommended Integration Flow

1. Your plugin detects that its own text input is focused.
2. Your plugin reads the user's QuickSymbols hotkey with `QuickSymbols.GetHotkey`.
3. When that keybind is pressed, your plugin calls `QuickSymbols.OpenPicker("YourPluginName")`.
4. QuickSymbols shows the symbol picker.
5. The user clicks a symbol.
6. QuickSymbols sends `QuickSymbols.SymbolSelected("YourPluginName", symbol)`.
7. Your plugin inserts the symbol into its own input and updates its own caret state.
8. The picker can remain open so the user can insert more symbols.
9. Your plugin can call `QuickSymbols.ClosePicker("YourPluginName")` when the input is no longer active or when you want to close the picker.

---

## Consumer Plugin Responsibilities

- Do not rely on QuickSymbols to write into your ImGui input directly.
- Insert the received symbol into your own text buffer.
- Update your own caret/selection state after insertion.
- Use a unique owner key, for example `"CreateXIV"` or `"Clock"`.
- Wrap IPC calls in `try/catch` so your plugin still works when QuickSymbols is not installed or not loaded.
- If the picker should only be used while a specific input is active, close it when that input loses focus.

---

## Minimal Consumer Example

```csharp
private ICallGateSubscriber<int[]>? quickSymbolsHotkey;
private ICallGateSubscriber<string, bool>? quickSymbolsOpenPicker;
private ICallGateSubscriber<string, bool>? quickSymbolsClosePicker;
private ICallGateSubscriber<string, string, object?>? quickSymbolsSelected;
private Action<string, string>? quickSymbolsSelectedHandler;

private const string QuickSymbolsOwner = "ExamplePlugin";

private void RegisterQuickSymbolsIpc(IDalamudPluginInterface pluginInterface)
{
    quickSymbolsHotkey = pluginInterface.GetIpcSubscriber<int[]>("QuickSymbols.GetHotkey");
    quickSymbolsOpenPicker = pluginInterface.GetIpcSubscriber<string, bool>("QuickSymbols.OpenPicker");
    quickSymbolsClosePicker = pluginInterface.GetIpcSubscriber<string, bool>("QuickSymbols.ClosePicker");
    quickSymbolsSelected = pluginInterface.GetIpcSubscriber<string, string, object?>("QuickSymbols.SymbolSelected");

    quickSymbolsSelectedHandler = (owner, symbol) =>
    {
        if (owner != QuickSymbolsOwner)
            return;

        // Insert symbol into your plugin-owned input buffer here.
    };

    quickSymbolsSelected.Subscribe(quickSymbolsSelectedHandler);
}

private void OpenQuickSymbolsPicker()
{
    try
    {
        quickSymbolsOpenPicker?.InvokeFunc(QuickSymbolsOwner);
    }
    catch
    {
        // QuickSymbols is not available.
    }
}

private void DisposeQuickSymbolsIpc()
{
    if (quickSymbolsSelected != null && quickSymbolsSelectedHandler != null)
    {
        quickSymbolsSelected.Unsubscribe(quickSymbolsSelectedHandler);
        quickSymbolsSelectedHandler = null;
    }
}
```
