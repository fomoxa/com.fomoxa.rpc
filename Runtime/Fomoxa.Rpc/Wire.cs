using System;
using System.Buffers.Binary;

namespace Fomoxa.Rpc
{
    public enum Kind : byte
    {
        Request = 0,
        Response = 1,
        Error = 2,
        Item = 3,
        End = 4,
        Cancel = 5,
    }

    public enum Sender
    {
        Caller,
        Callee,
    }

    public readonly struct CallId : IEquatable<CallId>
    {
        public CallId(uint value)
        {
            Value = value;
        }

        public uint Value { get; }

        public bool IsEven => Value % 2 == 0;

        public bool Equals(CallId other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is CallId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public static bool operator ==(CallId left, CallId right) => left.Equals(right);

        public static bool operator !=(CallId left, CallId right) => !left.Equals(right);

        public override string ToString() => $"call {Value}";
    }

    public readonly struct Header : IEquatable<Header>
    {
        public Header(Kind kind, CallId call, bool hasCtx = false)
        {
            Kind = kind;
            Call = call;
            HasCtx = hasCtx;
        }

        public Kind Kind { get; }

        public CallId Call { get; }

        public bool HasCtx { get; }

        public bool Equals(Header other) => Kind == other.Kind && Call == other.Call && HasCtx == other.HasCtx;

        public override bool Equals(object? obj) => obj is Header other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Kind, Call, HasCtx);

        public static bool operator ==(Header left, Header right) => left.Equals(right);

        public static bool operator !=(Header left, Header right) => !left.Equals(right);

        public override string ToString() => $"Header {{ Kind = {Kind}, Call = {Call}, HasCtx = {HasCtx} }}";
    }

    public enum WireError
    {
        None,
        Truncated,
        UnknownKind,
        ReservedBitsSet,
        CtxOnNonRequest,
        CtxTruncated,
        CtxTooLarge,
    }

    public readonly struct Parsed
    {
        public Parsed(Header header, ReadOnlyMemory<byte>? ctx, ReadOnlyMemory<byte> body)
        {
            Header = header;
            Ctx = ctx;
            Body = body;
        }

        public Header Header { get; }

        public ReadOnlyMemory<byte>? Ctx { get; }

        public ReadOnlyMemory<byte> Body { get; }
    }

    public static class Wire
    {
        public const int HeaderLength = 5;
        public const int CtxLengthLength = 4;

        private const byte FlagCtx = 0b0100_0000;
        private const byte ReservedMask = 0b1011_1000;
        private const byte KindMask = 0b0000_0111;

        public static string NameOf(Kind kind) => kind switch
        {
            Kind.Request => "REQUEST",
            Kind.Response => "RESPONSE",
            Kind.Error => "ERROR",
            Kind.Item => "ITEM",
            Kind.End => "END",
            Kind.Cancel => "CANCEL",
            _ => $"kind {(byte)kind}",
        };

        public static byte KindBits(byte first) => (byte)(first & KindMask);

        public static byte[] Encode(Header header, byte[]? ctx, ReadOnlySpan<byte> body)
        {
            int ctxLength = ctx == null ? 0 : CtxLengthLength + ctx.Length;
            var payload = new byte[HeaderLength + ctxLength + body.Length];
            byte first = (byte)header.Kind;
            if (ctx != null)
            {
                first |= FlagCtx;
            }
            payload[0] = first;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), header.Call.Value);
            int offset = HeaderLength;
            if (ctx != null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), (uint)ctx.Length);
                ctx.CopyTo(payload.AsSpan(offset + CtxLengthLength));
                offset += ctxLength;
            }
            body.CopyTo(payload.AsSpan(offset));
            return payload;
        }

        public static CallId ReadCallId(ReadOnlySpan<byte> payload) =>
            new CallId(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4)));

        public static WireError Decode(ReadOnlyMemory<byte> payload, int maxCtxLength, out Parsed parsed)
        {
            parsed = default;
            var bytes = payload.Span;
            if (bytes.Length < HeaderLength)
            {
                return WireError.Truncated;
            }
            byte first = bytes[0];
            if ((first & ReservedMask) != 0)
            {
                return WireError.ReservedBitsSet;
            }
            byte bits = KindBits(first);
            if (bits > (byte)Kind.Cancel)
            {
                return WireError.UnknownKind;
            }
            var header = new Header(
                (Kind)bits,
                ReadCallId(bytes),
                (first & FlagCtx) != 0);

            var rest = payload.Slice(HeaderLength);
            if (!header.HasCtx)
            {
                parsed = new Parsed(header, null, rest);
                return WireError.None;
            }
            if (header.Kind != Kind.Request)
            {
                return WireError.CtxOnNonRequest;
            }
            if (rest.Length < CtxLengthLength)
            {
                return WireError.CtxTruncated;
            }
            ulong declared = BinaryPrimitives.ReadUInt32LittleEndian(rest.Span);
            if (declared > (ulong)maxCtxLength)
            {
                return WireError.CtxTooLarge;
            }
            var afterLength = rest.Slice(CtxLengthLength);
            if ((ulong)afterLength.Length < declared)
            {
                return WireError.CtxTruncated;
            }
            parsed = new Parsed(header, afterLength.Slice(0, (int)declared), afterLength.Slice((int)declared));
            return WireError.None;
        }

        public static string Describe(WireError error, ReadOnlySpan<byte> payload)
        {
            byte first = payload.Length > 0 ? payload[0] : (byte)0;
            return error switch
            {
                WireError.Truncated => "payload ended before the 5-byte header",
                WireError.UnknownKind => $"kind {KindBits(first)} is not 0..=5",
                WireError.ReservedBitsSet => $"reserved bits set in kind byte 0x{first:X2}",
                WireError.CtxOnNonRequest => $"ctx block present on {NameOf((Kind)KindBits(first))}",
                WireError.CtxTruncated => "payload ended inside the ctx block",
                WireError.CtxTooLarge => "ctx block exceeds the ceiling",
                _ => "no error",
            };
        }
    }
}
