using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TcpTunnel.Utils;

internal static class StreamExtensions
{
    // TODO: For .NET 7.0, remove this class as these methods are built-in.
    // See: https://github.com/dotnet/runtime/issues/16598
    public static ValueTask<int> ReadAtLeastAsync(
            this Stream stream,
            Memory<byte> buffer,
            int minimumBytes,
            bool throwOnEndOfStrem = true,
            CancellationToken cancellationToken = default)
    {
        if (minimumBytes < 0 || minimumBytes > buffer.Length)
            throw new ArgumentException();

        return ReadAtLeastAsyncCore();

        async ValueTask<int> ReadAtLeastAsyncCore()
        {
            int bytesRead = 0;
            while (bytesRead < minimumBytes)
            {
                int read = await stream.ReadAsync(buffer[bytesRead..], cancellationToken)
                    .ConfigureAwait(false);

                if (read is 0)
                {
                    if (throwOnEndOfStrem)
                        throw new EndOfStreamException();

                    return bytesRead;
                }

                bytesRead += read;
            }

            return bytesRead;
        }
    }

    public static ValueTask ReadExactlyAsync(
           this Stream stream,
           Memory<byte> buffer,
           CancellationToken cancellationToken = default)
    {
        return new ValueTask(
            ReadAtLeastAsync(stream, buffer, buffer.Length, throwOnEndOfStrem: true, cancellationToken)
            .AsTask());
    }
}
