using Kestrel.Transport.IoUring.Transport;

namespace Kestrel.Transport.IoUring.Tests;

/// <summary>
/// Test lanes: the whole suite runs again with a transport variant applied, last, to every listener and host the
/// tests build (each construction site goes through <see cref="Apply"/> or <see cref="ApplyLane"/>).
/// </summary>
internal static class TestTransport
{
    /// <summary>
    /// <c>TEST_IOURING_SINGLESHOT=1</c>: no provided-buffer ring, so receives take the single-shot path that
    /// kernels 5.10-5.19 use. This covers that code path on the running kernel, not those kernels' semantics, and
    /// multishot accept stays on.
    /// </summary>
    public static bool SingleShotLane => Environment.GetEnvironmentVariable("TEST_IOURING_SINGLESHOT") == "1";

    public static IoUringTransportOptions Apply(IoUringTransportOptions options)
    {
        ApplyLane(options);
        return options;
    }

    public static void ApplyLane(IoUringTransportOptions options)
    {
        if (SingleShotLane)
            options.EnableBufferRing = false;
    }
}
