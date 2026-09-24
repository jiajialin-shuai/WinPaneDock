namespace Cmux.Terminal;

using System.ComponentModel;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 把 Windows Terminal 的 OpenConsole.exe 接到 Shell 上，
/// 不实现任何 VT 解析 / 渲染, 符合 Roadmap #12 "不自己实现 ConPTY / VT Parser"。
/// </summary>
public sealed class ConptyProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int ProcThreadAttributePseudoconsole = 0x00020016;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint HandleFlagInherit = 0x00000001;
    private const int BufferSize = 16 * 1024;

    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _environmentOverrides;

    private IntPtr _hpc;
    private IntPtr _hostProcessHandle;
    private IntPtr _referenceHandle;
    private IntPtr _signalWrite;
    private readonly object _hostLock = new();
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private IntPtr _attrList;
    private IntPtr _pipeConptyOutWrite; // conpty 写出 (conpty 侧)
    private IntPtr _pipeConptyOutRead;  // 我们读入
    private IntPtr _pipeConptyInRead;   // conpty 读入 (conpty 侧)
    private IntPtr _pipeConptyInWrite;  // 我们写出

    private Thread? _readThread;
    private Thread? _waitThread;
    private volatile bool _closing;
    private int _lastRows = 30;
    private int _lastCols = 120;

    public ConptyProcess(string commandLine, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
        _environmentOverrides = environmentOverrides;
    }

    public event Action<string>? Output;
    public event Action? Exited;

    public int ProcessId { get; private set; }

    public string CommandLine => _commandLine;

    /// <summary>启动 OpenConsole 与 Shell。</summary>
    public void Start()
    {
        var size = new Coord { X = (short)_lastCols, Y = (short)_lastRows };

        if (!CreatePipe(out _pipeConptyOutRead, out _pipeConptyOutWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) failed");
        }

        if (!CreatePipe(out _pipeConptyInRead, out _pipeConptyInWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) failed");
        }

        try
        {
            CreateOpenConsole(size);
            SpawnProcess(size);
        }
        catch
        {
            Close();
            throw;
        }

        // conpty 侧句柄在父进程里立即关闭, 否则读取端永远等不到 EOF。
        CloseHandle(ref _pipeConptyOutWrite);
        CloseHandle(ref _pipeConptyInRead);

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "cmux-conpty-read" };
        _readThread.Start();

        _waitThread = new Thread(WaitForExit) { IsBackground = true, Name = "cmux-conpty-wait" };
        _waitThread.Start();
    }

    public void WriteInput(string data)
    {
        if (string.IsNullOrEmpty(data) || _pipeConptyInWrite == IntPtr.Zero)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(data);
        if (WriteFile(_pipeConptyInWrite, bytes, bytes.Length, out _, IntPtr.Zero))
        {
            return;
        }

        // 管道已断开 (conpty 已关闭) 属正常退出路径, 不抛异常。
        var err = Marshal.GetLastWin32Error();
        if (err != 109 /* ERROR_BROKEN_PIPE */ && err != 232 /* ERROR_NO_DATA */)
        {
            throw new Win32Exception(err, "WriteFile to conpty failed");
        }
    }

    public void Resize(uint rows, uint columns)
    {
        if (_hpc == IntPtr.Zero || rows == 0 || columns == 0)
        {
            return;
        }

        // 与控件保持一致的下限 (官方 TerminalContainer 也是至少 2x2)。
        rows = Math.Max(rows, 2);
        columns = Math.Max(columns, 2);
        if (rows == _lastRows && columns == _lastCols)
        {
            return;
        }

        _lastRows = (int)rows;
        _lastCols = (int)columns;

        // winconpty 的信号包为三个 little-endian ushort: 命令、列、行。
        var packet = new byte[] { 8, 0, (byte)columns, (byte)(columns >> 8), (byte)rows, (byte)(rows >> 8) };
        lock (_hostLock)
        {
            if (_signalWrite != IntPtr.Zero && !WriteFile(_signalWrite, packet, packet.Length, out _, IntPtr.Zero))
            {
                System.Diagnostics.Trace.WriteLine($"OpenConsole resize failed: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    public void Close()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        CloseHost();

        if (_processHandle != IntPtr.Zero)
        {
            WaitForSingleObject(_processHandle, 200);
            if (GetExitCodeProcess(_processHandle, out var code) && code == 259 /* STILL_ACTIVE */)
            {
                TerminateProcess(_processHandle, 1);
            }
        }

        CloseHandle(ref _pipeConptyInWrite);
        CloseHandle(ref _pipeConptyOutRead);

        _waitThread?.Join(500);

        if (_processHandle != IntPtr.Zero)
        {
            WaitForSingleObject(_processHandle, 2000);
            CloseHandle(ref _processHandle);
        }

        CloseHandle(ref _threadHandle);

        if (_attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
    }

    public void Kill()
    {
        if (_processHandle != IntPtr.Zero) TerminateProcess(_processHandle, 1);
        Close();
    }

    public void Dispose() => Close();

    private void CreateOpenConsole(Coord size)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "OpenConsole.exe");
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("Build the spike to copy Windows Terminal's OpenConsole.exe", hostPath);
        }

        var status = OpenDevice(@"\Device\ConDrv\Server", IntPtr.Zero, 0x10000000, 0x42, 0, out var server);
        if (status < 0)
        {
            uint driverLoaded = 1;
            NtSetSystemInformation(132, ref driverLoaded, sizeof(uint));
            status = OpenDevice(@"\Device\ConDrv\Server", IntPtr.Zero, 0x10000000, 0x42, 0, out server);
        }
        if (status < 0) Marshal.ThrowExceptionForHR(unchecked((int)(0x10000000u | (uint)status)));

        IntPtr input = IntPtr.Zero, output = IntPtr.Zero, signalRead = IntPtr.Zero;
        IntPtr hostAttrs = IntPtr.Zero, handles = IntPtr.Zero;
        var attrsInitialized = false;
        try
        {
            status = OpenDevice(@"\Reference", server, 0xC0100000, 0x40, 0x20, out _referenceHandle);
            if (status < 0) Marshal.ThrowExceptionForHR(unchecked((int)(0x10000000u | (uint)status)));

            if (!DuplicateHandle(GetCurrentProcess(), _pipeConptyInRead, GetCurrentProcess(), out input, 0, true, DuplicateSameAccess) ||
                !DuplicateHandle(GetCurrentProcess(), _pipeConptyOutWrite, GetCurrentProcess(), out output, 0, true, DuplicateSameAccess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle for OpenConsole failed");
            if (!CreatePipe(out signalRead, out _signalWrite, IntPtr.Zero, 0) ||
                !SetHandleInformation(signalRead, HandleFlagInherit, HandleFlagInherit))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenConsole signal pipe failed");

            handles = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, server);
            Marshal.WriteIntPtr(handles, IntPtr.Size, input);
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, output);
            Marshal.WriteIntPtr(handles, 3 * IntPtr.Size, signalRead);

            var listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            hostAttrs = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(hostAttrs, 1, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenConsole attribute list failed");
            attrsInitialized = true;
            if (!UpdateProcThreadAttribute(hostAttrs, 0, ProcThreadAttributeHandleList, handles,
                    (IntPtr)(4 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenConsole handle list failed");

            var startup = new StartupInfoEx { StartupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = StartfUseStdHandles,
                hStdInput = input, hStdOutput = output, hStdError = output,
            }, lpAttributeList = hostAttrs };
            var command = new StringBuilder($"\"{hostPath}\" --headless --width {size.X} --height {size.Y} --signal 0x{signalRead.ToInt64():x} --server 0x{server.ToInt64():x}");
            if (!CreateProcessW(hostPath, command, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent, IntPtr.Zero, null, ref startup, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenConsole launch failed");
            _hostProcessHandle = pi.hProcess;
            CloseHandle(pi.hThread);

            // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE reads this shared ABI structure.
            _hpc = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(_hpc, 0, _signalWrite);
            Marshal.WriteIntPtr(_hpc, IntPtr.Size, _referenceHandle);
            Marshal.WriteIntPtr(_hpc, 2 * IntPtr.Size, _hostProcessHandle);
        }
        finally
        {
            CloseHandle(ref server);
            CloseHandle(ref input);
            CloseHandle(ref output);
            CloseHandle(ref signalRead);
            if (attrsInitialized) DeleteProcThreadAttributeList(hostAttrs);
            if (hostAttrs != IntPtr.Zero) Marshal.FreeHGlobal(hostAttrs);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }

    private static int OpenDevice(string name, IntPtr root, uint access, uint attributes, uint options, out IntPtr handle)
    {
        var nameMemory = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString
        {
            Length = (ushort)(name.Length * sizeof(char)),
            MaximumLength = (ushort)((name.Length + 1) * sizeof(char)), Buffer = nameMemory,
        };
        var unicodeMemory = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, unicodeMemory, false);
            var objectAttributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = root,
                ObjectName = unicodeMemory, Attributes = attributes,
            };
            return NtOpenFile(out handle, access, ref objectAttributes, out _, 7, options);
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeMemory);
            Marshal.FreeHGlobal(nameMemory);
        }
    }

    private void CloseHost()
    {
        lock (_hostLock)
        {
            CloseHandle(ref _referenceHandle);
            CloseHandle(ref _signalWrite);
            if (_hostProcessHandle != IntPtr.Zero)
            {
                if (WaitForSingleObject(_hostProcessHandle, 500) == 0x102)
                    TerminateProcess(_hostProcessHandle, 1);
                CloseHandle(ref _hostProcessHandle);
            }
            if (_hpc != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_hpc);
                _hpc = IntPtr.Zero;
            }
        }
    }

    private void SpawnProcess(Coord size)
    {
        var listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        _attrList = Marshal.AllocHGlobal(listSize);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
        }

        if (!UpdateProcThreadAttribute(
                _attrList,
                0,
                ProcThreadAttributePseudoconsole,
                _hpc,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
        }

        var siex = new StartupInfoEx { StartupInfo = new StartupInfo() };
        siex.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        siex.lpAttributeList = _attrList;

        var commandLine = new StringBuilder(_commandLine);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string)entry.Value!;
        if (!variables.TryGetValue("TERM", out var term) || string.IsNullOrEmpty(term) || term == "dumb")
            variables["TERM"] = "xterm-256color";
        variables["PATH"] = AppContext.BaseDirectory + Path.PathSeparator + variables.GetValueOrDefault("PATH", "");
        if (_environmentOverrides is not null)
            foreach (var (key, value) in _environmentOverrides) variables[key] = value;
        var environmentBlock = string.Join('\0', variables.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key}={p.Value}")) + "\0\0";
        var environment = Marshal.StringToHGlobalUni(environmentBlock);
        ProcessInformation pi;
        try
        {
            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environment,
                    _workingDirectory,
                    ref siex,
                    out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed: {_commandLine}");
        }
        finally { Marshal.FreeHGlobal(environment); }

        _processHandle = pi.hProcess;
        _threadHandle = pi.hThread;
        ProcessId = pi.dwProcessId;
    }

    private void ReadLoop()
    {
        var buffer = new byte[BufferSize];
        var decoder = Encoding.UTF8.GetDecoder();

        while (!_closing)
        {
            if (!ReadFile(_pipeConptyOutRead, buffer, buffer.Length, out var read, IntPtr.Zero) || read <= 0)
            {
                break;
            }

            var chars = new char[decoder.GetCharCount(buffer, 0, read)];
            decoder.GetChars(buffer, 0, read, chars, 0);
            RaiseOutput(new string(chars));
        }
    }

    private void WaitForExit()
    {
        WaitForSingleObject(_processHandle, 0xFFFFFFFF); // INFINITE

        // 子进程退出后 conpty 需要显式关闭, 否则读取端不会 EOF。
        if (!_closing && _hpc != IntPtr.Zero)
        {
            var exitCode = 0;
            GetExitCodeProcess(_processHandle, out exitCode);
            RaiseOutput($"\r\n[process exited with code {exitCode}]\r\n");
            CloseHost();
            Exited?.Invoke();
        }
    }

    private void RaiseOutput(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        Output?.Invoke(data);
    }

    private static void CloseHandle(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
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
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr pipeAttributes, int nSize);

    [DllImport("ntdll.dll")]
    private static extern int NtOpenFile(out IntPtr handle, uint desiredAccess,
        ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatus, uint shareAccess, uint openOptions);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int informationClass, ref uint information, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr hFile, byte[] buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr hFile, byte[] buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
