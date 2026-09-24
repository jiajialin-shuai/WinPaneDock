# M0 隔离复现: 在普通 pwsh 里跑同样的 CreatePseudoConsole 调用序列
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ConptyRepro {
    [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr a, int n);
    [DllImport("kernel32.dll")] public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);
    [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr h);
    public static string Run(int cols, int rows) {
        if (!CreatePipe(out IntPtr inR, out IntPtr inW, IntPtr.Zero, 0)) return "CreatePipe(in) failed: " + Marshal.GetLastWin32Error();
        if (!CreatePipe(out IntPtr outR, out IntPtr outW, IntPtr.Zero, 0)) return "CreatePipe(out) failed: " + Marshal.GetLastWin32Error();
        var s = new COORD { X = (short)cols, Y = (short)rows };
        int hr = CreatePseudoConsole(s, inR, outW, 0, out IntPtr hpc);
        string result = hr >= 0 ? "OK hpc=" + hpc : "FAIL hr=0x" + hr.ToString("X8") + " (" + new System.ComponentModel.Win32Exception(hr & 0xFFFF).Message + ")";
        if (hr >= 0) ClosePseudoConsole(hpc);
        CloseHandle(inR); CloseHandle(inW); CloseHandle(outR); CloseHandle(outW);
        return result;
    }
}
"@
Write-Output "120x30 : $([ConptyRepro]::Run(120,30))"
Write-Output "80x24  : $([ConptyRepro]::Run(80,24))"
Write-Output "0x0    : $([ConptyRepro]::Run(0,0))"
