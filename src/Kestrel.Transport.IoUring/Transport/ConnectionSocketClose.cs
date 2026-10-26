using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Kestrel.Transport.IoUring.Native;
using Microsoft.Extensions.Logging;

namespace Kestrel.Transport.IoUring.Transport;

internal static class ConnectionSocketClose
{
    internal interface ICloseAdapter<TSelf>
        where TSelf : struct, ICloseAdapter<TSelf>
    {
        static abstract bool UnregisterFixedFile(ref TSelf self, int fileIndex);

        static abstract void ShutdownSocket(ref TSelf self, int socketFd);

        static abstract int CloseSocketFd(ref TSelf self, int socketFd);

        static abstract void LogFixedFileUnregisterFailure(ref TSelf self, int fileIndex);

        static abstract void LogSocketCloseFailure(ref TSelf self, int socketFd, int errno);
    }

    internal static void Run<TCloseAdapter>(
        CancellationTokenSource connectionCts,
        int fileIndex,
        int socketFd,
        ref int socketClosed,
        ref TCloseAdapter closeAdapter)
        where TCloseAdapter : struct, ICloseAdapter<TCloseAdapter>
    {
        if (Interlocked.Exchange(ref socketClosed, 1) != 0)
            return;

        List<Exception>? failures = null;

        try
        {
            connectionCts.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException ex)
        {
            CaptureFailure(ref failures, ex);
        }

        // Until the unregister succeeds, the ring's fixed-file table holds its own reference to the socket.
        bool fileStillRegistered = fileIndex >= 0;
        try
        {
            if (fileStillRegistered && TCloseAdapter.UnregisterFixedFile(ref closeAdapter, fileIndex))
                fileStillRegistered = false;
            else if (fileStillRegistered)
                TCloseAdapter.LogFixedFileUnregisterFailure(ref closeAdapter, fileIndex);
        }
        catch (Exception ex)
        {
            CaptureFailure(ref failures, ex);
        }
        finally
        {
            // Closing our descriptor alone would leave the connection open while the table still references it;
            // shutting it down makes the peer see the close now. The listener retries the unregister.
            if (fileStillRegistered)
            {
                try
                {
                    TCloseAdapter.ShutdownSocket(ref closeAdapter, socketFd);
                }
                catch (Exception ex)
                {
                    CaptureFailure(ref failures, ex);
                }
            }

            try
            {
                TryCloseSocket(socketFd, ref closeAdapter);
            }
            catch (Exception ex)
            {
                CaptureFailure(ref failures, ex);
            }
        }

        ThrowCapturedFailures(failures);
    }

    private static void TryCloseSocket<TCloseAdapter>(
        int socketFd,
        ref TCloseAdapter closeAdapter)
        where TCloseAdapter : struct, ICloseAdapter<TCloseAdapter>
    {
        if (TCloseAdapter.CloseSocketFd(ref closeAdapter, socketFd) >= 0)
            return;

        int errno = Marshal.GetLastPInvokeError();
        TCloseAdapter.LogSocketCloseFailure(ref closeAdapter, socketFd, errno);
    }

    private static void CaptureFailure(ref List<Exception>? failures, Exception failure) =>
        (failures ??= []).Add(failure);

    private static void ThrowCapturedFailures(List<Exception>? failures)
    {
        if (failures is null || failures.Count == 0)
            return;

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
            return;
        }

        throw new AggregateException(failures);
    }

    internal readonly struct ProductionCloseAdapter(Ring ring, ILogger logger)
        : ICloseAdapter<ProductionCloseAdapter>
    {
        private readonly Ring _ring = ring;
        private readonly ILogger _logger = logger;

        public static bool UnregisterFixedFile(ref ProductionCloseAdapter self, int fileIndex) =>
            self._ring.UnregisterFd(fileIndex);

        public static void ShutdownSocket(ref ProductionCloseAdapter self, int socketFd) =>
            Libc.shutdown(socketFd, IoUringConstants.SHUT_RDWR);

        public static int CloseSocketFd(ref ProductionCloseAdapter self, int socketFd) =>
            Libc.close(socketFd);

        public static void LogFixedFileUnregisterFailure(ref ProductionCloseAdapter self, int fileIndex) =>
            self._logger.LogWarning(
                "Failed to unregister fixed-file slot {Slot}; the socket was shut down and the unregister is retried.",
                fileIndex);

        public static void LogSocketCloseFailure(ref ProductionCloseAdapter self, int socketFd, int errno) =>
            self._logger.LogWarning("close(fd={Fd}) failed with errno {Errno}", socketFd, errno);
    }
}
