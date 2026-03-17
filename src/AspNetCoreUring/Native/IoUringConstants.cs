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
}
