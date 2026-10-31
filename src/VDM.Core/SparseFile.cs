using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VDM.Core;

/// <summary>
/// Marks a file sparse so that writing far into a pre-sized file does not make NTFS zero-fill
/// everything before the write (which stalls a connection writing at 7/8 of a 4 GB file).
/// </summary>
static class SparseFile
{
    const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        ref byte lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    public static void Set(SafeFileHandle handle, bool sparse)
    {
        if (!OperatingSystem.IsWindows()) return;
        byte flag = sparse ? (byte)1 : (byte)0;
        // Best effort: FAT32 and exFAT drives do not support sparse files, and that is fine.
        DeviceIoControl(handle, FSCTL_SET_SPARSE, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }
}
