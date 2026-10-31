using System.Collections.Generic;
using Fomoxa;

namespace Fomoxa.Rpc.Directory
{
    [Network]
    [Codec("rpc")]
    public class DrainPod
    {
        [Network("u32")]
        [Codec("rpc")]
        public uint PodId { get; set; }
    }

    [Network]
    [Codec("rpc")]
    public class ReadPods
    {
    }

    [Network]
    [Codec("rpc")]
    public class PodState
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

        [Network("u32")]
        [Codec("rpc")]
        public uint Players { get; set; }

        [Network("u32")]
        [Codec("rpc")]
        public uint Rooms { get; set; }

        [Network("f32")]
        [Codec("rpc")]
        public float Load { get; set; }

        [Network("bool")]
        [Codec("rpc")]
        public bool Draining { get; set; }

        [Network("bool")]
        [Codec("rpc")]
        public bool Lost { get; set; }
    }

    [Network]
    [Codec("rpc")]
    public class PodList
    {
        [Network("Array<PodState>")]
        [Codec("rpc")]
        public List<PodState> Pods { get; set; } = new List<PodState>();
    }
}
