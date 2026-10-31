using System;
using System.Collections.Generic;
using Fomoxa;

namespace Fomoxa.Rpc.Directory
{
    [Network]
    [Codec("rpc")]
    public class RegisterPod
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint PodId { get; set; }

        [Network("string")]
        [Codec("rpc")]
        public string Address { get; set; } = "";

        [Network("u16")]
        [Codec("rpc")]
        public ushort Port { get; set; }

        [Network("string")]
        [Codec("rpc")]
        public string Target { get; set; } = "";

        [Network("string")]
        [Codec("rpc")]
        public string Version { get; set; } = "";

        [Network("string")]
        [Codec("rpc")]
        public string Region { get; set; } = "";

        [Network("Array<DirectoryLabel>")]
        [Codec("rpc")]
        public List<DirectoryLabel> Labels { get; set; } = new List<DirectoryLabel>();

        [Network("u32")]
        [Codec("rpc")]
        public uint MaxPlayers { get; set; }

        [Network("Array<string>")]
        [Codec("rpc")]
        public List<string> Rooms { get; set; } = new List<string>();
    }

    [Network]
    [Codec("rpc")]
    public class PodRegistered
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint PodId { get; set; }

        [Network("u32")]
        [Codec("rpc")]
        public uint ReportIntervalMs { get; set; }

        [Network("bytes")]
        [Codec("rpc")]
        public byte[] TicketKey { get; set; } = Array.Empty<byte>();
    }

    [Network]
    [Codec("rpc")]
    public class PodLoad
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint Players { get; set; }

        [Network("u32")]
        [Codec("rpc")]
        public uint Rooms { get; set; }

        [Network("f32")]
        [Codec("rpc")]
        public float Load { get; set; }
    }

    [Network]
    [Codec("rpc")]
    public class AcquireRoom
    {
        [Network("string")]
        [Codec("rpc")]
        public string RoomKey { get; set; } = "";
    }

    [Network]
    [Codec("rpc")]
    public class ReleaseRoom
    {
        [Network("string")]
        [Codec("rpc")]
        public string RoomKey { get; set; } = "";
    }

    [Network]
    [Codec("rpc")]
    public class DrainWatch
    {
    }

    [Network]
    [Codec("rpc")]
    public class DrainOrder
    {
        [Network("i64")]
        [Codec("rpc")]
        public long Deadline { get; set; }
    }

    [Network]
    [Codec("rpc")]
    public class DrainSelf
    {
    }
}
