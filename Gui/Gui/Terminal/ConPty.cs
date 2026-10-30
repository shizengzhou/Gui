using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gui.Terminal;

/// <summary>
/// Hosts a child shell inside a Windows pseudoconsole (ConPTY, Windows 10 1809+).
/// </summary>
internal sealed class ConPty : IDisposable
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int STARTF_USESTDHANDLES = 0x00000100;

    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _inputWrite;
    private readonly IntPtr _outputRead;
    private readonly IntPtr _process;
    private readonly Thread _reader;
    private volatile bool _disposed;

    /// <summary>Raised on the reader thread with raw PTY output.</summary>
    public event Action<byte[], int>? OutputReceived;

    public int ProcessId { get; }

    private ConPty(IntPtr pseudoConsole, IntPtr inputWrite, IntPtr outputRead, IntPtr process, int pid)
    {
        _pseudoConsole = pseudoConsole;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _process = process;
        ProcessId = pid;

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

    public static ConPty Start(string commandLine, short cols, short rows, string? workingDirectory = null)
    {
        // The console host duplicates these handles into its own process, and
        // CreatePseudoConsole expects inheritable pipes (see the ConPTY sample).
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out IntPtr inRead, out IntPtr inWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed");
        if (!CreatePipe(out IntPtr outRead, out IntPtr outWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed");

        var size = new COORD { X = cols, Y = rows };
        int hr = CreatePseudoConsole(size, inRead, outWrite, 0, out IntPtr hpc);
        if (hr != 0)
            throw new Win32Exception(hr, "CreatePseudoConsole failed");

        // The console owns its ends of the pipes; the host keeps the other ends.
        CloseHandle(inRead);
        CloseHandle(outWrite);

        IntPtr attrList = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
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

            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory,
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
        }

        CloseHandle(pi.hThread);
        return new ConPty(hpc, inWrite, outRead, pi.hProcess, pi.dwProcessId);
    }

    private unsafe void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            fixed (byte* pinned = buffer)
            {
                while (!_disposed)
                {
                    int read = ReadFile(_outputRead, pinned, buffer.Length, out int bytesRead, IntPtr.Zero);
                    if (read == 0 || bytesRead <= 0)
                        break;
                    OutputReceived?.Invoke(buffer, bytesRead);
                }
            }
        }
        catch (Exception)
        {
            // Pipe closed: treat as EOF.
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
        ResizePseudoConsole(_pseudoConsole, new COORD { X = cols, Y = rows });
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

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
    private static extern unsafe int ReadFile(IntPtr file, byte* buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(IntPtr file, byte* buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
