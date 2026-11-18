using System.Buffers.Binary;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Audio;

/// <summary>
/// Reads .wav (RIFF/WAVE) for audio banks: PCM 8/16/24/32-bit or 32-bit float, 1–8 channels.
/// The result is 16-bit mono PCM (channels are averaged), as in the game's banks.
/// </summary>
public static class WavFile
{
    public sealed record Pcm(short[] Samples, int SampleRate, int SourceChannels, int SourceBits);

    public static Pcm Read(byte[] file)
    {
        if (
            file.Length < 12
            || file.AsSpan(0, 4).SequenceEqual("RIFF"u8) == false
            || file.AsSpan(8, 4).SequenceEqual("WAVE"u8) == false
        )
            throw new InvalidDataException(L.T("core.audio.fileNotSupportedWavPcm"));
        int pos = 12,
            format = 0,
            channels = 0,
            rate = 0,
            bits = 0,
            align = 0;
        ReadOnlySpan<byte> data = default;
        bool haveData = false;
        while (pos + 8 <= file.Length)
        {
            var id = file.AsSpan(pos, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            int body = pos + 8;
            long available = Math.Min(size, file.Length - body);
            if (id.SequenceEqual("fmt "u8) && available >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(body + 4));
                align = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 14));
                // WAVE_FORMAT_EXTENSIBLE: the actual format is in the first two bytes of SubFormat.
                if (format == 0xFFFE && available >= 26)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(body + 24));
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = file.AsSpan(body, (int)available);
                haveData = true;
            }
            pos = (int)Math.Min(file.Length, body + size + (size & 1));
        }
        bool pcm = format == 1 && bits is 8 or 16 or 24 or 32;
        bool fl = format == 3 && bits == 32;
        if (!haveData || channels is < 1 or > 8 || rate <= 0 || !(pcm || fl) || align != channels * bits / 8)
            throw new InvalidDataException(L.T("core.audio.fileNotSupportedWavPcm"));

        int frames = data.Length / align;
        var samples = new short[frames];
        int bytes = bits / 8;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = data.Slice(f * align + c * bytes, bytes);
                sum += fl
                    ? Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(s), -1f, 1f) * 32767.0
                    : bits switch
                    {
                        8 => (s[0] - 128) * 256.0,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(s),
                        24 => ((s[0] | s[1] << 8 | s[2] << 16) << 8 >> 8) / 256.0,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 65536.0,
                    };
            }
            samples[f] = (short)Math.Clamp(Math.Round(sum / channels), short.MinValue, short.MaxValue);
        }
        return new Pcm(samples, rate, channels, bits);
    }
}
