using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Gui.Term;

/// <summary>
/// Hosts a child shell inside a Windows pseudoconsole (ConPTY, Windows 10 1809+).
/// </summary>
internal sealed class ConPty : IDisposable
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    /// <summary>
    /// CreatePseudoConsole flag: the console asks the terminal where its cursor is (DSR) and
    /// starts there. A restored pane relies on it, so the new shell draws under the history
    /// instead of at the top of the screen.
    /// </summary>
    private const uint PSEUDOCONSOLE_INHERIT_CURSOR = 0x0001;

    private delegate int CreatePseudoConsoleFn(COORD size, IntPtr input, IntPtr output, uint flags,
        out IntPtr pseudoConsole);

    private delegate int ResizePseudoConsoleFn(IntPtr pseudoConsole, COORD size);
    private delegate void ClosePseudoConsoleFn(IntPtr pseudoConsole);

    // The ConPTY entry points: a conpty.dll shipped next to the app wins, the system's own
    // kernel32 implementation is the fallback. The bundled one keeps PTY behaviour (cursor
    // inheritance, repaint, VT input details) tied to what we ship instead of the installed
    // Windows build; it needs its OpenConsole.exe beside it.
    private static readonly CreatePseudoConsoleFn? BundledCreate;
    private static readonly ResizePseudoConsoleFn? BundledResize;
    private static readonly ClosePseudoConsoleFn? BundledClose;

    static ConPty()
    {
        try
        {
            string directory = AppContext.BaseDirectory;
            string library = Path.Combine(directory, "conpty.dll");
            if (!File.Exists(library) || !File.Exists(Path.Combine(directory, "OpenConsole.exe"))) return;

            IntPtr module = NativeLibrary.Load(library);
            if (!NativeLibrary.TryGetExport(module, "CreatePseudoConsole", out IntPtr create) ||
                !NativeLibrary.TryGetExport(module, "ResizePseudoConsole", out IntPtr resize) ||
                !NativeLibrary.TryGetExport(module, "ClosePseudoConsole", out IntPtr close))
            {
                Diag.Warn("pty.conpty", "conpty.dll is missing an export; using the system ConPTY");
                return;
            }

            BundledCreate = Marshal.GetDelegateForFunctionPointer<CreatePseudoConsoleFn>(create);
            BundledResize = Marshal.GetDelegateForFunctionPointer<ResizePseudoConsoleFn>(resize);
            BundledClose = Marshal.GetDelegateForFunctionPointer<ClosePseudoConsoleFn>(close);
            Diag.Log("pty.conpty", $"using bundled conpty.dll from '{directory}'");
        }
        catch (Exception error)
        {
            Diag.Warn("pty.conpty", $"bundled ConPTY unusable ({error.Message}); using the system one");
        }
    }

    private static int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags,
        out IntPtr pseudoConsole)
    {
        if (BundledCreate is not null) return BundledCreate(size, input, output, flags, out pseudoConsole);
        return SystemCreatePseudoConsole(size, input, output, flags, out pseudoConsole);
    }

    private static int ResizePseudoConsole(IntPtr pseudoConsole, COORD size) =>
        BundledResize is not null ? BundledResize(pseudoConsole, size) : SystemResizePseudoConsole(pseudoConsole, size);

    private static void ClosePseudoConsole(IntPtr pseudoConsole)
    {
        if (BundledClose is not null) BundledClose(pseudoConsole);
        else SystemClosePseudoConsole(pseudoConsole);
    }

    /// <summary>What GetExitCodeProcess returns while the process is still running.</summary>
    private const uint StillActive = 259;

    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _inputWrite;
    private readonly IntPtr _outputRead;
    private readonly IntPtr _process;
    private readonly Thread _reader;
    private readonly long _startedTicks;
    private volatile bool _disposed;

    /// <summary>Raised on the reader thread with raw PTY output.</summary>
    public event Action<byte[], int>? OutputReceived;

    /// <summary>Raised on the reader thread once the shell's output pipe closes.</summary>
    public event Action? Exited;

    /// <summary>
    /// Raised on a pool thread as soon as the shell process is gone, with
    /// <see cref="ExitCode"/> and <see cref="Runtime"/> filled in. ConPTY keeps the output
    /// pipe open behind OpenConsole, so <see cref="Exited"/> is not enough to notice a shell
    /// that exited on its own.
    /// </summary>
    public event Action? ProcessExited;

    /// <summary>
    /// Exit code of the shell, read when its output closes. -1 when Windows still reports
    /// STILL_ACTIVE or the query failed, which is the only answer a killed process gives.
    /// </summary>
    public int ExitCode { get; private set; } = -1;

    /// <summary>
    /// How long the shell ran, from the moment the pseudoconsole was created to the moment its
    /// output closed. "abnormal-command-exit-runtime" is measured against it.
    /// </summary>
    public TimeSpan Runtime { get; private set; }

    public int ProcessId { get; }

    public IntPtr ProcessHandle => _process;

    private ConPty(IntPtr pseudoConsole, IntPtr inputWrite, IntPtr outputRead, IntPtr process, int pid)
    {
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = process;
        ProcessId = pid;
        // The shell starts a few instructions before this constructor runs; the difference is
        // far below the resolution "abnormal-command-exit-runtime" cares about.
        _startedTicks = Environment.TickCount64;
        WatchProcess();

        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ConPTY reader",
        };
    }

    /// <summary>
    /// Starts pumping PTY output. Call this after subscribing to <see cref="OutputReceived"/>,
    /// otherwise the shell banner can be read (and dropped) before anyone listens.
    /// </summary>
    public void BeginRead() => _reader.Start();

    public static ConPty Start(string commandLine, short cols, short rows, string? workingDirectory = null,
        IReadOnlyList<(string Name, string Value)>? environment = null, string term = "",
        bool inheritCursor = false)
    {
        // The console host duplicates these handles into its own process, and
        // CreatePseudoConsole expects inheritable pipes (see the ConPTY sample).
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out IntPtr inRead, out IntPtr inWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed");
        if (!CreatePipe(out IntPtr outRead, out IntPtr outWrite, ref sa, 0))
        {
            CloseHandle(inRead);
            CloseHandle(inWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed");
        }

        var size = new COORD { X = cols, Y = rows };
        uint flags = inheritCursor ? PSEUDOCONSOLE_INHERIT_CURSOR : 0;
        int hr = CreatePseudoConsole(size, inRead, outWrite, flags, out IntPtr hpc);
        if (hr != 0)
        {
            CloseHandle(inRead);
            CloseHandle(inWrite);
            CloseHandle(outRead);
            CloseHandle(outWrite);
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        // The console owns its ends of the pipes; the host keeps the other ends.
        CloseHandle(inRead);
        CloseHandle(outWrite);

        IntPtr attrList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            environmentBlock = BuildEnvironment(environment, term);
            nuint attrSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            attrList = Marshal.AllocHGlobal((int)attrSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hpc, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            // An invalid working directory makes CreateProcess fail outright; fall back to
            // the parent's directory instead.
            string? cwd = workingDirectory;
            if (!string.IsNullOrEmpty(cwd) && !System.IO.Directory.Exists(cwd))
                cwd = null;

            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, environmentBlock, cwd,
                    ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        }
        catch
        {
            CloseHandle(inWrite);
            CloseHandle(outRead);
            CloseHandle(hpc);
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
        }

        CloseHandle(pi.hThread);
        return new ConPty(hpc, inWrite, outRead, pi.hProcess, pi.dwProcessId);
    }

    /// <summary>
    /// Builds a sorted UTF-16 "NAME=VALUE\0...\0" block, or IntPtr.Zero to inherit the
    /// parent environment. CreateProcess requires the double NUL terminator.
    /// </summary>
    private static IntPtr BuildEnvironment(IReadOnlyList<(string Name, string Value)>? extra, string term)
    {
        bool hasTerm = !string.IsNullOrEmpty(term);
        if ((extra is null || extra.Count == 0) && !hasTerm && CliDirectory().Length == 0) return IntPtr.Zero;

        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
                variables[name] = entry.Value as string ?? string.Empty;
        }

        if (hasTerm) variables["TERM"] = term;
        if (extra is not null)
        {
            foreach ((string name, string value) in extra)
                variables[name] = value;
        }

        AddCliToPath(variables);

        var block = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in variables)
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');

        return Marshal.StringToHGlobalUni(block.ToString());
    }

    /// <summary>
    /// The folder holding the CLI for this build: bin\gui.exe, in an installed payload and in a
    /// build output alike. "gui" only ever lives in bin — in the app's own folder the name
    /// would be Gui.exe, which on Windows is the same file.
    /// </summary>
    private static string CliDirectory()
    {
        string bin = System.IO.Path.Combine(AppContext.BaseDirectory, "bin");
        return System.IO.File.Exists(System.IO.Path.Combine(bin, "gui.exe")) ? bin : string.Empty;
    }

    /// <summary>
    /// Puts the CLI on the shell's PATH instead of relying on the installer's PATH entry. A
    /// process only sees the environment it inherited when it started, so a Gui that was
    /// already running when the entry was added would otherwise hand its shells a PATH
    /// without "gui" until the next restart.
    /// </summary>
    private static void AddCliToPath(SortedDictionary<string, string> variables)
    {
        string cliDir = CliDirectory();
        if (cliDir.Length == 0) return;

        variables.TryGetValue("PATH", out string? existing);
        existing ??= string.Empty;
        foreach (string entry in existing.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(entry.Trim(), cliDir, StringComparison.OrdinalIgnoreCase)) return;
        }

        variables["PATH"] = cliDir + ";" + existing;
        Diag.Debug("pty.path", $"prepended {cliDir}");
    }

    /// <summary>
    /// Waits for the shell process so its exit code and runtime are known even when ConPTY
    /// keeps the output pipe open behind OpenConsole. The wait is short so Dispose is noticed
    /// without needing to close a handle someone else is blocked on.
    /// </summary>
    private void WatchProcess()
    {
        var thread = new Thread(() =>
        {
            const uint WaitTimeout = 0x102;
            while (!_disposed)
            {
                if (WaitForSingleObject(_process, 250) != WaitTimeout) break;
            }

            if (_disposed) return;

            if (GetExitCodeProcess(_process, out uint code) && code != StillActive)
                ExitCode = (int)code;
            Runtime = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedTicks);

            // The pane closes as soon as this is reported, so give the reader a moment to take
            // whatever the pty flushed; otherwise a command's last lines leave with the pane.
            Thread.Sleep(150);
            if (_disposed) return;

            ProcessExited?.Invoke();
        })
        {
            IsBackground = true,
            Name = "gui-pty-exit",
        };
        thread.Start();
    }

    private unsafe void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            fixed (byte* pinned = buffer)
            {
                while (!_disposed)
                {
                    if (!ReadFile(_outputRead, pinned, buffer.Length, out int bytesRead, IntPtr.Zero) || bytesRead <= 0)
                        break;
                    OutputReceived?.Invoke(buffer, bytesRead);
                }
            }
        }
        catch
        {
            // Pipe closed: treat as EOF.
        }

        if (!_disposed)
        {
            // The output pipe closes when the child goes away, but "gone" is not the same as
            // "reaped": wait for the process briefly so the code is the real one rather than
            // STILL_ACTIVE, and stamp the runtime before anyone reacts to the exit.
            WaitForSingleObject(_process, 2000);
            if (GetExitCodeProcess(_process, out uint code) && code != StillActive)
                ExitCode = (int)code;
            Runtime = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedTicks);
            Exited?.Invoke();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
            return;
        unsafe
        {
            fixed (byte* p = data)
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    if (!WriteFile(_inputWrite, p + offset, data.Length - offset, out int written, IntPtr.Zero) || written <= 0)
                        return;
                    offset += written;
                }
            }
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed)
            return;
        int hr = ResizePseudoConsole(_pseudoConsole, new COORD { X = cols, Y = rows });
        Diag.Debug("pty.resize", $"cols={cols} rows={rows} hr={hr}");
    }

    public string ExecutablePath()
    {
        if (_process == IntPtr.Zero) return string.Empty;
        var buffer = new StringBuilder(1024);
        uint size = (uint)buffer.Capacity;
        if (QueryFullProcessImageName(_process, 0, buffer, ref size))
            return buffer.ToString(0, (int)size);
        return string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { ClosePseudoConsole(_pseudoConsole); } catch { }
        try { TerminateProcess(_process, 0); } catch { }
        if (_process != IntPtr.Zero) CloseHandle(_process);
        if (_inputWrite != IntPtr.Zero) CloseHandle(_inputWrite);
        if (_outputRead != IntPtr.Zero) CloseHandle(_outputRead);
    }

    // ---------------- Win32 ----------------


    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SECURITY_ATTRIBUTES securityAttributes, int size);

    [DllImport("kernel32.dll", EntryPoint = "CreatePseudoConsole", SetLastError = true)]
    private static extern int SystemCreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", EntryPoint = "ResizePseudoConsole", SetLastError = true)]
    private static extern int SystemResizePseudoConsole(IntPtr pseudoConsole, COORD size);

    [DllImport("kernel32.dll", EntryPoint = "ClosePseudoConsole", SetLastError = true)]
    private static extern void SystemClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(IntPtr file, byte* buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(IntPtr file, byte* buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
}
