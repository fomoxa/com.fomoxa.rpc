using Fomoxa;

namespace Fomoxa.Rpc.Directory
{
    [Network]
    [Codec("rpc")]
    public class PodWatch
    {
    }

    [Network]
    [Codec("rpc")]
    public class PodChange
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

        [Network("bool")]
        [Codec("rpc")]
        public bool Removed { get; set; }
    }
}
