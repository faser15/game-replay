using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameReplay;

static class Native
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool CreateHardLink(string file,string existing,IntPtr security);
}

static class ProcessLifetime
{
    static readonly IntPtr job = Create();
    static IntPtr Create() {
        IntPtr h=CreateJobObject(IntPtr.Zero,null);
        var info=new ExtendedLimit {BasicLimitInformation=new BasicLimit{LimitFlags=0x2000}};
        int size=Marshal.SizeOf<ExtendedLimit>(); IntPtr ptr=Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(info,ptr,false); if(h==IntPtr.Zero || !SetInformationJobObject(h,9,ptr,(uint)size)) throw new System.ComponentModel.Win32Exception(); } finally {Marshal.FreeHGlobal(ptr);} return h;
    }
    public static void Attach(Process p) { if(!AssignProcessToJobObject(job,p.Handle)) {p.Kill(true); throw new System.ComponentModel.Win32Exception();} }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit {public long PerProcessUserTimeLimit,PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass,SchedulingClass;}
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit {public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr security,string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int type,IntPtr info,uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
}
