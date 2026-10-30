using System;
using System.Collections.Generic;

namespace Fomoxa.Rpc
{
    public readonly struct Decision
    {
        private Decision(Status? rejection)
        {
            Rejection = rejection;
        }

        public Status? Rejection { get; }

        public static Decision Continue => new Decision(null);

        public static Decision Reject(Status status) => new Decision(status);
    }

    public interface IInterceptor
    {
        Decision Inbound(CallContext context) => Decision.Continue;

        void Outbound(CallContext context, Status? status)
        {
        }
    }

    public sealed class Chain
    {
        private readonly List<IInterceptor> links = new List<IInterceptor>();

        public int Count => links.Count;

        public void Add(IInterceptor link) => links.Add(link);

        public int Inbound(CallContext context, out Status? rejection) =>
            Run(context, (link, entering) => link.Inbound(entering), out rejection);

        public void Outbound(int entered, CallContext context, Status? status)
        {
            for (int index = Math.Min(entered, links.Count) - 1; index >= 0; index--)
            {
                links[index].Outbound(context, status);
            }
        }

        private int Run(CallContext context, Func<IInterceptor, CallContext, Decision> step, out Status? rejection)
        {
            for (int index = 0; index < links.Count; index++)
            {
                var decision = step(links[index], context);
                if (decision.Rejection != null)
                {
                    rejection = decision.Rejection;
                    return index;
                }
            }
            rejection = null;
            return links.Count;
        }
    }

    public sealed class Authorization : IInterceptor
    {
        private readonly HashSet<uint> exempt;

        public Authorization()
        {
            exempt = new HashSet<uint>();
        }

        internal Authorization(IEnumerable<uint> exempt)
        {
            this.exempt = new HashSet<uint>(exempt);
        }

        public Authorization Exempting(uint requestId)
        {
            exempt.Add(requestId);
            return this;
        }

        public Decision Inbound(CallContext context)
        {
            var session = context.Session;
            if (session == null)
            {
                return exempt.Contains(context.Method.RequestId)
                    ? Decision.Continue
                    : Decision.Reject(Status.Unauthenticated("the session has no identity"));
            }

            context.Permissions = new SortedSet<string>(session.Permissions);

            string? required = context.Method.Policy.Requires;
            if (required != null && !context.Permits(required))
            {
                return Decision.Reject(Status.PermissionDenied($"{context.Method.Name} requires {required}"));
            }
            return Decision.Continue;
        }
    }
}
