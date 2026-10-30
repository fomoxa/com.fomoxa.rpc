using System;
using System.Buffers.Binary;
using System.Text;

namespace Fomoxa.Rpc
{
    public sealed class ModelFormatException : Exception
    {
        public ModelFormatException(string message) : base(message)
        {
        }
    }

    public ref struct BodyReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly ReadOnlySpan<byte> bytes;
        private int cursor;

        public BodyReader(ReadOnlySpan<byte> bytes)
        {
            this.bytes = bytes;
            cursor = 0;
        }

        public bool AtEnd => cursor >= bytes.Length;

        public bool TryU32(out uint value)
        {
            value = 0;
            if (AtEnd)
            {
                return false;
            }
            if (bytes.Length - cursor < 4)
            {
                throw new ModelFormatException("the body ended inside a u32");
            }
            value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
            cursor += 4;
            return true;
        }

        public bool TryBytes(out byte[] value)
        {
            value = Array.Empty<byte>();
            if (!TryU32(out uint length))
            {
                return false;
            }
            if ((ulong)(bytes.Length - cursor) < length)
            {
                throw new ModelFormatException($"a length of {length} runs past the end of the body");
            }
            value = bytes.Slice(cursor, (int)length).ToArray();
            cursor += (int)length;
            return true;
        }

        public bool TryString(out string value)
        {
            value = "";
            if (!TryBytes(out byte[] raw))
            {
                return false;
            }
            try
            {
                value = StrictUtf8.GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                throw new ModelFormatException("a string is not valid UTF-8");
            }
            return true;
        }
    }

    public sealed class BodyWriter
    {
        private byte[] buffer = new byte[64];
        private int length;

        public int Length => length;

        public BodyWriter U32(uint value)
        {
            Reserve(4);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length, 4), value);
            length += 4;
            return this;
        }

        public BodyWriter Bytes(ReadOnlySpan<byte> value)
        {
            U32((uint)value.Length);
            Reserve(value.Length);
            value.CopyTo(buffer.AsSpan(length));
            length += value.Length;
            return this;
        }

        public BodyWriter String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

        private void Reserve(int more)
        {
            if (buffer.Length - length >= more)
            {
                return;
            }
            int capacity = buffer.Length;
            while (capacity - length < more)
            {
                capacity *= 2;
            }
            Array.Resize(ref buffer, capacity);
        }
    }
}
