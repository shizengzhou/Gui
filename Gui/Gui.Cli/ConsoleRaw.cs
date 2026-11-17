using System;
using System.Runtime.InteropServices;

namespace Gui.Cli;

/// <summary>
/// Console plumbing shared by the full-screen browsers: raw input, so every key arrives as it
/// is pressed (nothing echoed, Ctrl+C comes back as a key instead of killing the process before
/// the screen is restored), and virtual terminal processing on output, without which a classic
/// conhost window prints the escape sequences as text.
/// </summary>
internal static class ConsoleRaw
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    internal readonly record struct Modes(uint Input, uint Output);

    /// <summary>Switches the console to raw input and VT output; null when there is no console.</summary>
    public static Modes? Enter()
    {
        IntPtr input = GetStdHandle(StdInput);
        IntPtr output = GetStdHandle(StdOutput);
        bool haveInput = GetConsoleMode(input, out uint inputMode);
        bool haveOutput = GetConsoleMode(output, out uint outputMode);
        if (!haveInput && !haveOutput) return null;

        if (haveInput)
            SetConsoleMode(input, inputMode & ~(EnableLineInput | EnableEchoInput));
        if (haveOutput)
            SetConsoleMode(output, outputMode | EnableProcessedOutput | EnableVirtualTerminalProcessing);

        try
        {
            Console.TreatControlCAsInput = true;
        }
        catch (Exception)
        {
            // Without a console the setting is meaningless; the keys still arrive.
        }

        return new Modes(haveInput ? inputMode : 0, haveOutput ? outputMode : 0);
    }

    public static void Restore(Modes modes)
    {
        try
        {
            Console.TreatControlCAsInput = false;
        }
        catch (Exception)
        {
            // Nothing to restore.
        }

        SetConsoleMode(GetStdHandle(StdInput), modes.Input);
        SetConsoleMode(GetStdHandle(StdOutput), modes.Output);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
