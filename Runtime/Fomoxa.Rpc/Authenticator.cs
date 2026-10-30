namespace Fomoxa.Rpc
{
    public interface IAuthenticator<TRequest, TResponse>
    {
        UnaryMethod<TRequest, TResponse> Method { get; }

        (Identity Identity, TResponse Response) Authenticate(CallContext context, TRequest request);
    }
}
