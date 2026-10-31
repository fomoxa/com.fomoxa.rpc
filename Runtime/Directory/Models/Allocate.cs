using System;
using System.Collections.Generic;
using Fomoxa;

namespace Fomoxa.Rpc.Directory
{
    [Network]
    [Codec("rpc")]
    public class DirectoryLabel
    {
        [Network("string")]
        [Codec("rpc")]
        public string Name { get; set; } = "";

        [Network("string")]
        [Codec("rpc")]
        public string Value { get; set; } = "";
    }

    [Network]
    [Codec("rpc")]
    public class AllocateRequest
    {
        [Network("string")]
        [Codec("rpc")]
        public string RoomKey { get; set; } = "";

        [Network("string")]
        [Codec("rpc")]
        public string Version { get; set; } = "";

        [Network("string")]
        [Codec("rpc")]
        public string Region { get; set; } = "";

        [Network("Array<DirectoryLabel>")]
        [Codec("rpc")]
        public List<DirectoryLabel> Attributes { get; set; } = new List<DirectoryLabel>();

        [Network("bytes")]
        [Codec("rpc")]
        public byte[] ApplicationData { get; set; } = Array.Empty<byte>();

        [Network("u32")]
        [Codec("rpc")]
        public uint Tickets { get; set; }
    }

    [Network]
    [Codec("rpc")]
    public class AllocateResponse
    {
        [Network("string")]
        [Codec("rpc")]
        public string Address { get; set; } = "";

        [Network("u16")]
        [Codec("rpc")]
        public ushort Port { get; set; }

        [Network("Array<bytes>")]
        [Codec("rpc")]
        public List<byte[]> Connections { get; set; } = new List<byte[]>();

        [Network("string")]
        [Codec("rpc")]
        public string WebSocketUrl { get; set; } = "";
    }

    [Network]
    [Codec("rpc")]
    public class RelocateRoom
    {
        [Network("string")]
        [Codec("rpc")]
        public string RoomKey { get; set; } = "";

        [Network("u32")]
        [Codec("rpc")]
        public uint Tickets { get; set; }

        [Network("bytes")]
        [Codec("rpc")]
        public byte[] ApplicationData { get; set; } = Array.Empty<byte>();
    }

    [Network]
    [Codec("rpc")]
    public class NoPodAvailable
    {
        [Network("string")]
        [Codec("rpc")]
        public string Version { get; set; } = "";
    }
}
