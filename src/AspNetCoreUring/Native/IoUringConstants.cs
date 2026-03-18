namespace AspNetCoreUring.Native;

internal static class IoUringConstants
{
    public const byte IORING_OP_NOP = 0;
    public const byte IORING_OP_READV = 1;
    public const byte IORING_OP_WRITEV = 2;
    public const byte IORING_OP_FSYNC = 3;
    public const byte IORING_OP_READ_FIXED = 4;
    public const byte IORING_OP_WRITE_FIXED = 5;
    public const byte IORING_OP_POLL_ADD = 6;
    public const byte IORING_OP_POLL_REMOVE = 7;
    public const byte IORING_OP_SYNC_FILE_RANGE = 8;
    public const byte IORING_OP_SENDMSG = 9;
    public const byte IORING_OP_RECVMSG = 10;
    public const byte IORING_OP_TIMEOUT = 11;
    public const byte IORING_OP_TIMEOUT_REMOVE = 12;
    public const byte IORING_OP_ACCEPT = 13;
    public const byte IORING_OP_ASYNC_CANCEL = 14;
    public const byte IORING_OP_LINK_TIMEOUT = 15;
    public const byte IORING_OP_CONNECT = 16;
    public const byte IORING_OP_FALLOCATE = 17;
    public const byte IORING_OP_OPENAT = 18;
    public const byte IORING_OP_CLOSE = 19;
    public const byte IORING_OP_READ = 22;
    public const byte IORING_OP_WRITE = 23;
    public const byte IORING_OP_SEND = 26;
    public const byte IORING_OP_RECV = 27;

    public const uint IORING_ENTER_GETEVENTS = 1u;
    public const uint IORING_ENTER_SQ_WAKEUP = 2u;

    public const uint IORING_SETUP_SQPOLL = 2u;
    public const uint IORING_SETUP_CQSIZE = 8u;

    public const ulong IORING_OFF_SQ_RING = 0UL;
    public const ulong IORING_OFF_CQ_RING = 0x8000000UL;
    public const ulong IORING_OFF_SQES = 0x10000000UL;

    public const int PROT_READ = 1;
    public const int PROT_WRITE = 2;

    public const int MAP_SHARED = 1;
    public const int MAP_POPULATE = 0x8000;
    public const nint MAP_FAILED = -1;

    public const uint IORING_SQ_NEED_WAKEUP = 1u;

    public const uint IORING_FEAT_SINGLE_MMAP = 1u;

    public const ulong TIMEOUT_USER_DATA = ulong.MaxValue;

    /// <summary>User-data sentinel for the persistent eventfd READ SQE used to wake the IO loop on sends.</summary>
    public const ulong EVENTFD_USER_DATA = ulong.MaxValue - 1;

    // Socket option constants for setsockopt.
    public const int SOL_SOCKET = 1;
    public const int SO_REUSEADDR = 2;
    public const int SO_REUSEPORT = 15;
    public const int IPPROTO_TCP = 6;
    public const int TCP_NODELAY = 1;

    // errno constants.
    public const int ENOSYS = 38;
    public const int EINTR = 4;
    public const int EPERM = 1;
    public const int EAGAIN = 11;

    // Multishot flags — set in the SQE OpFlags field for ACCEPT/RECV.
    public const uint IORING_ACCEPT_MULTISHOT = 1u << 1;
    public const uint IORING_RECV_MULTISHOT = 1u << 1;

    // SQE flags for buffer selection.
    public const byte IOSQE_BUFFER_SELECT = 1 << 3;

    // CQE flags.
    public const uint IORING_CQE_F_BUFFER = 1u << 0; // CQE has buffer ID in upper flags
    public const uint IORING_CQE_F_MORE = 1u << 1;
    public const uint IORING_CQE_F_NOTIF = 1u << 3;   // multishot: more CQEs will follow

    // CQE buffer ID extraction.
    public const int IORING_CQE_BUFFER_SHIFT = 16;

    // io_uring_register opcodes for buffer rings.
    public const uint IORING_REGISTER_PBUF_RING = 22;
    public const uint IORING_UNREGISTER_PBUF_RING = 23;

    // io_uring_register opcodes for file and ring registration.
    public const uint IORING_REGISTER_FILES = 2;
    public const uint IORING_UNREGISTER_FILES = 3;
    public const uint IORING_REGISTER_RING_FDS = 20;
    public const uint IORING_UNREGISTER_RING_FDS = 21;

    // io_uring_setup flags.
    public const uint IORING_SETUP_COOP_TASKRUN = 1u << 8;
    public const uint IORING_SETUP_SINGLE_ISSUER = 1u << 12;

    // SQE flags for fixed files.
    public const byte IOSQE_FIXED_FILE = 1 << 0;

    // Send zero-copy.
    public const byte IORING_OP_SEND_ZC = 47;
}
