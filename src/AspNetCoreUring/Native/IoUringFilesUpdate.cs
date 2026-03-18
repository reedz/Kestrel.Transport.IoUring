using System.Runtime.InteropServices;

namespace AspNetCoreUring.Native;

/// <summary>
/// Argument for <c>IORING_REGISTER_FILES_UPDATE</c> (opcode 6).
/// Matches the kernel's <c>struct io_uring_files_update</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringFilesUpdate
{
    public uint Offset;
    public uint Resv;
    public ulong Fds;  // pointer to int[] of fds (__aligned_u64)
}
