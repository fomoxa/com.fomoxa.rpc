using System;
using Fomoxa;

namespace Fomoxa.Rpc.Directory
{
    [Network]
    [Codec("rpc")]
    public class DirectoryAuth
    {
        [Network("string")]
        [Codec("rpc")]
        public string Role { get; set; } = "";

        [Network("bytes")]
        [Codec("rpc")]
        public byte[] Key { get; set; } = Array.Empty<byte>();
    }
}
