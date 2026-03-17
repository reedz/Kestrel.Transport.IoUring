using System.Runtime.InteropServices;

namespace AspNetCoreUring.Native;

internal static partial class Libc
{
    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int munmap(nint addr, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int setsockopt(int sockfd, int level, int optname, nint optval, int optlen);

    /// <summary>Creates an event file descriptor (Linux eventfd2 syscall 290).</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int eventfd(uint initval, int flags);

    /// <summary>Writes 8 bytes to an eventfd to increment its counter (wakes any waiting reader).</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static unsafe partial nint write(int fd, void* buf, nuint count);

    /// <summary>Reads 8 bytes from an eventfd, resetting its counter to zero.</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static unsafe partial nint read(int fd, void* buf, nuint count);
}
