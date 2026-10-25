using System.Threading;
using Microsoft.AspNetCore.Connections;

namespace Kestrel.Transport.IoUring.Transport;

internal static class ConnectionTransportAbort
{
    internal interface IAbortAdapter<TSelf>
        where TSelf : struct, IAbortAdapter<TSelf>
    {
        static abstract void CancelSendRetry(ref TSelf self);

        static abstract void CompleteInput(ref TSelf self, ConnectionAbortedException reason);

        static abstract void CancelOutputPendingRead(ref TSelf self);

        static abstract void ShutdownSocket(ref TSelf self);

        static abstract void LogConnectionClosedCallbackFailure(ref TSelf self, Exception exception);
    }

    internal static void Run<TAbortAdapter>(
        CancellationTokenSource connectionCts,
        ConnectionAbortedException reason,
        ref TAbortAdapter abortAdapter)
        where TAbortAdapter : struct, IAbortAdapter<TAbortAdapter>
    {
        try
        {
            connectionCts.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException ex)
        {
            try
            {
                TAbortAdapter.LogConnectionClosedCallbackFailure(ref abortAdapter, ex);
            }
            catch
            {
            }
        }

        TAbortAdapter.CancelSendRetry(ref abortAdapter);
        try
        {
            TAbortAdapter.CompleteInput(ref abortAdapter, reason);
        }
        catch (InvalidOperationException) { }
        TAbortAdapter.CancelOutputPendingRead(ref abortAdapter);
        TAbortAdapter.ShutdownSocket(ref abortAdapter);
    }
}
