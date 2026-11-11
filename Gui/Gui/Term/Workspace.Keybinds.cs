using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Gui.Term;

public sealed partial class Workspace
{
    private Keybinds _keybinds = null!;

    /// <summary>
    /// When nonzero, Ctrl+K was pressed and the next chord (Ctrl+O) opens a directory. Holds
    /// the tick count of the first key so a stale prefix expires instead of firing later.
    /// </summary>
    private long _openDirectoryChord;

    /// <summary>
    /// Handlers keyed by the action ids the "keybind" lines use. The palette registers one
    /// per command row, so a bound action runs exactly the same code as clicking the row.
    /// </summary>
    private readonly Dictionary<string, Action> _keybindActions = new(StringComparer.Ordinal);

    private void RefreshKeybinds() => _keybinds = new Keybinds(_config);

    private void RunKeybindAction(string? action)
    {
        if (action is null) return;
        if (_keybindActions.TryGetValue(action, out Action? run))
        {
            run();
            return;
        }
        Diag.Log("keybind", $"no handler for action '{action}'");
    }

    /// <summary>
    /// Maps a palette row's localized label onto the action id the config uses. Matching by
    /// label keeps the row's handler and its displayed chord in one place: there is no second
    /// table of actions that could drift from the commands the palette actually runs.
    /// </summary>
    private static string? KeybindActionForLabel(string label)
    {
        if (label == Strings.ResetTerminal) return "reset";
        if (label == Strings.CopyToClipboard) return "copy_to_clipboard";
        if (label == Strings.CopySelectionPlain) return "copy_to_clipboard:plain";
        if (label == Strings.CopySelectionAnsi) return "copy_to_clipboard:vt";
        if (label == Strings.CopySelectionHtml) return "copy_to_clipboard:html";
        if (label == Strings.CopyUrl) return "copy_url_to_clipboard";
        if (label == Strings.CopyTitle) return "copy_title_to_clipboard";
        if (label == Strings.PasteFromClipboard) return "paste_from_clipboard";
        if (label == Strings.PasteFromSelection) return "paste_from_selection";

        if (label == Strings.StartSearch) return "start_search";
        if (label == Strings.SearchSelection) return "search_selection";
        if (label == Strings.EndSearch) return "end_search";
        if (label == Strings.NextSearchResult) return "navigate_search:next";
        if (label == Strings.PreviousSearchResult) return "navigate_search:previous";

        if (label == Strings.IncreaseFontSize) return "increase_font_size:1";
        if (label == Strings.DecreaseFontSize) return "decrease_font_size:1";
        if (label == Strings.ResetFontSize) return "reset_font_size";

        if (label == Strings.ClearScreen) return "clear_screen";
        if (label == Strings.SelectAll) return "select_all";
        if (label == Strings.ScrollToTop) return "scroll_to_top";
        if (label == Strings.ScrollToBottom) return "scroll_to_bottom";
        if (label == Strings.ScrollToSelection) return "scroll_to_selection";
        if (label == Strings.ScrollPageUp) return "scroll_page_up";
        if (label == Strings.ScrollPageDown) return "scroll_page_down";

        if (label == Strings.WriteScreenFile("copy", string.Empty)) return "write_screen_file:copy";
        if (label == Strings.WriteScreenFile("paste", string.Empty)) return "write_screen_file:paste";
        if (label == Strings.WriteScreenFile("open", string.Empty)) return "write_screen_file:open";

        if (label == Strings.NewWindow) return "new_window";
        if (label == Strings.NewTab) return "new_tab";
        if (label == Strings.Settings) return "open_config";
        if (label == Strings.MoveTabLeft) return "move_tab:-1";
        if (label == Strings.MoveTabRight) return "move_tab:1";
        if (label == Strings.MoveTabToNewWindow) return "move_tab_to_new_window";
        if (label == Strings.NextTab) return "next_tab";
        if (label == Strings.PreviousTab) return "previous_tab";
        if (label == Strings.LastTab) return "last_tab";
        if (label == Strings.ToggleSidebar) return "toggle_sidebar";

        if (label == Strings.ChangeTerminalTitle) return "prompt_surface_title";
        if (label == Strings.ChangeTabTitle) return "prompt_tab_title";
        if (label == Strings.ChangeWindowTitle) return "prompt_window_title";

        if (label == Strings.SplitRight) return "new_split:right";
        if (label == Strings.SplitDown) return "new_split:down";
        if (label == Strings.SplitLeft) return "new_split:left";
        if (label == Strings.SplitUp) return "new_split:up";
        if (label == Strings.FocusSplitPrevious) return "goto_split:previous";
        if (label == Strings.FocusSplitNext) return "goto_split:next";
        if (label == Strings.FocusSplitLeft) return "goto_split:left";
        if (label == Strings.FocusSplitRight) return "goto_split:right";
        if (label == Strings.FocusSplitUp) return "goto_split:up";
        if (label == Strings.FocusSplitDown) return "goto_split:down";
        if (label == Strings.ToggleZoom) return "toggle_split_zoom";
        if (label == Strings.ToggleReadOnly) return "toggle_readonly";
        if (label == Strings.EqualizeSplits) return "equalize_splits";
        if (label == Strings.ResizeSplitLeft) return "resize_split:left";
        if (label == Strings.ResizeSplitRight) return "resize_split:right";
        if (label == Strings.ResizeSplitUp) return "resize_split:up";
        if (label == Strings.ResizeSplitDown) return "resize_split:down";

        if (label == Strings.ResetWindowSize) return "reset_window_size";
        if (label == Strings.ShowOnScreenKeyboard) return "show_on_screen_keyboard";
        if (label == Strings.OpenConfigOs) return "open_config_file";
        if (label == Strings.OpenConfigNewWindow) return "open_config:new_window";
        if (label == Strings.ReloadConfig) return "reload_config";

        if (label == Strings.CloseTerminal) return "close_surface";
        if (label == Strings.CloseTab) return "close_tab:this";
        if (label == Strings.CloseOtherTabs) return "close_tab:other";
        if (label == Strings.CloseTabsToTheRight) return "close_tab:right";
        if (label == Strings.CloseWindowCommand) return "close_window";
        if (label == Strings.CloseAllWindows) return "close_all_windows";

        if (label == Strings.ToggleMaximize) return "toggle_maximize";
        if (label == Strings.ToggleFullscreen) return "toggle_fullscreen";
        if (label == Strings.ToggleFloatOnTop) return "toggle_window_float_on_top";
        if (label == Strings.ToggleMouseReporting) return "toggle_mouse_reporting";
        if (label == Strings.ToggleBackgroundOpacity) return "toggle_background_opacity";

        if (label == Strings.Quit) return "quit";
        return null;
    }

    /// <summary>
    /// Actions the palette has no row for: the palette toggle itself, the Alt+1..8 tab slots
    /// and the hands-on terminal commands that only exist as chords.
    /// </summary>
    private void RegisterBuiltInKeybindActions()
    {
        _keybindActions.Clear();

        _keybindActions["jump_to_prompt:-1"] = () => ActivePane()?.JumpToPrompt(-1);
        _keybindActions["jump_to_prompt:1"] = () => ActivePane()?.JumpToPrompt(1);

        _keybindActions["adjust_selection:left"] = () => ActivePane()?.AdjustSelection(-1, 0);
        _keybindActions["adjust_selection:right"] = () => ActivePane()?.AdjustSelection(1, 0);
        _keybindActions["adjust_selection:up"] = () => ActivePane()?.AdjustSelection(0, -1);
        _keybindActions["adjust_selection:down"] = () => ActivePane()?.AdjustSelection(0, 1);

        _keybindActions["toggle_command_palette"] = () =>
        {
            if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible)
                HideCommandPalette();
            else
                ShowCommandPalette();
        };

        for (int slot = 0; slot < 8; slot++)
        {
            int target = slot;
            _keybindActions[$"goto_tab:{target + 1}"] = () =>
            {
                if (target < _tabs.Count) Activate(target);
            };
        }
    }

    /// <summary>Ghostty's mixed copy spelling means the same as the plain action here.</summary>
    private void LinkKeybindAliases()
    {
        if (_keybindActions.TryGetValue("copy_to_clipboard", out Action? copy))
            _keybindActions["copy_to_clipboard:mixed"] = copy;
    }
}
