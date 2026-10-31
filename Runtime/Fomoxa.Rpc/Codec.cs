using System;

namespace Fomoxa.Rpc
{
    public interface IRpcCodec<T>
    {
        uint MessageId { get; }

        byte[] Encode(T value);

        T Decode(ReadOnlySpan<byte> body);
    }

    public abstract class DetailCodec
    {
        private protected DetailCodec()
        {
        }

        public abstract uint MessageId { get; }

        public abstract object Decode(ReadOnlySpan<byte> body);

        public abstract bool TryEncode(object detail, out byte[] body);

        public static DetailCodec Of<T>(IRpcCodec<T> codec) where T : notnull => new Typed<T>(codec);

        private sealed class Typed<T> : DetailCodec where T : notnull
        {
            private readonly IRpcCodec<T> codec;

            public Typed(IRpcCodec<T> codec)
            {
                this.codec = codec;
            }

            public override uint MessageId => codec.MessageId;

            public override object Decode(ReadOnlySpan<byte> body) => codec.Decode(body);

            public override bool TryEncode(object detail, out byte[] body)
            {
                if (detail is T value)
                {
                    body = codec.Encode(value);
                    return true;
                }
                body = Array.Empty<byte>();
                return false;
            }
        }
    }
}
