using System.Buffers.Binary;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Rpf;

/// <summary>
/// Constants and low-level layout of RPF7 (GTA V).
/// Layout source: public format descriptions and the CodeWalker sources (MIT).
/// Anything not confirmed by tests on real archives is treated conservatively.
/// </summary>
public static class Rpf7Format
{
    public const uint Magic = 0x52504637; // "RPF7"
    public const uint DirectoryIdent = 0x7FFFFF00;
    public const int HeaderSize = 16;
    public const int EntrySize = 16;
    public const int BlockSize = 512;
    public const uint MaxThreeByte = 0xFFFFFF;
    public const uint ResourceMagic = 0x37435352; // "RSC7"
}

public enum RpfEncryption : uint
{
    None = 0,
    Open = 0x4E45504F, // "OPEN": the TOC is not encrypted (OpenIV / mods format)
    Aes = 0x0FFFFFF9,
    Ng = 0x0FEFFFFF,
}

public readonly record struct RpfHeader(uint Magic, uint EntryCount, uint NamesLength, uint EncryptionRaw)
{
    public bool IsRpf7 => Magic == Rpf7Format.Magic;

    public RpfEncryption Encryption =>
        EncryptionRaw switch
        {
            0 => RpfEncryption.None,
            (uint)RpfEncryption.Open => RpfEncryption.Open,
            (uint)RpfEncryption.Aes => RpfEncryption.Aes,
            (uint)RpfEncryption.Ng => RpfEncryption.Ng,
            _ => (RpfEncryption)EncryptionRaw,
        };

    /// <summary>The TOC can be read without keys.</summary>
    public bool TocIsPlain => EncryptionRaw == 0 || EncryptionRaw == (uint)RpfEncryption.Open;

    public static RpfHeader Parse(ReadOnlySpan<byte> b) =>
        new(
            BinaryPrimitives.ReadUInt32LittleEndian(b),
            BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[12..])
        );

    public static RpfHeader Read(Stream s)
    {
        Span<byte> buf = stackalloc byte[Rpf7Format.HeaderSize];
        s.ReadExactly(buf);
        return Parse(buf);
    }

    public void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], NamesLength);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], EncryptionRaw);
    }
}

public class RpfException(string message) : Exception(message);

/// <summary>The TOC is encrypted (AES/NG): it cannot be read without the game keys.</summary>
public sealed class RpfEncryptedException(RpfEncryption enc)
    : RpfException(L.T("core.rpf.archiveTableContentsEncryptedGame", enc))
{
    public RpfEncryption Encryption { get; } = enc;
}
