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
}
