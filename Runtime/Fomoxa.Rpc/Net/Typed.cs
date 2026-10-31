using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fomoxa.Rpc.Net
{
    internal static class Typed
    {
        public static void Settle<TRequest, TResponse>(
            TaskCompletionSource<TResponse> done,
            CallEvent next,
            UnaryMethod<TRequest, TResponse> method,
            CancellationToken cancellation)
        {
            var outcome = next.Outcome;
            if (outcome == null)
            {
                done.TrySetException(new RpcException(Refused(next.Refusal)));
                return;
            }
            if (outcome.Kind != OutcomeKind.Response)
            {
                var failure = Failure(outcome, method.Details, cancellation, default);
                if (failure is OperationCanceledException)
                {
                    done.TrySetCanceled(cancellation);
                    return;
                }
                done.TrySetException(failure);
                return;
            }
            if (TryDecode(method.Response, outcome.Body, out var response, out var error))
            {
                done.TrySetResult(response);
                return;
            }
            done.TrySetException(new RpcException(Undecodable(method.Method, "response", error)));
        }

        public static Exception Failure(
            Outcome outcome,
            IReadOnlyList<DetailCodec> details,
            CancellationToken cancellation,
            CancellationToken enumeration)
        {
            var status = outcome.Status ?? Status.Internal($"the call ended with {outcome.Kind}");
            if (status.Code == StatusCode.Cancelled)
            {
                if (cancellation.IsCancellationRequested)
                {
                    return new OperationCanceledException(cancellation);
                }
                if (enumeration.IsCancellationRequested)
                {
                    return new OperationCanceledException(enumeration);
                }
            }
            return new RpcException(Read(status, details));
        }

        public static Status Read(Status status, IReadOnlyList<DetailCodec> details)
        {
            if (status.DetailsId == 0)
            {
                return status;
            }
            foreach (var codec in details)
            {
                if (codec.MessageId != status.DetailsId)
                {
                    continue;
                }
                try
                {
                    return status with { Details = codec.Decode(status.RawDetails) };
                }
                catch (Exception)
                {
                    return status;
                }
            }
            return status;
        }

        public static bool TryDecode<T>(IRpcCodec<T> codec, byte[] body, out T value, out string error)
        {
            try
            {
                value = codec.Decode(body);
                error = "";
                return true;
            }
            catch (Exception failed)
            {
                value = default!;
                error = failed.Message;
                return false;
            }
        }

        public static Status Undecodable(Method method, string kind, string error) =>
            Status.InvalidArgument($"{method.Name} {kind} does not decode: {error}");

        public static Status Refused(CallError refusal)
        {
            uint code = refusal switch
            {
                CallError.NotReady => StatusCode.Unavailable,
                CallError.TooManyPending => StatusCode.ResourceExhausted,
                CallError.Congested => StatusCode.ResourceExhausted,
                CallError.BodyTooLarge => StatusCode.ResourceExhausted,
                CallError.CtxTooLarge => StatusCode.ResourceExhausted,
                CallError.CallIdExhausted => StatusCode.ResourceExhausted,
                CallError.DeadlineExceeded => StatusCode.DeadlineExceeded,
                _ => StatusCode.Internal,
            };
            return new Status(code, $"the call was refused before sending: {refusal}");
        }
    }

    internal sealed class Signal
    {
        private readonly object gate = new object();
        private readonly bool inline;
        private TaskCompletionSource<bool>? waiting;
        private bool raised;

        public Signal(bool inline)
        {
            this.inline = inline;
        }

        public Task Wait()
        {
            lock (gate)
            {
                if (raised)
                {
                    raised = false;
                    return Task.CompletedTask;
                }
                waiting = new TaskCompletionSource<bool>(
                    inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
                return waiting.Task;
            }
        }

        public void Raise()
        {
            TaskCompletionSource<bool>? woken;
            lock (gate)
            {
                woken = waiting;
                waiting = null;
                raised = woken == null;
            }
            woken?.TrySetResult(true);
        }
    }
}
