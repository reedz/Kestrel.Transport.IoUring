using System.Runtime.InteropServices;

namespace Kestrel.Transport.IoUring.Native;

internal static partial class IoUringNative
{
    private const long SYS_IO_URING_SETUP = 425;
    private const long SYS_IO_URING_ENTER = 426;
    private const long SYS_IO_URING_REGISTER = 427;

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number, long a1, nint a2);

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number, long a1, long a2, long a3, long a4, nint a5, long a6);

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number, long a1, long a2, nint a3, long a4);

    internal static unsafe int IoUringSetup(uint entries, IoUringParams* p)
    {
        return (int)syscall(SYS_IO_URING_SETUP, (long)entries, (nint)p);
    }

    internal static int IoUringEnter(int fd, uint toSubmit, uint minComplete, uint flags)
    {
        return (int)syscall(SYS_IO_URING_ENTER, fd, toSubmit, minComplete, flags, nint.Zero, 0L);
    }

    internal static unsafe int IoUringRegister(int fd, uint opcode, nint arg, uint nrArgs)
    {
        return (int)syscall(SYS_IO_URING_REGISTER, fd, opcode, arg, nrArgs);
    }
}
