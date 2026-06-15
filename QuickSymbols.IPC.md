# QuickSymbols IPC

This is meant for plugin-owned ImGui inputs, where QuickSymbols should not try to write into the input directly. The other plugin stays responsible for inserting text and maintaining its own caret state.

## IPC Endpoints

### `QuickSymbols.GetVersion`

Returns the current QuickSymbols IPC version.

```csharp
var getVersion = pluginInterface.GetIpcSubscriber<int>("QuickSymbols.GetVersion");
var version = getVersion.InvokeFunc();
```

### `QuickSymbols.OpenPicker`

Opens the QuickSymbols picker for a specific owner key.

```csharp
var openPicker = pluginInterface.GetIpcSubscriber<string, bool>("QuickSymbols.OpenPicker");
var opened = openPicker.InvokeFunc("Example");
```

### `QuickSymbols.ClosePicker`

Closes the picker if it is currently open for that owner key.

```csharp
var closePicker = pluginInterface.GetIpcSubscriber<string, bool>("QuickSymbols.ClosePicker");
var closed = closePicker.InvokeFunc("Example");
```


### `QuickSymbols.GetHotkey`

Returns the currently configured QuickSymbols picker hotkey as an array of virtual-key integer values.

This lets another plugin listen for the same user-configured QuickSymbols keybind instead of hard-coding its own shortcut.

```csharp
var getHotkey = pluginInterface.GetIpcSubscriber<int[]>("QuickSymbols.GetHotkey");
var keys = getHotkey.InvokeFunc();
```

### `QuickSymbols.SymbolSelected`

Subscribe to this event to receive the selected symbol.

The first argument is the owner key passed to `OpenPicker`.
The second argument is the selected symbol/text.

```csharp
private Action<string, string>? quickSymbolsSelected;

quickSymbolsSelected = (owner, symbol) =>
{
    if (owner != "ChatTwo")
        return;

    // Insert into your own input here.
    // Your plugin keeps ownership of focus, selection and caret state.
};

var selected = pluginInterface.GetIpcSubscriber<string, string, object?>("QuickSymbols.SymbolSelected");
selected.Subscribe(quickSymbolsSelected);
```

Remember to unsubscribe on dispose:

```csharp
if (quickSymbolsSelected != null)
{
    selected.Unsubscribe(quickSymbolsSelected);
    quickSymbolsSelected = null;
}
```

## Recommended
1. Your plugin detects that its text input is focused.
2. Your plugin checks `QuickSymbols.GetHotkey` and opens the picker when the user presses their configured QuickSymbols keybind.
3. Your plugin calls `QuickSymbols.OpenPicker("YourPluginName")`.
4. QuickSymbols shows the symbol picker.
5. The user clicks a symbol.
6. QuickSymbols sends `QuickSymbols.SymbolSelected("YourPluginName", symbol)`.
7. Your plugin inserts the symbol into its own input and updates its own caret state.
