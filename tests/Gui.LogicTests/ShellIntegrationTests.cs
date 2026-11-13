using System;
using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The OSC 133 marks the injected PowerShell wrapper writes: a command is running between
/// C and D, and D closes it with the shell's exit code.
/// </summary>
public class ShellIntegrationTests
{
    private static void Feed(ShellMarkParser parser, string text) =>
        parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void InjectionEmitsTheMarksAndHonorsTheMode()
    {
        const string prefix = "pwsh.exe -NoExit -EncodedCommand ";

        Assert.Equal("pwsh.exe", ShellIntegration.Inject("pwsh.exe", "none", ShellIntegrationFeatures.Default, "pwsh.exe"));
        Assert.Equal("pwsh.exe", ShellIntegration.Inject("pwsh.exe", "bash", ShellIntegrationFeatures.Default, "pwsh.exe"));

        string injected = ShellIntegration.Inject("pwsh.exe", "detect", ShellIntegrationFeatures.Default, "pwsh.exe");
        Assert.StartsWith(prefix, injected);
        string script = Encoding.Unicode.GetString(
            Convert.FromBase64String(injected[prefix.Length..]));

        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C", script);
        Assert.Contains("]133;D;$status", script);
        Assert.Contains("]7;file:///", script);
        Assert.Contains("$global:__guiCursor = $true", script);
        Assert.Contains("$global:__guiTitle = $true", script);

        string disabled = ShellIntegration.Inject("pwsh.exe", "powershell",
            new ShellIntegrationFeatures(false, false, false, false, false, false, false), "pwsh.exe");
        string trimmed = Encoding.Unicode.GetString(Convert.FromBase64String(disabled[prefix.Length..]));
        Assert.Contains("$global:__guiCursor = $false", trimmed);
        Assert.Contains("$global:__guiTitle = $false", trimmed);
    }

    [Fact]
    public void BashGetsAnRcFileOnlyWhenItCanUseOne()
    {
        // A pane does not look interactive to bash, and --rcfile is only read by an
        // interactive shell, so the wrapper adds -i when the command has no arguments.
        string injected = ShellIntegration.Inject("bash.exe", "detect", ShellIntegrationFeatures.Default, "bash.exe");
        Assert.StartsWith("bash.exe --rcfile \"", injected);
        Assert.EndsWith("-i", injected);

        Assert.StartsWith("bash.exe --rcfile \"",
            ShellIntegration.Inject("bash.exe -i", "detect", ShellIntegrationFeatures.Default, "bash.exe"));

        // Git's Windows Terminal profile runs "bash.exe -i -l"; a login shell ignores
        // --rcfile, so the login flag is dropped and the rc file runs /etc/profile instead.
        foreach (string command in new[] { "bash.exe -i -l", "bash.exe -li", "bash.exe --login -i" })
        {
            string rewritten = ShellIntegration.Inject(command, "detect", ShellIntegrationFeatures.Default, "bash.exe");
            Assert.StartsWith("bash.exe --rcfile \"", rewritten);
            Assert.EndsWith("-i", rewritten);
            Assert.DoesNotContain(" -l", rewritten);
            Assert.DoesNotContain("--login", rewritten);
        }

        // A command line that picks its own startup files, or runs a command, is left alone.
        foreach (string command in new[]
                 {
                     "bash.exe --rcfile C:/elsewhere.bash",
                     "bash.exe -c \"echo hi\"",
                     "bash.exe --norc",
                 })
        {
            Assert.Equal(command, ShellIntegration.Inject(command, "detect", ShellIntegrationFeatures.Default, "bash.exe"));
        }

        Assert.Equal("bash.exe", ShellIntegration.Inject("bash.exe", "none", ShellIntegrationFeatures.Default, "bash.exe"));
        // A mode naming another shell must not add the bash startup file.
        Assert.Equal("bash.exe", ShellIntegration.Inject("bash.exe", "powershell", ShellIntegrationFeatures.Default, "bash.exe"));
        Assert.StartsWith("bash.exe --rcfile \"",
            ShellIntegration.Inject("bash.exe", "bash", ShellIntegrationFeatures.Default, "bash.exe"));
    }

    [Fact]
    public void BashInjectionHandlesAQuotedExecutable()
    {
        const string exe = "C:\\Program Files\\Git\\bin\\bash.exe";
        string command = "\"" + exe + "\"";
        string injected = ShellIntegration.Inject(command, "detect", ShellIntegrationFeatures.Default, exe);

        Assert.StartsWith("\"" + exe + "\" --rcfile \"", injected);
        Assert.EndsWith("-i", injected);
    }

    [Fact]
    public void BashScriptCarriesTheMarksAndTheFeatures()
    {
        string script = ShellIntegration.BashScript(ShellIntegrationFeatures.Default);

        Assert.Contains("]133;A", script);
        Assert.Contains("]133;C", script);
        Assert.Contains("]133;D;", script);
        Assert.Contains("]7;file:///", script);
        Assert.Contains("PROMPT_COMMAND", script);
        Assert.Contains("PS0=", script);
        Assert.Contains("__gui_cursor=1", script);
        Assert.Contains("__gui_cursor_steady=0", script);
        Assert.Contains("__gui_title=1", script);
        Assert.Contains("[5 q", script);
        // Every placeholder is resolved before the file is written.
        Assert.DoesNotContain("@CURSOR@", script);
        Assert.DoesNotContain("@TITLE@", script);

        string off = ShellIntegration.BashScript(
            new ShellIntegrationFeatures(false, false, false, false, false, false, false));
        Assert.Contains("__gui_cursor=0", off);
        Assert.Contains("__gui_title=0", off);
        // The sequences stay in the script; the flags are what switch them off.
        Assert.Contains("[5 q", off);
    }

    [Fact]
    public void MarksTrackACommandLifetime()
    {
        var parser = new ShellMarkParser();
        (int Exit, TimeSpan Duration)? finished = null;
        parser.CommandFinished = (exit, duration) => finished = (exit, duration);

        Feed(parser, "\u001b]133;A\u0007prompt\u001b]133;B\u0007");
        Assert.False(parser.CommandRunning);
        Assert.True(parser.AtPrompt);

        Feed(parser, "\u001b]133;C\u0007output");
        Assert.True(parser.CommandRunning);
        Assert.False(parser.AtPrompt);

        Feed(parser, "\u001b]133;D;7\u0007");
        Assert.False(parser.CommandRunning);
        // D closes the command, but only the next A mark means the prompt is on screen.
        Assert.False(parser.AtPrompt);
        Assert.NotNull(finished);
        Assert.Equal(7, finished!.Value.Exit);
        Assert.True(finished.Value.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void MarksSplitAcrossReadsAreStillParsed()
    {
        var parser = new ShellMarkParser();
        int exit = -1;
        parser.CommandFinished = (code, _) => exit = code;

        Feed(parser, "\u001b]13");
        Feed(parser, "3;C\u0007");
        Assert.True(parser.CommandRunning);

        Feed(parser, "\u001b]133;D");
        Feed(parser, ";3\u0007");
        Assert.False(parser.CommandRunning);
        Assert.Equal(3, exit);
    }

    [Fact]
    public void OtherOscSequencesAreIgnored()
    {
        var parser = new ShellMarkParser();
        bool finished = false;
        parser.CommandFinished = (_, _) => finished = true;

        Feed(parser, "\u001b]7;file:///C:/temp\u0007");
        Feed(parser, "\u001b]0;title\u0007");

        Assert.False(parser.CommandRunning);
        Assert.False(finished);
    }

    [Fact]
    public void ANewPromptClosesAClostCommand()
    {
        var parser = new ShellMarkParser();
        Feed(parser, "\u001b]133;C\u0007");
        Assert.True(parser.CommandRunning);

        Feed(parser, "\u001b]133;A\u0007");
        Assert.False(parser.CommandRunning);
    }

    [Fact]
    public void ProgressReportsSetTheStateAndThePercentage()
    {
        var parser = new ShellMarkParser();
        (int State, int Value)? last = null;
        parser.ProgressChanged = (state, value) => last = (state, value);

        Feed(parser, "\u001b]9;4;1;42\u0007");
        Assert.Equal(1, parser.ProgressState);
        Assert.Equal(42, parser.ProgressValue);
        Assert.Equal((1, 42), last);

        // Indeterminate has no percentage; ST terminates the sequence like BEL.
        Feed(parser, "\u001b]9;4;3\u001b\\");
        Assert.Equal(3, parser.ProgressState);
        Assert.Equal(0, parser.ProgressValue);

        Feed(parser, "\u001b]9;4;0\u0007");
        Assert.Equal(0, parser.ProgressState);
        Assert.Equal((0, 0), last);
    }

    [Fact]
    public void ProgressReportsSurviveSplitReadsAndClampTheValue()
    {
        var parser = new ShellMarkParser();
        Feed(parser, "\u001b]9;4;2");
        Feed(parser, ";140\u0007");

        Assert.Equal(2, parser.ProgressState);
        Assert.Equal(100, parser.ProgressValue);
    }

    [Fact]
    public void ProgressIgnoresUnknownStatesAndClearsWhenTheCommandEnds()
    {
        var parser = new ShellMarkParser();
        Feed(parser, "\u001b]9;4;9;50\u0007");
        Assert.Equal(0, parser.ProgressState);

        Feed(parser, "\u001b]133;C\u0007");
        Feed(parser, "\u001b]9;4;1;50\u0007");
        Assert.Equal(1, parser.ProgressState);

        Feed(parser, "\u001b]133;D;0\u0007");
        Assert.Equal(0, parser.ProgressState);
        Assert.Equal(0, parser.ProgressValue);
    }
}
