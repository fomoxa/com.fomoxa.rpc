using System;

namespace Fomoxa.Rpc
{
    public static class StatusCode
    {
        public const uint Cancelled = 1;
        public const uint DeadlineExceeded = 2;
        public const uint Unimplemented = 3;
        public const uint InvalidArgument = 4;
        public const uint Unauthenticated = 5;
        public const uint PermissionDenied = 6;
        public const uint ResourceExhausted = 7;
        public const uint FailedPrecondition = 8;
        public const uint Unavailable = 9;
        public const uint Internal = 10;

        public static string NameOf(uint code) => code switch
        {
            Cancelled => "CANCELLED",
            DeadlineExceeded => "DEADLINE_EXCEEDED",
            Unimplemented => "UNIMPLEMENTED",
            InvalidArgument => "INVALID_ARGUMENT",
            Unauthenticated => "UNAUTHENTICATED",
            PermissionDenied => "PERMISSION_DENIED",
            ResourceExhausted => "RESOURCE_EXHAUSTED",
            FailedPrecondition => "FAILED_PRECONDITION",
            Unavailable => "UNAVAILABLE",
            Internal => "INTERNAL",
            _ => "UNKNOWN",
        };
    }

    public sealed record Status(uint Code, string Message)
    {
        public uint DetailsId { get; init; }

        public byte[] RawDetails { get; init; } = Array.Empty<byte>();

        public object? Details { get; init; }

        public static Status Cancelled() => new Status(StatusCode.Cancelled, "cancelled by the caller");

        public static Status DeadlineExceeded() => new Status(StatusCode.DeadlineExceeded, "deadline exceeded");

        public static Status Unimplemented(uint requestId) =>
            new Status(StatusCode.Unimplemented, $"no handler for request type 0x{requestId:X8}");

        public static Status InvalidArgument(string message) => new Status(StatusCode.InvalidArgument, message);

        public static Status Unauthenticated(string message) => new Status(StatusCode.Unauthenticated, message);

        public static Status PermissionDenied(string message) => new Status(StatusCode.PermissionDenied, message);

        public static Status ResourceExhausted(string message) => new Status(StatusCode.ResourceExhausted, message);

        public static Status FailedPrecondition(string message) => new Status(StatusCode.FailedPrecondition, message);

        public static Status Unavailable() =>
            new Status(StatusCode.Unavailable, "the session ended before the call completed");

        public static Status Internal(string message) => new Status(StatusCode.Internal, message);

        public Status WithDetails(uint detailsId, byte[] details) => this with { DetailsId = detailsId, RawDetails = details, Details = null };

        public bool IsRetryable => Code == StatusCode.Unavailable || Code == StatusCode.ResourceExhausted;

        public string Name => StatusCode.NameOf(Code);

        public byte[] Encode() => new BodyWriter().U32(Code).String(Message).U32(DetailsId).Bytes(RawDetails).ToArray();

        public static Status Decode(ReadOnlySpan<byte> bytes)
        {
            var reader = new BodyReader(bytes);
            uint code;
            try
            {
                if (!reader.TryU32(out code))
                {
                    return Internal("error frame carried no status code");
                }
            }
            catch (ModelFormatException)
            {
                return Internal("error frame carried no status code");
            }
            string message;
            try
            {
                reader.TryString(out message);
            }
            catch (ModelFormatException)
            {
                return Internal("error frame carried an undecodable status message");
            }
            uint detailsId;
            byte[] details;
            try
            {
                reader.TryU32(out detailsId);
                reader.TryBytes(out details);
            }
            catch (ModelFormatException)
            {
                return Internal("error frame carried undecodable details");
            }
            if (detailsId == 0 && details.Length > 0)
            {
                return Internal("error frame carried details without a details_id");
            }
            return new Status(code, message) { DetailsId = detailsId, RawDetails = details };
        }

        public override string ToString() => $"{Message} ({Name})";
    }

    public sealed class RpcException : Exception
    {
        public RpcException(Status status) : base(status.ToString())
        {
            Status = status;
        }

        public Status Status { get; }
    }
}
