namespace Fomoxa.Rpc.Net
{
    public sealed class Credential
    {
        private Credential(Method method, byte[] body)
        {
            Method = method;
            Body = body;
        }

        public Method Method { get; }

        internal byte[] Body { get; }

        public static Credential Of<TRequest, TResponse>(UnaryMethod<TRequest, TResponse> method, TRequest request) =>
            new Credential(method.Method, method.Request.Encode(request));
    }
}
