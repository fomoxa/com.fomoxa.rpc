using System;

namespace Fomoxa.Rpc
{
    public enum Role
    {
        Client,
        Server,
    }

    public static class RoleExtensions
    {
        public static bool Allocates(this Role role, CallId call) =>
            role == Role.Client ? call.IsEven : !call.IsEven;
    }

    public sealed class CallIdAllocator
    {
        private uint next;

        public CallIdAllocator(Role role)
        {
            Role = role;
            next = role == Role.Client ? 0u : 1u;
        }

        public Role Role { get; }

        public CallId? Allocate(Func<CallId, bool> inUse)
        {
            for (uint attempt = 0; attempt <= uint.MaxValue / 2; attempt++)
            {
                var candidate = new CallId(next);
                next = unchecked(next + 2);
                if (!inUse(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }
    }
}
