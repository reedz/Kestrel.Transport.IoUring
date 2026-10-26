using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Kestrel.Transport.IoUring.Transport;

internal interface IMultiListenerWorker : IAsyncDisposable
{
    EndPoint EndPoint { get; }

    ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default);

    ValueTask UnbindAsync(CancellationToken cancellationToken = default);
}
