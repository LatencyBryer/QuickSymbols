using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Interface;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace QuickSymbols;

// Had to clarify that Notes here can be a bit confusing since I initialy did
// this project as a Tweak integration for SimpleTweaks, wich was not approved.
// So I rebuild it as a actual plugin again instead.

// QuickSymbols work with any native window with textinput but it show the plugin button
// for some windows to make it easier to use without keybind.

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    private const string ChatLogAddonName = "ChatLog";
    // Macro is the game's User Macros window, while TofuInputString is the textinput from StrategyBoard
    private const string MacroAddonName = "Macro";
    private const string TofuInputStringAddonName = "TofuInputString";
    private const int ButtonPlacementLeft = 0;
    private const int ButtonPlacementRight = 1;
    private const int ButtonPlacementKeybindOnly = 2;
    private static readonly string[] ButtonPlacementLabels = ["Left of Chat", "Right of Chat", "Keybind Only"];
    private static readonly string[] RecruitmentCriteriaAddonNames =
    [
        "LookingForGroupCondition",
        "LookingForGroup",
        "LookingForGroupDetail",
        "LookingForGroupSearch",
        "LookingForGroupSelectRole",
    ];

    private static readonly string[] MessageBookInputAddonNames =
    [
        "InputMessage",
        "HousingGuestBook",
        "HousingGuestBookInputMessage",
    ];

    private const int MaxColumns = 10;

    // Home tab | Main/default symbols list.
    private static readonly string[] Symbols = BuildSymbols();

    // Numbers tab | Numeric symbols.
    private static readonly string[] NumberSymbols = BuildNumberSymbols();

    // Letters tab | Letter/alphabet related symbols.
    private static readonly string[] LetterSymbols = BuildLetterSymbols();

    // Common tab | Commonly used symbols + common text symbols.
    private static readonly string[] CommonSymbols = BuildCommonSymbols();

    // Time tab | Small curated set of time-related symbols.
    private static readonly string[] TimeSymbols = BuildTimeSymbols();

    // Others tab | Miscellaneous symbols + extra useful ones.
    private static readonly string[] OthersSymbols = BuildOthersSymbols();

    private const string CommandShort = "/qs";
    private const string CommandLong = "/quicksymbols";
    private const string CommandConfig = "/qsconfig";


    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    // Auto-symbol rules are stored separately from the normal Custom tab entries on purpose.
    // "Custom" is just a symbol/snippet list shown inside the picker, while these rules change text
    // that the player is actively typing in vanilla chat. Keeping them as Text + Symbol pairs makes
    // the config easier to read and also keeps old QuickSymbols configs safe since the default list
    // starts empty and only fills when the user creates their own replacements.
    public sealed class TextSymbolReplacement
    {
        public string Text { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
    }

    public sealed class PluginConfiguration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;
        public VirtualKey[] ToggleHotkey { get; set; } = [VirtualKey.MENU, VirtualKey.S];
        public List<string> Custom { get; set; } = [];

        // Current plugin config keeps the original Quick Symbols field while
        // still accepting the temporary tweak field name used by the SimpleTweaks version.(My old attempt of merging the plugin as a Tweak in SimpleTweaks. There is nothing to do with the plugin SimpleTweaks itself - Had to reclarify)
        public List<string> FavoriteSymbols { get; set; } = [];
        public List<string> favsymbols { get; set; } = [];

        // Keep this in sake of compatibility with older 'QuickSymbols' config files
        public List<string> History { get; set; } = [];
        public bool ShowHistory { get; set; } = false;
        public bool ShowAllTab { get; set; } = false;
        public bool ShowTitles { get; set; } = true;
        // #
        public int MaxHistory { get; set; } = 25;

        // Original Quick Symbols position fields.
        public bool HasCustomButtonPosition { get; set; }
        public Vector2 ButtonPosition { get; set; }

        // Compatibility with the temporary SimpleTweaks version of this code. (Again, please read above)
        public bool HasCustombPosition { get; set; }
        public bool UsesRelativeButtonOffset { get; set; }
        public Vector2 bPosition { get; set; }
        public Vector2 ButtonOffset { get; set; }
        public bool ClosePopupOnLostFocus { get; set; }

        // Main chat button placement. This is kept as a int so old configs still load safely if this list changes later.
        public int ButtonPlacement { get; set; } = ButtonPlacementRight;

        public bool ReplaceSpecificTextsForSymbols { get; set; } = false;
        public List<TextSymbolReplacement> CustomTextReplacements { get; set; } = [];

        // Users can enable this so the list uses Dalamud style instead of FFXIV theme.
        public bool UseDalamudTheme { get; set; }
        public bool ConfigWindowHadFirstOpen { get; set; }
    }

    private readonly PluginConfiguration Config;


    // UI and State stuff
    private IFontHandle? symbolFont;
    private PopupTab selectedPopupTab = PopupTab.Symbols;
    private string newCustomEntry = string.Empty;
    private string newTextReplacementText = string.Empty;
    private string newTextReplacementSymbol = "\uE04B";
    private string newTextReplacementError = string.Empty;
    // #

    // Control and Visibility stuff
    private bool popupOpen;
    private bool partyFinderPopupOpen;
    private bool messageBookPopupOpen;
    private bool keybindPopupOpen;
    // #

    // Main positioning related stuff
    private bool editbPosition;
    private bool draggingButton;
    private bool bPositionDirty;
    // Position changes can happen every draw frame while dragging, so saving is delayed to FrameworkUpdate.
    // This avoids stacking multiple SavePluginConfig writes while Dalamud is still writing the previous config.
    private bool bPositionSaveQueued;
    private Vector2 nativebPos;
    private Vector2 currentbPos;
    private Vector2 currentbSize;
    // #

    // Party Finder/House Message Book/User Macros
    private Vector2 partyFinderbPos;
    private Vector2 partyFinderbSize;
    private Vector2 messageBookbPos;
    private Vector2 messageBookbSize;
    private Vector2 macrobPos;
    private Vector2 macrobSize;
    private bool macroPopupOpen;
    private Vector2 tofuInputbPos;
    private Vector2 tofuInputbSize;
    private bool tofuInputPopupOpen;
    // #

    // New Keybind Popup | Kept 'Toggle Character Selector' option from original 'QuickSymbols'
    private Vector2 keybindPopupAnchorPos;
    private Vector2 keybindPopupAnchorSize;
    private Vector2 keybindPopupLivePos;
    private bool keybindPopupPosValid;
    private AtkComponentTextInput* keybindTextInput;
    private int popupClickGuardFrames;
    private bool leftMouseWasDown;
    private bool leftMouseClickedThisFrame;
    // #

    // Scroll
    // Home tab scroll
    private float symbolScrollY;

    // Custom tab scroll
    private float customScrollY;

    // Numbers tab scroll
    private float numbersScrollY;

    // Letters tab scroll
    private float lettersScrollY;

    // Common tab scroll
    private float commonScrollY;

    // Time tab scroll
    private float timeScrollY;

    // Others tab scroll
    private float othersScrollY;
    private float tabVisualIndex = -1f;
    private int tabTargetIndex = -1;
    private double tabMoveStartedAt;
    private bool draggingScrollBar;
    private float scrollDragOffsetY;
    private bool configWindowOpen;
    private bool autoSymbolListOpen;
    private bool autoSymbolPickerOpen;
    private bool autoSymbolHelpOpen;
    // Track its popup anchor ourselves so the picker can open from the keybind inside the config window.
    private bool configCustomEntryPopupOpen;
    private Vector2 configCustomEntryPopupAnchorPos;
    private Vector2 configCustomEntryPopupAnchorSize;
    private bool configCustomEntryActive;
    private bool hotkeyRecording;
    private bool hotkeyWasDown;
    private bool textReplacementPending;
    private int hotkeyCaptureDelayFrames;
    private readonly List<VirtualKey> pendingHotkey = [];
    private readonly Stopwatch hotkeySafety = Stopwatch.StartNew();
    // #

    public Plugin()
    {
        this.Config = PluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        this.ConfigChanged();

        this.ipcGetVersion = PluginInterface.GetIpcProvider<int>(IpcGetVersion);
        this.ipcOpenPicker = PluginInterface.GetIpcProvider<string, bool>(IpcOpenPicker);
        this.ipcClosePicker = PluginInterface.GetIpcProvider<string, bool>(IpcClosePicker);
        this.ipcGetHotkey = PluginInterface.GetIpcProvider<int[]>(IpcGetHotkey);
        this.ipcWatchInput = PluginInterface.GetIpcProvider<string, bool>(IpcWatchInput);
        this.ipcSymbolSelected = PluginInterface.GetIpcProvider<string, string, object?>(IpcSymbolSelected);
        this.RegisterIpc();

        this.symbolFont = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 18f));
        PluginInterface.UiBuilder.Draw += this.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenMainWindow;
        Framework.Update += this.FrameworkUpdate;

        this.RegisterCommand(CommandShort, "Open Quick Symbols.");
        this.RegisterCommand(CommandLong, "Open Quick Symbols.");
        this.RegisterCommand(CommandConfig, "Open Quick Symbols configuration.");

        Log.Information("Quick Symbols loaded.");
    }

    private void ConfigChanged()
    {
        this.Config.Custom ??= [];
        this.Config.FavoriteSymbols ??= [];
        this.Config.favsymbols ??= [];
        this.Config.History ??= [];
        this.Config.ToggleHotkey = NormalizeHotkey(this.Config.ToggleHotkey ?? [VirtualKey.MENU, VirtualKey.S]);

        if (this.Config.favsymbols.Count == 0 && this.Config.FavoriteSymbols.Count > 0)
        {
            this.Config.favsymbols = this.Config.FavoriteSymbols.ToList();
        }
        else if (this.Config.FavoriteSymbols.Count == 0 && this.Config.favsymbols.Count > 0)
        {
            this.Config.FavoriteSymbols = this.Config.favsymbols.ToList();
        }

        if (!this.Config.HasCustombPosition && this.Config.HasCustomButtonPosition)
        {
            this.Config.HasCustombPosition = true;
            this.Config.bPosition = this.Config.ButtonPosition;
        }
        else if (!this.Config.HasCustomButtonPosition && this.Config.HasCustombPosition)
        {
            this.Config.HasCustomButtonPosition = true;
            this.Config.ButtonPosition = this.Config.bPosition;
        }
    }

    public void Dispose()
    {
        Framework.Update -= this.FrameworkUpdate;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigWindow;
        PluginInterface.UiBuilder.Draw -= this.Draw;

        CommandManager.RemoveHandler(CommandShort);
        CommandManager.RemoveHandler(CommandLong);
        CommandManager.RemoveHandler(CommandConfig);

        this.UnregisterIpc();
        this.symbolFont?.Dispose();
        this.keybindTextInput = null;
        this.FlushButtonPositionSave();
        this.SaveConfig();
    }

    private void RegisterCommand(string command, string helpMessage)
    {
        if (CommandManager.Commands.ContainsKey(command))
        {
            Log.Warning($"Quick Symbols skipped registering command {command} because it is already registered.");
            return;
        }

        CommandManager.AddHandler(command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = helpMessage,
            ShowInHelp = true,
        });
    }

    private void OnCommand(string command, string args)
    {
        if (command.Equals(CommandConfig, StringComparison.OrdinalIgnoreCase) || args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            this.configWindowOpen = true;
            return;
        }

        this.OpenMainPopup();
    }

    private void OpenConfigWindow()
    {
        this.configWindowOpen = true;
    }

    private void OpenMainWindow()
    {
        this.selectedPopupTab = PopupTab.Symbols;

        var focused = GetFocusedTextInput();
        if (focused != null)
        {
            var scale = ImGuiHelpers.GlobalScale;
            this.keybindTextInput = focused;
            this.keybindPopupAnchorSize = new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));

            var mousePos = ImGui.GetIO().MousePos;
            var displaySize = ImGui.GetIO().DisplaySize;
            if (mousePos.X < 0f || mousePos.Y < 0f || mousePos.X > displaySize.X || mousePos.Y > displaySize.Y)
            {
                mousePos = displaySize * 0.5f;
            }

            this.keybindPopupAnchorPos = ClampPositionToScreen(mousePos + new Vector2(12f * scale, 12f * scale), this.keybindPopupAnchorSize);
            this.OpenKeybindPopup();
            return;
        }

        this.OpenMainPopup();
    }

    private void DrawConfigWindow()
    {
        if (!this.configWindowOpen)
        {
            return;
        }

        var changed = false;
        var scale = ImGuiHelpers.GlobalScale;
        var minSize = new Vector2(360f * scale, 218f * scale);
        ImGui.SetNextWindowSizeConstraints(minSize, new Vector2(float.MaxValue, float.MaxValue));

        if (!this.Config.ConfigWindowHadFirstOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(424f * scale, 242f * scale), ImGuiCond.Always);
        }

        if (ImGui.Begin("Quick Symbols Config", ref this.configWindowOpen, ImGuiWindowFlags.NoCollapse))
        {
            if (!this.Config.ConfigWindowHadFirstOpen)
            {
                this.Config.ConfigWindowHadFirstOpen = true;
                changed = true;
            }

            this.DrawConfig(ref changed);
        }

        ImGui.End();

        if (this.configCustomEntryPopupOpen)
        {
            // Draw this from the config window path instead of the normal game overlay path.
            // The config input lives in this ImGui window, so keeping the picker here avoids losing the anchor.
            var colors = UiColors.FromGameTheme(GetCurrentGameUiTheme(), null);
            this.DrawSymbolsPopup(
                "ConfigCustomEntry",
                colors,
                this.configCustomEntryPopupAnchorPos,
                this.configCustomEntryPopupAnchorSize,
                PopupPlacement.Below,
                includePositionEditor: false,
                SymbolInsertTarget.ConfigCustomEntry,
                ref this.configCustomEntryPopupOpen);
        }

        this.DrawAutoSymbolListWindow(ref changed);
        this.DrawAutoSymbolHelpWindow();

        if (changed)
        {
            this.SaveConfig();
        }
    }

    // This is the little management window for the auto-symbol feature.
    // It ended up being more than a simple list because custom replacements need some guard rails:
    // no normal words like "star", no reusing the built-in shortcuts, no duplicate symbol targets,
    // and no command-looking text. The UI tries to show those rules right where the user is typing.
    // The warning icon is also kept visible here because this feature only makes sense for the game
    //  own chat input, not Chat2 or random plugin ImGui boxes.
    private void DrawAutoSymbolListWindow(ref bool hasChanged)
    {
        if (!this.autoSymbolListOpen)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(new Vector2(720f * scale, 520f * scale), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Chat Auto-Symbol List", ref this.autoSymbolListOpen, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.78f, 0.14f, 1f), "Automatic text replacements");
        ImGui.SameLine();
        ImGui.TextDisabled("Vanilla chat only");

        ImGui.Spacing();
        ImGui.TextUnformatted("New:");
        ImGui.SameLine();

        var status = this.GetNewTextReplacementStatus();
        var invalidText = status.HighlightText && !string.IsNullOrEmpty(this.newTextReplacementText);
        var inputWidth = Math.Max(150f * scale, ImGui.GetContentRegionAvail().X - 238f * scale);

        ImGui.SetNextItemWidth(inputWidth);
        using (invalidText ? ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 0.35f, 0.35f, 1f)) : null)
        {
            var newText = this.newTextReplacementText;
            if (ImGui.InputText("##NewTextReplacementText", ref newText, 32))
            {
                this.newTextReplacementText = SanitizeReplacementText(newText);
                this.newTextReplacementError = string.Empty;
            }
        }

        ImGui.SameLine();
        using (this.symbolFont is { Available: true } ? this.symbolFont.Push() : null)
        {
            if (ImGui.Button($"{this.newTextReplacementSymbol}##PickTextReplacementSymbol", new Vector2(32f * scale, 0f)))
            {
                ImGui.OpenPopup("##AutoSymbolPickerPopup");
            }
        }

        this.DrawAutoSymbolPickerPopup();

        ImGui.SameLine();
        if (!status.Valid)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Save##SaveTextReplacement", new Vector2(54f * scale, 0f)))
        {
            if (this.TrySaveTextReplacement(out var error))
            {
                hasChanged = true;
                this.newTextReplacementText = string.Empty;
                this.newTextReplacementSymbol = this.GetDefaultNewReplacementSymbol();
                this.newTextReplacementError = string.Empty;
            }
            else
            {
                this.newTextReplacementError = error;
            }
        }

        if (!status.Valid)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Help##AutoSymbolHelp", new Vector2(54f * scale, 0f)))
        {
            this.autoSymbolHelpOpen = true;
        }

        ImGui.SameLine();
        using (PushQuickSymbolsIconFont())
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), char.ConvertFromUtf32(0xF071));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("This is only compatible with vanilla chat and vanilla chat ONLY.");
        }

        if (!status.Valid && !string.IsNullOrEmpty(status.Message))
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), status.Message);
        }
        else if (!string.IsNullOrEmpty(this.newTextReplacementError))
        {
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), this.newTextReplacementError);
        }
        else
        {
            ImGui.TextDisabled("Examples: :sparkle:, [sparkle], ;sparkle;, -sparkle-, =sparkle=");
        }

        ImGui.Spacing();
        this.DrawTextReplacementListPanel(ref hasChanged);
        ImGui.End();
    }

    private void DrawAutoSymbolHelpWindow()
    {
        if (!this.autoSymbolHelpOpen)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(new Vector2(560f * scale, 420f * scale), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Chat Auto-Symbol Help", ref this.autoSymbolHelpOpen, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.78f, 0.14f, 1f), "How to create automatic symbol text");
        ImGui.Separator();

        ImGui.TextWrapped("This feature watches the current vanilla chat input. When the end of your message matches a registered text, QuickSymbols removes that text and inserts the selected symbol.");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1f, 1f), "Valid custom text formats");
        ImGui.BulletText("Must start and finish with the same supported wrapper:");
        ImGui.Indent();
        ImGui.TextUnformatted(":name:");
        ImGui.TextUnformatted("[name]");
        ImGui.TextUnformatted(";name;");
        ImGui.TextUnformatted("-name-");
        ImGui.TextUnformatted("=name=");
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1f, 1f), "Rules");
        ImGui.BulletText("The name inside the wrapper can only use letters and numbers.");
        ImGui.BulletText("Spaces are not allowed.");
        ImGui.BulletText("Texts cannot start with / or \\.");
        ImGui.BulletText("You cannot reuse an existing text, existing name, or existing symbol.");
        ImGui.BulletText("Auto replacement is skipped for vanilla chat commands, meaning messages starting with / are ignored.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 0.85f, 1f, 1f), "Examples");
        ImGui.BulletText(":star: -> selected star symbol");
        ImGui.BulletText("[dice] -> selected dice symbol");
        ImGui.BulletText("-heart- -> selected heart symbol");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "Important");
        ImGui.TextWrapped("This is only compatible with vanilla chat and vanilla chat ONLY.");
        ImGui.End();
    }

    private void DrawTextReplacementListPanel(ref bool hasChanged)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var panelHeight = Math.Max(220f * scale, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild("##ChatAutoSymbolListPanel", new Vector2(0f, panelHeight), true);

        ImGui.Columns(3, "##AutoSymbolColumns", false);

        this.Config.CustomTextReplacements ??= [];
        for (var i = 0; i < this.Config.CustomTextReplacements.Count; i++)
        {
            var custom = this.Config.CustomTextReplacements[i];
            if (string.IsNullOrWhiteSpace(custom.Text) || string.IsNullOrEmpty(custom.Symbol))
            {
                continue;
            }

            this.DrawReplacementCard(custom.Text, custom.Symbol, "Custom", removable: true, removeIndex: i, customCard: true, ref hasChanged);
            ImGui.NextColumn();
        }

        foreach (var row in GetDefaultReplacementDisplayRows())
        {
            this.DrawReplacementCard(row.Text, row.Symbol, row.Kind, removable: false, removeIndex: -1, customCard: false, ref hasChanged);
            ImGui.NextColumn();
        }

        ImGui.Columns(1);
        ImGui.EndChild();
    }

    private void DrawReplacementCard(string text, string symbol, string kind, bool removable, int removeIndex, bool customCard, ref bool hasChanged)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(120f * scale, ImGui.GetColumnWidth() - 8f * scale);
        var height = 58f * scale;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var bg = customCard
            ? new Vector4(1f, 0.78f, 0.14f, 0.10f)
            : ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
        var border = ImGui.GetStyle().Colors[(int)ImGuiCol.Border];

        drawList.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(bg), 5f * scale);
        drawList.AddRect(pos, pos + new Vector2(width, height), ImGui.GetColorU32(border), 5f * scale, ImDrawFlags.None, Math.Max(1f, scale));

        ImGui.SetCursorScreenPos(pos + new Vector2(8f * scale, 5f * scale));
        ImGui.TextDisabled(kind);

        if (removable)
        {
            var xPos = pos + new Vector2(width - 24f * scale, 5f * scale);
            ImGui.SetCursorScreenPos(xPos);
            if (ImGui.SmallButton($"X##RemoveTextReplacement{removeIndex}"))
            {
                this.Config.CustomTextReplacements.RemoveAt(removeIndex);
                hasChanged = true;
            }
        }

        ImGui.SetCursorScreenPos(pos + new Vector2(8f * scale, 27f * scale));
        ImGui.TextUnformatted(text);
        ImGui.SameLine();
        ImGui.TextDisabled("=>");
        ImGui.SameLine();
        using (this.symbolFont is { Available: true } ? this.symbolFont.Push() : null)
        {
            ImGui.TextUnformatted(symbol);
        }

        ImGui.SetCursorScreenPos(pos + new Vector2(0f, height + 6f * scale));
    }

    // Symbol picker used only by the custom replacement creator.
    // It intentionally filters out symbols that already belong to a default or user rule. The goal is
    // to avoid ambiguous replacements like two different texts both becoming the same icon.
    private void DrawAutoSymbolPickerPopup()
    {
        if (!ImGui.BeginPopup("##AutoSymbolPickerPopup"))
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var entries = this.GetAvailablePickerSymbols();
        var columns = 9;
        var cell = 26f * scale;
        var spacing = 3f * scale;
        var scrollPad = ImGui.GetStyle().ScrollbarSize + 12f * scale;
        var rows = Math.Min(8, Math.Max(1, (int)Math.Ceiling(entries.Count / (double)columns)));
        var height = rows * cell + Math.Max(0, rows - 1) * spacing + 8f * scale;
        var width = columns * cell + Math.Max(0, columns - 1) * spacing + scrollPad;

        ImGui.BeginChild("##AutoSymbolPickerPopupBody", new Vector2(width, height), true);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        using (this.symbolFont is { Available: true } ? this.symbolFont.Push() : null)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var row = i / columns;
                var col = i % columns;
                var pos = start + new Vector2(col * (cell + spacing), row * (cell + spacing));
                ImGui.SetCursorScreenPos(pos);
                ImGui.InvisibleButton($"##AutoSymbolPick{i}", new Vector2(cell, cell));

                var hovered = ImGui.IsItemHovered();
                drawList.AddRectFilled(pos, pos + new Vector2(cell, cell), ImGui.GetColorU32(hovered ? ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered] : ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]), 4f * scale);

                var symbol = entries[i];
                var symbolSize = ImGui.CalcTextSize(symbol);
                drawList.AddText(pos + (new Vector2(cell, cell) - symbolSize) * 0.5f, ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), symbol);

                if (ImGui.IsItemClicked())
                {
                    this.newTextReplacementSymbol = symbol;
                    this.newTextReplacementError = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private List<string> GetAvailablePickerSymbols()
    {
        this.Config.CustomTextReplacements ??= [];

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var replacement in DefaultTextReplacements)
        {
            if (!string.IsNullOrEmpty(replacement.Symbol))
            {
                used.Add(replacement.Symbol);
            }
        }

        foreach (var replacement in this.Config.CustomTextReplacements)
        {
            if (!string.IsNullOrEmpty(replacement.Symbol))
            {
                used.Add(replacement.Symbol);
            }
        }

        return Symbols
            .Concat(NumberSymbols)
            .Concat(LetterSymbols)
            .Concat(CommonSymbols)
            .Concat(OthersSymbols)
            .Concat(TimeSymbols)
            .Distinct(StringComparer.Ordinal)
            .Where(symbol => !used.Contains(symbol))
            .ToList();
    }

    private string GetDefaultNewReplacementSymbol()
    {
        const string fallback = "\uE04B";
        var available = this.GetAvailablePickerSymbols();
        return available.Contains(fallback, StringComparer.Ordinal)
            ? fallback
            : available.FirstOrDefault() ?? fallback;
    }

    private bool TrySaveTextReplacement(out string error)
    {
        this.Config.CustomTextReplacements ??= [];

        var status = this.GetNewTextReplacementStatus();
        if (!status.Valid)
        {
            error = status.Message;
            return false;
        }

        this.Config.CustomTextReplacements.Add(new TextSymbolReplacement
        {
            Text = SanitizeReplacementText(this.newTextReplacementText),
            Symbol = this.newTextReplacementSymbol,
        });

        error = string.Empty;
        return true;
    }

    // Validation for user-created auto-symbol rules.
    // The wrapper requirement is intentional. Without it, someone could create a plain word like
    // "square" and then normal conversation would start replacing itself. For the same reason I compare
    // the inner name too, so :star: and ;star; are treated as the same name even though the wrapper is
    // different. It is a little stricter but it avoids a lot of possible issues later.
    private NewTextReplacementStatus GetNewTextReplacementStatus()
    {
        var text = SanitizeReplacementText(this.newTextReplacementText);
        var symbol = this.newTextReplacementSymbol;
        var name = GetReplacementName(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            return NewTextReplacementStatus.Invalid(string.Empty, highlightText: false);
        }

        if (!IsValidReplacementWrapper(text))
        {
            return NewTextReplacementStatus.Invalid("Need to start and finish with \":\" or \"[ ]\" or \";\" or \"-\" or \"=\".", highlightText: true);
        }

        if (string.IsNullOrWhiteSpace(name) || !name.All(char.IsLetterOrDigit))
        {
            return NewTextReplacementStatus.Invalid("Only letters and numbers are allowed inside the wrapper.", highlightText: true);
        }

        if (text.StartsWith("/", StringComparison.Ordinal) || text.StartsWith("\\", StringComparison.Ordinal))
        {
            return NewTextReplacementStatus.Invalid("Text cannot start with / or \\.", highlightText: true);
        }

        if (DefaultTextReplacements.Any(rule => string.Equals(rule.Text, text, StringComparison.Ordinal))
            || DefaultTextReplacements.Any(rule => string.Equals(GetReplacementName(rule.Text), name, StringComparison.OrdinalIgnoreCase))
            || this.Config.CustomTextReplacements.Any(rule => string.Equals(rule.Text, text, StringComparison.Ordinal))
            || this.Config.CustomTextReplacements.Any(rule => string.Equals(GetReplacementName(rule.Text), name, StringComparison.OrdinalIgnoreCase)))
        {
            return NewTextReplacementStatus.Invalid("You can't create or use already existing Texts/Symbols", highlightText: true);
        }

        if (DefaultTextReplacements.Any(rule => string.Equals(rule.Symbol, symbol, StringComparison.Ordinal))
            || this.Config.CustomTextReplacements.Any(rule => string.Equals(rule.Symbol, symbol, StringComparison.Ordinal)))
        {
            return NewTextReplacementStatus.Invalid("You can't create or use already existing Texts/Symbols", highlightText: false);
        }

        if (string.IsNullOrEmpty(symbol))
        {
            return NewTextReplacementStatus.Invalid("Pick a symbol first.", highlightText: false);
        }

        return NewTextReplacementStatus.ValidStatus;
    }

    private static bool IsValidReplacementWrapper(string text)
    {
        if (text.Length < 3)
        {
            return false;
        }

        return text.StartsWith(":", StringComparison.Ordinal) && text.EndsWith(":", StringComparison.Ordinal)
               || text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal)
               || text.StartsWith(";", StringComparison.Ordinal) && text.EndsWith(";", StringComparison.Ordinal)
               || text.StartsWith("-", StringComparison.Ordinal) && text.EndsWith("-", StringComparison.Ordinal)
               || text.StartsWith("=", StringComparison.Ordinal) && text.EndsWith("=", StringComparison.Ordinal);
    }

    private static string GetReplacementName(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            return string.Empty;
        }

        if (IsValidReplacementWrapper(text))
        {
            return text[1..^1];
        }

        return text;
    }

    private static string SanitizeReplacementText(string text)
    {
        var allowed = new HashSet<char> { ':', '[', ']', ';', '-', '=' };
        return new string(text.Where(ch => char.IsLetterOrDigit(ch) || allowed.Contains(ch)).ToArray());
    }

    private static IEnumerable<(string Text, string Symbol, string Kind)> GetDefaultReplacementDisplayRows()
    {
        yield return ("[a] to [z]", $"{char.ConvertFromUtf32(0xE071)} to {char.ConvertFromUtf32(0xE08A)}", "Default");
        yield return ("[1] to [31]", $"{char.ConvertFromUtf32(0xE090)} to {char.ConvertFromUtf32(0xE0AE)}", "Default");

        foreach (var replacement in DefaultTextReplacements
                     .Where(rule => !rule.Text.StartsWith("[", StringComparison.Ordinal))
                     .OrderByDescending(rule => rule.Text.Length))
        {
            yield return (replacement.Text, replacement.Symbol, "Default");
        }
    }

    private bool DrawConfigIconButton(string id, int codepoint)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var text = char.ConvertFromUtf32(codepoint);
        using (PushQuickSymbolsIconFont())
        {
            return ImGui.SmallButton($"{text}{id}");
        }
    }

    private void SetAutomaticTextReplacement(bool enabled, bool printEnabledMessage)
    {
        var wasEnabled = this.Config.ReplaceSpecificTextsForSymbols;
        this.Config.ReplaceSpecificTextsForSymbols = enabled;

        if (!wasEnabled && enabled && printEnabledMessage)
        {
            ChatGui.Print("Automatic text to symbol Enabled. You can test typing the following: <3 or :clock: or :dice:. Open the settings menu for the full list.");
        }
    }

    private void SaveConfig()
    {
        this.ConfigChanged();
        PluginInterface.SavePluginConfig(this.Config);
    }

    private bool CheckHotkeyState(VirtualKey[] keys)
    {
        this.CheckHotkeyEditorSafety();
        if (this.hotkeyRecording || keys.Length == 0)
        {
            this.hotkeyWasDown = false;
            return false;
        }

        foreach (var key in keys)
        {
            if (!KeyState[key])
            {
                this.hotkeyWasDown = false;
                return false;
            }
        }

        if (this.hotkeyWasDown)
        {
            return false;
        }

        this.hotkeyWasDown = true;
        foreach (var key in keys)
        {
            KeyState[key] = false;
        }

        return true;
    }

    private bool CheckHotkeyStateRaw(VirtualKey[] keys)
    {
        // Use the OS key state for ImGui-owned fields where Dalamud's text-input focus helper cannot help.
        // Keep the same hotkeyWasDown guard so holding the combo does not spam-open the picker.
        this.CheckHotkeyEditorSafety();
        if (this.hotkeyRecording || keys.Length == 0)
        {
            this.hotkeyWasDown = false;
            return false;
        }

        foreach (var key in keys)
        {
            var down = false;
            try
            {
                down = (GetAsyncKeyState((int)key) & unchecked((short)0x8000)) != 0;
            }
            catch
            {
                down = KeyState[key];
            }

            if (!down)
            {
                this.hotkeyWasDown = false;
                return false;
            }
        }

        if (this.hotkeyWasDown)
        {
            return false;
        }

        this.hotkeyWasDown = true;
        foreach (var key in keys)
        {
            KeyState[key] = false;
        }

        return true;
    }

    private bool DrawHotkeyConfigEditor(string label, VirtualKey[] keys, out VirtualKey[] outKeys)
    {
        outKeys = [];
        var changed = false;
        var hotkeyText = this.hotkeyRecording
            ? string.Join("+", this.pendingHotkey.Select(GetKeyName))
            : string.Join("+", keys.Select(GetKeyName));

        if (string.IsNullOrWhiteSpace(hotkeyText))
        {
            hotkeyText = this.hotkeyRecording ? "Press keys..." : "None";
        }

        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        var displaySize = new Vector2(Math.Clamp(ImGui.CalcTextSize(hotkeyText).X + 14f * ImGuiHelpers.GlobalScale, 62f * ImGuiHelpers.GlobalScale, 108f * ImGuiHelpers.GlobalScale), ImGui.GetFrameHeight());
        this.DrawReadonlyHotkeyBox(hotkeyText, displaySize);

        if (this.hotkeyRecording)
        {
            if (this.CaptureHotkeyInput())
            {
                outKeys = NormalizeHotkey(this.pendingHotkey);
                changed = outKeys.Length > 0;
                if (changed)
                {
                    this.hotkeyRecording = false;
                    this.hotkeyWasDown = true;
                    this.hotkeySafety.Reset();
                    this.pendingHotkey.Clear();
                    this.hotkeyCaptureDelayFrames = 0;
                    this.ClearPressedGameKeysDuringHotkeyRecording();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##QuickSymbolsHotkeyCancel"))
            {
                this.CancelHotkeyRecording();
            }
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.Button("Set Keybind##QuickSymbolsSetHotkey"))
            {
                this.BeginHotkeyRecording();
            }

            ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
            this.DrawHelpIcon("QuickSymbolsSetKeybindHelp", "Click specificaly the button 'Set Keybind' and hit the Keys combo you want to save.\nIf you press the Keybind without having any kind of input text field in focus,\nnothing will show up.");

            ImGui.SameLine(0f, 9f * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted("Chat2:");
            ImGui.SameLine(0f, 3f * ImGuiHelpers.GlobalScale);
            this.DrawChat2DisclaimerIcon();
        }

        return changed;
    }

    private void DrawReadonlyHotkeyBox(string text, Vector2 size)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);

        var drawList = ImGui.GetWindowDrawList();
        var bg = new Vector4(0.10f, 0.10f, 0.10f, 0.42f);
        var border = this.hotkeyRecording
            ? new Vector4(1f, 0.15f, 0.12f, 1f)
            : new Vector4(0.28f, 0.28f, 0.28f, 0.85f);
        var textColor = new Vector4(1f, 0.74f, 0.18f, 1f);

        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(bg), 3f * scale);
        drawList.AddRect(pos, pos + size, ImGui.GetColorU32(border), 3f * scale, ImDrawFlags.None, Math.Max(1f, scale));

        var clipped = text;
        var maxTextWidth = Math.Max(8f * scale, size.X - 10f * scale);
        while (clipped.Length > 1 && ImGui.CalcTextSize(clipped).X > maxTextWidth)
        {
            clipped = clipped[..^1];
        }

        if (clipped.Length < text.Length && clipped.Length > 1)
        {
            clipped = clipped[..^1] + "…";
        }

        var textSize = ImGui.CalcTextSize(clipped);
        drawList.AddText(new Vector2(pos.X + 7f * scale, pos.Y + (size.Y - textSize.Y) * 0.5f), ImGui.GetColorU32(textColor), clipped);
    }

    private void DrawHelpIcon(string id, string tooltip)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var text = char.ConvertFromUtf32(0xF128);
        var iconFont = PushQuickSymbolsIconFont();
        var textSize = ImGui.CalcTextSize(text);
        iconFont?.Dispose();

        var size = new Vector2(Math.Max(ImGui.GetTextLineHeight(), textSize.X + 6f * scale), ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##{id}", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();
        var color = active
            ? new Vector4(1f, 0.08f, 0.08f, 1f)
            : hovered
                ? new Vector4(1f, 0.88f, 0.05f, 1f)
                : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        iconFont = PushQuickSymbolsIconFont();
        drawList.AddText(pos + (size - textSize) * 0.5f, ImGui.GetColorU32(color), text);
        iconFont?.Dispose();

        if (hovered)
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawChat2DisclaimerIcon()
    {
        const int Chat2Icon = 0xF086;

        var scale = ImGuiHelpers.GlobalScale;
        var text = char.ConvertFromUtf32(Chat2Icon);
        var iconFont = PushQuickSymbolsIconFont();
        var textSize = ImGui.CalcTextSize(text);
        iconFont?.Dispose();

        var size = new Vector2(Math.Max(ImGui.GetTextLineHeight(), textSize.X + 6f * scale), ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##QuickSymbolsChat2Disclaimer", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var drawList = ImGui.GetWindowDrawList();
        var color = active
            ? new Vector4(0.82f, 0.50f, 1f, 1f)
            : hovered
                ? new Vector4(0.76f, 0.50f, 1f, 1f)
                : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        iconFont = PushQuickSymbolsIconFont();
        drawList.AddText(pos + (size - textSize) * 0.5f, ImGui.GetColorU32(color), text);
        iconFont?.Dispose();

        if (hovered)
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted("Chat2 Compatibility Disclaimer:");
            ImGui.Spacing();
            ImGui.TextWrapped("I heard and appreciate everyone feedback about Plugins compatibility <3.");
            ImGui.Spacing();
            ImGui.TextWrapped("Chat2 specificaly, uses a Dalamud (ImGuii) interface for its chat, which prevents QuickSymbols from inserting its usual symbols like it does in vanilla chat and native windows.");
            ImGui.Spacing();
            ImGui.TextWrapped("QuickSymbols already provides a way of compatibility, allowing other plugins to be compatible with QuickSymbols.");
            ImGui.Spacing();
            ImGui.TextWrapped("Plugin compatibility is ultimately up to the individual plugins, not QuickSymbols itself.");
            ImGui.Spacing();
            ImGui.TextWrapped("Thank You for using QuickSymbols! I hope more and more plugins opt for the compatibility soon.");
            ImGui.PopTextWrapPos();
        }
    }

    private static IDisposable? PushQuickSymbolsIconFont()
    {
        ImGui.PushFont(UiBuilder.IconFont);
        return new PushedFont();
    }

    private sealed class PushedFont : IDisposable
    {
        public void Dispose()
        {
            ImGui.PopFont();
        }
    }

    private bool CaptureHotkeyInput()
    {
        return this.ProcessHotkeyRecordingInput();
    }

    private bool ProcessHotkeyRecordingInput()
    {
        this.CheckHotkeyEditorSafety();

        if (!this.hotkeyRecording)
        {
            return false;
        }

        if (this.hotkeyCaptureDelayFrames > 0)
        {
            this.ClearPressedGameKeysDuringHotkeyRecording();
            this.hotkeyCaptureDelayFrames--;
            return false;
        }

        foreach (var virtualKey in KeyState.GetValidVirtualKeys())
        {
            if (!KeyState[virtualKey])
            {
                continue;
            }

            KeyState[virtualKey] = false;

            if (IsMouseButtonKey(virtualKey))
            {
                continue;
            }

            if (virtualKey == VirtualKey.ESCAPE)
            {
                this.CancelHotkeyRecording();
                return false;
            }

            if (!this.pendingHotkey.Contains(virtualKey))
            {
                this.pendingHotkey.Add(virtualKey);
            }
        }

        return this.pendingHotkey.Any(key => !IsModifierKey(key));
    }

    private void ClearPressedGameKeysDuringHotkeyRecording()
    {
        foreach (var virtualKey in KeyState.GetValidVirtualKeys())
        {
            if (KeyState[virtualKey])
            {
                KeyState[virtualKey] = false;
            }
        }
    }

    private void FinishHotkeyRecording(VirtualKey[] keys)
    {
        this.Config.ToggleHotkey = keys;
        this.hotkeyRecording = false;
        this.hotkeyWasDown = true;
        this.hotkeySafety.Reset();
        this.pendingHotkey.Clear();
        this.hotkeyCaptureDelayFrames = 0;
        this.ClearPressedGameKeysDuringHotkeyRecording();
        this.SaveConfig();
    }

    private void CheckHotkeyEditorSafety()
    {
        if (this.hotkeySafety.IsRunning && this.hotkeySafety.ElapsedMilliseconds > 5000)
        {
            this.CancelHotkeyRecording();
        }
    }

    private void BeginHotkeyRecording()
    {
        this.hotkeyRecording = true;
        this.hotkeyCaptureDelayFrames = 1;
        this.pendingHotkey.Clear();
        this.hotkeySafety.Restart();

        foreach (var key in KeyState.GetValidVirtualKeys())
        {
            if (KeyState[key])
            {
                KeyState[key] = false;
            }
        }
    }

    private void CancelHotkeyRecording()
    {
        this.hotkeyRecording = false;
        this.hotkeyCaptureDelayFrames = 0;
        this.pendingHotkey.Clear();
        this.hotkeySafety.Reset();
    }

    private static bool IsMouseButtonKey(VirtualKey key)
    {
        var value = (int)key;
        return value is >= 0x01 and <= 0x06;
    }

    private static VirtualKey[] NormalizeHotkey(IEnumerable<VirtualKey> keys)
    {
        var validKeys = KeyState.GetValidVirtualKeys().ToHashSet();
        return keys
            .Where(key => validKeys.Contains(key) && !IsMouseButtonKey(key))
            .Distinct()
            .OrderBy(key => (int)key)
            .ToArray();
    }

    private static string GetKeyName(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.KEY_0 => "0",
            VirtualKey.KEY_1 => "1",
            VirtualKey.KEY_2 => "2",
            VirtualKey.KEY_3 => "3",
            VirtualKey.KEY_4 => "4",
            VirtualKey.KEY_5 => "5",
            VirtualKey.KEY_6 => "6",
            VirtualKey.KEY_7 => "7",
            VirtualKey.KEY_8 => "8",
            VirtualKey.KEY_9 => "9",
            VirtualKey.CONTROL => "Ctrl",
            VirtualKey.MENU => "Alt",
            VirtualKey.SHIFT => "Shift",
            _ => key.ToString(),
        };
    }

    private void SuppressFocusedTextInputHotkeyCharacter(AtkComponentTextInput* textInput, VirtualKey[] keys)
    {
        if (textInput == null || !textInput->Enabled || !textInput->IsActive)
        {
            return;
        }

        if (!HasModifierKey(keys) || !TryGetSingleTextKey(keys, out var textKey))
        {
            return;
        }

        foreach (var key in keys)
        {
            KeyState[key] = false;
        }

        KeyState[textKey] = false;
        _ = Framework.RunOnTick(() =>
        {
            var activeInput = this.keybindTextInput;
            if (activeInput == null || !activeInput->Enabled || !activeInput->IsActive)
            {
                activeInput = GetFocusedTextInput();
            }

            if (activeInput == null || !activeInput->Enabled || !activeInput->IsActive)
            {
                return;
            }

            SendBackspaceKeyPress();
        }, delayTicks: 1);
    }

    private static bool HasModifierKey(IEnumerable<VirtualKey> keys)
    {
        return keys.Any(IsModifierKey);
    }

    private static bool TryGetSingleTextKey(IEnumerable<VirtualKey> keys, out VirtualKey textKey)
    {
        textKey = default;
        var textKeys = keys.Where(IsTextKey).Distinct().ToArray();
        if (textKeys.Length != 1)
        {
            return false;
        }

        textKey = textKeys[0];
        return true;
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        var value = (int)key;
        return value is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5;
    }

    private static bool IsTextKey(VirtualKey key)
    {
        var value = (int)key;
        return value is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;
    }



    private void SetButtonPlacement(int placement)
    {
        // Picking Left/Right will always go back to the native chat anchor.
        // Manual dragging stores a custom offset, so both old and current position flags are cleared here.
        this.Config.ButtonPlacement = Math.Clamp(placement, 0, ButtonPlacementLabels.Length - 1);
        this.Config.HasCustombPosition = false;
        this.Config.HasCustomButtonPosition = false;
        this.Config.UsesRelativeButtonOffset = false;
        this.Config.bPosition = Vector2.Zero;
        this.Config.ButtonPosition = Vector2.Zero;
        this.Config.ButtonOffset = Vector2.Zero;
        this.bPositionDirty = false;
        this.editbPosition = false;
        this.draggingButton = false;
    }

    private void DrawConfig(ref bool hasChanged)
    {
        this.ConfigChanged();

        if (this.DrawHotkeyConfigEditor("Open symbol list pressing:", this.Config.ToggleHotkey, out var newKeys))
        {
            this.Config.ToggleHotkey = newKeys;
            hasChanged = true;
        }

        // Button position controls only the main chat heart button.
        // Context buttons like PF/Message Book will still stay next to their own text inputs.
        var buttonPlacement = Math.Clamp(this.Config.ButtonPlacement, 0, ButtonPlacementLabels.Length - 1);
        ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X * 0.48f));
        if (ImGui.BeginCombo("Button position", ButtonPlacementLabels[buttonPlacement]))
        {
            for (var i = 0; i < ButtonPlacementLabels.Length; i++)
            {
                var selected = buttonPlacement == i;
                if (ImGui.Selectable(ButtonPlacementLabels[i], selected))
                {
                    this.SetButtonPlacement(i);
                    hasChanged = true;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        var replaceSpecificTexts = this.Config.ReplaceSpecificTextsForSymbols;
        if (ImGui.Checkbox("Automatic replace text to symbol", ref replaceSpecificTexts))
        {
            this.SetAutomaticTextReplacement(replaceSpecificTexts, printEnabledMessage: true);
            hasChanged = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Texts like <3, :dice: etc will be automaticaly replaced\nwith it actual symbol");
        }

        ImGui.SameLine();
        if (this.DrawConfigIconButton("##OpenAutoSymbolList", 0xF00B))
        {
            this.autoSymbolListOpen = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Symbol list and creation");
        }

        // This only swaps the popup list to Dalamud style colors.
        // Leaving it off keeps the current FFXIV/chat-theme look.
        var useDalamudTheme = this.Config.UseDalamudTheme;
        if (ImGui.Checkbox("Use Dalamud theme", ref useDalamudTheme))
        {
            this.Config.UseDalamudTheme = useDalamudTheme;
            hasChanged = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use your Dalamud theme instead of FFXIV themes for the popup list.");
        }

        var closeOnFocus = this.Config.ClosePopupOnLostFocus;
        if (ImGui.Checkbox("Close popup on lost focus", ref closeOnFocus))
        {
            this.Config.ClosePopupOnLostFocus = closeOnFocus;
            hasChanged = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("If enabled, symbol list will auto-close when clicked outside of it.");
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader($"Custom Entries ({this.Config.Custom.Count})###cEntriesHeader", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var delete = -1;
            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4f * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemSpacing.Y)))
            {
                for (var i = 0; i < this.Config.Custom.Count; i++)
                {
                    if (ImGui.SmallButton($"{(char)SeIconChar.Cross}##deleteCustom{i}"))
                    {
                        delete = i;
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X));
                    var val = this.Config.Custom[i];
                    if (ImGui.InputText($"##custom_{i}", ref val, 128))
                    {
                        this.Config.Custom[i] = val;
                        hasChanged = true;
                    }

                    if (string.IsNullOrWhiteSpace(val) && !ImGui.IsItemActive())
                    {
                        delete = i;
                    }
                }

                if (delete >= 0)
                {
                    this.Config.Custom.RemoveAt(delete);
                    hasChanged = true;
                }

                ImGui.Separator();
                ImGui.TextDisabled("Available in the \"Custom\" tab of the list popup");
                ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X - 76f * ImGuiHelpers.GlobalScale));
                ImGui.InputText("##newCustomEntry", ref this.newCustomEntry, 128);
                var newEntryPos = ImGui.GetItemRectMin();
                var newEntrySize = ImGui.GetItemRectSize();
                this.configCustomEntryActive = ImGui.IsItemActive();

                // Save the exact ImGui input rect while it is alive.
                // The popup uses this as a fake button anchor, since there is no native addon node here.
                if (this.configCustomEntryActive)
                {
                    this.configCustomEntryPopupAnchorPos = newEntryPos;
                    this.configCustomEntryPopupAnchorSize = newEntrySize;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Open the symbol list through keybind to insert here");
                }

                // Once the popup gets clicked, ImGui focus can leave the input and hide the real caret.
                // Draw a tiny local caret at the end of the text instead of forcing focus and selecting everything.
                if (this.configCustomEntryPopupOpen && !this.configCustomEntryActive)
                {
                    var style = ImGui.GetStyle();
                    var drawList = ImGui.GetWindowDrawList();
                    var textSize = ImGui.CalcTextSize(this.newCustomEntry);
                    var caretX = Math.Min(
                        newEntryPos.X + newEntrySize.X - style.FramePadding.X,
                        newEntryPos.X + style.FramePadding.X + textSize.X + 1f * ImGuiHelpers.GlobalScale);
                    var caretTop = newEntryPos.Y + 4f * ImGuiHelpers.GlobalScale;
                    var caretBottom = newEntryPos.Y + newEntrySize.Y - 4f * ImGuiHelpers.GlobalScale;

                    if ((ImGui.GetTime() % 1.0) < 0.55)
                    {
                        drawList.AddLine(
                            new Vector2(caretX, caretTop),
                            new Vector2(caretX, caretBottom),
                            ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]),
                            Math.Max(1f, ImGuiHelpers.GlobalScale));
                    }
                }

                if (this.configCustomEntryActive)
                {
                    // Raw key state is used here because this field is an ImGui input owned by the config window.
                    if (this.CheckHotkeyStateRaw(this.Config.ToggleHotkey))
                    {
                        this.configCustomEntryPopupOpen = !this.configCustomEntryPopupOpen;
                        this.selectedPopupTab = PopupTab.Symbols;
                        this.popupClickGuardFrames = 2;
                    }
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("+ Add##addCustomEntry"))
                {
                    var entry = this.newCustomEntry.Trim();
                    if (!string.IsNullOrWhiteSpace(entry))
                    {
                        this.Config.Custom.Add(entry);
                        this.newCustomEntry = string.Empty;
                        hasChanged = true;
                    }
                }
            }
        }
    }

    private void FrameworkUpdate(IFramework framework)
    {
        this.FlushButtonPositionSave();
        this.TryRewriteHeartShortcutFromChatKeys();

        if (this.TryOpenConfigCustomEntryPopup())
        {
            return;
        }

        if (this.hotkeyRecording)
        {
            if (this.ProcessHotkeyRecordingInput())
            {
                var keys = NormalizeHotkey(this.pendingHotkey);
                if (keys.Length > 0)
                {
                    this.FinishHotkeyRecording(keys);
                }
            }

            return;
        }

        if (this.TryOpenWatchedInputPopup())
        {
            return;
        }

        this.TryOpenKeybindPopupFromCurrentFocus();
    }

    // The actual replacement loop is based on the current text in the vanilla ChatLog input.
    // Earlier attempts tried to track key presses but that breaks as soon as someone mistypes,
    // clicks the caret somewhere else, uses arrows or fixes the text with backspace. Reading the
    // input text itself is much closer to how people actually type. I only look for a replacement
    // at the end of the current message and skip anything starting with "/" so commands are left alone.
    private void TryRewriteHeartShortcutFromChatKeys()
    {
        if (!this.Config.ReplaceSpecificTextsForSymbols)
        {
            this.textReplacementPending = false;
            return;
        }

        try
        {
            if (this.textReplacementPending)
            {
                return;
            }

            if (!this.IsGameWindowFocused())
            {
                return;
            }

            var chatUnit = GameGui.GetAddonByName(ChatLogAddonName);
            var chatLog = GameGui.GetAddonByName<AddonChatLog>(ChatLogAddonName);

            if (chatUnit.IsNull || !chatUnit.IsReady || !chatUnit.IsVisible || chatLog == null || chatLog->TextInput == null)
            {
                return;
            }

            var input = chatLog->TextInput;
            if (!input->Enabled || !input->IsActive)
            {
                return;
            }

            var text = GetTextInputString(input);
            if (string.IsNullOrEmpty(text) || text.StartsWith("/", StringComparison.Ordinal) || !this.TryGetTrailingTextReplacement(text, out var charsToRemove, out var symbol))
            {
                return;
            }

            this.textReplacementPending = true;
            _ = Framework.RunOnTick(() => this.ReplaceTrailingShortcutInChatInput(charsToRemove, symbol), delayTicks: 1);
        }
        catch (Exception ex)
        {
            this.textReplacementPending = false;
            Log.Debug($"QuickSymbols chat input text replacement skipped. {ex}");
        }
    }

    // Once a match is found I do not rewrite the whole input with SetText.
    // SetText worked but it made the field flicker and could cause the raw shortcut text to come back
    // after the next key press. The safer is: confirm the same shortcut is still at the end, send
    // only the needed backspaces, then insert the final symbol through the same native InsertText path
    // QuickSymbols already uses everywhere else. It feels a bit indirect but it keeps the chat input
    // behaving like the player typed the symbol normally.
    private void ReplaceTrailingShortcutInChatInput(int charsToRemove, string symbol)
    {
        try
        {
            if (!this.IsGameWindowFocused())
            {
                this.textReplacementPending = false;
                return;
            }

            var chatUnit = GameGui.GetAddonByName(ChatLogAddonName);
            var chatLog = GameGui.GetAddonByName<AddonChatLog>(ChatLogAddonName);

            if (chatUnit.IsNull || !chatUnit.IsReady || !chatUnit.IsVisible || chatLog == null || chatLog->TextInput == null)
            {
                this.textReplacementPending = false;
                return;
            }

            var input = chatLog->TextInput;
            if (!input->Enabled || !input->IsActive)
            {
                this.textReplacementPending = false;
                return;
            }

            var text = GetTextInputString(input);
            if (text.StartsWith("/", StringComparison.Ordinal)
                || !this.TryGetTrailingTextReplacement(text, out var currentRemove, out var currentSymbol)
                || currentRemove != charsToRemove
                || !string.Equals(currentSymbol, symbol, StringComparison.Ordinal))
            {
                this.textReplacementPending = false;
                return;
            }

            SendKeyPress(VirtualKeyBackspace, charsToRemove);

            _ = Framework.RunOnTick(() =>
            {
                try
                {
                    if (!this.IsGameWindowFocused())
                    {
                        return;
                    }

                    var nextChatUnit = GameGui.GetAddonByName(ChatLogAddonName);
                    var nextChatLog = GameGui.GetAddonByName<AddonChatLog>(ChatLogAddonName);

                    if (nextChatUnit.IsNull || !nextChatUnit.IsReady || !nextChatUnit.IsVisible || nextChatLog == null || nextChatLog->TextInput == null)
                    {
                        return;
                    }

                    if (!nextChatLog->TextInput->Enabled || !nextChatLog->TextInput->IsActive)
                    {
                        return;
                    }

                    nextChatLog->TextInput->InsertText(symbol, false);
                    this.AdvanceCaretOnNextTick(this.AdvanceChatCaretRightIfStillActive, symbol);
                }
                catch (Exception ex)
                {
                    Log.Debug($"QuickSymbols chat input replacement insert skipped. {ex}");
                }
                finally
                {
                    this.textReplacementPending = false;
                }
            }, delayTicks: 1);
        }
        catch (Exception ex)
        {
            this.textReplacementPending = false;
            Log.Debug($"QuickSymbols chat input replacement skipped. {ex}");
        }
    }

    private static string GetTextInputString(AtkComponentTextInput* input)
    {
        if (input == null)
        {
            return string.Empty;
        }

        var raw = input->AtkComponentInputBase.RawString.ToString();
        if (!string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        return input->AtkComponentInputBase.EvaluatedString.ToString();
    }

    // Built-in auto-symbol shortcuts.
    // The array is sorted by shortcut length so longer entries win first. That matters for things like
    // [10] before [1] and it also makes future additions less fragile. User-created replacements are
    // added after these defaults but the validation UI blocks users from creating duplicated names/symbols
    // so this list stays the source of truth for the shipped shortcuts.
    private static readonly (string Text, string Symbol)[] DefaultTextReplacements =
    [
        (":colectible:", "\uE03D"),
        (":flower:", "\uE05D"),
        (":sprout:", "\uE034"),
        (":clock:", "\uE031"),
        (":shard:", "\uE048"),
        (":dice:", "\uE03E"),
        (":star:", "★"),
        (":plus:", "\uE04E"),
        (":am:", "\uE06D"),
        (":pm:", "\uE06E"),
        (":hq:", "\uE03C"),
        ("[10]", "\uE099"),
        ("[11]", "\uE09A"),
        ("[12]", "\uE09B"),
        ("[13]", "\uE09C"),
        ("[14]", "\uE09D"),
        ("[15]", "\uE09E"),
        ("[16]", "\uE09F"),
        ("[17]", "\uE0A0"),
        ("[18]", "\uE0A1"),
        ("[19]", "\uE0A2"),
        ("[20]", "\uE0A3"),
        ("[21]", "\uE0A4"),
        ("[22]", "\uE0A5"),
        ("[23]", "\uE0A6"),
        ("[24]", "\uE0A7"),
        ("[25]", "\uE0A8"),
        ("[26]", "\uE0A9"),
        ("[27]", "\uE0AA"),
        ("[28]", "\uE0AB"),
        ("[29]", "\uE0AC"),
        ("[30]", "\uE0AD"),
        ("[31]", "\uE0AE"),
        (":x:", "\uE04C"),
        ("[a]", "\uE071"),
        ("[b]", "\uE072"),
        ("[c]", "\uE073"),
        ("[d]", "\uE074"),
        ("[e]", "\uE075"),
        ("[f]", "\uE076"),
        ("[g]", "\uE077"),
        ("[h]", "\uE078"),
        ("[i]", "\uE079"),
        ("[j]", "\uE07A"),
        ("[k]", "\uE07B"),
        ("[l]", "\uE07C"),
        ("[m]", "\uE07D"),
        ("[n]", "\uE07E"),
        ("[o]", "\uE07F"),
        ("[p]", "\uE080"),
        ("[q]", "\uE081"),
        ("[r]", "\uE082"),
        ("[s]", "\uE083"),
        ("[t]", "\uE084"),
        ("[u]", "\uE085"),
        ("[v]", "\uE086"),
        ("[w]", "\uE087"),
        ("[x]", "\uE088"),
        ("[y]", "\uE089"),
        ("[z]", "\uE08A"),
        ("[1]", "\uE090"),
        ("[2]", "\uE091"),
        ("[3]", "\uE092"),
        ("[4]", "\uE093"),
        ("[5]", "\uE094"),
        ("[6]", "\uE095"),
        ("[7]", "\uE096"),
        ("[8]", "\uE097"),
        ("[9]", "\uE098"),
        ("<3", "♥"),
    ];

    private bool TryGetTrailingTextReplacement(string text, out int charsToRemove, out string symbol)
    {
        foreach (var replacement in this.GetAllTextReplacements())
        {
            if (!text.EndsWith(replacement.Text, StringComparison.Ordinal))
            {
                continue;
            }

            charsToRemove = replacement.Text.Length;
            symbol = replacement.Symbol;
            return true;
        }

        charsToRemove = 0;
        symbol = string.Empty;
        return false;
    }

    private IEnumerable<(string Text, string Symbol)> GetAllTextReplacements()
    {
        foreach (var replacement in DefaultTextReplacements)
        {
            yield return replacement;
        }

        this.Config.CustomTextReplacements ??= [];
        foreach (var custom in this.Config.CustomTextReplacements
                     .Where(rule => !string.IsNullOrWhiteSpace(rule.Text) && !string.IsNullOrEmpty(rule.Symbol))
                     .OrderByDescending(rule => rule.Text.Length))
        {
            yield return (custom.Text, custom.Symbol);
        }
    }

    private bool IsGameWindowFocused()
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    private bool TryOpenConfigCustomEntryPopup()
    {
        // Also check from FrameworkUpdate so the config field can react even when draw timing misses the key edge.
        // This is just for the Quick Symbols config input; normal game inputs still use their own path below.
        if (!this.configWindowOpen || !this.configCustomEntryActive)
        {
            return false;
        }

        if (!this.CheckHotkeyStateRaw(this.Config.ToggleHotkey))
        {
            return false;
        }

        if (this.configCustomEntryPopupOpen)
        {
            this.configCustomEntryPopupOpen = false;
            return true;
        }

        this.CloseAllPopups(clearKeybindTarget: true);
        this.configCustomEntryPopupOpen = true;
        this.selectedPopupTab = PopupTab.Symbols;
        this.popupClickGuardFrames = 2;
        return true;
    }

    private bool TryOpenKeybindPopupFromCurrentFocus()
    {
        var focused = GetFocusedTextInput();
        if (focused == null && !this.keybindPopupOpen)
        {
            return false;
        }

        if (!this.CheckHotkeyState(this.Config.ToggleHotkey))
        {
            return false;
        }

        if (this.keybindPopupOpen)
        {
            this.CloseAllPopups(clearKeybindTarget: true);
            return true;
        }

        if (focused == null)
        {
            return false;
        }

        var scale = ImGuiHelpers.GlobalScale;
        this.keybindTextInput = focused;
        this.SuppressFocusedTextInputHotkeyCharacter(focused, this.Config.ToggleHotkey);

        this.keybindPopupAnchorSize = new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));
        this.keybindPopupAnchorPos = ClampPositionToScreen(ImGui.GetIO().MousePos + new Vector2(12f * scale, 12f * scale), this.keybindPopupAnchorSize);
        this.OpenKeybindPopup();
        return true;
    }

    private static AtkComponentTextInput* GetFocusedTextInput()
    {
        var atkStage = AtkStage.Instance();
        if (atkStage == null)
        {
            return null;
        }

        var focus = atkStage->GetFocus();
        var node = focus;
        // Try to move up the hierarchy to find the input
        for (var i = 0; node != null && i < 8; i++)
        {
            var input = node->GetAsAtkComponentTextInput();
            if (input != null && input->Enabled)
            {
                return input;
            }

            node = node->ParentNode;
        }

        if (focus == null || focus->ParentNode == null)
        {
            return null;
        }

        var focusParentComponent = focus->ParentNode->GetComponent();
        if (focusParentComponent == null)
        {
            return null;
        }

        var componentInfo = (AtkUldComponentInfo*)focusParentComponent->UldManager.Objects;
        if (componentInfo == null || componentInfo->ComponentType != ComponentType.TextInput)
        {
            return null;
        }

        var inputComponent = (AtkComponentTextInput*)focusParentComponent;
        return inputComponent->Enabled ? inputComponent : null;
    }

    private bool IsLeftMouseDown()
    {
        if (ImGui.GetIO().MouseDown[0])
        {
            return true;
        }

        try
        {
            if ((GetAsyncKeyState(0x01) & unchecked((short)0x8000)) != 0)
            {
                return true;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            return KeyState[(VirtualKey)0x01];
        }
        catch
        {
            return false;
        }
    }

    private bool WasLeftMouseClickedBySystem()
    {
        try
        {
            return (GetAsyncKeyState(0x01) & 0x0001) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void Draw()
    {
        var mouseDownNow = this.IsLeftMouseDown();
        this.leftMouseClickedThisFrame = (mouseDownNow && !this.leftMouseWasDown) || this.WasLeftMouseClickedBySystem();

        this.DrawConfigWindow();
        this.TryOpenKeybindPopupFromCurrentFocus();

        if (GameGui.GameUiHidden)
        {
            this.leftMouseWasDown = mouseDownNow;
            return;
        }

        var Theme = GetCurrentGameUiTheme();
        var colors = UiColors.FromGameTheme(Theme, null);
        var ChatPopup = false;
        var PartyFinderPopup = false;
        var MsgBookPopup = false;
        var MacroPopup = false;
        var TofuInputPopup = false;

        // Chat Log button
        if (this.TryGetNativeChatButtonPlacement(out var nPos, out var nSize, out colors))
        {
            this.nativebPos = nPos;
            this.currentbSize = nSize;
            this.currentbPos = this.GetCurrentbPosition(nPos, nSize);

            if (this.Config.ButtonPlacement == ButtonPlacementKeybindOnly)
            {
                // "Keybind Only" keeps the chat integration active but intentionally hides the heart button.
                // The popup can still open from the keybind when a supported text input is focused.
                this.popupOpen = false;
                this.editbPosition = false;
            }
            else if (this.keybindPopupOpen)
            {
                this.DrawHeartButtonGhost(this.currentbPos, nSize, colors, this.editbPosition);
            }
            else
            {
                this.DrawChatButton(this.currentbPos, nSize, colors);
            }

            ChatPopup = this.popupOpen;
        }
        else
        {
            this.popupOpen = false;
            this.editbPosition = false;
        }
        // Party Finder button
        if (this.TryGetRecruitmentCommentTarget(out var pfTarget))
        {
            var scale = ImGuiHelpers.GlobalScale;
            this.partyFinderbSize = this.currentbSize.X > 0.1f && this.currentbSize.Y > 0.1f
                ? this.currentbSize
                : new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));
            this.partyFinderbPos = ClampPositionToScreen(
                new Vector2(pfTarget.Position.X + 6f * scale, pfTarget.Position.Y + pfTarget.Size.Y + 2f * scale),
                this.partyFinderbSize);

            this.DrawContextButton(
                "##QuickSymbolsRecruitmentCommentButtonOverlay",
                "##QuickSymbolsRecruitmentCommentOpenButton",
                this.partyFinderbPos,
                this.partyFinderbSize,
                colors,
                ref this.partyFinderPopupOpen);
            PartyFinderPopup = this.partyFinderPopupOpen;
        }
        else
        {
            this.partyFinderPopupOpen = false;
        }
        // Guestbook/Message Book button
        if (this.TryGetMessageBookInputTarget(out var messageTarget))
        {
            var scale = ImGuiHelpers.GlobalScale;
            this.messageBookbSize = this.currentbSize.X > 0.1f && this.currentbSize.Y > 0.1f
                ? this.currentbSize
                : new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));
            this.messageBookbPos = ClampPositionToScreen(
                new Vector2(messageTarget.Position.X + 6f * scale, messageTarget.Position.Y + messageTarget.Size.Y + 3f * scale),
                this.messageBookbSize);

            this.DrawContextButton(
                "##QuickSymbolsMessageBookButtonOverlay",
                "##QuickSymbolsMessageBookOpenButton",
                this.messageBookbPos,
                this.messageBookbSize,
                colors,
                ref this.messageBookPopupOpen);
            MsgBookPopup = this.messageBookPopupOpen;
        }
        else
        {
            this.messageBookPopupOpen = false;
        }
        // User Macros button
        // Place the picker button under the big macro body input, not the macro name field.
        if (this.TryGetMacroInputTarget(out var macroTarget))
        {
            var scale = ImGuiHelpers.GlobalScale;
            this.macrobSize = this.currentbSize.X > 0.1f && this.currentbSize.Y > 0.1f
                ? this.currentbSize
                : new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));
            this.macrobPos = ClampPositionToScreen(
                new Vector2(macroTarget.Position.X + 6f * scale, macroTarget.Position.Y + macroTarget.Size.Y + 3f * scale),
                this.macrobSize);

            this.DrawContextButton(
                "##QuickSymbolsMacroButtonOverlay",
                "##QuickSymbolsMacroOpenButton",
                this.macrobPos,
                this.macrobSize,
                colors,
                ref this.macroPopupOpen);
            MacroPopup = this.macroPopupOpen;
        }
        else
        {
            this.macroPopupOpen = false;
        }
        // Strategy Board button
        // TofuInputString is a generic prompt addon, so anchor to its detected text field instead of hard-coding a screen position.
        if (this.TryGetTofuInputStringTarget(out var tofuTarget))
        {
            var scale = ImGuiHelpers.GlobalScale;
            this.tofuInputbSize = this.currentbSize.X > 0.1f && this.currentbSize.Y > 0.1f
                ? this.currentbSize
                : new Vector2(Math.Clamp(24f * scale, 18f * scale, 28f * scale));
            this.tofuInputbPos = ClampPositionToScreen(
                new Vector2(tofuTarget.Position.X + 6f * scale, tofuTarget.Position.Y + tofuTarget.Size.Y + 3f * scale),
                this.tofuInputbSize);

            this.DrawContextButton(
                "##QuickSymbolsTofuInputButtonOverlay",
                "##QuickSymbolsTofuInputOpenButton",
                this.tofuInputbPos,
                this.tofuInputbSize,
                colors,
                ref this.tofuInputPopupOpen);
            TofuInputPopup = this.tofuInputPopupOpen;
        }
        else
        {
            this.tofuInputPopupOpen = false;
        }
        // Popup Rendering
        if (ChatPopup)
        {
            this.DrawSymbolsPopup(
                "Chat",
                colors,
                this.currentbPos,
                this.currentbSize,
                PopupPlacement.AboveRight, includePositionEditor: true, SymbolInsertTarget.Chat, ref this.popupOpen);
        }

        if (PartyFinderPopup)
        {
            this.DrawSymbolsPopup(
                "PartyFinder",
                colors,
                this.partyFinderbPos,
                this.partyFinderbSize,
                PopupPlacement.Below, includePositionEditor: false, SymbolInsertTarget.RecruitmentComment, ref this.partyFinderPopupOpen);
        }

        if (MsgBookPopup)
        {
            this.DrawSymbolsPopup(
                "MessageBook",
                colors,
                this.messageBookbPos,
                this.messageBookbSize, PopupPlacement.Below, includePositionEditor: false, SymbolInsertTarget.MessageBookInput, ref this.messageBookPopupOpen);
        }

        if (MacroPopup)
        {
            this.DrawSymbolsPopup(
                "Macro",
                colors,
                this.macrobPos,
                this.macrobSize, PopupPlacement.Below, includePositionEditor: false, SymbolInsertTarget.MacroInput, ref this.macroPopupOpen);
        }

        if (TofuInputPopup)
        {
            this.DrawSymbolsPopup(
                "TofuInputString",
                colors,
                this.tofuInputbPos,
                this.tofuInputbSize, PopupPlacement.Below, includePositionEditor: false, SymbolInsertTarget.TofuInputString, ref this.tofuInputPopupOpen);
        }

        if (this.keybindPopupOpen)
        {
            this.DrawSymbolsPopup(
                "Keybind",
                colors,
                this.keybindPopupAnchorPos,
                this.keybindPopupAnchorSize, PopupPlacement.Below, includePositionEditor: false, SymbolInsertTarget.FocusedTextInput, ref this.keybindPopupOpen);
        }

        if (this.ipcPopupOpen)
        {
            this.DrawSymbolsPopup(
                "Ipc",
                colors,
                this.ipcPopupAnchorPos,
                this.ipcPopupAnchorSize, PopupPlacement.Below, includePositionEditor: false, SymbolInsertTarget.IpcCallback, ref this.ipcPopupOpen);
        }

        this.leftMouseWasDown = mouseDownNow;
    }

    private void OpenMainPopup()
    {
        this.CloseAllPopups(clearKeybindTarget: true);
        this.selectedPopupTab = PopupTab.Symbols;
        this.popupOpen = true;
        this.popupClickGuardFrames = 2;
    }

    private void OpenNamedPopup(ref bool popup)
    {
        this.CloseAllPopups(clearKeybindTarget: true);
        this.selectedPopupTab = PopupTab.Symbols;
        popup = true;
        this.popupClickGuardFrames = 2;
    }

    private void OpenKeybindPopup()
    {
        this.CloseAllPopups(clearKeybindTarget: false);
        this.selectedPopupTab = PopupTab.Symbols;
        this.keybindPopupOpen = true;
        this.keybindPopupLivePos = Vector2.Zero;
        this.keybindPopupPosValid = false;
        this.popupClickGuardFrames = 2;
    }

    private void CloseAllPopups(bool clearKeybindTarget)
    {
        this.popupOpen = false;
        this.partyFinderPopupOpen = false;
        this.messageBookPopupOpen = false;
        this.macroPopupOpen = false;
        this.tofuInputPopupOpen = false;
        this.keybindPopupOpen = false;
        this.ipcPopupOpen = false;
        this.ipcPopupOwner = null;
        this.ipcActiveOwner = null;
        this.ipcActiveFrames = 0;
        this.ipcPopupRaiseFrames = 0;
        this.keybindPopupPosValid = false;
        if (clearKeybindTarget)
        {
            this.keybindTextInput = null;
        }
    }

    private Vector2 GetCurrentbPosition(Vector2 nPos, Vector2 nSize)
    {
        if (!this.Config.HasCustombPosition)
        {
            return nPos;
        }

        if (!this.Config.UsesRelativeButtonOffset)
        {
            this.Config.ButtonOffset = this.Config.bPosition - nPos;
            this.Config.UsesRelativeButtonOffset = true;
            this.bPositionDirty = true;
        }

        var desired = nPos + this.Config.ButtonOffset;
        var clamped = ClampPositionToScreen(desired, nSize);

        if (Vector2.DistanceSquared(desired, clamped) > 0.01f)
        {
            this.Config.ButtonOffset = clamped - nPos;
            this.Config.bPosition = clamped;
            this.bPositionDirty = true;
        }

        this.QueueButtonPositionSave();
        return clamped;
    }

    private bool TryGetNativeChatButtonPlacement(out Vector2 bPos, out Vector2 bSize, out UiColors colors)
    {
        bPos = Vector2.Zero;
        bSize = Vector2.Zero;
        colors = UiColors.Default;

        var chatUnit = GameGui.GetAddonByName(ChatLogAddonName);
        if (chatUnit.IsNull || !chatUnit.IsReady || !chatUnit.IsVisible)
        {
            return false;
        }

        var chatLog = GameGui.GetAddonByName<AddonChatLog>(ChatLogAddonName);
        if (chatLog == null)
        {
            return false;
        }

        colors = UiColors.FromGameTheme(GetCurrentGameUiTheme(), chatLog);

        var scale = Math.Clamp(chatUnit.Scale, 0.65f, 2.4f);
        var gap = Math.Max(2f, 2f * scale);

        if (this.Config.ButtonPlacement == ButtonPlacementRight && this.TryGetRightChatButtonPlacement(chatLog, scale, gap, out bPos, out bSize))
        {
            return true;
        }

        //Try to find the channel dropdown
        if (chatLog->ChannelSelectDropDown != null)
        {
            var node = chatLog->ChannelSelectDropDown->AtkComponentBase.OwnerNode;
            if (node != null && node->AtkResNode.IsVisible())
            {
                var res = &node->AtkResNode;
                var nodeHeight = GetNodeScreenSize(res, scale).Y;
                var sq = Math.Clamp(nodeHeight, 18f * scale, 28f * scale);

                bSize = new Vector2(sq, sq);
                bPos = new Vector2(
                    res->ScreenX - sq - gap,
                    res->ScreenY + Math.Max(0f, (nodeHeight - sq) * 0.5f));
                return true;
            }
        }

        if (chatLog->CurrentChannelTextNode != null && chatLog->CurrentChannelTextNode->AtkResNode.IsVisible())
        {
            var res = &chatLog->CurrentChannelTextNode->AtkResNode;
            var h = GetNodeScreenSize(res, scale).Y;
            var sq = Math.Clamp(h + 8f * scale, 18f * scale, 28f * scale);

            bSize = new Vector2(sq, sq);
            bPos = new Vector2(
                res->ScreenX - sq - gap,
                res->ScreenY - 4f * scale);
            return true;
        }

        // Last resort: bottom left corner
        var fSize = Math.Clamp(24f * scale, 18f, 32f * scale);
        bSize = new Vector2(fSize, fSize);
        bPos = new Vector2(
            chatUnit.Position.X + 4f * scale,
            chatUnit.Position.Y + chatUnit.ScaledSize.Y - fSize - 4f * scale);
        return true;
    }


    private bool TryGetRightChatButtonPlacement(AddonChatLog* chatLog, float scale, float gap, out Vector2 bPos, out Vector2 bSize)
    {
        bPos = Vector2.Zero;
        bSize = Vector2.Zero;

        // "Right of Chat" follows the native chat text input instead of the channel selector.
        // This keeps the button near the message box and avoids the left-edge off-screen issue that commonly happens.
        if (chatLog == null || chatLog->TextInput == null)
        {
            return false;
        }

        var node = chatLog->TextInput->AtkComponentInputBase.AtkComponentBase.OwnerNode;
        if (node == null || !node->AtkResNode.IsVisible())
        {
            return false;
        }

        var res = &node->AtkResNode;
        var size = GetNodeScreenSize(res, scale);
        if (size.X <= 10f || size.Y <= 10f)
        {
            return false;
        }

        var sq = Math.Clamp(size.Y, 18f * scale, 28f * scale);
        bSize = new Vector2(sq, sq);
        bPos = new Vector2(
            res->ScreenX + size.X + gap,
            res->ScreenY + Math.Max(0f, (size.Y - sq) * 0.5f));

        bPos = ClampPositionToScreen(bPos, bSize);
        return true;
    }


    private static void DrawHeartIcon(ImDrawListPtr drawList, Vector2 min, Vector2 size, Vector4 color)
    {
        // Use the FontAwesome heart instead of the text glyph so the shape centers properly.
        // The tiny upward nudge is the optical fix that made the ChatLog button look centered in-game.
        using var iconFont = PushQuickSymbolsIconFont();
        var text = char.ConvertFromUtf32(0xF004);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * 0.82f;
        var textSize = ImGui.CalcTextSize(text) * 0.82f;
        var pos = min + (size - textSize) * 0.5f + new Vector2(0f, -1f);
        drawList.AddText(font, fontSize, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), Color(color), text);
    }

    private void DrawHeartButtonGhost(Vector2 position, Vector2 size, UiColors colors, bool editing)
    {
        // Ghost uses the same pixel trims as the clickable button so edit mode does not jump visually.
        var drawList = ImGui.GetBackgroundDrawList();
        var min = position;
        var iconSize = new Vector2(Math.Max(1f, size.X - 1f), size.Y);
        var buttonSize = new Vector2(iconSize.X, Math.Max(1f, size.Y - 1f));
        var buttonMin = min + new Vector2(0f, 1f);
        var buttonMax = min + buttonSize;
        var background = editing ? colors.EditButton : colors.Button;
        var rounding = Math.Max(2f, size.Y * 0.14f);

        drawList.AddRectFilled(buttonMin, buttonMax, Color(background), rounding);
        drawList.AddRect(buttonMin, buttonMax, Color(colors.Border), rounding, ImDrawFlags.None, Math.Max(1f, size.Y * 0.045f));
        DrawHeartIcon(drawList, min, iconSize, colors.Text);
    }

    private void DrawChatButton(Vector2 position, Vector2 size, UiColors colors)
    {
        var clicked = this.DrawHeartButtonOverlay(
            "##QuickSymbolsChatButtonOverlay",
            "##QuickSymbolsOpenButton", position, size, colors,
            this.editbPosition, out var active);

        if (this.editbPosition)
        {
            this.HandleButtonDragging(size, active);
        }
        else if (clicked)
        {
            if (this.popupOpen)
                this.CloseAllPopups(clearKeybindTarget: true);
            else
                this.OpenMainPopup();
        }
    }

    private void DrawContextButton(string windowId, string buttonId, Vector2 position, Vector2 size, UiColors colors, ref bool isOpen)
    {
        // Context buttons borrow the current ChatLog sizing/colors when available.
        // Keep the +1px icon offset here because PF/House/extra prompt buttons sit slightly different than the chat button.
        var cloneSize = size;
        var cloneColors = colors;

        if (this.TryGetNativeChatButtonPlacement(out _, out var chatSize, out var chatColors))
        {
            cloneSize = chatSize;
            cloneColors = chatColors;
        }

        var clicked = this.DrawHeartButtonOverlay(
            windowId,
            buttonId, position, cloneSize, cloneColors,
            editing: false, out _, iconYOffset: 1f);

        if (clicked)
        {
            if (isOpen)
                this.CloseAllPopups(clearKeybindTarget: true);
            else
                this.OpenNamedPopup(ref isOpen);
        }
    }

    private bool DrawHeartButtonOverlay(string windowId, string buttonId, Vector2 position, Vector2 size, UiColors colors, bool editing, out bool active, float iconYOffset = 0f)
    {
        var hovered = false;
        var clicked = false;
        active = false;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        var flags = ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoBringToFrontOnFocus
                    | ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoBackground;

        var began = false;
        using var wPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var wBorder = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f);
        try
        {
            if (ImGui.Begin(windowId, flags))
            {
                began = true;
                ImGui.SetCursorScreenPos(position);
                clicked = ImGui.InvisibleButton(buttonId, size);
                hovered = ImGui.IsItemHovered();
                active = ImGui.IsItemActive();

                var drawList = ImGui.GetWindowDrawList();
                var min = position;

                // Trim only the drawn rectangle, not the hitbox, so the button keeps the same clickable area.
                // The heart still uses the matching icon box plus optional per-context Y offset.
                var iconSize = new Vector2(Math.Max(1f, size.X - 1f), size.Y);
                var buttonSize = new Vector2(iconSize.X, Math.Max(1f, size.Y - 1f));
                var buttonMin = min + new Vector2(0f, 1f);
                var buttonMax = min + buttonSize;
                var background = editing
                    ? colors.EditButton
                    : active
                        ? colors.ButtonActive
                        : colors.Button;

                var rounding = Math.Max(2f, size.Y * 0.14f);
                drawList.AddRectFilled(buttonMin, buttonMax, Color(background), rounding);
                drawList.AddRect(buttonMin, buttonMax, Color(colors.Border), rounding, ImDrawFlags.None, Math.Max(1f, size.Y * 0.045f));

                var textColor = hovered && !editing
                    ? new Vector4(1f, 0.08f, 0.08f, 1f)
                    : colors.Text;

                DrawHeartIcon(drawList, min + new Vector2(0f, iconYOffset), iconSize, textColor);

                if (hovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
            }
            else
            {
                began = true;
            }
        }
        finally
        {
            if (began)
            {
                ImGui.End();
            }
        }

        return clicked;
    }

    private void DrawPopupTextReplaceToggle(Vector2 pos, Vector2 size, UiColors colors, string idSuffix)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorScreenPos(pos);
        if (ImGui.InvisibleButton($"##QuickSymbolsTextReplacementButton{idSuffix}", size))
        {
            this.SetAutomaticTextReplacement(!this.Config.ReplaceSpecificTextsForSymbols, printEnabledMessage: true);
            this.SaveConfig();
        }

        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetTooltip(this.Config.ReplaceSpecificTextsForSymbols
                ? "Disable text to symbol replacement"
                : "Enable text to symbol replacement");
        }

        drawList.AddRectFilled(pos, pos + size, Color(hovered ? colors.CellHovered : colors.CellBackground), 4f * scale);

        var iconText = char.ConvertFromUtf32(0xE2CA);
        var iconFont = PushQuickSymbolsIconFont();
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * 0.82f;
        var iconSize = ImGui.CalcTextSize(iconText) * 0.82f;
        var iconColor = this.Config.ReplaceSpecificTextsForSymbols
            ? new Vector4(1f, 0.78f, 0.14f, 1f)
            : hovered
                ? new Vector4(1f, 0.88f, 0.05f, 1f)
                : colors.Text;

        drawList.AddText(font, fontSize, pos + (size - iconSize) * 0.5f, Color(iconColor), iconText);
        iconFont?.Dispose();
    }

    private void HandleButtonDragging(Vector2 size, bool active)
    {
        var io = ImGui.GetIO();
        if (active && this.IsLeftMouseDown())
        {
            this.draggingButton = true;
            var clamped = ClampPositionToScreen(this.currentbPos + io.MouseDelta, size);
            this.Config.HasCustombPosition = true;
            this.Config.UsesRelativeButtonOffset = true;
            this.Config.bPosition = clamped;
            this.Config.ButtonOffset = clamped - this.nativebPos;
            this.currentbPos = clamped;
            this.bPositionDirty = true;
        }
        else if (this.draggingButton && !this.IsLeftMouseDown())
        {
            this.draggingButton = false;
            this.QueueButtonPositionSave();
        }
    }

    private void DrawSymbolsPopup(
        string idSuffix, UiColors colors, Vector2 anchorPos, Vector2 anchorSize, PopupPlacement placement,
        bool includePositionEditor, SymbolInsertTarget insertTarget, ref bool isOpen)
    {
        this.ConfigChanged();

        // Popup-only theme override. Buttons can still use the FFXIV chat look,
        // while the list itself can follow the user Dalamud theme when requested.
        if (this.Config.UseDalamudTheme)
        {
            colors = UiColors.FromDalamudStyle();
        }

        var cEntries = this.GetcEntries();
        var scale = ImGuiHelpers.GlobalScale;
        var dSize = ImGui.GetIO().DisplaySize;
        var cell = Math.Clamp(anchorSize.Y * 1.05f, 22f * scale, 34f * scale);
        var spacing = Math.Max(3f, 4f * scale);
        var padding = Math.Max(8f, 10f * scale);
        var scrollWidth = Math.Max(3f, 4f * scale);
        var availableWidth = dSize.X - 16f * scale;

        // Grid Calc | Keep the popup dimensions tied to the normal Symbols tab. Custom entries can scroll inside the same space but they should never resize the window
        var columns = Math.Clamp((int)((availableWidth - padding * 2f - scrollWidth - 8f * scale + spacing) / (cell + spacing)), 1, MaxColumns);
        var sRows = Math.Max(1, (int)Math.Ceiling(Symbols.Length / (double)columns));
        var visibleRows = Math.Min(sRows, 8);
        var gridWidth = columns * cell + Math.Max(0, columns - 1) * spacing;
        var gridHeight = visibleRows * cell + Math.Max(0, visibleRows - 1) * spacing;
        var headerHeight = 24f * scale;
        var tabHeight = 22f * scale;
        var contentGap = 3f * scale;
        var pWidth = Math.Min(availableWidth, padding * 2f + gridWidth + scrollWidth + 8f * scale);
        var pHeight = padding * 2f + headerHeight + tabHeight + contentGap + gridHeight;

        float posX;
        float posY;
        if (placement == PopupPlacement.Below)
        {
            posX = anchorPos.X;
            posY = anchorPos.Y + anchorSize.Y + 6f * scale;
        }
        else
        {
            posX = anchorPos.X + anchorSize.X + 10f * scale;
            posY = anchorPos.Y - pHeight - 8f * scale;
        }

        posX = Math.Clamp(posX, 8f * scale, Math.Max(8f * scale, dSize.X - pWidth - 8f * scale));
        posY = Math.Clamp(posY, 8f * scale, Math.Max(8f * scale, dSize.Y - pHeight - 8f * scale));

        var popupPos = new Vector2(posX, posY);
        if (idSuffix == "Keybind" && this.keybindPopupPosValid)
        {
            popupPos = ClampPositionToScreen(this.keybindPopupLivePos, new Vector2(pWidth, pHeight));
        }

        var posCond = idSuffix == "Keybind" && this.keybindPopupPosValid ? ImGuiCond.Once : ImGuiCond.Always;
        ImGui.SetNextWindowPos(popupPos, posCond);
        ImGui.SetNextWindowSize(new Vector2(pWidth, pHeight), ImGuiCond.Always);

        var keepAboveOtherPlugin = insertTarget == SymbolInsertTarget.IpcCallback;
        var flags = ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoNav;

        if (!keepAboveOtherPlugin)
        {
            flags |= ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus;
        }

        var beginCalled = false;

        using var pPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));
        using var pBorderSize = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 1f * scale);
        using var pRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 8f * scale);
        using var pBackground = ImRaii.PushColor(ImGuiCol.WindowBg, colors.PopupBackground);
        using var pBorder = ImRaii.PushColor(ImGuiCol.Border, colors.Border);

        try
        {
            var popupWindowName = $"##QuickSymbolsPopup{idSuffix}";
            var windowVisible = ImGui.Begin(popupWindowName, flags);
            beginCalled = true;
            if (windowVisible)
            {
                if (keepAboveOtherPlugin && this.ipcPopupRaiseFrames > 0)
                {
                    ImGui.SetWindowFocus(popupWindowName);
                    this.ipcPopupRaiseFrames--;
                }

                var wPos = ImGui.GetWindowPos();
                var drawList = ImGui.GetWindowDrawList();
                var title = "Bryer - Quick Symbols";
                ImGui.TextColored(colors.MutedText, title);

                var closeSize = new Vector2(22f * scale, 22f * scale);
                var closePos = new Vector2(wPos.X + pWidth - padding - closeSize.X, wPos.Y + padding - 1f * scale);
                var settingsSize = closeSize;
                var settingsPos = new Vector2(closePos.X - settingsSize.X - 5f * scale, closePos.Y);

                if (includePositionEditor)
                {
                    var replaceText = char.ConvertFromUtf32(0xE2CA);
                    var replaceSize = closeSize;
                    var replacePos = new Vector2(settingsPos.X - replaceSize.X - 5f * scale, closePos.Y);
                    this.DrawPopupTextReplaceToggle(replacePos, replaceSize, colors, idSuffix);

                    var editText = char.ConvertFromUtf32(0xF047);
                    var editbSize = closeSize;
                    var editbPos = new Vector2(replacePos.X - editbSize.X - 5f * scale, closePos.Y);

                    ImGui.SetCursorScreenPos(editbPos);
                    if (ImGui.InvisibleButton($"##QuickSymbolsMoveButton{idSuffix}", editbSize))
                    {
                        this.editbPosition = !this.editbPosition;
                        this.draggingButton = false;
                        isOpen = true;
                        this.popupOpen = true;
                        this.popupClickGuardFrames = 2;
                        this.QueueButtonPositionSave();
                    }

                    var moveHover = ImGui.IsItemHovered();
                    var moveActive = ImGui.IsItemActive();
                    if (moveHover)
                    {
                        ImGui.SetTooltip(this.editbPosition
                            ? "Drag&Drop the button \"♥\" and click here again"
                            : "Click to change button \"♥\" position");
                    }

                    drawList.AddRectFilled(editbPos, editbPos + editbSize, Color(moveHover ? colors.CellHovered : colors.CellBackground), 4f * scale);
                    var moveIconFont = PushQuickSymbolsIconFont();
                    var moveFont = ImGui.GetFont();
                    var moveFontSize = ImGui.GetFontSize() * 0.82f;
                    var moveSize = ImGui.CalcTextSize(editText) * 0.82f;
                    var moveColor = (this.editbPosition || moveActive)
                        ? new Vector4(1f, 0.08f, 0.08f, 1f)
                        : moveHover
                            ? new Vector4(1f, 0.88f, 0.05f, 1f)
                            : colors.Text;
                    drawList.AddText(moveFont, moveFontSize, editbPos + (editbSize - moveSize) * 0.5f, Color(moveColor), editText);
                    moveIconFont?.Dispose();
                }

                ImGui.SetCursorScreenPos(settingsPos);
                if (ImGui.InvisibleButton($"##QuickSymbolsConfigButton{idSuffix}", settingsSize))
                {
                    this.configWindowOpen = true;
                }

                var settingsHover = ImGui.IsItemHovered();
                if (settingsHover)
                {
                    ImGui.SetTooltip("Open config window");
                }

                drawList.AddRectFilled(settingsPos, settingsPos + settingsSize, Color(settingsHover ? colors.CellHovered : colors.CellBackground), 4f * scale);
                var settingsText = char.ConvertFromUtf32(0xF085);
                var settingsIconFont = PushQuickSymbolsIconFont();
                var settingsFont = ImGui.GetFont();
                var settingsFontSize = ImGui.GetFontSize() * 0.82f;
                var settingsTextSize = ImGui.CalcTextSize(settingsText) * 0.82f;
                var settingsColor = settingsHover
                    ? new Vector4(1f, 0.88f, 0.05f, 1f)
                    : colors.Text;
                drawList.AddText(settingsFont, settingsFontSize, settingsPos + (settingsSize - settingsTextSize) * 0.5f, Color(settingsColor), settingsText);
                settingsIconFont?.Dispose();

                var closeLocked = includePositionEditor && this.editbPosition;
                ImGui.SetCursorScreenPos(closePos);
                if (ImGui.InvisibleButton($"##QuickSymbolsCloseButton{idSuffix}", closeSize) && !closeLocked)
                {
                    isOpen = false;
                    if (idSuffix == "Keybind")
                    {
                        this.keybindTextInput = null;
                    }
                }

                var cHover = ImGui.IsItemHovered();
                if (cHover)
                {
                    ImGui.SetTooltip("Close");
                }

                drawList.AddRectFilled(closePos, closePos + closeSize, Color(cHover ? colors.CellHovered : colors.CellBackground), 4f * scale);
                var xText = "X";
                var xSize = ImGui.CalcTextSize(xText);
                var xColor = cHover ? new Vector4(1f, 0.08f, 0.08f, 1f) : colors.Text;
                drawList.AddText(closePos + (closeSize - xSize) * 0.5f, Color(xColor), xText);

                var tabStartY = wPos.Y + padding + headerHeight;
                var tabWidth = pWidth - padding * 2f;
                this.DrawPopupTabs(new Vector2(wPos.X + padding, tabStartY), tabWidth, tabHeight, colors, scale);

                var contentStartY = tabStartY + tabHeight + contentGap;
                var contentHeight = pHeight - padding - (contentStartY - wPos.Y);
                ImGui.SetCursorScreenPos(new Vector2(wPos.X + padding, contentStartY));

                if (idSuffix == "Keybind")
                {
                    this.keybindPopupLivePos = ImGui.GetWindowPos();
                    this.keybindPopupPosValid = true;
                }

                // Custom tab | User-created entries from /qsconfig.
                if (this.selectedPopupTab == PopupTab.Custom)
                {
                    this.DrawCustomTab(idSuffix, cEntries, columns, gridWidth, cell, spacing, contentHeight, scrollWidth, colors, insertTarget);
                }
                // Numbers tab | Number symbols and regular circled/parenthesized number symbols.
                else if (this.selectedPopupTab == PopupTab.Numbers)
                {
                    this.DrawCategoryTab(idSuffix, "Numbers", NumberSymbols, columns, cell, spacing, contentHeight, scrollWidth, colors, insertTarget, ref this.numbersScrollY);
                }
                // Letters tab | letter/alphabet style symbols.
                else if (this.selectedPopupTab == PopupTab.Letters)
                {
                    this.DrawCategoryTab(idSuffix, "Letters", LetterSymbols, columns, cell, spacing, contentHeight, scrollWidth, colors, insertTarget, ref this.lettersScrollY);
                }
                // Common tab | Frequently useful symbols, separated from the full Home list.
                else if (this.selectedPopupTab == PopupTab.Common)
                {
                    this.DrawCategoryTab(idSuffix, "Common", CommonSymbols, columns, cell, spacing, contentHeight, scrollWidth, colors, insertTarget, ref this.commonScrollY);
                }
                // Others tab | Large miscellaneous set that does not fit Numbers/Letters/Common/Time.
                else if (this.selectedPopupTab == PopupTab.Others)
                {
                    this.DrawCategoryTab(idSuffix, "Others", OthersSymbols, columns, cell, spacing, contentHeight, scrollWidth, colors, insertTarget, ref this.othersScrollY);
                }
                // Time tab | Small time-related symbol group.
                else if (this.selectedPopupTab == PopupTab.Time)
                {
                    this.DrawCategoryTab(idSuffix, "Time", TimeSymbols, columns, cell, spacing, contentHeight, scrollWidth, colors, insertTarget, ref this.timeScrollY);
                }
                // Home tab | Original full symbols list + Favorites.
                else
                {
                    var favsHeight = this.DrawfavsSection(idSuffix, columns, cell, spacing, gridWidth, colors, insertTarget);
                    if (favsHeight > 0f)
                    {
                        ImGui.SetCursorScreenPos(new Vector2(wPos.X + padding, contentStartY + favsHeight));
                    }
                    else
                    {
                        var dividerHeight = this.DrawHomeDivider(gridWidth, colors, scale);
                        ImGui.SetCursorScreenPos(new Vector2(wPos.X + padding, contentStartY + dividerHeight));
                        favsHeight = dividerHeight;
                    }

                    var availableGridHeight = Math.Max(cell, contentHeight - favsHeight);
                    this.DrawEntriesGrid(idSuffix, Symbols, columns, sRows, cell, cell, spacing, availableGridHeight, scrollWidth, colors, insertTarget, ref this.symbolScrollY, allowfavs: true);
                }

                if (this.Config.ClosePopupOnLostFocus && !this.editbPosition && this.popupClickGuardFrames <= 0 && this.leftMouseClickedThisFrame)
                {
                    var mouse = ImGui.GetIO().MousePos;
                    var insidePopup = mouse.X >= wPos.X && mouse.X <= wPos.X + pWidth && mouse.Y >= wPos.Y && mouse.Y <= wPos.Y + pHeight;
                    var insideMainButton = this.popupOpen && mouse.X >= this.currentbPos.X && mouse.X <= this.currentbPos.X + this.currentbSize.X && mouse.Y >= this.currentbPos.Y && mouse.Y <= this.currentbPos.Y + this.currentbSize.Y;
                    var insidePfButton = this.partyFinderPopupOpen && mouse.X >= this.partyFinderbPos.X && mouse.X <= this.partyFinderbPos.X + this.partyFinderbSize.X && mouse.Y >= this.partyFinderbPos.Y && mouse.Y <= this.partyFinderbPos.Y + this.partyFinderbSize.Y;
                    var insideMsgButton = this.messageBookPopupOpen && mouse.X >= this.messageBookbPos.X && mouse.X <= this.messageBookbPos.X + this.messageBookbSize.X && mouse.Y >= this.messageBookbPos.Y && mouse.Y <= this.messageBookbPos.Y + this.messageBookbSize.Y;
                    var insideMacroButton = this.macroPopupOpen && mouse.X >= this.macrobPos.X && mouse.X <= this.macrobPos.X + this.macrobSize.X && mouse.Y >= this.macrobPos.Y && mouse.Y <= this.macrobPos.Y + this.macrobSize.Y;
                    var insideTofuInputButton = this.tofuInputPopupOpen && mouse.X >= this.tofuInputbPos.X && mouse.X <= this.tofuInputbPos.X + this.tofuInputbSize.X && mouse.Y >= this.tofuInputbPos.Y && mouse.Y <= this.tofuInputbPos.Y + this.tofuInputbSize.Y;
                    var insideIpcAnchor = this.ipcPopupOpen && mouse.X >= this.ipcPopupAnchorPos.X && mouse.X <= this.ipcPopupAnchorPos.X + this.ipcPopupAnchorSize.X && mouse.Y >= this.ipcPopupAnchorPos.Y && mouse.Y <= this.ipcPopupAnchorPos.Y + this.ipcPopupAnchorSize.Y;
                    if (!insidePopup && !insideMainButton && !insidePfButton && !insideMsgButton && !insideMacroButton && !insideTofuInputButton && !insideIpcAnchor)
                    {
                        isOpen = false;
                        if (idSuffix == "Keybind")
                        {
                            this.keybindTextInput = null;
                        }
                    }
                }

                if (this.popupClickGuardFrames > 0)
                {
                    this.popupClickGuardFrames--;
                }
            }
        }
        finally
        {
            if (beginCalled)
            {
                ImGui.End();
            }
        }
    }

    private void DrawPopupTabs(Vector2 start, float width, float height, UiColors colors, float scale)
    {
        // Popup tab bar | Visual order and icons for each tab.
        var tabs = new[]
        {
            // Home tab | Full original symbols list.
            (Tab: PopupTab.Symbols, Label: char.ConvertFromUtf32(0xF015), Tooltip: "Home", Icon: true),

            // Numbers tab | Number-related symbols.
            (Tab: PopupTab.Numbers, Label: char.ConvertFromUtf32(0xE69B), Tooltip: "Numbers", Icon: true),

            // Letters tab | Letter-related symbols.
            (Tab: PopupTab.Letters, Label: char.ConvertFromUtf32(0xF0FD), Tooltip: "Letters", Icon: true),

            // Common tab | Most common misc symbols.
            (Tab: PopupTab.Common, Label: char.ConvertFromUtf32(0xF86D), Tooltip: "Common", Icon: true),

            // Others tab | Extended/miscellaneous symbols.
            (Tab: PopupTab.Others, Label: char.ConvertFromUtf32(0xE0BB), Tooltip: "Others", Icon: true),

            // Time tab | Time-related symbols.
            (Tab: PopupTab.Time, Label: char.ConvertFromUtf32(0xF017), Tooltip: "Time", Icon: true),

            // Custom tab | User-created symbols/strings.
            (Tab: PopupTab.Custom, Label: char.ConvertFromUtf32(0xE185), Tooltip: "Custom", Icon: true),
        };

        var targetIndex = Math.Max(0, Array.FindIndex(tabs, tab => tab.Tab == this.selectedPopupTab));
        if (this.tabVisualIndex < 0f)
        {
            this.tabVisualIndex = targetIndex;
            this.tabTargetIndex = targetIndex;
            this.tabMoveStartedAt = ImGui.GetTime();
        }

        if (this.tabTargetIndex != targetIndex)
        {
            this.tabVisualIndex = this.GetAnimatedTabIndex();
            this.tabTargetIndex = targetIndex;
            this.tabMoveStartedAt = ImGui.GetTime();
        }

        var drawList = ImGui.GetWindowDrawList();
        var max = start + new Vector2(width, height);
        var pieceWidth = width / tabs.Length;
        var visualIndex = this.GetAnimatedTabIndex();
        var pillMin = new Vector2(start.X + pieceWidth * visualIndex + 3f * scale, start.Y + 3f * scale);
        var pillSize = new Vector2(pieceWidth - 6f * scale, height - 6f * scale);

        drawList.AddRectFilled(start, max, Color(colors.CellBackground), 5f * scale);
        drawList.AddRect(start, max, Color(colors.Border), 5f * scale, ImDrawFlags.None, Math.Max(1f, scale));
        drawList.AddRectFilled(pillMin, pillMin + pillSize, Color(colors.ButtonActive), 4f * scale);

        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            var selected = i == targetIndex;
            var min = start + new Vector2(pieceWidth * i + 3f * scale, 3f * scale);
            var size = new Vector2(pieceWidth - 6f * scale, height - 6f * scale);
            var maxItem = min + size;

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##QuickSymbolsTab{tab.Tab}", size);
            var hovered = ImGui.IsItemHovered();
            if (!selected && hovered)
            {
                drawList.AddRectFilled(min, maxItem, Color(colors.CellHovered), 4f * scale);
            }

            if (ImGui.IsItemClicked())
            {
                this.selectedPopupTab = tab.Tab;
            }

            if (hovered)
            {
                ImGui.SetTooltip(tab.Tooltip);
            }

            var textColor = selected ? colors.Text : colors.MutedText;
            IDisposable? font = null;
            if (tab.Icon)
            {
                font = PushQuickSymbolsIconFont();
            }

            using (font)
            {
                var labelSize = ImGui.CalcTextSize(tab.Label);
                drawList.AddText(
                    new Vector2(min.X + (size.X - labelSize.X) * 0.5f, min.Y + (size.Y - labelSize.Y) * 0.5f),
                    ImGui.GetColorU32(textColor),
                    tab.Label);
            }
        }
    }

    private float GetAnimatedTabIndex()
    {
        var age = Math.Clamp((float)((ImGui.GetTime() - this.tabMoveStartedAt) / 0.18), 0f, 1f);
        var move = 1f - MathF.Pow(1f - age, 3f);
        var visual = this.tabVisualIndex + (this.tabTargetIndex - this.tabVisualIndex) * move;
        if (age >= 1f)
        {
            this.tabVisualIndex = this.tabTargetIndex;
            return this.tabTargetIndex;
        }

        return visual;
    }

    // Custom tab | Shows only user-created entries and the Create custom button when empty.
    private void DrawCustomTab(
        string idSuffix, IReadOnlyList<string> entries, int columns, float gridWidth, float cell, float spacing,
        float contentHeight, float scrollWidth, UiColors colors, SymbolInsertTarget insertTarget)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var titleHeight = ImGui.GetTextLineHeight() + 8f * scale;

        ImGui.TextColored(colors.MutedText, "Custom");
        var dividerY = start.Y + titleHeight - 3f * scale;
        drawList.AddLine(
            new Vector2(start.X, dividerY),
            new Vector2(start.X + ImGui.GetContentRegionAvail().X, dividerY),
            Color(colors.Border),
            Math.Max(1f, scale));

        var bodyStart = new Vector2(start.X, start.Y + titleHeight);
        var bodyHeight = Math.Max(cell, contentHeight - titleHeight);
        ImGui.SetCursorScreenPos(bodyStart);

        if (entries.Count == 0)
        {
            var buttonSize = new Vector2(Math.Max(120f * scale, ImGui.CalcTextSize("Create custom").X + 24f * scale), 28f * scale);
            var buttonPos = new Vector2(
                start.X + (gridWidth - buttonSize.X) * 0.5f,
                bodyStart.Y + Math.Max(0f, (bodyHeight - buttonSize.Y) * 0.5f));

            ImGui.SetCursorScreenPos(buttonPos);
            ImGui.InvisibleButton($"Create custom##QuickSymbolsCreateCustom{idSuffix}", buttonSize);
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (ImGui.IsItemClicked())
            {
                this.configWindowOpen = true;
            }

            var buttonColor = active ? colors.ButtonActive : hovered ? colors.ButtonHovered : colors.Button;
            drawList.AddRectFilled(buttonPos, buttonPos + buttonSize, Color(buttonColor), 4f * scale);
            drawList.AddRect(buttonPos, buttonPos + buttonSize, Color(colors.Border), 4f * scale, ImDrawFlags.None, Math.Max(1f, scale));
            var buttonText = "Create custom";
            var textSize = ImGui.CalcTextSize(buttonText);
            drawList.AddText(buttonPos + (buttonSize - textSize) * 0.5f, Color(colors.Text), buttonText);

            return;
        }

        var customCellWidth = Math.Clamp(entries.Max(entry => ImGui.CalcTextSize(entry).X + 18f * scale), cell, gridWidth);
        var customColumns = Math.Clamp((int)((gridWidth + spacing) / (customCellWidth + spacing)), 1, columns);
        var customRows = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)customColumns));
        this.DrawEntriesGrid(idSuffix, entries, customColumns, customRows, customCellWidth, cell, spacing, bodyHeight, scrollWidth, colors, insertTarget, ref this.customScrollY, allowfavs: false);
    }

    // Category tabs | Shared renderer used by Numbers, Letters, Common, Others and Time.
    // Each category passes its own title, symbol list and scroll value.
    private void DrawCategoryTab(
        string idSuffix, string title, IReadOnlyList<string> entries, int columns, float cell, float spacing,
        float contentHeight, float scrollWidth, UiColors colors, SymbolInsertTarget insertTarget, ref float scrollY)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var gridWidth = columns * cell + Math.Max(0, columns - 1) * spacing;
        var favsHeight = this.DrawfavsSection(idSuffix, columns, cell, spacing, gridWidth, colors, insertTarget);
        if (favsHeight > 0f)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + favsHeight));
        }

        start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var titleHeight = ImGui.GetTextLineHeight() + 8f * scale;

        ImGui.TextColored(colors.MutedText, title);
        var dividerY = start.Y + titleHeight - 3f * scale;
        drawList.AddLine(
            new Vector2(start.X, dividerY),
            new Vector2(start.X + ImGui.GetContentRegionAvail().X, dividerY),
            Color(colors.Border),
            Math.Max(1f, scale));

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + titleHeight));
        var rows = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)columns));
        this.DrawEntriesGrid(idSuffix, entries, columns, rows, cell, cell, spacing, Math.Max(cell, contentHeight - favsHeight - titleHeight), scrollWidth, colors, insertTarget, ref scrollY, allowfavs: true);
    }

    private IReadOnlyList<string> GetcEntries()
    {
        this.Config.Custom ??= [];
        return this.Config.Custom.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToArray();
    }

    private float DrawfavsSection(string idSuffix, int columns, float cell, float spacing, float gridWidth, UiColors colors, SymbolInsertTarget insertTarget)
    {
        var favs = this.Getfavsymbols();
        if (favs.Count == 0)
        {
            return 0f;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var dList = ImGui.GetWindowDrawList();
        var label = "Favorites";
        var labelHeight = 20f * scale;
        var rows = (int)Math.Ceiling(favs.Count / (double)columns);
        var favsGridHeight = rows * cell + Math.Max(0, rows - 1) * spacing;
        var dividerY = origin.Y + labelHeight + favsGridHeight + 6f * scale;

        ImGui.TextColored(colors.MutedText, label);

        IDisposable? pushedFont = null;
        if (this.symbolFont is { Available: true })
        {
            pushedFont = this.symbolFont.Push();
        }

        for (var i = 0; i < favs.Count; i++)
        {
            var row = i / columns;
            var col = i % columns;
            var cellMin = new Vector2(origin.X + col * (cell + spacing), origin.Y + labelHeight + row * (cell + spacing));
            this.DrawSymbolCell(favs[i], $"{idSuffix}-favorite-{i}", cellMin, new Vector2(cell, cell), colors, isFavorite: true, insertTarget, allowFavoriteToggle: true);
        }

        pushedFont?.Dispose();

        if (this.selectedPopupTab == PopupTab.Symbols)
        {
            dList.AddLine(
                new Vector2(origin.X, dividerY),
                new Vector2(origin.X + gridWidth, dividerY),
                Color(colors.CellBorder),
                Math.Max(1f, scale));
        }

        var totalHeight = labelHeight + favsGridHeight + (this.selectedPopupTab == PopupTab.Symbols ? 12f : 8f) * scale;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + totalHeight));
        return totalHeight;
    }

    private float DrawHomeDivider(float gridWidth, UiColors colors, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var dList = ImGui.GetWindowDrawList();
        var height = 7f * scale;
        var y = origin.Y + 3f * scale;

        dList.AddLine(
            new Vector2(origin.X, y),
            new Vector2(origin.X + gridWidth, y),
            Color(colors.CellBorder),
            Math.Max(1f, scale));

        return height;
    }

    private void DrawEntriesGrid(
        string idSuffix,
        IReadOnlyList<string> entries,
        int columns,
        int rows,
        float cellWidth,
        float cellHeight,
        float spacing,
        float gridHeight,
        float scrollWidth,
        UiColors colors, SymbolInsertTarget insertTarget,
        ref float scrollY,
        bool allowfavs)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rowHeight = cellHeight + spacing;
        var gridWidth = columns * cellWidth + Math.Max(0, columns - 1) * spacing;
        var gridSize = new Vector2(gridWidth + scrollWidth + 8f * scale, gridHeight);
        var maxScroll = Math.Max(0f, rows * rowHeight - spacing - gridHeight);

        scrollY = Math.Clamp(scrollY, 0f, maxScroll);

        var childFlags = ImGuiWindowFlags.NoScrollbar
                         | ImGuiWindowFlags.NoScrollWithMouse
                         | ImGuiWindowFlags.NoNav;

        using var child = ImRaii.Child($"##QuickSymbolsGridChild{idSuffix}{this.selectedPopupTab}", gridSize, false, childFlags);
        if (!child.Success)
        {
            return;
        }

        var childOrigin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.01f)
            {
                scrollY = Math.Clamp(scrollY - wheel * rowHeight * 2f, 0f, maxScroll);
            }
        }

        var firstRow = Math.Max(0, (int)Math.Floor(scrollY / rowHeight));
        var lastRow = Math.Min(rows - 1, (int)Math.Ceiling((scrollY + gridHeight) / rowHeight));

        IDisposable? pushedFont = null;
        if (this.symbolFont is { Available: true })
        {
            pushedFont = this.symbolFont.Push();
        }

        using (pushedFont)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var index = row * columns + col;
                    if (index >= entries.Count)
                    {
                        break;
                    }

                    var entry = entries[index];
                    var cellMin = childOrigin + new Vector2(col * (cellWidth + spacing), row * rowHeight - scrollY);
                    var cellMax = cellMin + new Vector2(cellWidth, cellHeight);

                    if (cellMax.Y < childOrigin.Y || cellMin.Y > childOrigin.Y + gridHeight)
                    {
                        continue;
                    }

                    this.DrawSymbolCell(entry, $"{idSuffix}-entry-{this.selectedPopupTab}-{index}", cellMin, new Vector2(cellWidth, cellHeight), colors, allowfavs && this.IsFavorite(entry), insertTarget, allowfavs);
                }
            }
        }

        // Custom scrollbar
        if (maxScroll > 0f)
        {
            var barX = childOrigin.X + gridWidth + 6f * scale;
            var barMin = new Vector2(barX, childOrigin.Y);
            var barMax = new Vector2(barX + scrollWidth, childOrigin.Y + gridHeight);
            var thumbHeight = Math.Max(18f * scale, gridHeight * (gridHeight / (gridHeight + maxScroll)));
            var thumbY = childOrigin.Y + (gridHeight - thumbHeight) * (scrollY / maxScroll);
            var thumbMin = new Vector2(barX, thumbY);
            var thumbMax = new Vector2(barX + scrollWidth, thumbY + thumbHeight);

            var mouse = ImGui.GetIO().MousePos;
            var tHover = mouse.X >= thumbMin.X - 4f * scale && mouse.X <= thumbMax.X + 4f * scale && mouse.Y >= thumbMin.Y && mouse.Y <= thumbMax.Y;
            var trackHover = mouse.X >= barMin.X - 5f * scale && mouse.X <= barMax.X + 5f * scale && mouse.Y >= barMin.Y && mouse.Y <= barMax.Y;

            if (tHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                this.draggingScrollBar = true;
                this.scrollDragOffsetY = mouse.Y - thumbY;
            }
            else if (!tHover && trackHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                this.draggingScrollBar = true;
                this.scrollDragOffsetY = thumbHeight * 0.5f;
                var targetThumbY = Math.Clamp(mouse.Y - this.scrollDragOffsetY, childOrigin.Y, childOrigin.Y + gridHeight - thumbHeight);
                scrollY = Math.Clamp(((targetThumbY - childOrigin.Y) / Math.Max(1f, gridHeight - thumbHeight)) * maxScroll, 0f, maxScroll);
            }

            if (this.draggingScrollBar)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var targetThumbY = Math.Clamp(mouse.Y - this.scrollDragOffsetY, childOrigin.Y, childOrigin.Y + gridHeight - thumbHeight);
                    scrollY = Math.Clamp(((targetThumbY - childOrigin.Y) / Math.Max(1f, gridHeight - thumbHeight)) * maxScroll, 0f, maxScroll);
                }
                else
                {
                    this.draggingScrollBar = false;
                }
            }

            thumbY = childOrigin.Y + (gridHeight - thumbHeight) * (scrollY / maxScroll);
            thumbMin = new Vector2(barX, thumbY);
            thumbMax = new Vector2(barX + scrollWidth, thumbY + thumbHeight);

            drawList.AddRectFilled(barMin, barMax, Color(colors.ScrollTrack), scrollWidth * 0.5f);
            drawList.AddRectFilled(thumbMin, thumbMax, Color((tHover || this.draggingScrollBar) ? colors.ButtonHovered : colors.ScrollThumb), scrollWidth * 0.5f);
        }
        else
        {
            this.draggingScrollBar = false;
        }
    }

    private void DrawSymbolCell(string symbol, string id, Vector2 cellMin, Vector2 cellSize, UiColors colors, bool isFavorite, SymbolInsertTarget insertTarget, bool allowFavoriteToggle)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var cellMax = cellMin + cellSize;

        var mouse = ImGui.GetIO().MousePos;
        var hovered = mouse.X >= cellMin.X && mouse.X <= cellMax.X && mouse.Y >= cellMin.Y && mouse.Y <= cellMax.Y;
        var clicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        ImGui.SetCursorScreenPos(cellMin);
        ImGui.Dummy(cellSize);

        drawList.AddRectFilled(cellMin, cellMax, Color(hovered ? colors.CellHovered : colors.CellBackground), 5f * scale);

        var textSize = ImGui.CalcTextSize(symbol);
        var textPos = cellMin + (cellSize - textSize) * 0.5f;
        var clipMin = cellMin + new Vector2(3f * scale, 1f * scale);
        var clipMax = cellMax - new Vector2(3f * scale, 1f * scale);
        ImGui.PushClipRect(clipMin, clipMax, true);
        drawList.AddText(textPos, Color(colors.SymbolText), symbol);
        ImGui.PopClipRect();

        if (hovered)
        {
            if (allowFavoriteToggle)
            {
                ImGui.SetTooltip(isFavorite ? "CTRL+Click to Unfavorite" : "CTRL+Click to Favorite");
            }
            else
            {
                ImGui.SetTooltip(symbol);
            }
        }

        if (!clicked)
        {
            return;
        }

        if (allowFavoriteToggle && ImGui.GetIO().KeyCtrl)
        {
            this.ToggleFavorite(symbol);
        }
        else
        {
            this.QueueInsertSymbol(symbol, insertTarget);
        }
    }

    private void QueueInsertSymbol(string symbol, SymbolInsertTarget insertTarget)
    {
        if (insertTarget == SymbolInsertTarget.FocusedTextInput)
        {
            this.InsertTextIntoFocusedTextInput(symbol);
            return;
        }

        if (insertTarget == SymbolInsertTarget.RecruitmentComment)
        {
            this.InsertTextIntoRecruitmentComment(symbol);
            return;
        }

        if (insertTarget == SymbolInsertTarget.MessageBookInput)
        {
            this.InsertTextIntoMessageBook(symbol);
            return;
        }

        if (insertTarget == SymbolInsertTarget.MacroInput)
        {
            // Native macro input needs its own path so insertion goes into the macro body field.
            this.InsertTextIntoMacro(symbol);
            return;
        }

        if (insertTarget == SymbolInsertTarget.TofuInputString)
        {
            // Generic Tofu prompt input uses the same native insertion flow as other game text fields.
            this.InsertTextIntoTofuInputString(symbol);
            return;
        }

        if (insertTarget == SymbolInsertTarget.ConfigCustomEntry)
        {
            // Config custom entry is just a string field, so append directly instead of sending native input.
            this.InsertTextIntoConfigCustomEntry(symbol);
            return;
        }

        if (insertTarget == SymbolInsertTarget.IpcCallback)
        {
            this.SendSymbolToIpcOwner(symbol);
            return;
        }

        _ = Framework.RunOnTick(() => this.InsertTextIntoChat(symbol), delayTicks: 2);
    }

    private void InsertTextIntoConfigCustomEntry(string text)
    {
        this.newCustomEntry += text;
    }

    private void AdvanceCaretOnNextTick(Action<int> advanceCaret, string insertedText)
    {
        var caretMoves = GetCaretMoveCount(insertedText);
        if (caretMoves <= 0)
        {
            return;
        }

        _ = Framework.RunOnTick(() => advanceCaret(caretMoves), delayTicks: 1);
    }

    private void InsertTextIntoFocusedTextInput(string text)
    {
        try
        {
            var textInput = this.keybindTextInput;
            if (textInput == null || !textInput->Enabled)
            {
                textInput = GetFocusedTextInput();
            }

            if (textInput == null || !textInput->Enabled)
            {
                return;
            }

            textInput->InsertText(text, false);
            this.AdvanceCaretOnNextTick(this.AdvanceFocusedTextInputCaretRightIfStillActive, text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert Quick Symbols text into focused text input.");
        }
    }

    private void AdvanceFocusedTextInputCaretRightIfStillActive(int caretMoves)
    {
        try
        {
            var textInput = this.keybindTextInput;
            if (textInput == null || !textInput->Enabled)
            {
                textInput = GetFocusedTextInput();
            }

            if (textInput == null || !textInput->Enabled)
            {
                return;
            }

            SendRightArrowKeyPress(caretMoves);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to advance Quick Symbols focused text input caret after insertion. {ex}");
        }
    }

    private void InsertTextIntoChat(string text)
    {
        try
        {
            var chatUnit = GameGui.GetAddonByName(ChatLogAddonName);
            var chatLog = GameGui.GetAddonByName<AddonChatLog>(ChatLogAddonName);

            if (chatUnit.IsNull || !chatUnit.IsReady || !chatUnit.IsVisible || chatLog == null || chatLog->TextInput == null)
            {
                return;
            }

            // Keep the native chat input in control; forcing focus here can desync its cursor state
            var textInput = chatLog->TextInput;
            if (!textInput->IsActive)
            {
                return;
            }

            textInput->InsertText(text, false);

            this.AdvanceCaretOnNextTick(this.AdvanceChatCaretRightIfStillActive, text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert QuickSymbols text into chat input.");
        }
    }

    private void InsertTextIntoRecruitmentComment(string text)
    {
        try
        {
            if (!this.TryGetRecruitmentCommentTarget(out var target) || target.Input == null || target.Addon == null || target.Node == null)
            {
                return;
            }

            // Only insert while the native field is active. Rebuilding the whole buffer is risky here.
            if (!target.Input->IsActive)
            {
                return;
            }

            target.Input->InsertText(text, false);

            this.AdvanceCaretOnNextTick(this.AdvanceRecruitmentCommentCaretRightIfStillActive, text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert QuickSymbols text into the Party Finder recruitment comment input.");
        }
    }

    private void AdvanceChatCaretRightIfStillActive(int caretMoves)
    {
        try
        {
            var chatUnit = GameGui.GetAddonByName(ChatLogAddonName);
            var chatLog = GameGui.GetAddonByName<AddonChatLog>(ChatLogAddonName);

            if (chatUnit.IsNull || !chatUnit.IsReady || !chatUnit.IsVisible || chatLog == null || chatLog->TextInput == null)
            {
                return;
            }

            if (!chatLog->TextInput->IsActive)
            {
                return;
            }

            SendRightArrowKeyPress(caretMoves);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to advance QuickSymbols chat input caret after insertion. {ex}");
        }
    }

    private void AdvanceRecruitmentCommentCaretRightIfStillActive(int caretMoves)
    {
        try
        {
            if (!this.TryGetRecruitmentCommentTarget(out var target) || target.Input == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            SendRightArrowKeyPress(caretMoves);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to advance QuickSymbols recruitment comment caret after insertion. {ex}");
        }
    }

    private void InsertTextIntoMessageBook(string text)
    {
        try
        {
            if (!this.TryGetMessageBookInputTarget(out var target) || target.Input == null || target.Addon == null || target.Node == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            target.Input->InsertText(text, false);

            this.AdvanceCaretOnNextTick(this.AdvanceMessageBookCaretRightIfStillActive, text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert QuickSymbols text into the Message Book input.");
        }
    }

    private void AdvanceMessageBookCaretRightIfStillActive(int caretMoves)
    {
        try
        {
            if (!this.TryGetMessageBookInputTarget(out var target) || target.Input == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            SendRightArrowKeyPress(caretMoves);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to advance QuickSymbols Message Book caret after insertion. {ex}");
        }
    }

    private void InsertTextIntoMacro(string text)
    {
        // Re-find the macro input before inserting so stale addon pointers are not reused after the window refreshes.
        try
        {
            if (!this.TryGetMacroInputTarget(out var target) || target.Input == null || target.Addon == null || target.Node == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            target.Input->InsertText(text, false);

            this.AdvanceCaretOnNextTick(this.AdvanceMacroCaretRightIfStillActive, text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert QuickSymbols text into the User Macros input.");
        }
    }

    private void AdvanceMacroCaretRightIfStillActive(int caretMoves)
    {
        try
        {
            if (!this.TryGetMacroInputTarget(out var target) || target.Input == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            SendRightArrowKeyPress(caretMoves);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to advance QuickSymbols User Macros caret after insertion. {ex}");
        }
    }

    private void InsertTextIntoTofuInputString(string text)
    {
        // Re-find the Tofu input each time because this prompt is short-lived and can be recreated often.
        try
        {
            if (!this.TryGetTofuInputStringTarget(out var target) || target.Input == null || target.Addon == null || target.Node == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            target.Input->InsertText(text, false);

            this.AdvanceCaretOnNextTick(this.AdvanceTofuInputStringCaretRightIfStillActive, text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to insert QuickSymbols text into the TofuInputString input.");
        }
    }

    private void AdvanceTofuInputStringCaretRightIfStillActive(int caretMoves)
    {
        try
        {
            if (!this.TryGetTofuInputStringTarget(out var target) || target.Input == null)
            {
                return;
            }

            if (!target.Input->IsActive)
            {
                return;
            }

            SendRightArrowKeyPress(caretMoves);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to advance QuickSymbols TofuInputString caret after insertion. {ex}");
        }
    }

    // Addons (PF/Guestbook/User Macros/TofuInputString) searching logic
    private bool TryGetRecruitmentCommentTarget(out TextInputTarget target)
    {
        target = default;

        foreach (var addonName in RecruitmentCriteriaAddonNames)
        {
            var addonPtr = GameGui.GetAddonByName(addonName);
            if (addonPtr.IsNull)
            {
                continue;
            }

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon == null || !addon->IsReady || !addon->IsVisible || addon->RootNode == null)
            {
                continue;
            }

            var scale = Math.Clamp(addon->Scale, 0.65f, 2.4f);
            var candidates = new List<TextInputTarget>();

            // Scan paths so the button can appear in Party Finder even when the Comment field is not reachable through RootNode recursion alone
            CollectTextInputTargetsFromNodeList(addon, scale, candidates);
            CollectTextInputTargetsFromTree(addon, addon->RootNode, scale, candidates, 0);

            var best = PickBestRecruitmentCommentCandidate(candidates, scale);
            if (best.Input == null)
            {
                continue;
            }

            target = best;
            return true;
        }

        return false;
    }

    private bool TryGetMessageBookInputTarget(out TextInputTarget target)
    {
        target = default;

        foreach (var addonName in MessageBookInputAddonNames)
        {
            var addonPtr = GameGui.GetAddonByName(addonName);
            if (addonPtr.IsNull)
            {
                continue;
            }

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon == null || !addon->IsReady || !addon->IsVisible || addon->RootNode == null)
            {
                continue;
            }

            var scale = Math.Clamp(addon->Scale, 0.65f, 2.4f);
            var candidates = new List<TextInputTarget>();

            CollectTextInputTargetsFromNodeList(addon, scale, candidates);
            CollectTextInputTargetsFromTree(addon, addon->RootNode, scale, candidates, 0);

            var best = PickBestMessageBookInputCandidate(candidates, scale);
            if (best.Input == null)
            {
                continue;
            }

            target = best;
            return true;
        }

        return false;
    }

    private bool TryGetMacroInputTarget(out TextInputTarget target)
    {
        // Macro has multiple inputs, so collect every text input and then pick the large body box.
        target = default;

        var addonPtr = GameGui.GetAddonByName(MacroAddonName);
        if (addonPtr.IsNull)
        {
            return false;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->RootNode == null)
        {
            return false;
        }

        var scale = Math.Clamp(addon->Scale, 0.65f, 2.4f);
        var candidates = new List<TextInputTarget>();

        CollectTextInputTargetsFromNodeList(addon, scale, candidates);
        CollectTextInputTargetsFromTree(addon, addon->RootNode, scale, candidates, 0);

        var best = PickBestMacroInputCandidate(candidates, scale);
        if (best.Input == null)
        {
            return false;
        }

        target = best;
        return true;
    }

    private bool TryGetTofuInputStringTarget(out TextInputTarget target)
    {
        // Prefer NodeID 2 when available but keep a size fallback for minor addon layout changes.
        target = default;

        var addonPtr = GameGui.GetAddonByName(TofuInputStringAddonName);
        if (addonPtr.IsNull)
        {
            return false;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->RootNode == null)
        {
            return false;
        }

        var scale = Math.Clamp(addon->Scale, 0.65f, 2.4f);
        var candidates = new List<TextInputTarget>();

        CollectTextInputTargetsFromNodeList(addon, scale, candidates);
        CollectTextInputTargetsFromTree(addon, addon->RootNode, scale, candidates, 0);

        var best = PickBestTofuInputStringCandidate(candidates, scale);
        if (best.Input == null)
        {
            return false;
        }

        target = best;
        return true;
    }

    private static TextInputTarget PickBestRecruitmentCommentCandidate(List<TextInputTarget> candidates, float addonScale)
    {
        if (candidates.Count == 0)
        {
            return default;
        }

        var minimumWidth = 140f * addonScale;
        var minimumHeight = 24f * addonScale;

        var best = candidates
            .Where(candidate => candidate.Size.X >= minimumWidth && candidate.Size.Y >= minimumHeight)
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .ThenBy(candidate => candidate.Position.Y)
            .FirstOrDefault();

        if (best.Input != null)
        {
            return best;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .FirstOrDefault();
    }

    private static TextInputTarget PickBestMessageBookInputCandidate(List<TextInputTarget> candidates, float addonScale)
    {
        if (candidates.Count == 0)
        {
            return default;
        }

        var minimumWidth = 160f * addonScale;
        var minimumHeight = 22f * addonScale;

        var best = candidates
            .Where(candidate => candidate.Size.X >= minimumWidth && candidate.Size.Y >= minimumHeight)
            .OrderByDescending(candidate => candidate.Size.X)
            .ThenByDescending(candidate => candidate.Size.Y)
            .FirstOrDefault();

        if (best.Input != null)
        {
            return best;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .FirstOrDefault();
    }

    private static TextInputTarget PickBestMacroInputCandidate(List<TextInputTarget> candidates, float addonScale)
    {
        // The macro body is the biggest multiline text area, so area sorting is the safest match here.
        if (candidates.Count == 0)
        {
            return default;
        }

        var minimumWidth = 180f * addonScale;
        var minimumHeight = 120f * addonScale;

        var best = candidates
            .Where(candidate => candidate.Size.X >= minimumWidth && candidate.Size.Y >= minimumHeight)
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .FirstOrDefault();

        if (best.Input != null)
        {
            return best;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .FirstOrDefault();
    }

    private static TextInputTarget PickBestTofuInputStringCandidate(List<TextInputTarget> candidates, float addonScale)
    {
        // NodeID 2 is the known Tofu input; fallback keeps the button working if the node id shifts later.
        if (candidates.Count == 0)
        {
            return default;
        }

        var best = candidates
            .FirstOrDefault(candidate => candidate.Node != null && candidate.Node->NodeId == 2);

        if (best.Input != null)
        {
            return best;
        }

        var minimumWidth = 120f * addonScale;
        var minimumHeight = 20f * addonScale;

        best = candidates
            .Where(candidate => candidate.Size.X >= minimumWidth && candidate.Size.Y >= minimumHeight)
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .FirstOrDefault();

        if (best.Input != null)
        {
            return best;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Size.X * candidate.Size.Y)
            .FirstOrDefault();
    }

    private static void CollectTextInputTargetsFromNodeList(AtkUnitBase* addon, float scale, List<TextInputTarget> output)
    {
        if (addon == null || addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= 0)
        {
            return;
        }

        var count = Math.Min((uint)addon->UldManager.NodeListCount, 4096u);
        for (var i = 0u; i < count; i++)
        {
            AddTextInputTargetFromNode(addon, addon->UldManager.NodeList[i], scale, output);
        }
    }

    private static void CollectTextInputTargetsFromTree(AtkUnitBase* addon, AtkResNode* startNode, float scale, List<TextInputTarget> output, int depth)
    {
        if (addon == null || startNode == null || depth > 64)
        {
            return;
        }

        var node = startNode;
        var guard = 0;
        while (node != null && guard++ < 4096)
        {
            AddTextInputTargetFromNode(addon, node, scale, output);

            if (node->ChildNode != null)
            {
                CollectTextInputTargetsFromTree(addon, node->ChildNode, scale, output, depth + 1);
            }

            node = node->NextSiblingNode;
        }
    }

    private static void AddTextInputTargetFromNode(AtkUnitBase* addon, AtkResNode* node, float scale, List<TextInputTarget> output)
    {
        if (addon == null || node == null || !node->IsVisible())
        {
            return;
        }

        var input = node->GetAsAtkComponentTextInput();
        if (input == null || !input->Enabled)
        {
            return;
        }

        var size = GetNodeScreenSize(node, scale);
        if (size.X <= 10f || size.Y <= 10f)
        {
            return;
        }

        var position = new Vector2(node->ScreenX, node->ScreenY);

        // Avoid exact duplicate entries
        foreach (var existing in output)
        {
            if (existing.Node == node)
            {
                return;
            }
        }

        output.Add(new TextInputTarget(addon, input, node, position, size));
    }

    private static int GetCaretMoveCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Clamp(StringInfo.ParseCombiningCharacters(text).Length, 1, 128);
    }

    // Win32 Interop to simulate arrows
    private static void SendRightArrowKeyPress(int caretMoves)
    {
        SendKeyPress(VirtualKeyRight, caretMoves);
    }

    private static void SendBackspaceKeyPress()
    {
        SendKeyPress(VirtualKeyBackspace, 1);
    }

    private static void SendKeyPress(ushort virtualKey, int pressCount)
    {
        if (pressCount <= 0)
        {
            return;
        }

        var inputs = new Input[Math.Clamp(pressCount, 1, 128) * 2];
        for (var i = 0; i < inputs.Length; i += 2)
        {
            inputs[i] = Input.Keyboard(virtualKey, 0);
            inputs[i + 1] = Input.Keyboard(virtualKey, KeyEventKeyUp);
        }

        _ = SendInput((uint)inputs.Length, ref inputs[0], Marshal.SizeOf<Input>());
    }

    private const ushort VirtualKeyBackspace = 0x08;
    private const ushort VirtualKeyRight = 0x27;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;

        public static Input Keyboard(ushort virtualKey, uint flags)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    KeyboardInput = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        ScanCode = 0,
                        Flags = flags,
                        Time = 0,
                        ExtraInfo = UIntPtr.Zero,
                    },
                },
            };
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput KeyboardInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, ref Input inputs, int sizeOfInputStructure);

    // Configuration helpers
    private List<string> Getfavsymbols()
    {
        this.Config.favsymbols ??= new List<string>();
        this.Config.FavoriteSymbols ??= new List<string>();
        if (this.Config.favsymbols.Count == 0 && this.Config.FavoriteSymbols.Count > 0)
        {
            this.Config.favsymbols = this.Config.FavoriteSymbols.ToList();
        }

        if (this.Config.favsymbols.Count <= 1)
        {
            return this.Config.favsymbols;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        for (var i = this.Config.favsymbols.Count - 1; i >= 0; i--)
        {
            var symbol = this.Config.favsymbols[i];
            if (string.IsNullOrWhiteSpace(symbol) || !seen.Add(symbol))
            {
                this.Config.favsymbols.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
        {
            this.Config.FavoriteSymbols = this.Config.favsymbols.ToList();
            this.SaveConfig();
        }

        return this.Config.favsymbols;
    }

    private bool IsFavorite(string symbol)
    {
        return this.Getfavsymbols().Contains(symbol, StringComparer.Ordinal);
    }

    private void ToggleFavorite(string symbol)
    {
        var favs = this.Getfavsymbols();
        var existingIndex = favs.FindIndex(item => string.Equals(item, symbol, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            favs.RemoveAt(existingIndex);
        }
        else
        {
            favs.Add(symbol);
        }

        this.Config.FavoriteSymbols = favs.ToList();
        this.SaveConfig();
    }

    private void QueueButtonPositionSave()
    {
        if (!this.bPositionDirty)
        {
            return;
        }

        // Keep the compatibility fields in sync immediately, but leave the actual file write queued.
        // Dragging can update this every frame, and writing right here can hit Dalamud config storage too often.
        this.Config.HasCustomButtonPosition = this.Config.HasCustombPosition;
        this.Config.ButtonPosition = this.Config.bPosition;
        this.bPositionSaveQueued = true;
    }

    private void FlushButtonPositionSave()
    {
        if (!this.bPositionSaveQueued || this.draggingButton)
        {
            return;
        }

        // The queued write is flushed from FrameworkUpdate so config saves stay limited to the normal frame loop.
        // Waiting until dragging ends also avoids saving a new file for every tiny mouse movement.
        this.bPositionSaveQueued = false;
        this.bPositionDirty = false;
        this.SaveConfig();
    }

    private static Vector2 GetNodeScreenSize(AtkResNode* node, float scale)
    {
        var scaleX = Math.Abs(node->ScaleX);
        var scaleY = Math.Abs(node->ScaleY);
        if (scaleX <= 0.01f)
        {
            scaleX = 1f;
        }

        if (scaleY <= 0.01f)
        {
            scaleY = 1f;
        }

        return new Vector2(node->Width * scaleX * scale, node->Height * scaleY * scale);
    }

    private static Vector2 ClampPositionToScreen(Vector2 position, Vector2 size)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        return new Vector2(
            Math.Clamp(position.X, 0f, Math.Max(0f, displaySize.X - size.X)),
            Math.Clamp(position.Y, 0f, Math.Max(0f, displaySize.Y - size.Y)));
    }

    private static uint Color(Vector4 color)
    {
        return ImGui.GetColorU32(color);
    }

    // Home tab symbols | Original full game-symbol list.
    private static string[] BuildSymbols()
    {
        var symbols = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string symbol)
        {
            if (!string.IsNullOrWhiteSpace(symbol) && seen.Add(symbol))
            {
                symbols.Add(symbol);
            }
        }

        void AddRange(int startInclusive, int endInclusive)
        {
            for (var codepoint = startInclusive; codepoint <= endInclusive; codepoint++)
            {
                Add(char.ConvertFromUtf32(codepoint));
            }
        }

        void AddTextSymbols(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text, i))
                {
                    continue;
                }

                var codepoint = char.ConvertToUtf32(text, i);
                if (char.IsHighSurrogate(text[i]))
                {
                    i++;
                }

                Add(char.ConvertFromUtf32(codepoint));
            }
        }

        AddTextSymbols("★☆♠ ♡ ♢ ♣ ♤ ♥ ♦ ♧♪ ♭ ♯ °。・ ○ ◎ ● □ ■ △ ▼ ◆ ◇☀ ☁ ☂ ☃ ℃ ℉← ↑ → ↓ ⇔ ⇒ © ® ™ ℡ № § ¶ $ € ¥ £ ¢ ¤ 円∀ ∂ ∃ ⊇ ⊂ ≠ ≡ ≦ ∽ ∫ ∥ ∙ ∋ ∀ + - = ┓┗ ┐└ ┏┛ 』『」「┘┌├┝ ┥┤┣┠ ┨┫┰ ┯ ┬ ┳ ┴  ┷ ┼ ┻ ┸ ┿ ╂ ╂ ┿ ╋ 〒⊥∟⓪ ① ② ③ ④ ⑤ ⑥ ⑦ ⑧ ⑨ ⑩ ⑪ ⑫ ⑬ ⑭ ⑮ ⑯ ⑰ ⑱ ⑲ ⑳ ⑴ ⑵ ⑶ ⑷ ⑸ ⑹ ⑺ ⑻ ⑼ ⑽ ⑾ ⑿ ⒀ ⒁ ⒂ ⒃ ⒄ ⒅ ⒆ ⒇ ⒈⒉⒊⒋⒌⒍⒎⒏⒐㎎ ㎏ ㎜ ㎝ ㎞ ㎡ ㏄ ‐ –— ―‘’‚“”„†‡•‥…‰′  ⌒ ♣ ω  εïз ✓ ♀ † Å");

        AddRange(0xE020, 0xE02B);
        AddRange(0xE031, 0xE035);
        AddRange(0xE037, 0xE03F);
        AddRange(0xE040, 0xE044);
        AddRange(0xE048, 0xE04E);
        AddRange(0xE050, 0xE05F);
        AddRange(0xE060, 0xE06F);
        AddRange(0xE070, 0xE07F);
        AddRange(0xE080, 0xE08A);
        AddRange(0xE08F, 0xE08F);
        AddRange(0xE090, 0xE09F);
        AddRange(0xE0A0, 0xE0AF);
        AddRange(0xE0B0, 0xE0BF);
        AddRange(0xE0C0, 0xE0C6);
        AddRange(0xE0D0, 0xE0DB);
        AddRange(0xE0E0, 0xE0E9);

        return symbols.ToArray();
    }

    // Numbers tab symbols | Game number ranges + Unicode number variants.
    private static string[] BuildNumberSymbols()
    {
        var symbols = new List<string>();
        AddRange(symbols, 0xE060, 0xE069);
        AddRange(symbols, 0xE08F, 0xE09F);
        AddRange(symbols, 0xE0A0, 0xE0AE);
        AddRange(symbols, 0xE0B1, 0xE0B9);
        AddRange(symbols, 0xE0E0, 0xE0E9);
        AddTextSymbols(symbols, "⓪①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳⑴⑵⑶⑷⑸⑹⑺⑻⑼⑽⑾⑿⒀⒁⒂⒃⒄⒅⒆⒇⒈⒉⒊⒋⒌⒍⒎⒏⒐");
        return symbols.ToArray();
    }

    // Letters tab symbols | letter/alphabet related symbols.
    private static string[] BuildLetterSymbols()
    {
        var symbols = new List<string>
        {
            char.ConvertFromUtf32(0xE022),
            char.ConvertFromUtf32(0xE024),
        };

        AddRange(symbols, 0xE071, 0xE07F);
        AddRange(symbols, 0xE080, 0xE08A);
        return symbols.ToArray();
    }

    // Common tab symbols | Frequently useful symbols and small text symbols.
    private static string[] BuildCommonSymbols()
    {
        var symbols = new List<string>();
        AddRange(symbols, 0xE031, 0xE03F);
        AddRange(symbols, 0xE040, 0xE044);
        AddRange(symbols, 0xE048, 0xE04E);
        AddRange(symbols, 0xE050, 0xE05E);
        AddRange(symbols, 0xE06A, 0xE06F);
        AddRange(symbols, 0xE070, 0xE070);
        AddRange(symbols, 0xE0AF, 0xE0AF);
        AddRange(symbols, 0xE0BA, 0xE0BF);
        AddRange(symbols, 0xE0C0, 0xE0C0);
        AddTextSymbols(symbols, "★☆♠♡♢♣♤♥♦♧♪♭♯");
        return symbols.ToArray();
    }

    // Time tab symbols | Curated time-related symbols.
    private static string[] BuildTimeSymbols()
    {
        return
        [
            char.ConvertFromUtf32(0xE031),
            char.ConvertFromUtf32(0xE06B),
            char.ConvertFromUtf32(0xE06D),
            char.ConvertFromUtf32(0xE06E),
            char.ConvertFromUtf32(0xE0D0),
            char.ConvertFromUtf32(0xE0D1),
            char.ConvertFromUtf32(0xE0D2),
        ];
    }

    // Others tab symbols | Miscellaneous and extra Unicode symbols.
    private static string[] BuildOthersSymbols()
    {
        var symbols = new List<string>();
        AddRange(symbols, 0xE0D9, 0xE0DB);
        AddRange(symbols, 0xE0C1, 0xE0C6);
        AddRange(symbols, 0xE020, 0xE021);
        symbols.Add(char.ConvertFromUtf32(0xE023));
        AddRange(symbols, 0xE025, 0xE02B);
        AddRange(symbols, 0xE050, 0xE05A);
        AddTextSymbols(symbols, "☀☁☂☃℃℉°。・○◎●□■△▼◆◇←↑→↓⇔⇒©®™℡№§¶$€¥£¢¤円∀∂∃⊇⊂≠≡≦∽∫∥∙∋∀+-=┓┗┐└┏┛』『」「┘┌├┝┥┤┣┠┨┫┰┯┬┳┴┷┼┻┸┿╂╂┿╋〒⊥∟㎎㎏㎜㎝㎞㎡㏄‐–—―‘’‚“”„†‡•‥…‰′⌒ωεïз✓♀†Å");
        return symbols.ToArray();
    }

    private static void AddTextSymbols(List<string> symbols, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text, i))
            {
                continue;
            }

            var codepoint = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
            {
                i++;
            }

            symbols.Add(char.ConvertFromUtf32(codepoint));
        }
    }

    private static void AddRange(List<string> symbols, int startInclusive, int endInclusive)
    {
        for (var codepoint = startInclusive; codepoint <= endInclusive; codepoint++)
        {
            symbols.Add(char.ConvertFromUtf32(codepoint));
        }
    }


    // Auxiliary Types
    private readonly struct NewTextReplacementStatus
    {
        private NewTextReplacementStatus(bool valid, string message, bool highlightText)
        {
            this.Valid = valid;
            this.Message = message;
            this.HighlightText = highlightText;
        }

        public bool Valid { get; }
        public string Message { get; }
        public bool HighlightText { get; }

        public static NewTextReplacementStatus ValidStatus { get; } = new(true, string.Empty, false);

        public static NewTextReplacementStatus Invalid(string message, bool highlightText)
            => new(false, message, highlightText);
    }

    private enum PopupPlacement { AboveRight, Below }

    // Popup tabs | Must match the tab routing in DrawSymbolsPopup and the visual order in DrawPopupTabs.
    private enum PopupTab { Symbols, Numbers, Letters, Common, Others, Time, Custom }

    private enum SymbolInsertTarget { Chat, RecruitmentComment, MessageBookInput, MacroInput, TofuInputString, ConfigCustomEntry, FocusedTextInput, IpcCallback }
    private readonly unsafe struct TextInputTarget
    {
        public TextInputTarget(AtkUnitBase* addon, AtkComponentTextInput* input, AtkResNode* node, Vector2 position, Vector2 size)
        {
            this.Addon = addon;
            this.Input = input;
            this.Node = node;
            this.Position = position;
            this.Size = size;
        }

        public AtkUnitBase* Addon { get; }
        public AtkComponentTextInput* Input { get; }
        public AtkResNode* Node { get; }
        public Vector2 Position { get; }
        public Vector2 Size { get; }
    }

    private enum GameUiTheme
    {
        Dark = 0,
        Light = 1,
        ClassicFF = 2,
        ClearBlue = 3,
        ClearWhite = 4,
        ClearGreen = 5,
        ClearGrey = 6,
        ClearPink = 7,
        Unknown = 255,
    }

    private static GameUiTheme GetCurrentGameUiTheme()
    {
        try
        {
            if (GameConfig.System.TryGet("ColorThemeType", out uint themeId))
            {
                return themeId switch
                {
                    0 => GameUiTheme.Dark,
                    1 => GameUiTheme.Light,
                    2 => GameUiTheme.ClassicFF,
                    3 => GameUiTheme.ClearBlue,
                    4 => GameUiTheme.ClearWhite,
                    5 => GameUiTheme.ClearGreen,
                    6 => GameUiTheme.ClearGrey,
                    7 => GameUiTheme.ClearPink,
                    _ => GameUiTheme.Unknown,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read ColorThemeType from game config. {ex}");
        }

        return GameUiTheme.Unknown;
    }

    private readonly struct UiColors
    {
        public static readonly UiColors Default = Dark;

        private static readonly UiColors Dark = Create(
            popup: new Vector4(0.035f, 0.040f, 0.050f, 0.62f),
            button: new Vector4(0.105f, 0.110f, 0.125f, 0.90f),
            border: new Vector4(0.74f, 0.68f, 0.50f, 0.62f),
            text: new Vector4(0.94f, 0.94f, 0.92f, 1f),
            muted: new Vector4(0.62f, 0.62f, 0.66f, 0.74f),
            symbol: new Vector4(1f, 1f, 1f, 1f),
            clearStyle: false);

        private static readonly UiColors Light = Create(
            popup: new Vector4(0.72f, 0.66f, 0.55f, 0.66f),
            button: new Vector4(0.60f, 0.52f, 0.41f, 0.90f),
            border: new Vector4(0.34f, 0.27f, 0.19f, 0.54f),
            text: new Vector4(0.13f, 0.11f, 0.09f, 1f),
            muted: new Vector4(0.20f, 0.18f, 0.15f, 0.68f),
            symbol: new Vector4(0.10f, 0.08f, 0.06f, 1f),
            clearStyle: false);

        private static readonly UiColors ClassicFF = Create(
            popup: new Vector4(0.010f, 0.055f, 0.310f, 0.72f),
            button: new Vector4(0.020f, 0.090f, 0.470f, 0.92f),
            border: new Vector4(0.88f, 0.88f, 1.00f, 0.72f),
            text: new Vector4(0.98f, 0.98f, 1.00f, 1f),
            muted: new Vector4(0.80f, 0.82f, 0.95f, 0.76f),
            symbol: new Vector4(1f, 1f, 1f, 1f),
            clearStyle: false);

        private static readonly UiColors ClearBlue = Create(
            popup: new Vector4(0.145f, 0.265f, 0.455f, 0.56f),
            button: new Vector4(0.170f, 0.335f, 0.620f, 0.76f),
            border: new Vector4(0.66f, 0.80f, 1.00f, 0.54f),
            text: new Vector4(0.95f, 0.98f, 1.00f, 1f),
            muted: new Vector4(0.75f, 0.86f, 1.00f, 0.78f),
            symbol: new Vector4(1f, 1f, 1f, 1f),
            clearStyle: true);

        private static readonly UiColors ClearWhite = Create(
            popup: new Vector4(0.88f, 0.90f, 0.94f, 0.58f),
            button: new Vector4(0.88f, 0.91f, 0.96f, 0.78f),
            border: new Vector4(0.30f, 0.36f, 0.45f, 0.46f),
            text: new Vector4(0.08f, 0.10f, 0.13f, 1f),
            muted: new Vector4(0.20f, 0.23f, 0.28f, 0.66f),
            symbol: new Vector4(0.04f, 0.05f, 0.07f, 1f),
            clearStyle: true);

        private static readonly UiColors ClearGreen = Create(
            popup: new Vector4(0.125f, 0.335f, 0.265f, 0.56f),
            button: new Vector4(0.120f, 0.400f, 0.315f, 0.76f),
            border: new Vector4(0.70f, 0.95f, 0.82f, 0.52f),
            text: new Vector4(0.94f, 1.00f, 0.96f, 1f),
            muted: new Vector4(0.74f, 0.95f, 0.82f, 0.76f),
            symbol: new Vector4(1f, 1f, 1f, 1f),
            clearStyle: true);

        private static readonly UiColors ClearGrey = Create(
            popup: new Vector4(0.270f, 0.285f, 0.305f, 0.56f),
            button: new Vector4(0.345f, 0.365f, 0.390f, 0.76f),
            border: new Vector4(0.78f, 0.82f, 0.86f, 0.50f),
            text: new Vector4(0.96f, 0.97f, 0.98f, 1f),
            muted: new Vector4(0.78f, 0.80f, 0.84f, 0.76f),
            symbol: new Vector4(1f, 1f, 1f, 1f),
            clearStyle: true);

        private static readonly UiColors ClearPink = Create(
            popup: new Vector4(0.480f, 0.185f, 0.315f, 0.56f),
            button: new Vector4(0.620f, 0.250f, 0.425f, 0.76f),
            border: new Vector4(1.00f, 0.72f, 0.86f, 0.52f),
            text: new Vector4(1.00f, 0.95f, 0.98f, 1f),
            muted: new Vector4(1.00f, 0.77f, 0.90f, 0.76f),
            symbol: new Vector4(1f, 1f, 1f, 1f),
            clearStyle: true);

        public UiColors(
            Vector4 PopupBackground,
            Vector4 Button,
            Vector4 ButtonHovered,
            Vector4 ButtonActive,
            Vector4 EditButton,
            Vector4 EditButtonHovered,
            Vector4 Border,
            Vector4 CellBackground,
            Vector4 CellHovered,
            Vector4 CellBorder,
            Vector4 Text,
            Vector4 SymbolText,
            Vector4 MutedText,
            Vector4 ScrollTrack,
            Vector4 ScrollThumb)
        {
            this.PopupBackground = PopupBackground;
            this.Button = Button;
            this.ButtonHovered = ButtonHovered;
            this.ButtonActive = ButtonActive;
            this.EditButton = EditButton;
            this.EditButtonHovered = EditButtonHovered;
            this.Border = Border;
            this.CellBackground = CellBackground;
            this.CellHovered = CellHovered;
            this.CellBorder = CellBorder;
            this.Text = Text;
            this.SymbolText = SymbolText;
            this.MutedText = MutedText;
            this.ScrollTrack = ScrollTrack;
            this.ScrollThumb = ScrollThumb;
        }

        public Vector4 PopupBackground { get; }
        public Vector4 Button { get; }
        public Vector4 ButtonHovered { get; }
        public Vector4 ButtonActive { get; }
        public Vector4 EditButton { get; }
        public Vector4 EditButtonHovered { get; }
        public Vector4 Border { get; }
        public Vector4 CellBackground { get; }
        public Vector4 CellHovered { get; }
        public Vector4 CellBorder { get; }
        public Vector4 Text { get; }
        public Vector4 SymbolText { get; }
        public Vector4 MutedText { get; }
        public Vector4 ScrollTrack { get; }
        public Vector4 ScrollThumb { get; }

        public static UiColors FromDalamudStyle()
        {
            var style = ImGui.GetStyle();
            var text = style.Colors[(int)ImGuiCol.Text];
            var muted = style.Colors[(int)ImGuiCol.TextDisabled];
            var popup = style.Colors[(int)ImGuiCol.PopupBg];
            var button = style.Colors[(int)ImGuiCol.Button];
            var border = style.Colors[(int)ImGuiCol.Border];

            return new UiColors(
                PopupBackground: popup,
                Button: button,
                ButtonHovered: style.Colors[(int)ImGuiCol.ButtonHovered],
                ButtonActive: style.Colors[(int)ImGuiCol.ButtonActive],
                EditButton: style.Colors[(int)ImGuiCol.Button],
                EditButtonHovered: style.Colors[(int)ImGuiCol.ButtonHovered],
                Border: border,
                CellBackground: style.Colors[(int)ImGuiCol.FrameBg],
                CellHovered: style.Colors[(int)ImGuiCol.FrameBgHovered],
                CellBorder: border,
                Text: text,
                SymbolText: text,
                MutedText: muted,
                ScrollTrack: style.Colors[(int)ImGuiCol.ScrollbarBg],
                ScrollThumb: style.Colors[(int)ImGuiCol.ScrollbarGrab]);
        }

        public static UiColors FromGameTheme(GameUiTheme theme, AddonChatLog* chatLog)
        {
            return theme switch
            {
                GameUiTheme.Dark => Dark,
                GameUiTheme.Light => Light,
                GameUiTheme.ClassicFF => ClassicFF,
                GameUiTheme.ClearBlue => ClearBlue,
                GameUiTheme.ClearWhite => ClearWhite,
                GameUiTheme.ClearGreen => ClearGreen,
                GameUiTheme.ClearGrey => ClearGrey,
                GameUiTheme.ClearPink => ClearPink,
                _ => FromChatLogFallback(chatLog),
            };
        }

        private static UiColors Create(Vector4 popup, Vector4 button, Vector4 border, Vector4 text, Vector4 muted, Vector4 symbol, bool clearStyle)
        {
            var hoverLift = IsLight(text) ? -0.055f : 0.075f;
            var activeLift = IsLight(text) ? -0.100f : -0.055f;
            var cellAlpha = clearStyle ? 0.16f : 0.22f;
            var cellHoverAlpha = clearStyle ? 0.34f : 0.42f;
            var scrollTrackAlpha = clearStyle ? 0.22f : 0.32f;

            return new UiColors(
                PopupBackground: WithAlpha(popup, Math.Clamp(popup.W + 0.15f, 0f, 1f)),
                Button: button,
                ButtonHovered: Lift(button, hoverLift, Math.Clamp(button.W + 0.08f, 0f, 1f)),
                ButtonActive: Lift(button, activeLift, Math.Clamp(button.W + 0.12f, 0f, 1f)),
                EditButton: new Vector4(0.78f, 0.05f, 0.05f, 0.90f),
                EditButtonHovered: new Vector4(0.95f, 0.08f, 0.08f, 0.96f),
                Border: border,
                CellBackground: WithAlpha(button, cellAlpha),
                CellHovered: WithAlpha(Lift(button, hoverLift, 1f), cellHoverAlpha),
                CellBorder: WithAlpha(border, clearStyle ? 0.22f : 0.28f),
                Text: text,
                SymbolText: symbol,
                MutedText: muted,
                ScrollTrack: WithAlpha(button, scrollTrackAlpha),
                ScrollThumb: WithAlpha(border, 0.72f));
        }

        private static UiColors FromChatLogFallback(AddonChatLog* chatLog)
        {
            if (chatLog == null)
            {
                return Default;
            }

            var baseColor = TryExtractThemeColor(chatLog, Default.Button);
            if (!IsUsableThemeColor(baseColor))
            {
                return Default;
            }

            var luminance = Luminance(baseColor);
            var text = luminance > 0.58f
                ? new Vector4(0.08f, 0.08f, 0.08f, 1f)
                : new Vector4(0.96f, 0.96f, 0.96f, 1f);
            var muted = WithAlpha(text, 0.70f);
            var symbol = text;
            var border = WithAlpha(Lift(baseColor, luminance > 0.58f ? -0.24f : 0.24f, 1f), 0.56f);

            return Create(
                popup: WithAlpha(baseColor, 0.58f),
                button: WithAlpha(baseColor, 0.86f),
                border: border,
                text: text,
                muted: muted,
                symbol: symbol,
                clearStyle: false);
        }

        private static Vector4 TryExtractThemeColor(AddonChatLog* chatLog, Vector4 fallback)
        {
            if (chatLog == null)
            {
                return fallback;
            }

            if (chatLog->BackgroundNode != null)
            {
                var color = FromNodeColor(&chatLog->BackgroundNode->AtkResNode, fallback, 0.80f);
                if (IsUsableThemeColor(color))
                {
                    return color;
                }
            }

            if (chatLog->ChannelSelectDropDown != null)
            {
                var node = chatLog->ChannelSelectDropDown->AtkComponentBase.OwnerNode;
                if (node != null)
                {
                    var color = FromNodeColor(&node->AtkResNode, fallback, 0.82f);
                    if (IsUsableThemeColor(color))
                    {
                        return color;
                    }
                }
            }

            return fallback;
        }

        private static Vector4 FromNodeColor(AtkResNode* node, Vector4 fallback, float alpha)
        {
            if (node == null)
            {
                return fallback;
            }

            var color = node->Color;
            if (color.R == 0 && color.G == 0 && color.B == 0 && color.A == 0)
            {
                return fallback;
            }

            return new Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                Math.Clamp((color.A / 255f) * alpha, 0.20f, 0.95f));
        }

        private static bool IsUsableThemeColor(Vector4 color)
        {
            var max = Math.Max(color.X, Math.Max(color.Y, color.Z));
            var min = Math.Min(color.X, Math.Min(color.Y, color.Z));
            var saturation = max - min;
            var luminance = Luminance(color);

            if (luminance > 0.78f && saturation < 0.12f)
            {
                return false;
            }

            return color.W > 0.05f;
        }

        private static bool IsLight(Vector4 color)
        {
            return Luminance(color) > 0.70f;
        }

        private static float Luminance(Vector4 color)
        {
            return color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
        }

        private static Vector4 WithAlpha(Vector4 color, float alpha)
        {
            return new Vector4(color.X, color.Y, color.Z, alpha);
        }

        private static Vector4 Lift(Vector4 color, float amount, float alpha)
        {
            return new Vector4(
                Math.Clamp(color.X + amount, 0f, 1f),
                Math.Clamp(color.Y + amount, 0f, 1f),
                Math.Clamp(color.Z + amount, 0f, 1f),
                alpha);
        }
    }

}
