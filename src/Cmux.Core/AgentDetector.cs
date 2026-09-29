using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cmux.Core;

public enum AgentType { Unknown, Codex, Claude, Grok, Gemini, OpenCode }
public enum AgentDetectionSource { None, ProcessTree, LaunchCommand, TerminalTitle, Explicit }
public sealed record AgentDetection(AgentType Type, AgentDetectionSource Source, int ProcessId);
public sealed record AgentProcess(int ProcessId, int ParentProcessId, string Executable);

public sealed class AgentProcessSnapshot
{
    private readonly Dictionary<int, List<AgentProcess>> _childrenByParent;
    private readonly HashSet<int> _processIds;

    public AgentProcessSnapshot(IReadOnlyList<AgentProcess> processes)
    {
        Processes = processes;
        _processIds = processes.Select(process => process.ProcessId).ToHashSet();
        _childrenByParent = processes
            .GroupBy(process => process.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    public IReadOnlyList<AgentProcess> Processes { get; }
    public bool Contains(int processId) => _processIds.Contains(processId);
    public IReadOnlyList<AgentProcess> ChildrenOf(int parentProcessId) =>
        _childrenByParent.TryGetValue(parentProcessId, out var children) ? children : [];
}

public static class AgentDetector
{
    public static IReadOnlyList<AgentProcess> Scan()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return [];
        try
        {
            var processes = new List<AgentProcess>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            if (!Process32FirstW(snapshot, ref entry)) return processes;
            do
            {
                processes.Add(new AgentProcess((int)entry.ProcessId, (int)entry.ParentProcessId, entry.Executable));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
            } while (Process32NextW(snapshot, ref entry));
            return processes;
        }
        finally { CloseHandle(snapshot); }
    }

    public static AgentDetection Detect(int rootPid, IReadOnlyList<AgentProcess> processes,
        string launchCommand = "", string terminalTitle = "", AgentType explicitAgent = AgentType.Unknown) =>
        Detect(rootPid, new AgentProcessSnapshot(processes), launchCommand, terminalTitle, explicitAgent);

    public static AgentDetection Detect(int rootPid, AgentProcessSnapshot snapshot,
        string launchCommand = "", string terminalTitle = "", AgentType explicitAgent = AgentType.Unknown)
    {
        if (explicitAgent != AgentType.Unknown)
            return new AgentDetection(explicitAgent, AgentDetectionSource.Explicit, rootPid);
        if (rootPid <= 0 || !snapshot.Contains(rootPid))
            return new AgentDetection(AgentType.Unknown, AgentDetectionSource.None, 0);

        var descendants = new HashSet<int> { rootPid };
        var pending = new Queue<int>();
        pending.Enqueue(rootPid);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            foreach (var child in snapshot.ChildrenOf(parent))
                if (descendants.Add(child.ProcessId)) pending.Enqueue(child.ProcessId);
        }

        foreach (var process in snapshot.Processes)
        {
            if (!descendants.Contains(process.ProcessId)) continue;
            var agent = NameToAgent(Path.GetFileNameWithoutExtension(process.Executable));
            if (agent != AgentType.Unknown)
                return new AgentDetection(agent, AgentDetectionSource.ProcessTree, process.ProcessId);
        }

        foreach (var agent in new[] { AgentType.Codex, AgentType.Claude, AgentType.Grok, AgentType.Gemini, AgentType.OpenCode })
        {
            if (Regex.IsMatch(launchCommand, $@"(?i)(?:^|[\s\""'\\]){agent}(?:\.exe|\.ps1)?(?:$|[\s\""'])"))
                return new AgentDetection(agent, AgentDetectionSource.LaunchCommand, rootPid);
            if (terminalTitle.Equals(agent.ToString(), StringComparison.OrdinalIgnoreCase))
                return new AgentDetection(agent, AgentDetectionSource.TerminalTitle, rootPid);
        }
        return new AgentDetection(AgentType.Unknown, AgentDetectionSource.None, 0);
    }

    private static AgentType NameToAgent(string name) => name.ToLowerInvariant() switch
    {
        "codex" => AgentType.Codex,
        "claude" => AgentType.Claude,
        "grok" => AgentType.Grok,
        "gemini" => AgentType.Gemini,
        "opencode" => AgentType.OpenCode,
        _ => AgentType.Unknown,
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
