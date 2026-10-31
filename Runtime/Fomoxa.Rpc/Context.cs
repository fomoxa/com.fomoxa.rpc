using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fomoxa.Rpc
{
    public sealed class CallContext
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        public CallContext(CallId call, Method method, TimeSpan now, Ctx ctx, Identity? session)
        {
            Call = call;
            Method = method;
            Now = now;
            Ctx = ctx;
            Session = session;
            Permissions = new SortedSet<string>(session?.Permissions ?? Array.Empty<string>());
            Clock = () => now;
        }

        public CallId Call { get; }

        public Method Method { get; }

        public TimeSpan Now { get; }

        public Func<TimeSpan> Clock { get; init; }

        public TimeSpan? Deadline { get; set; }

        public Ctx Ctx { get; set; }

        public Identity? Session { get; }

        public long? Connection { get; init; }

        public SortedSet<string> Permissions { get; set; }

        public CancellationToken Cancellation => cancellation.Token;

        public bool Permits(string permission) => Permissions.Contains(permission);

        public uint? RemainingMs(TimeSpan now)
        {
            if (!Deadline.HasValue)
            {
                return null;
            }
            var left = Deadline.Value - now;
            if (left <= TimeSpan.Zero)
            {
                return 0;
            }
            return (uint)Math.Min(uint.MaxValue, (long)left.TotalMilliseconds);
        }

        public CallOptions Onward() =>
            Onward(Clock()) ?? CallOptions.Within(TimeSpan.Zero).Traced(Ctx.TraceId);

        public CallOptions? Onward(TimeSpan now)
        {
            uint? remaining = RemainingMs(now);
            if (remaining == 0)
            {
                return null;
            }
            var options = remaining.HasValue
                ? CallOptions.Within(TimeSpan.FromMilliseconds(remaining.Value))
                : CallOptions.WithoutDeadline;
            return options.Traced(Ctx.TraceId);
        }

        internal void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (AggregateException)
            {
            }
        }
    }

    public enum AnswerKind
    {
        Respond,
        Fail,
        Later,
        Await,
        Run,
    }

    public readonly struct Answer
    {
        private Answer(AnswerKind kind, byte[]? body, Status? status, Task<byte[]>? task, Func<Answer>? job = null)
        {
            Kind = kind;
            Body = body;
            Status = status;
            Task = task;
            Job = job;
        }

        public AnswerKind Kind { get; }

        public byte[]? Body { get; }

        public Status? Status { get; }

        public Task<byte[]>? Task { get; }

        public Func<Answer>? Job { get; }

        public static Answer Later => new Answer(AnswerKind.Later, null, null, null);

        public static Answer Respond(byte[] body) => new Answer(AnswerKind.Respond, body, null, null);

        public static Answer Fail(Status status) => new Answer(AnswerKind.Fail, null, status, null);

        public static Answer Await(Task<byte[]> task) => new Answer(AnswerKind.Await, null, null, task);

        public static Answer Run(Func<Answer> job) => new Answer(AnswerKind.Run, null, null, null, job);

        internal static Answer Guarded(Func<Answer> job)
        {
            try
            {
                return job();
            }
            catch (RpcException refused)
            {
                return Fail(refused.Status);
            }
            catch (Exception crashed)
            {
                return Fail(Rpc.Status.Internal(crashed.Message));
            }
        }

        internal static Answer Inline(Answer answer)
        {
            while (answer.Kind == AnswerKind.Run)
            {
                answer = Guarded(answer.Job!);
            }
            return answer;
        }
    }

    public enum SendResult
    {
        Sent,
        Congested,
        Ended,
    }

    public delegate Answer Handler(Responder responder, byte[] body);

    internal enum OpKind
    {
        Authenticate,
        Respond,
        Fail,
        Item,
        End,
    }

    internal readonly struct Command
    {
        public Command(CallId call, OpKind kind, byte[]? body, Status? status, Identity? identity = null)
        {
            Call = call;
            Kind = kind;
            Body = body;
            Status = status;
            Identity = identity;
        }

        public CallId Call { get; }

        public OpKind Kind { get; }

        public byte[]? Body { get; }

        public Status? Status { get; }

        public Identity? Identity { get; }
    }

    internal sealed class CallState
    {
        private int finished;
        private int queued;

        public bool IsFinished => Volatile.Read(ref finished) != 0;

        public int Queued => Volatile.Read(ref queued);

        public bool TryFinish() => Interlocked.Exchange(ref finished, 1) == 0;

        public void Close() => Volatile.Write(ref finished, 1);

        public void AddQueued() => Interlocked.Increment(ref queued);

        public void PublishQueued(int count) => Volatile.Write(ref queued, count);
    }

    internal sealed class Commands
    {
        private readonly ConcurrentQueue<Command> queue = new ConcurrentQueue<Command>();
        private int queuedTotal;
        private Action? wake;

        public int QueuedTotal => Volatile.Read(ref queuedTotal);

        public void Push(Command command)
        {
            queue.Enqueue(command);
            Volatile.Read(ref wake)?.Invoke();
        }

        public void SetWake(Action wake) => Volatile.Write(ref this.wake, wake);

        public void AddQueued() => Interlocked.Increment(ref queuedTotal);

        public void PublishTotal(int total) => Volatile.Write(ref queuedTotal, total);

        public List<Command> Take()
        {
            var taken = new List<Command>();
            while (queue.TryDequeue(out var command))
            {
                taken.Add(command);
            }
            return taken;
        }

        public void Clear()
        {
            while (queue.TryDequeue(out _))
            {
            }
        }
    }

    public sealed class Responder
    {
        private readonly CallState state;
        private readonly Commands commands;
        private readonly int perCall;
        private readonly int total;

        internal Responder(CallContext context, CallState state, Commands commands, int perCall, int total)
        {
            Context = context;
            this.state = state;
            this.commands = commands;
            this.perCall = perCall;
            this.total = total;
        }

        public CallContext Context { get; }

        public CancellationToken Cancellation => Context.Cancellation;

        public bool IsAlive => !state.IsFinished;

        public bool Respond(byte[] body)
        {
            if (Context.Method.Shape.CalleeStreams())
            {
                Finish(OpKind.Fail, null, Status.Internal("a server stream answered with a unary response"));
                return false;
            }
            return Finish(OpKind.Respond, body, null);
        }

        public bool Fail(Status status) => Finish(OpKind.Fail, null, status);

        public SendResult Send(byte[] item)
        {
            if (!Context.Method.Shape.CalleeStreams())
            {
                Finish(OpKind.Fail, null, Status.Internal("a unary call sent a stream item"));
                return SendResult.Ended;
            }
            if (!IsAlive)
            {
                return SendResult.Ended;
            }
            if (state.Queued >= perCall || commands.QueuedTotal >= total)
            {
                return SendResult.Congested;
            }
            state.AddQueued();
            commands.AddQueued();
            commands.Push(new Command(Context.Call, OpKind.Item, item, null));
            return SendResult.Sent;
        }

        public bool End()
        {
            if (!Context.Method.Shape.CalleeStreams())
            {
                Finish(OpKind.Fail, null, Status.Internal("a unary call ended like a stream"));
                return false;
            }
            return Finish(OpKind.End, null, null);
        }

        internal void Settle(Answer answer)
        {
            answer = Answer.Inline(answer);
            switch (answer.Kind)
            {
                case AnswerKind.Respond:
                    Respond(answer.Body ?? Array.Empty<byte>());
                    return;

                case AnswerKind.Fail:
                    Fail(answer.Status ?? Status.Internal("handler failed"));
                    return;

                case AnswerKind.Await:
                    answer.Task!.ContinueWith(
                        done => Settle(Answered(done)),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
            }
        }

        private static Answer Answered(Task<byte[]> done)
        {
            if (done.Status == TaskStatus.RanToCompletion)
            {
                return Answer.Respond(done.Result);
            }
            return done.Exception?.InnerException is RpcException refused
                ? Answer.Fail(refused.Status)
                : Answer.Fail(Status.Internal(done.Exception?.InnerException?.Message ?? "the handler task was cancelled"));
        }

        internal void Authenticate(Identity identity) =>
            commands.Push(new Command(Context.Call, OpKind.Authenticate, null, null, identity));

        private bool Finish(OpKind kind, byte[]? body, Status? status)
        {
            if (!state.TryFinish())
            {
                return false;
            }
            commands.Push(new Command(Context.Call, kind, body, status));
            return true;
        }
    }

    public sealed class Reply<T>
    {
        private readonly Responder responder;
        private readonly IRpcCodec<T> codec;
        private readonly IReadOnlyList<DetailCodec> details;

        internal Reply(Responder responder, IRpcCodec<T> codec, IReadOnlyList<DetailCodec> details)
        {
            this.responder = responder;
            this.codec = codec;
            this.details = details;
        }

        public CallContext Context => responder.Context;

        public CancellationToken Cancellation => responder.Cancellation;

        public bool IsAlive => responder.IsAlive;

        public CallOptions Onward() => responder.Context.Onward();

        public bool Respond(T value) => responder.Respond(codec.Encode(value));

        public bool Fail(Status status) => responder.Fail(status.EncodedWith(details));
    }

    public sealed class ItemStream<T>
    {
        private readonly Responder responder;
        private readonly IRpcCodec<T> codec;
        private readonly IReadOnlyList<DetailCodec> details;

        internal ItemStream(Responder responder, IRpcCodec<T> codec, IReadOnlyList<DetailCodec> details)
        {
            this.responder = responder;
            this.codec = codec;
            this.details = details;
        }

        public CallContext Context => responder.Context;

        public CancellationToken Cancellation => responder.Cancellation;

        public bool IsAlive => responder.IsAlive;

        public SendResult Send(T item) => responder.Send(codec.Encode(item));

        public CallOptions Onward() => responder.Context.Onward();

        public bool End() => responder.End();

        public bool Fail(Status status) => responder.Fail(status.EncodedWith(details));
    }
}
