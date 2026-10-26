using System.Net;

namespace Kestrel.Transport.IoUring.Transport;

/// <summary>Which transport served a bind.</summary>
public enum IoUringBindingMode
{
    /// <summary>One or more io_uring rings serve the listener.</summary>
    IoUring,

    /// <summary>Kestrel's <c>SocketTransport</c> (epoll) serves the listener.</summary>
    SocketFallback,
}

/// <summary>Describes what actually served one <see cref="IoUringTransportFactory.BindAsync"/> call.</summary>
/// <param name="EndPoint">The bound endpoint (the real port when port 0 was requested).</param>
/// <param name="Mode">The transport that serves the listener.</param>
/// <param name="RingCount">Number of io_uring rings (0 for <see cref="IoUringBindingMode.SocketFallback"/>).</param>
/// <param name="SetupFlags">io_uring setup flags the kernel accepted after any fallbacks (0 for sockets).</param>
/// <param name="UsesProvidedBufferRing">Whether multishot receive uses a provided buffer ring.</param>
/// <param name="FallbackReason">Why the socket transport was used, or <see langword="null"/> for io_uring.</param>
public sealed record IoUringBindingInfo(
    EndPoint EndPoint,
    IoUringBindingMode Mode,
    int RingCount,
    uint SetupFlags,
    bool UsesProvidedBufferRing,
    string? FallbackReason);

/// <summary>Internal view over a listener that is backed by io_uring rings.</summary>
internal interface IIoUringListenerInfo
{
    int RingCount { get; }

    uint SetupFlags { get; }

    bool UsesProvidedBufferRing { get; }
}
