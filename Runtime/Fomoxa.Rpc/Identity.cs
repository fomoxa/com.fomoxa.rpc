using System.Collections.Generic;
using System.Linq;

namespace Fomoxa.Rpc
{
    public sealed class Identity
    {
        public Identity(string principal, IEnumerable<string>? permissions = null)
        {
            Principal = principal;
            Permissions = new SortedSet<string>(permissions ?? Enumerable.Empty<string>());
        }

        public string Principal { get; }

        public IReadOnlyCollection<string> Permissions { get; }

        public bool Permits(string permission) => Permissions.Contains(permission);

        public override string ToString() => Principal;
    }
}
