using System;

namespace Fomoxa.Rpc
{
    public sealed class Ctx
    {
        public uint DeadlineMs { get; set; }

        public byte[] TraceId { get; set; } = Array.Empty<byte>();

        public bool HasDeadline => DeadlineMs != 0;

        public bool IsEmpty => DeadlineMs == 0 && TraceId.Length == 0;

        public static Ctx WithDeadlineMs(uint deadlineMs) => new Ctx { DeadlineMs = deadlineMs };

        public byte[] Encode() => new BodyWriter()
            .U32(DeadlineMs)
            .Bytes(TraceId)
            .ToArray();

        public static Ctx Decode(ReadOnlySpan<byte> bytes)
        {
            var reader = new BodyReader(bytes);
            var ctx = new Ctx();
            if (!reader.TryU32(out uint deadlineMs))
            {
                return ctx;
            }
            ctx.DeadlineMs = deadlineMs;
            if (!reader.TryBytes(out byte[] traceId))
            {
                return ctx;
            }
            ctx.TraceId = traceId;
            return ctx;
        }
    }
}
