using System;
using System.Threading.Tasks;

namespace Fomoxa.Rpc.Net
{
    internal sealed class Retrying<TRequest, TResponse>
    {
        private readonly RpcConnection connection;
        private readonly UnaryMethod<TRequest, TResponse> method;
        private readonly byte[] body;
        private readonly CallOptions options;
        private readonly RetryPolicy policy;
        private readonly RetryState state;
        private readonly TaskCompletionSource<TResponse> done;

        public Retrying(
            RpcConnection connection,
            UnaryMethod<TRequest, TResponse> method,
            byte[] body,
            CallOptions options,
            RetryPolicy policy,
            TaskCompletionSource<TResponse> done)
        {
            this.connection = connection;
            this.method = method;
            this.body = body;
            this.options = options;
            this.policy = policy;
            this.done = done;
            state = new RetryState(connection.Now, options.Budget(method.Method, connection.Config));
        }

        public void Start() => Send(options, TimeSpan.Zero);

        private void Send(CallOptions chosen, TimeSpan delay) =>
            connection.Start(method.Method.RequestId, body, chosen, delay, call => connection.Defer(() => Arrived(call)));

        private void Arrived(RpcCall call)
        {
            if (call.TryNext(out var next))
            {
                if (next.Outcome?.Kind == OutcomeKind.Response)
                {
                    Typed.Settle(done, next, method, options.Cancellation);
                    return;
                }
                var status = next.Outcome == null
                    ? Typed.Refused(next.Refusal)
                    : next.Outcome.Status ?? Status.Internal($"the call ended with {next.Outcome.Kind}");
                Failed(status, () => Typed.Settle(done, next, method, options.Cancellation));
            }
            else if (call.IsFinished)
            {
                Failed(Status.Unavailable(), () => done.TrySetException(new RpcException(Status.Unavailable())));
            }
        }

        private void Failed(Status status, Action giveUp)
        {
            if (options.Cancellation.IsCancellationRequested)
            {
                done.TrySetCanceled(options.Cancellation);
                return;
            }
            var decision = state.OnFailure(policy, status, method.Method, connection.Now);
            if (!decision.ShouldRetry)
            {
                giveUp();
                return;
            }
            var at = decision.RetryAt!.Value;
            if (!connection.HasEnded)
            {
                Again(at);
                return;
            }
            if (!connection.CanReopen)
            {
                giveUp();
                return;
            }
            connection.Reopen(reopened =>
            {
                if (options.Cancellation.IsCancellationRequested)
                {
                    done.TrySetCanceled(options.Cancellation);
                }
                else if (reopened)
                {
                    Again(at);
                }
                else
                {
                    giveUp();
                }
            });
        }

        private void Again(TimeSpan at)
        {
            var delay = at - connection.Now;
            Send(options.WithBudget(state.Remaining(at)), delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        }
    }
}
