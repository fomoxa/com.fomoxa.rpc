using System.Collections.Generic;

namespace Fomoxa.Rpc.Net
{
    public sealed class Credential
    {
        private Credential(Method method, byte[] body, IReadOnlyList<DetailCodec> details)
        {
            Method = method;
            Body = body;
            Details = details;
        }

        public Method Method { get; }

        internal byte[] Body { get; }

        internal IReadOnlyList<DetailCodec> Details { get; }

        public static Credential Of<TRequest, TResponse>(UnaryMethod<TRequest, TResponse> method, TRequest request) =>
            new Credential(method.Method, method.Request.Encode(request), method.Details);
    }
}
