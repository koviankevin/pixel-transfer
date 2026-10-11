using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PixelTransfer.Core;

public sealed record RestoredFile(string Name, byte[] Data);

public static class CryptoEnvelope
{
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int MaxEnvelopeBytes = MaxFileBytes + 2048;
    private const int HeaderSize = 32;
    private const int Iterations = 600_000;

    public static string SafeName(string name)
    {
        name = name.Replace('\\', '/').Split('/').Last();
        var invalid = "<>:\"/\\|?*";
        name = new string(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(name) ? "restored.bin" : name[..Math.Min(name.Length, 160)];
    }

    public static byte[] Encrypt(byte[] data, string name, string password)
    {
        if (data.Length > MaxFileBytes) throw new InvalidDataException("檔案上限為 64 MiB。");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("請輸入加密密碼。");
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var stream = new BrotliStream(output, CompressionLevel.SmallestSize, true)) stream.Write(data);
            compressed = output.ToArray();
        }
        bool useCompression = compressed.Length < data.Length;
        byte[] nameBytes = Encoding.UTF8.GetBytes(SafeName(name));
        byte[] payload = useCompression ? compressed : data;
        byte[] plain = new byte[7 + nameBytes.Length + payload.Length];
        plain[0] = useCompression ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(1), data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(5), (ushort)nameBytes.Length);
        nameBytes.CopyTo(plain, 7);
        payload.CopyTo(plain, 7 + nameBytes.Length);
        byte[] result = new byte[HeaderSize + plain.Length + 16];
        "PCE1"u8.CopyTo(result);
        RandomNumberGenerator.Fill(result.AsSpan(4, 28)); // 16-byte salt + 12-byte nonce
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, result.AsSpan(4, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(result.AsSpan(20, 12), plain, result.AsSpan(HeaderSize, plain.Length), result.AsSpan(result.Length - 16), result.AsSpan(0, HeaderSize));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    public static RestoredFile Decrypt(byte[] envelope, string password)
    {
        if (envelope.Length < HeaderSize + 23 || envelope.Length > MaxEnvelopeBytes || !envelope.AsSpan(0, 4).SequenceEqual("PCE1"u8))
            throw new InvalidDataException("加密檔案格式不正確。");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("請輸入解密密碼。");
        byte[] plain = new byte[envelope.Length - HeaderSize - 16];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, envelope.AsSpan(4, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.AsSpan(20, 12), envelope.AsSpan(HeaderSize, plain.Length), envelope.AsSpan(envelope.Length - 16), plain, envelope.AsSpan(0, HeaderSize));
            int size = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(1));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(5));
            if (plain[0] > 1 || size < 0 || size > MaxFileBytes || nameLength > 1024 || 7 + nameLength > plain.Length)
                throw new InvalidDataException("解密後的標頭不正確。");
            string name = SafeName(Encoding.UTF8.GetString(plain, 7, nameLength));
            int offset = 7 + nameLength;
            byte[] data;
            if (plain[0] == 0) data = plain.AsSpan(offset).ToArray();
            else
            {
                using var input = new MemoryStream(plain, offset, plain.Length - offset);
                using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
                data = new byte[size];
                decompressor.ReadExactly(data);
                if (decompressor.ReadByte() != -1) throw new InvalidDataException("解壓縮結果超過宣告長度。");
            }
            if (data.Length != size) throw new InvalidDataException("還原的檔案長度不符。");
            return new RestoredFile(name, data);
        }
        catch (AuthenticationTagMismatchException ex) { throw new InvalidDataException("密碼錯誤或資料已遭修改；請重新確認密碼。", ex); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
}
