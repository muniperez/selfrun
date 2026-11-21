using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

internal static class BootstrapProtectedInput
{
    private const int LinuxNonBlock = 0x800;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxX64NoFollow = 0x20000;
    private const int LinuxArm64NoFollow = 0x8000;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 1;
    private const uint StatxMode = 2;
    private const uint StatxUid = 8;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Stat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx stat);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    public static FileStream OpenRead(string path)
    {
        if (!OperatingSystem.IsLinux()) throw BootstrapException.Conflict();
        // O_NOFOLLOW differs between x86-64 and arm64; the other flags are asm-generic.
        var fd = Open(path, GetReadOpenFlags(RuntimeInformation.ProcessArchitecture));
        if (fd < 0) throw BootstrapException.Conflict();
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            const uint fields = StatxType | StatxMode | StatxUid;
            if (Stat(fd, "", AtEmptyPath, fields, out var stat) != 0 ||
                (stat.Mask & fields) != fields || (stat.Mode & 0xF000) != 0x8000 ||
                stat.UserId != EffectiveUserId() ||
                (stat.Mode & 0x0FFF & ~0x180) != 0 || (stat.Mode & 0x100) == 0)
                throw BootstrapException.Conflict();
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static int GetReadOpenFlags(Architecture architecture)
    {
        var noFollow = architecture switch
        {
            Architecture.X64 => LinuxX64NoFollow,
            Architecture.Arm64 => LinuxArm64NoFollow,
            _ => throw new BootstrapException("bootstrap_unsupported_architecture", 400,
                $"Protected bootstrap input files are not supported on Linux architecture '{architecture}'. Use --stdin instead.")
        };
        // O_NONBLOCK avoids hanging on FIFOs before their file type can be checked.
        return noFollow | LinuxNonBlock | LinuxCloseOnExec;
    }
}
