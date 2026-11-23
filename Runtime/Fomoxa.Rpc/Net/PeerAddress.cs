using System.Net;

namespace Fomoxa.Rpc.Net
{
    public interface IPeerAddress
    {
        EndPoint? PeerAddress { get; }
    }
}
