using OpenWSR.Nexrad.Internal;

namespace OpenWSR.Nexrad;

/// <summary>The 24-byte Archive II volume header: "AR2V00xx." + extension + date/time + ICAO.</summary>
public sealed record VolumeHeader(string Version, string Extension, DateTime DateUtc, string Icao)
{
    public const int Size = 24;

    public static VolumeHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
            throw new NexradFormatException("Volume header truncated.");

        var version = Be.Ascii(bytes, 0, 9);
        if (!version.StartsWith("AR2V", StringComparison.Ordinal))
            throw new NexradFormatException($"Not an Archive II volume: signature '{version}'.");

        var extension = Be.Ascii(bytes, 9, 3);
        var julianDate = Be.I32(bytes, 12);
        var millis = Be.U32(bytes, 16);
        var icao = Be.Ascii(bytes, 20, 4);
        return new VolumeHeader(version, extension, NexradTime.FromJulian(julianDate, millis), icao);
    }
}

/// <summary>Thrown when the byte stream violates the Archive II format (ICD 2620010H §7).</summary>
public sealed class NexradFormatException(string message) : Exception(message);
