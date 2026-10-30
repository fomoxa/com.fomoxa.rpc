using System;
using System.Collections.Generic;
using Fomoxa;

namespace Fomoxa.Rpc
{
    [Network]
    [Codec("rpc")]
    public class FRpcVoid
    {
    }

    [Network]
    [Codec("rpc")]
    public class FRpcError
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint Code { get; set; }

        [Network("string")]
        [Codec("rpc")]
        public string Message { get; set; } = "";

        [Network("u32")]
        [Codec("rpc")]
        public uint DetailsId { get; set; }

        [Network("bytes")]
        [Codec("rpc")]
        public byte[] Details { get; set; } = Array.Empty<byte>();
    }

    [Network]
    [Codec("rpc")]
    public class FRpcCtx
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint DeadlineMs { get; set; }

        [Network("bytes")]
        [Codec("rpc")]
        public byte[] TraceId { get; set; } = Array.Empty<byte>();
    }

    [Network]
    [Codec("rpc")]
    public class FRpcHealthRequest
    {
        [Network("string")]
        [Codec("rpc")]
        public string Service { get; set; } = "";
    }

    [Network]
    [Codec("rpc")]
    public class FRpcHealthResponse
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint Status { get; set; }
    }
}
