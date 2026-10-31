namespace Fomoxa.Rpc.Directory
{
    public sealed class DirectoryRegistration
    {
        internal DirectoryRegistration(uint podId, uint previousPodId, byte[] ticketKey)
        {
            PodId = podId;
            PreviousPodId = previousPodId;
            TicketKey = ticketKey;
        }

        public uint PodId { get; }

        public uint PreviousPodId { get; }

        public byte[] TicketKey { get; }

        public bool ChangedPodId => PreviousPodId != 0 && PreviousPodId != PodId;
    }
}
