using System.Collections.Generic;
using System.Linq;

namespace Fomoxa.Rpc
{
    public static class HealthStatus
    {
        public const string Method = "FRpc.Health";

        public const uint Serving = 1;
        public const uint NotServing = 2;
        public const uint ServiceUnknown = 3;

        public static string? NameOf(uint status) => status switch
        {
            Serving => "SERVING",
            NotServing => "NOT_SERVING",
            ServiceUnknown => "SERVICE_UNKNOWN",
            _ => null,
        };
    }

    public sealed class Health
    {
        private readonly object gate = new object();
        private readonly HashSet<string> services;
        private readonly Dictionary<string, uint> statuses = new Dictionary<string, uint>();

        internal Health(IEnumerable<string> services)
        {
            this.services = new HashSet<string>(services);
        }

        public IReadOnlyCollection<string> Services => services;

        public void Set(string service, uint status)
        {
            lock (gate)
            {
                statuses[service] = status;
            }
        }

        public uint Status(string service)
        {
            lock (gate)
            {
                if (statuses.TryGetValue(service, out uint status))
                {
                    return status;
                }
            }
            return service.Length == 0 || services.Contains(service) ? HealthStatus.Serving : HealthStatus.ServiceUnknown;
        }

        internal static string? ServiceOf(string method)
        {
            int dot = method.LastIndexOf('.');
            if (dot <= 0)
            {
                return null;
            }
            string service = method.Substring(0, dot);
            return service == "FRpc" ? null : service;
        }

        internal static IEnumerable<string> ServicesOf(IEnumerable<Entry> entries) =>
            entries.Where(entry => entry.Handler != null)
                .Select(entry => ServiceOf(entry.Method.Name))
                .Where(service => service != null)
                .Select(service => service!);

        internal Handler Handler() => (_, body) =>
        {
            string service;
            try
            {
                service = Decode(body);
            }
            catch (ModelFormatException error)
            {
                return Answer.Fail(Rpc.Status.InvalidArgument($"{HealthStatus.Method} request does not decode: {error.Message}"));
            }
            return Answer.Respond(new BodyWriter().U32(Status(service)).ToArray());
        };

        private static string Decode(byte[] body)
        {
            var reader = new BodyReader(body);
            return reader.TryString(out string service) ? service : "";
        }
    }
}
