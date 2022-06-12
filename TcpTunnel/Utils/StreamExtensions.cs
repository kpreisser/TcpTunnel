using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TcpTunnel.Utils;

internal static class StreamExtensions
{
    public static async ValueTask<bool> ReadCompleteAsync(
            this Stream stream,
            Memory<byte> buffer,
            bool allowEndOfStream = false,
            CancellationToken cancellationToken = default)
    {
        bool firstIteration = true;

        do
        {
            int read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (read is 0 && buffer.Length > 0 && allowEndOfStream && firstIteration)
                return false;
            else if (read is 0 && buffer.Length > 0)
                throw new EndOfStreamException();

            firstIteration = false;
            buffer = buffer[read..];
        }
        while (buffer.Length > 0);

        return true;
    }
}
