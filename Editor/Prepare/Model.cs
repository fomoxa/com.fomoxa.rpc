using System.Collections.Generic;
using System.Linq;

namespace Fomoxa.Rpc.Prepare
{
    public enum Kind
    {
        Unary,
        ServerStream,
    }

    public sealed record Method(
        string Name,
        Kind Kind,
        string? Model,
        IReadOnlyList<string> Requests,
        string? Response,
        string? Item,
        IReadOnlyList<string> Details)
    {
        public IEnumerable<string> Types =>
            Requests.Concat(new[] { Response, Item }.Where(type => type != null).Select(type => type!)).Concat(Details);

        public bool HasNoModelParameter => Requests.Count == 0;
    }

    public sealed record Service(string Name, string? Namespace, string Source, IReadOnlyList<Method> Methods);

    public sealed record EmptyModel(string Name, string Service, string Method, string? Namespace);
}
