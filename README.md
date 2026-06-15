## What to expect

### Quick symbol button

- Added a new `♥` button near the game's ChatLog controls.
  - Clicking the button opens a symbol picker popup.
  - Clicking the button again closes the popup.
- Added `Custom` tab to the symbol popup
  - Show custom entries created by user to use your own symbols, text or short snippets
  - Can be configured with the command `/qsconfig`
- Added a configurable keybind for opening the symbol popup near the current mouse position for any text input
- The popup also has an `X` button to close it.

### Symbol picker popup

- Added a popup with many special symbols that can be inserted into text fields.
- Symbols are shown in a clean grid with up to 10 symbols per row.
- Clicking a symbol inserts it into the active text field.
- The popup stays open after choosing a symbol, so multiple symbols can be inserted quickly.

### Supported places

QuickSymbols button currently appears for:

- ChatLog message input
- Party Finder recruitment comment field
- Housing Message Book message field
> **You can use the Keybind to open the popup list in any Text Field from any other windows like User Macros for example.**

### Favorites

- Added support for favorite symbols.
- Hold `CTRL` and click a symbol to favorite it.
- Favorite symbols appear in a `Favorites` section at the top of the popup.
- Hold `CTRL` and click a favorite symbol again to remove it from favorites.
- Favorites are shared between all QuickSymbols popups.

### Custom button position

- Added a `Change button position` option in the main QuickSymbols popup.
- When editing is enabled, the `♥` button can be dragged to a custom position.
- The custom position follows the ChatLog if the ChatLog is moved.

### Game theme support

- QuickSymbols changes its colors based on the UI theme selected in FFXIV.
- Supported themes include:
  - Dark
  - Light
  - Classic FF
  - Clear Blue
  - Clear White
  - Clear Green
  - Clear Grey
  - Clear Pink

## Notes

- It only inserts the selected symbol into the text field you are currently using.
- The popup remains open until you close it manually if the option for it is disbaled in config.
- Favorite symbols and custom entries are saved between game sessions.
- The popup list by keybind will not work for Plugin windows.

`End of changelog`
