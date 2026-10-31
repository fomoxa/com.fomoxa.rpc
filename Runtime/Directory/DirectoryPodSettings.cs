using System;
using System.Collections.Generic;
using Fomoxa.Networking.Transports;

namespace Fomoxa.Rpc.Directory
{
    public sealed class DirectoryPodSettings
    {
        public const ushort DefaultDirectoryPort = 9500;

        public ITransportFactory Transport { get; set; } = new TcpTransportFactory();

        public string DirectoryAddress { get; set; } = "";

        public ushort DirectoryPort { get; set; } = DefaultDirectoryPort;

        public byte[] Key { get; set; } = Array.Empty<byte>();

        public string Address { get; set; } = "";

        public ushort Port { get; set; }

        public string Target { get; set; } = "";

        public string Version { get; set; } = "";

        public string Region { get; set; } = "";

        public IDictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();

        public uint MaxPlayers { get; set; }

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        internal void Validate()
        {
            if (Transport == null)
            {
                throw new ArgumentException("DirectoryPodSettings.Transport is not set");
            }

            if (string.IsNullOrEmpty(DirectoryAddress) || DirectoryPort == 0)
            {
                throw new ArgumentException("DirectoryPodSettings needs the directory's address and port");
            }

            if (Key == null || Key.Length == 0)
            {
                throw new ArgumentException("DirectoryPodSettings.Key is empty; set the key of the pod role");
            }

            if (string.IsNullOrEmpty(Address) || Port == 0)
            {
                throw new ArgumentException("DirectoryPodSettings needs the address and port players reach this pod on");
            }

            if (string.IsNullOrEmpty(Version))
            {
                throw new ArgumentException("DirectoryPodSettings.Version is empty");
            }

            if (MaxPlayers == 0)
            {
                throw new ArgumentException("DirectoryPodSettings.MaxPlayers is zero");
            }

            if (ConnectTimeout <= TimeSpan.Zero || RetryDelay < TimeSpan.Zero)
            {
                throw new ArgumentException("DirectoryPodSettings needs a positive ConnectTimeout and a RetryDelay of zero or more");
            }
        }
    }
}
