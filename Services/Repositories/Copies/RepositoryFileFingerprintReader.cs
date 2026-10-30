using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using GitManager.Models.RepositoryCopies;

namespace GitManager.Services.Repositories.Copies;

internal static class RepositoryFileFingerprintReader
{
    // 一次读取同时计算原始哈希和 CRLF→LF 内容哈希；前 8000 字节含 NUL 视为二进制。
    public static RepositoryFileFingerprint Read(Stream stream, CancellationToken token)
    {
        const int BufferSize = 81920;
        const byte CarriageReturn = 13, LineFeed = 10;
        using var raw = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize), normalized = ArrayPool<byte>.Shared.Rent(BufferSize + 1);
        try
        {
            bool? binary = null;
            bool pendingCr = false;
            long crlf = 0, lf = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, BufferSize);
                if (read == 0) break;
                raw.AppendData(buffer, 0, read);
                binary ??= buffer.AsSpan(0, Math.Min(read, 8000)).Contains((byte)0);
                if (binary.Value) continue;
                int length = 0;
                for (int i = 0; i < read; i++)
                {
                    byte value = buffer[i];
                    if (pendingCr)
                    {
                        pendingCr = false;
                        if (value == LineFeed) { crlf++; normalized[length++] = value; continue; }
                        normalized[length++] = CarriageReturn;
                    }
                    if (value == CarriageReturn) { pendingCr = true; continue; }
                    if (value == LineFeed) lf++;
                    normalized[length++] = value;
                }
                content.AppendData(normalized, 0, length);
            }
            if (pendingCr) content.AppendData([CarriageReturn]);
            string hash = Convert.ToHexString(raw.GetHashAndReset());
            if (binary == true) return new(hash, hash, "");
            string lineEnding = (crlf, lf) switch { (0, 0) => "无换行", (_, 0) => "CRLF", (0, _) => "LF", _ => "CRLF/LF 混合" };
            return new(hash, Convert.ToHexString(content.GetHashAndReset()), lineEnding);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(normalized);
        }
    }
}
