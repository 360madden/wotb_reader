using System.Buffers;
using System.Runtime.CompilerServices;

namespace WotBTreader.CaptureLogs.Ndjson;

internal sealed record BoundedUtf8Line(byte[]? Bytes, bool LimitExceeded);

internal static class BoundedUtf8LineReader
{
    public static async IAsyncEnumerable<BoundedUtf8Line> ReadAsync(
        Stream source,
        int maximumLineBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineBytes);

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumLineBytes, 16 * 1024));
        byte[] lineBuffer = ArrayPool<byte>.Shared.Rent(maximumLineBytes);
        int lineLength = 0;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int index = 0; index < read; index++)
                {
                    byte value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        int outputLength = lineLength > 0 && lineBuffer[lineLength - 1] == (byte)'\r'
                            ? lineLength - 1
                            : lineLength;
                        yield return new BoundedUtf8Line(
                            lineBuffer.AsSpan(0, outputLength).ToArray(),
                            LimitExceeded: false);
                        lineLength = 0;
                        continue;
                    }

                    if (lineLength >= maximumLineBytes)
                    {
                        yield return new BoundedUtf8Line(Bytes: null, LimitExceeded: true);
                        yield break;
                    }

                    lineBuffer[lineLength++] = value;
                }
            }

            if (lineLength > 0)
            {
                int outputLength = lineBuffer[lineLength - 1] == (byte)'\r'
                    ? lineLength - 1
                    : lineLength;
                yield return new BoundedUtf8Line(
                    lineBuffer.AsSpan(0, outputLength).ToArray(),
                    LimitExceeded: false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(lineBuffer, clearArray: true);
        }
    }
}
