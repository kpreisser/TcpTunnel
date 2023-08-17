using System;
using System.Threading;

namespace TcpTunnel.Utils
{
    internal static class CancellationTokenSourceExtensions
    {
        public static void CancelAndIgnoreAggregateException(
            this CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (AggregateException)
            {
                // Ignore.
                // This can occur with some implementations, e.g. registered callbacks
                // from  WebSocket operations using HTTP.sys (from ASP.NET Core) can
                // throw here when calling Cancel() and the IWebHost has already been
                // disposed.
            }
        }
    }
}
