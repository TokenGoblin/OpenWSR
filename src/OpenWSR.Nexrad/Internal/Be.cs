using System.Buffers.Binary;
using System.Text;

namespace OpenWSR.Nexrad.Internal;

/// <summary>Big-endian span readers. Everything on the NEXRAD wire is big-endian.</summary>
internal static class Be
{
    public static ushort U16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16BigEndian(s[offset..]);
    public static short I16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadInt16BigEndian(s[offset..]);
    public static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32BigEndian(s[offset..]);
    public static int I32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadInt32BigEndian(s[offset..]);
    public static float F32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleBigEndian(s[offset..]);
    public static string Ascii(ReadOnlySpan<byte> s, int offset, int length) =>
        Encoding.ASCII.GetString(s.Slice(offset, length));
}
