using System.Buffers.Binary;
using Google.Protobuf;

namespace ToyCity.Protocol;

/// <summary>Messages on a stream, each as a 4-byte little-endian length and the message's bytes.</summary>
public static class Framing
{
    /// <summary>Larger than any city's state; a frame bigger than this means the stream is broken or hostile.</summary>
    public const int MaxFrame = 64 << 20;

    public static void Write(Stream s, IMessage m)
    {
        var body = m.ToByteArray();
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, body.Length);
        body.CopyTo(buf, 4);
        s.Write(buf);
        s.Flush();
    }

    /// <summary>The next message, or null when the stream has ended.</summary>
    public static T? Read<T>(Stream s, MessageParser<T> parser) where T : class, IMessage<T>
    {
        var head = new byte[4];
        if (!Fill(s, head)) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (n < 0 || n > MaxFrame) throw new InvalidDataException($"a frame of {n} bytes");
        var body = new byte[n];
        if (!Fill(s, body)) throw new EndOfStreamException("the stream ended inside a frame");
        return parser.ParseFrom(body);
    }

    static bool Fill(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int k = s.Read(buf, got, buf.Length - got);
            if (k == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException("the stream ended inside a frame");
            }
            got += k;
        }
        return true;
    }
}
