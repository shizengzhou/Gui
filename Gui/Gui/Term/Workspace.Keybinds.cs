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
    /// Cached once: "performable:" bindings ask this on every key press, and a method-group
    /// conversion would allocate a delegate each time.
    /// </summary>
    private Func<string, bool>? _canPerform;

    /// <summary>
    /// The pane an "all:" action is currently running for; ActivePane() answers it while
    /// the flag is set, so the shared action table targets each surface in turn.
    /// </summary>
    private TerminalPane? _surfaceTarget;

    /// <summary>
    /// Handlers keyed by the action ids the "keybind" lines use. The palette registers one
    /// per command row, so a bound action runs exactly the same code as clicking the row.
    /// </summary>
    private readonly Dictionary<string, Action> _keybindActions = new(StringComparer.Ordinal);

    /// <summary>The chords of the trigger sequence that is waiting for its next key.</summary>
    private string? _pendingSequence;

    /// <summary>The keys that built <see cref="_pendingSequence"/>, in press order.</summary>
    private readonly List<(int Mods, VirtualKey Key)> _pendingKeys = new();

    private void RefreshKeybinds()
    {
        _keybinds = new Keybinds(_config);
        ClearPendingSequence();

        // The "global:" set is registered with the OS once per process, and only a real
        // window owns it: the quick terminal rebuilds its config from the file alone (no
        // launch overrides), so applying that would drop every hotkey the app registered.
        if (!_quickTerminal) GlobalKeys.Apply(_config);
    }

    /// <summary>Drops a sequence that is still waiting for its next key.</summary>
    private void ClearPendingSequence()
    {
        _pendingSequence = null;
        _pendingKeys.Clear();
    }

    /// <summary>
    /// Sends the held prefix to the shell because the key that followed it did not continue
    /// the sequence. Ghostty replays the whole sequence in that case, so "ctrl+a" then an
    /// unbound key still reaches the program as ^A and that key.
    /// </summary>
    private void ReplayPendingKeys()
    {
        if (_pendingKeys.Count == 0) return;

        TerminalPane? pane = ActivePane();
        if (pane is not null)
        {
            foreach ((int mods, VirtualKey key) in _pendingKeys)
                pane.SendBufferedKey(key, mods);
        }
        _pendingKeys.Clear();
    }

    private void RunKeybindAction(string? action, bool all = false)
    {
        if (action is null) return;

        // "all:" runs a surface action once per pane in the window. The action table only
        // ever reaches a pane through ActivePane(), so pointing that at each pane in turn is
        // enough — no second, pane-targeted copy of the table. A window- or app-level action
        // (new_tab, quit, splits...) runs once, which is Ghostty's rule for the prefix.
        if (all && IsSurfaceAction(action))
        {
            foreach (TerminalPane pane in PanesInWindow())
            {
                _surfaceTarget = pane;
                try
                {
                    RunKeybindActionCore(action);
                }
                finally
                {
                    _surfaceTarget = null;
                }
            }
            return;
        }

        RunKeybindActionCore(action);
    }

    /// <summary>ESC, the CSI introducer when asked for, and the body, as one write.</summary>
    private static byte[] Introduce(byte[] body, bool csi)
    {
        int prefix = csi ? 2 : 1;
        var sequence = new byte[prefix + body.Length];
        sequence[0] = 0x1B;
        if (csi) sequence[1] = (byte)'[';
        body.CopyTo(sequence, prefix);
        return sequence;
    }

    /// <summary>
    /// Runs a "global:" binding's action on this window, whichever window had the focus when
    /// the chord was pressed. "global:" implies "all:", so a surface action runs once per
    /// pane; window- and app-level actions run once.
    /// </summary>
    internal void RunGlobalAction(string action) => RunKeybindAction(action, all: true);

    /// <summary>
    /// The actions that act on one terminal surface. Everything else is window- or
    /// app-level, so "all:" must not repeat it.
    /// </summary>
    private static bool IsSurfaceAction(string action) => action switch
    {
        "reset" or "clear_screen" or "select_all" or "toggle_readonly" or "close_surface"
            or "prompt_surface_title" or "toggle_mouse_reporting"
            or "copy_to_clipboard" or "copy_to_clipboard:plain"
            or "copy_to_clipboard:vt" or "copy_to_clipboard:html"
            or "copy_url_to_clipboard" or "copy_title_to_clipboard"
            or "paste_from_clipboard" or "paste_from_selection"
            or "start_search" or "search_selection" or "end_search"
            or "increase_font_size:1" or "decrease_font_size:1" or "reset_font_size"
            or "scroll_to_top" or "scroll_to_bottom" or "scroll_to_selection"
            or "scroll_page_up" or "scroll_page_down"
            or "write_screen_file:copy" or "write_screen_file:paste" or "write_screen_file:open"
            => true,
        _ => action.StartsWith("adjust_selection:", StringComparison.Ordinal)
            || action.StartsWith("jump_to_prompt:", StringComparison.Ordinal)
            || action.StartsWith("navigate_search:", StringComparison.Ordinal)
            || action.StartsWith("text:", StringComparison.Ordinal)
            || action.StartsWith("csi:", StringComparison.Ordinal)
            || action.StartsWith("esc:", StringComparison.Ordinal)
            || action.StartsWith("cursor_key:", StringComparison.Ordinal),
    };

    /// <summary>A snapshot, because an "all:" action may close panes while it runs.</summary>
    private List<TerminalPane> PanesInWindow()
    {
        var panes = new List<TerminalPane>();
        foreach (Tab tab in _tabs) ForEachPane(tab, panes.Add);
        return panes;
    }

    /// <summary>The one action dispatch, with ActivePane() resolved by the caller.</summary>
    private void RunKeybindActionCore(string action)
    {
        // "ignore" is a real binding that does nothing: the key is consumed and nothing is
        // encoded. "unconsumed:ctrl+a=ignore" is the documented way to forward a key.
        if (string.Equals(action, "ignore", StringComparison.Ordinal)) return;

        // "text:" takes Zig string-literal escapes and is decoded when the binding runs, like
        // Ghostty; "csi:" and "esc:" take their value literally and only get the prefix.
        const string TextAction = "text:";
        const string CsiAction = "csi:";
        const string EscAction = "esc:";

        if (action.StartsWith(TextAction, StringComparison.Ordinal))
        {
            if (ActionText.TryDecode(action[TextAction.Length..], out byte[] text))
                ActivePane()?.SendRaw(text);
            else
                Diag.Warn("keybind", $"text binding has an invalid escape: '{action}'");
            return;
        }

        // "csi:" takes the sequence without the ESC [ header. Its value goes through the
        // same escape decoder as "text:", so csi:\x41 and csi:A send the same bytes.
        if (action.StartsWith(CsiAction, StringComparison.Ordinal))
        {
            if (ActionText.TryDecode(action[CsiAction.Length..], out byte[] body))
                ActivePane()?.SendRaw(Introduce(body, csi: true));
            else
                Diag.Warn("keybind", $"csi binding has an invalid escape: '{action}'");
            return;
        }

        // "cursor_key:<normal>,<application>" sends one of two sequences depending on DECCKM,
        // so a custom chord can behave like an arrow key in vim/less. Upstream defines the
        // action but cannot parse it from a config file, so the spelling here is ours.
        const string CursorKeyAction = "cursor_key:";
        if (action.StartsWith(CursorKeyAction, StringComparison.Ordinal))
        {
            if (!ActionText.TryDecodePair(action[CursorKeyAction.Length..], out byte[] normal, out byte[] application))
            {
                Diag.Warn("keybind", $"cursor_key needs 'normal,application': '{action}'");
                return;
            }

            TerminalPane? pane = ActivePane();
            if (pane is null) return;
            pane.SendRaw(pane.CursorKeysApplication && application.Length > 0 ? application : normal);
            return;
        }

        // "esc:" keeps its value literal, matching Ghostty's esc: action.
        if (action.StartsWith(EscAction, StringComparison.Ordinal))
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(action[EscAction.Length..]);
            ActivePane()?.SendRaw(Introduce(body, csi: false));
            return;
        }

        // Key tables live on the surface that owns the keyboard, so the stack actions
        // always target the focused pane (or the one "all:" is currently pointing at).
        const string Activate = "activate_key_table:";
        const string ActivateOnce = "activate_key_table_once:";
        if (action.StartsWith(Activate, StringComparison.Ordinal))
        {
            ActivateKeyTable(action[Activate.Length..], once: false);
            return;
        }
        if (action.StartsWith(ActivateOnce, StringComparison.Ordinal))
        {
            ActivateKeyTable(action[ActivateOnce.Length..], once: true);
            return;
        }
        if (action == "deactivate_key_table")
        {
            ActivePane()?.DeactivateKeyTable();
            return;
        }
        if (action == "deactivate_all_key_tables")
        {
            ActivePane()?.DeactivateAllKeyTables();
            return;
        }

        if (_keybindActions.TryGetValue(action, out Action? run))
        {
            run();
            return;
        }
        Diag.Log("keybind", $"no handler for action '{action}'");
    }

    /// <summary>
    /// The "performable:" prefix asks whether an action can do anything right now, so a
    /// binding that cannot run leaves the key to the program instead of swallowing it. Only
    /// actions with a real precondition are listed; everything else always runs.
    /// </summary>
    private bool CanPerform(string action)
    {
        TerminalPane? pane = ActivePane();
        switch (action)
        {
            // Copying and selection motions need a selection to act on.
            case "copy_to_clipboard":
            case "copy_to_clipboard:plain":
            case "copy_to_clipboard:vt":
            case "copy_to_clipboard:html":
            case "paste_from_selection":
            case "search_selection":
            case "scroll_to_selection":
            case "adjust_selection:left":
            case "adjust_selection:right":
            case "adjust_selection:up":
            case "adjust_selection:down":
                return pane?.HasSelection() == true;

            // Search navigation only means something while the find bar is open.
            case "navigate_search:next":
            case "navigate_search:previous":
            case "end_search":
                return pane?.SearchActive == true;

            // Tab and split motions need somewhere to move to.
            case "next_tab":
            case "previous_tab":
            case "last_tab":
            case "move_tab:-1":
            case "move_tab:1":
                return _tabs.Count > 1;

            case "goto_split:previous":
            case "goto_split:next":
            case "goto_split:left":
            case "goto_split:right":
            case "goto_split:up":
            case "goto_split:down":
            case "toggle_split_zoom":
            case "resize_split:left":
            case "resize_split:right":
            case "resize_split:up":
            case "resize_split:down":
                return ActiveTabPaneCount() > 1;

            // Scrolling only moves when there is history in that direction.
            case "scroll_to_top":
            case "scroll_page_up":
                return pane?.CanScrollBack == true;

            case "scroll_to_bottom":
            case "scroll_page_down":
                return pane?.ScrolledBack == true;
        }

        const string GotoTab = "goto_tab:";
        if (action.StartsWith(GotoTab, StringComparison.Ordinal) &&
            int.TryParse(action[GotoTab.Length..], out int slot))
            return slot >= 1 && slot <= _tabs.Count;

        return true;
    }

    /// <summary>How many panes the active tab holds, for the split actions.</summary>
    private int ActiveTabPaneCount()
    {
        if (_tabs.Count == 0) return 0;
        int count = 0;
        ForEachPane(_tabs[_active], _ => count++);
        return count;
    }

    /// <summary>
    /// Pushes a named table onto the focused surface. An unknown name is a no-op, which is
    /// what Ghostty does for a table the config never defined.
    /// </summary>
    private void ActivateKeyTable(string name, bool once)
    {
        TerminalPane? pane = ActivePane();
        if (pane is null) return;
        if (!_keybinds.HasTable(name))
        {
            Diag.Log("keybind", $"no key table '{name}'");
            return;
        }
        pane.ActivateKeyTable(name, once);
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
        if (label == Strings.ToggleQuickTerminal) return "toggle_quick_terminal";
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

        _keybindActions["toggle_quick_terminal"] = QuickTerminal.Toggle;

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
