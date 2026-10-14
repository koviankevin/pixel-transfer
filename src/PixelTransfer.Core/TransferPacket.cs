using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PixelTransfer.Core;

public sealed record TransferPacket(byte[] TransferId, int Index, int Total, FrameSize Size, byte[] Payload)
{
    public const int Overhead = 40;
    public byte[] Serialize()
    {
        var bytes = new byte[Overhead + Payload.Length];
        "PCF1"u8.CopyTo(bytes);
        TransferId.CopyTo(bytes, 4);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), Index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), Total);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), Payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), (ushort)Size.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), (ushort)Size.Height);
        Payload.CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), Crc32(bytes.AsSpan(0, bytes.Length - 4)));
        return bytes;
    }
    public static TransferPacket? Parse(byte[] bytes, FrameSize detected)
        => Parse(bytes, detected, out _);

    public static TransferPacket? Parse(byte[] bytes, FrameSize detected, out bool crcFailed)
    {
        crcFailed = false;
        if (bytes.Length < Overhead || !bytes.AsSpan(0, 4).SequenceEqual("PCF1"u8)) return null;
        int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        int total = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28));
        var size = new FrameSize(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        if (size != detected) return null;
        int capacity = size.PayloadBytes;
        int maxPackets = (CryptoEnvelope.MaxEnvelopeBytes + capacity - 1) / capacity;
        if (index < 0 || total < 1 || index >= total || total > maxPackets || length < 1 || length > capacity || length > bytes.Length - Overhead) return null;
        if (index < total - 1 && length != capacity) return null;
        if (Crc32(bytes.AsSpan(0, 36 + length)) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36 + length)))
        { crcFailed = true; return null; }
        return new TransferPacket(bytes.AsSpan(4, 16).ToArray(), index, total, size, bytes.AsSpan(36, length).ToArray());
    }
    public static IReadOnlyList<TransferPacket> Split(byte[] envelope, FrameSize size)
    {
        if (envelope.Length < 1 || envelope.Length > CryptoEnvelope.MaxEnvelopeBytes) throw new InvalidDataException("加密資料長度超過限制。");
        int capacity = size.PayloadBytes;
        int total = (envelope.Length + capacity - 1) / capacity;
        byte[] id = SHA256.HashData(envelope)[..16];
        var result = new List<TransferPacket>();
        for (int i = 0; i < total; i++) result.Add(new(id, i, total, size, envelope.AsSpan(i * capacity, Math.Min(capacity, envelope.Length - i * capacity)).ToArray()));
        return result;
    }
    private static readonly uint[] Table = BuildTable();
    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int j = 0; j < 8; j++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
            table[i] = value;
        }
        return table;
    }
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value = 0xffffffff;
        foreach (byte b in bytes) value = (value >> 8) ^ Table[(value ^ b) & 255];
        return ~value;
    }
}

public sealed class TransferCollector
{
    private readonly Dictionary<int, byte[]> parts = new();
    private byte[]? id;
    private FrameSize size;
    private long bytes;
    public int Total { get; private set; }
    public int Count => parts.Count;
    public bool Complete => Total > 0 && Count == Total;
    public void Add(TransferPacket packet)
    {
        if (id == null) { id = packet.TransferId; size = packet.Size; Total = packet.Total; }
        if (!id.AsSpan().SequenceEqual(packet.TransferId) || size != packet.Size || Total != packet.Total)
            throw new InvalidDataException("擷取區域混入另一個傳輸；請停止後重新框選。");
        if (parts.TryGetValue(packet.Index, out var old))
        {
            if (!old.AsSpan().SequenceEqual(packet.Payload)) throw new InvalidDataException("重複分片的內容不同。");
            return;
        }
        if (bytes + packet.Payload.Length > CryptoEnvelope.MaxEnvelopeBytes) throw new InvalidDataException("接收內容超過上限。");
        parts.Add(packet.Index, packet.Payload); bytes += packet.Payload.Length;
    }
    public byte[] Assemble()
    {
        if (!Complete) throw new InvalidOperationException("分片尚未收齊。");
        using var output = new MemoryStream((int)bytes);
        for (int i = 0; i < Total; i++) output.Write(parts[i]);
        byte[] result = output.ToArray();
        if (!SHA256.HashData(result).AsSpan(0, 16).SequenceEqual(id)) throw new InvalidDataException("完整資料校驗失敗。");
        return result;
    }
}
