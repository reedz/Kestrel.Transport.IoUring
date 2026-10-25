namespace Kestrel.Transport.IoUring.Transport;

internal static class ListenerCloseFinalization
{
    internal interface IFinalizationAdapter<TSelf>
        where TSelf : struct, IFinalizationAdapter<TSelf>
    {
        static abstract void ShutdownSocket(ref TSelf self);

        static abstract void RemoveClosingConnection(ref TSelf self, long connectionId);

        static abstract void CloseSocketFd(ref TSelf self);

        static abstract void ReleaseConnection(ref TSelf self, long connectionId);

        static abstract void LogCloseFinalizationFailure(ref TSelf self, long connectionId, Exception exception);
    }

    internal static void Run<TFinalizationAdapter>(
        long connectionId,
        bool hasRecvInFlight,
        ref TFinalizationAdapter finalizationAdapter)
        where TFinalizationAdapter : struct, IFinalizationAdapter<TFinalizationAdapter>
    {
        if (hasRecvInFlight)
        {
            TFinalizationAdapter.ShutdownSocket(ref finalizationAdapter);
            return;
        }

        TFinalizationAdapter.RemoveClosingConnection(ref finalizationAdapter, connectionId);
        try
        {
            try
            {
                TFinalizationAdapter.CloseSocketFd(ref finalizationAdapter);
            }
            catch (Exception ex)
            {
                try
                {
                    TFinalizationAdapter.LogCloseFinalizationFailure(
                        ref finalizationAdapter,
                        connectionId,
                        ex);
                }
                catch
                {
                }
            }
        }
        finally
        {
            TFinalizationAdapter.ReleaseConnection(ref finalizationAdapter, connectionId);
        }
    }
}
