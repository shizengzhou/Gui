# Gui shell integration for bash.
#
# Passed to bash as --rcfile, so bash skips ~/.bashrc: the user's own startup runs
# here first, then the prompt hooks below. The marks follow Ghostty's convention:
#   OSC 133 A prompt start   B prompt end   C command start   D command end;exit
# and OSC 7 reports the working directory so the pane header follows cd. A D without a
# matching C is ignored by the reader, which covers the first prompt and bash older
# than 4.4, where PS0 (the C mark) is never expanded.
# --rcfile is only read for an interactive shell, and bash skips it for a login shell,
# so the login startup runs here. MSYS's /etc/profile already sources the user's
# profile files; without it the user's bashrc is the right fallback.
if [ -f /etc/profile ]; then
    . /etc/profile
elif [ -f "$HOME/.bashrc" ]; then
    . "$HOME/.bashrc"
fi

__gui_esc=$'\e'
__gui_bel=$'\a'
__gui_cursor=@CURSOR@
__gui_cursor_steady=@CURSOR_STEADY@
__gui_title=@TITLE@

# Runs before every prompt: close the command that just finished, report the new
# directory, then mark the prompt start. Builtins only, so a prompt costs no fork.
__gui_precmd() {
    local status=$?
    local dir="$PWD"
    printf '%s]133;D;%s%s' "$__gui_esc" "$status" "$__gui_bel"

    # MSYS bash reports /c/Users/me; the header wants C:\Users\me, so rebuild the drive.
    case $dir in
        /[A-Za-z]|/[A-Za-z]/*)
            local drive=${dir:1:1}
            dir="${drive^^}:${dir:2}"
            ;;
    esac
    dir=${dir//%/%25}

    printf '%s]7;file:///%s%s' "$__gui_esc" "$dir" "$__gui_bel"
    if [ "$__gui_title" = 1 ]; then
        printf '%s]2;%s%s' "$__gui_esc" "$dir" "$__gui_bel"
    fi

    printf '%s]133;A%s' "$__gui_esc" "$__gui_bel"
    if [ "$__gui_cursor" = 1 ]; then
        if [ "$__gui_cursor_steady" = 1 ]; then
            printf '%s[6 q' "$__gui_esc"
        else
            printf '%s[5 q' "$__gui_esc"
        fi
    fi
}

# The prompt end mark travels with PS1, so a prompt that spans lines still ends it in
# the right place. It is zero-width and never moves the cursor.
PS1="${PS1-}\[\e]133;B\a\]"

# Command start comes from PS0: expanded once per command line, before anything runs,
# and ignored by bash older than 4.4.
__gui_ps0='\[\e]133;C\a\]'
if [ "$__gui_cursor" = 1 ]; then
    if [ "$__gui_cursor_steady" = 1 ]; then
        __gui_ps0='\[\e[6q\e]133;C\a\]'
    else
        __gui_ps0='\[\e[5q\e]133;C\a\]'
    fi
fi
PS0="${__gui_ps0}${PS0-}"

# PROMPT_COMMAND may be a string or (bash 5.1+) an array; ours goes first so it can
# capture $? before the user's own entries run.
case "$(declare -p PROMPT_COMMAND 2>/dev/null)" in
    'declare -a'*) PROMPT_COMMAND=(__gui_precmd "${PROMPT_COMMAND[@]}") ;;
    *)
        if [ -n "$PROMPT_COMMAND" ]; then
            PROMPT_COMMAND="__gui_precmd;$PROMPT_COMMAND"
        else
            PROMPT_COMMAND=__gui_precmd
        fi
        ;;
esac
