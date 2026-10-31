using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fomoxa.Rpc
{
    public sealed class RpcConfig
    {
        public const int MaxBodyCeiling = 16 * 1024 * 1024 - Wire.HeaderLength;

        public int MaxPending { get; set; } = 65_536;

        public int MaxCallQueue { get; set; } = 64;

        public int MaxConnectionQueue { get; set; } = 1_024;

        public int MaxCtxLength { get; set; } = 64 * 1024;

        public int MaxBodyLength { get; set; } = MaxBodyCeiling;

        public TimeSpan HandlerLimit { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan QueueFullLimit { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan DefaultDeadline { get; set; } = TimeSpan.FromSeconds(5);

        public RpcConfig Clone() => (RpcConfig)MemberwiseClone();
    }

    public enum CallError
    {
        None,
        NotReady,
        UnknownMethod,
        UnknownCall,
        WrongShape,
        TooManyPending,
        Congested,
        BodyTooLarge,
        CtxTooLarge,
        CallIdExhausted,
        DeadlineExceeded,
    }

    public readonly struct CallResult
    {
        private CallResult(CallError error, CallId call)
        {
            Error = error;
            Call = call;
        }

        public CallError Error { get; }

        public CallId Call { get; }

        public bool IsOk => Error == CallError.None;

        public static CallResult Opened(CallId call) => new CallResult(CallError.None, call);

        public static CallResult Failed(CallError error) => new CallResult(error, default);

        public override string ToString() => IsOk ? Call.ToString() : Error.ToString();
    }

    public enum OutcomeKind
    {
        Response,
        Item,
        End,
        Failed,
    }

    public sealed class Outcome
    {
        private Outcome(OutcomeKind kind, CallId call, byte[] body, Status? status)
        {
            Kind = kind;
            Call = call;
            Body = body;
            Status = status;
        }

        public OutcomeKind Kind { get; }

        public CallId Call { get; }

        public byte[] Body { get; }

        public Status? Status { get; }

        public bool TerminatesCall => Kind != OutcomeKind.Item;

        public static Outcome Response(CallId call, byte[] body) => new Outcome(OutcomeKind.Response, call, body, null);

        public static Outcome Item(CallId call, byte[] body) => new Outcome(OutcomeKind.Item, call, body, null);

        public static Outcome End(CallId call) => new Outcome(OutcomeKind.End, call, Array.Empty<byte>(), null);

        public static Outcome Failed(CallId call, Status status) =>
            new Outcome(OutcomeKind.Failed, call, Array.Empty<byte>(), status);

        public override string ToString() => Kind switch
        {
            OutcomeKind.Failed => $"{Call} failed: {Status}",
            OutcomeKind.End => $"{Call} ended",
            _ => $"{Call} {Kind.ToString().ToLowerInvariant()} of {Body.Length} bytes",
        };
    }

    public readonly struct CallOptions
    {
        public static readonly TimeSpan NoDeadline = Timeout.InfiniteTimeSpan;

        public TimeSpan? Deadline { get; init; }

        public byte[]? TraceId { get; init; }

        public CancellationToken Cancellation { get; init; }

        public RetryPolicy? Retry { get; init; }

        public static CallOptions Default => default;

        public static CallOptions WithoutDeadline => new CallOptions { Deadline = NoDeadline };

        public static CallOptions Within(TimeSpan deadline) => new CallOptions { Deadline = deadline };

        public CallOptions Traced(byte[] traceId) =>
            new CallOptions { Deadline = Deadline, TraceId = traceId, Cancellation = Cancellation, Retry = Retry };

        public CallOptions CancelledBy(CancellationToken cancellation) =>
            new CallOptions { Deadline = Deadline, TraceId = TraceId, Cancellation = cancellation, Retry = Retry };

        public CallOptions Retrying(RetryPolicy policy) =>
            new CallOptions { Deadline = Deadline, TraceId = TraceId, Cancellation = Cancellation, Retry = policy };

        internal CallOptions WithBudget(TimeSpan? remaining) =>
            remaining.HasValue
                ? new CallOptions { Deadline = remaining, TraceId = TraceId, Cancellation = Cancellation, Retry = Retry }
                : this;

        internal TimeSpan? Budget(Method method, RpcConfig config) =>
            Deadline == null ? method.Policy.Deadline ?? config.DefaultDeadline
            : Deadline == NoDeadline ? (TimeSpan?)null
            : Deadline;
    }

    public sealed class RpcCore
    {
        private readonly Registry registry;
        private readonly RpcConfig config;
        private readonly CallIdAllocator ids;
        private readonly SortedDictionary<uint, Pending> pending = new SortedDictionary<uint, Pending>();
        private readonly SortedDictionary<uint, Inflight> inflight = new SortedDictionary<uint, Inflight>();
        private readonly Outbox outbox;
        private readonly Commands commands = new Commands();
        private Action<Action>? executor;
        private readonly List<Outcome> outcomes = new List<Outcome>();
        private readonly Chain chain = new Chain();
        private bool ready;
        private bool finished;
        private long seenTicks = long.MinValue;
        private Func<TimeSpan> clock;

        public RpcCore(Registry registry, Role role, RpcConfig? config = null)
        {
            this.registry = registry;
            this.config = (config ?? new RpcConfig()).Clone();
            ids = new CallIdAllocator(role);
            outbox = new Outbox(this.config.MaxCallQueue, this.config.MaxConnectionQueue);
            clock = () => TimeSpan.FromTicks(Interlocked.Read(ref seenTicks));
            if (registry.Authentication is uint authentication)
            {
                chain.Add(new Authorization(registry.Exempt.Append(authentication)));
            }
        }

        public Identity? Identity { get; private set; }

        public long? Connection { get; set; }

        public Role Role => ids.Role;

        public Registry Registry => registry;

        public bool IsReady => ready;

        public int PendingCount => pending.Count;

        public int InflightCount => inflight.Count;

        public int QueuedFrames => outbox.Count;

        public bool HasUnsentFrames => !outbox.IsEmpty;

        public void AddInterceptor(IInterceptor link) => chain.Add(link);

        public void Authenticate(Identity identity) => Identity = identity;

        public void ClearIdentity() => Identity = null;

        public uint? PendingRequestId(CallId call) =>
            pending.TryGetValue(call.Value, out var waiting) ? waiting.RequestId : (uint?)null;

        public TimeSpan? NextDeadline()
        {
            TimeSpan? nearest = null;
            foreach (var waiting in pending.Values)
            {
                nearest = Earlier(nearest, waiting.Deadline);
            }
            foreach (var running in inflight.Values)
            {
                nearest = Earlier(nearest, running.NextExpiry());
            }
            return nearest;
        }

        public bool HasRunnableWork() => inflight.Values.Any(running => running.Task != null);

        public void SetWaker(Action wake) => commands.SetWake(wake);

        public void SetExecutor(Action<Action> executor) => this.executor = executor;

        public void OnReady() => ready = true;

        public void SetClock(Func<TimeSpan> clock) => this.clock = clock;

        private void Observe(TimeSpan now)
        {
            if (now.Ticks > Interlocked.Read(ref seenTicks))
            {
                Interlocked.Exchange(ref seenTicks, now.Ticks);
            }
        }

        public CallResult Call(uint requestId, ReadOnlySpan<byte> body, CallOptions options, TimeSpan now)
        {
            Observe(now);
            if (!ready)
            {
                return CallResult.Failed(CallError.NotReady);
            }
            var method = registry.MethodFor(requestId);
            if (method == null)
            {
                return CallResult.Failed(CallError.UnknownMethod);
            }
            if (body.Length > config.MaxBodyLength)
            {
                return CallResult.Failed(CallError.BodyTooLarge);
            }
            if (pending.Count >= config.MaxPending)
            {
                return CallResult.Failed(CallError.TooManyPending);
            }
            var allocated = ids.Allocate(candidate => pending.ContainsKey(candidate.Value));
            if (!allocated.HasValue)
            {
                return CallResult.Failed(CallError.CallIdExhausted);
            }
            var call = allocated.Value;

            var budget = options.Budget(method, config);
            if (budget.HasValue && budget.Value <= TimeSpan.Zero)
            {
                return CallResult.Failed(CallError.DeadlineExceeded);
            }
            var ctx = new Ctx
            {
                DeadlineMs = budget.HasValue ? MillisOf(budget.Value) : 0,
                TraceId = options.TraceId ?? Array.Empty<byte>(),
            };
            TimeSpan? deadline = ctx.HasDeadline ? now + TimeSpan.FromMilliseconds(ctx.DeadlineMs) : null;

            byte[]? encodedCtx = null;
            if (!ctx.IsEmpty)
            {
                encodedCtx = ctx.Encode();
                if (encodedCtx.Length > config.MaxCtxLength)
                {
                    return CallResult.Failed(CallError.CtxTooLarge);
                }
            }

            var payload = Wire.Encode(new Header(Kind.Request, call), encodedCtx, body);
            if (!outbox.Push(call, new Outgoing(requestId, payload)))
            {
                return CallResult.Failed(CallError.Congested);
            }

            pending.Add(call.Value, new Pending(method, deadline));
            return CallResult.Opened(call);
        }

        public void Cancel(CallId call)
        {
            if (pending.ContainsKey(call.Value))
            {
                AbandonPending(call, Status.Cancelled());
            }
        }

        public bool OnMessage(uint messageId, ReadOnlyMemory<byte> payload, TimeSpan now)
        {
            Observe(now);
            if (registry.IsPlain(messageId))
            {
                return false;
            }
            var error = Wire.Decode(payload, config.MaxCtxLength, out var parsed);
            if (error != WireError.None)
            {
                if (registry.Owns(messageId))
                {
                    RejectUndecodable(payload.Span, error);
                }
                return true;
            }
            if (!ready)
            {
                return true;
            }

            var call = parsed.Header.Call;
            switch (parsed.Header.Kind)
            {
                case Kind.Request:
                    OnRequest(messageId, parsed, now);
                    break;
                case Kind.Response:
                    OnResponse(messageId, call, parsed.Body);
                    break;
                case Kind.Error:
                    OnError(messageId, call, parsed.Body);
                    break;
                case Kind.Item:
                    OnItem(messageId, call, parsed.Body);
                    break;
                case Kind.End:
                    OnEnd(messageId, call);
                    break;
                case Kind.Cancel:
                    OnCancel(messageId, call);
                    break;
            }
            return true;
        }

        public void Tick(TimeSpan now)
        {
            Observe(now);
            ExpirePending(now);
            ApplyCommands(now);
            CheckTasks();
            ExpireInflight(now);
            CheckStalled(now);
            PublishQueue();
        }

        public void Drain(Func<Outgoing, Delivery> send)
        {
            outbox.Drain(send);
            PublishQueue();
        }

        public void OnSessionEnd()
        {
            if (finished)
            {
                return;
            }
            finished = true;
            ready = false;
            foreach (uint call in pending.Keys.ToList())
            {
                SettlePending(new CallId(call), Outcome.Failed(new CallId(call), Status.Unavailable()));
            }
            var ended = Status.Unavailable();
            var running = inflight.Values.ToList();
            inflight.Clear();
            foreach (var abandoned in running)
            {
                Close(abandoned);
                chain.Outbound(abandoned.Entered, abandoned.Context, ended);
            }
            commands.Clear();
            outbox.Clear();
            PublishQueue();
        }

        public List<Outcome> TakeOutcomes()
        {
            var taken = new List<Outcome>(outcomes);
            outcomes.Clear();
            return taken;
        }

        private void OnRequest(uint messageId, Parsed parsed, TimeSpan now)
        {
            var call = parsed.Header.Call;
            if (ids.Role.Allocates(call))
            {
                FailCall(call, Status.InvalidArgument("call id parity belongs to the receiving side"));
                return;
            }
            if (inflight.ContainsKey(call.Value))
            {
                return;
            }
            var entry = registry.EntryFor(messageId);
            if (entry?.Handler == null)
            {
                FailCall(call, Status.Unimplemented(messageId));
                return;
            }
            if (inflight.Count >= config.MaxPending)
            {
                FailCall(call, Status.ResourceExhausted("too many calls in flight"));
                return;
            }

            var method = entry.Method;
            Ctx ctx;
            try
            {
                ctx = parsed.Ctx.HasValue ? Ctx.Decode(parsed.Ctx.Value.Span) : new Ctx();
            }
            catch (ModelFormatException)
            {
                FailCall(call, Status.InvalidArgument("ctx block failed to decode"));
                return;
            }

            TimeSpan? declared = ctx.HasDeadline ? now + TimeSpan.FromMilliseconds(ctx.DeadlineMs) : null;
            TimeSpan? limit = declared;
            if (method.Shape == Shape.Unary)
            {
                var own = now + config.HandlerLimit;
                limit = declared.HasValue && declared.Value < own ? declared : own;
            }

            var context = new CallContext(call, method, now, ctx, Identity) { Deadline = limit, Clock = clock, Connection = Connection };
            int entered = chain.Inbound(context, out var rejection);
            if (rejection != null)
            {
                chain.Outbound(entered, context, rejection);
                FailCall(call, rejection);
                return;
            }

            var state = new CallState();
            inflight.Add(call.Value, new Inflight(method, limit, now, context, state, entered));
            var responder = new Responder(context, state, commands, config.MaxCallQueue, config.MaxConnectionQueue);
            var answer = Invoke(entry.Handler, responder, parsed.Body.ToArray());
            if (answer.Kind == AnswerKind.Run && method.Policy.Parallel && executor != null)
            {
                var job = answer.Job!;
                executor(() => responder.Settle(Answer.Guarded(job)));
                answer = Answer.Later;
            }
            answer = Answer.Inline(answer);
            ApplyCommands(now);
            ApplyAnswer(call, answer);
            CheckStalled(now);
            PublishQueue();
        }

        private void OnResponse(uint messageId, CallId call, ReadOnlyMemory<byte> body)
        {
            if (!pending.TryGetValue(call.Value, out var waiting))
            {
                return;
            }
            if (waiting.Shape.CalleeStreams() || messageId != waiting.ReplyId)
            {
                SettlePending(call, Outcome.Failed(call, Status.InvalidArgument(
                    $"expected response type 0x{waiting.ReplyId:X8}, received 0x{messageId:X8}")));
                return;
            }
            SettlePending(call, Outcome.Response(call, body.ToArray()));
        }

        private void OnError(uint messageId, CallId call, ReadOnlyMemory<byte> body)
        {
            if (!pending.ContainsKey(call.Value))
            {
                return;
            }
            var status = messageId == registry.ErrorId
                ? Status.Decode(body.Span)
                : Status.InvalidArgument($"error frame carried 0x{messageId:X8}, which is not FRpcError");
            SettlePending(call, Outcome.Failed(call, status));
        }

        private void OnItem(uint messageId, CallId call, ReadOnlyMemory<byte> body)
        {
            if (inflight.ContainsKey(call.Value))
            {
                FinishInflight(call, FromTheCaller("ITEM"));
                return;
            }
            if (!pending.TryGetValue(call.Value, out var waiting))
            {
                return;
            }
            if (!waiting.Shape.CalleeStreams() || messageId != waiting.ReplyId)
            {
                AbandonPending(call, Status.InvalidArgument(
                    $"expected item type 0x{waiting.ReplyId:X8}, received 0x{messageId:X8}"));
                return;
            }
            outcomes.Add(Outcome.Item(call, body.ToArray()));
        }

        private void OnEnd(uint messageId, CallId call)
        {
            if (inflight.ContainsKey(call.Value))
            {
                FinishInflight(call, FromTheCaller("END"));
                return;
            }
            if (!pending.TryGetValue(call.Value, out var waiting))
            {
                return;
            }
            if (messageId != registry.VoidId)
            {
                AbandonPending(call, Status.InvalidArgument(
                    $"end frame carried 0x{messageId:X8}, which is not FRpcVoid"));
                return;
            }
            if (!waiting.Shape.CalleeStreams())
            {
                AbandonPending(call, Status.InvalidArgument("END arrived on a call that carries no reply stream"));
                return;
            }
            SettlePending(call, Outcome.End(call));
        }

        private void OnCancel(uint messageId, CallId call)
        {
            if (!inflight.ContainsKey(call.Value))
            {
                return;
            }
            FinishInflight(call, messageId == registry.VoidId
                ? Status.Cancelled()
                : Status.InvalidArgument($"cancel frame carried 0x{messageId:X8}, which is not FRpcVoid"));
        }

        private void RejectUndecodable(ReadOnlySpan<byte> payload, WireError error)
        {
            if (payload.Length < Wire.HeaderLength)
            {
                return;
            }
            FailReceived(Wire.ReadCallId(payload), Status.Internal(Wire.Describe(error, payload)));
        }

        private void ExpirePending(TimeSpan now)
        {
            var expired = pending
                .Where(entry => entry.Value.Deadline.HasValue && now >= entry.Value.Deadline.Value)
                .Select(entry => entry.Key)
                .ToList();
            foreach (uint call in expired)
            {
                AbandonPending(new CallId(call), Status.DeadlineExceeded());
            }
        }

        private void ExpireInflight(TimeSpan now)
        {
            var expired = inflight
                .Where(entry => entry.Value.NextExpiry() is TimeSpan expiry && now >= expiry)
                .Select(entry => entry.Key)
                .ToList();
            foreach (uint call in expired)
            {
                FinishInflight(new CallId(call), Status.DeadlineExceeded());
            }
        }

        private void AbandonPending(CallId call, Status status)
        {
            bool requestQueued = outbox.RequestStillQueued(call);
            SettlePending(call, Outcome.Failed(call, status));
            if (!requestQueued)
            {
                outbox.PushTerminal(call, ControlFrame(call, Kind.Cancel));
            }
        }

        private void SettlePending(CallId call, Outcome outcome)
        {
            if (!pending.Remove(call.Value))
            {
                return;
            }
            outbox.Discard(call);
            outcomes.Add(outcome);
        }

        private void FailReceived(CallId call, Status status)
        {
            if (inflight.ContainsKey(call.Value))
            {
                FinishInflight(call, status);
            }
            else if (pending.ContainsKey(call.Value))
            {
                AbandonPending(call, status);
            }
            else
            {
                FailCall(call, status);
            }
        }

        private void ApplyCommands(TimeSpan now)
        {
            foreach (var command in commands.Take())
            {
                if (!inflight.ContainsKey(command.Call.Value))
                {
                    continue;
                }
                switch (command.Kind)
                {
                    case OpKind.Authenticate:
                        Identity = command.Identity;
                        break;

                    case OpKind.Respond:
                        Complete(command.Call, Kind.Response, command.Body);
                        break;

                    case OpKind.End:
                        Complete(command.Call, Kind.End, null);
                        break;

                    case OpKind.Item:
                        SendItem(command.Call, command.Body!, now);
                        break;

                    default:
                        FinishInflight(command.Call, command.Status ?? Status.Internal("handler failed"));
                        break;
                }
            }
        }

        private void ApplyAnswer(CallId call, Answer answer)
        {
            if (!inflight.TryGetValue(call.Value, out var running) || running.State.IsFinished)
            {
                return;
            }
            switch (answer.Kind)
            {
                case AnswerKind.Later:
                    return;

                case AnswerKind.Run:
                    ApplyAnswer(call, Answer.Inline(answer));
                    return;

                case AnswerKind.Await:
                    running.Task = answer.Task;
                    CheckTask(call, running);
                    return;

                case AnswerKind.Fail:
                    FinishInflight(call, answer.Status ?? Status.Internal("handler failed"));
                    return;

                default:
                    Answered(call, running, answer.Body ?? Array.Empty<byte>());
                    return;
            }
        }

        private void Answered(CallId call, Inflight running, byte[] body)
        {
            if (running.Shape.CalleeStreams())
            {
                FinishInflight(call, Status.Internal("a server stream answered with a unary response"));
                return;
            }
            Complete(call, Kind.Response, body);
        }

        private void CheckTasks()
        {
            foreach (var entry in inflight.Where(entry => entry.Value.Task != null).ToList())
            {
                CheckTask(new CallId(entry.Key), entry.Value);
            }
        }

        private void CheckTask(CallId call, Inflight running)
        {
            var task = running.Task;
            if (task == null || !task.IsCompleted)
            {
                return;
            }
            running.Task = null;
            if (running.State.IsFinished)
            {
                return;
            }
            if (task.Status == TaskStatus.RanToCompletion)
            {
                Answered(call, running, task.Result);
                return;
            }
            FinishInflight(call, task.Exception?.InnerException is RpcException refused
                ? refused.Status
                : Status.Internal(task.Exception?.InnerException?.Message ?? "the handler task was cancelled"));
        }

        private void SendItem(CallId call, byte[] body, TimeSpan now)
        {
            if (!inflight.TryGetValue(call.Value, out var running))
            {
                return;
            }
            running.LastItem = now;
            outbox.Push(call, BodyFrame(call, Kind.Item, running.ReplyId, body));
        }

        private void Complete(CallId call, Kind kind, byte[]? body)
        {
            if (!inflight.Remove(call.Value, out var finished))
            {
                return;
            }
            Close(finished);
            outbox.PushTerminal(call, body == null
                ? ControlFrame(call, kind)
                : BodyFrame(call, kind, finished.ReplyId, body));
            chain.Outbound(finished.Entered, finished.Context, null);
        }

        private void CheckStalled(TimeSpan now)
        {
            var stalled = new List<uint>();
            foreach (var entry in inflight)
            {
                if (!outbox.IsFullFor(new CallId(entry.Key)))
                {
                    entry.Value.FullSince = null;
                    continue;
                }
                entry.Value.FullSince ??= now;
                if (now - entry.Value.FullSince.Value >= config.QueueFullLimit)
                {
                    stalled.Add(entry.Key);
                }
            }
            foreach (uint call in stalled)
            {
                FinishInflight(new CallId(call), Status.ResourceExhausted("the peer stopped consuming this call"));
            }
        }

        private void PublishQueue()
        {
            commands.PublishTotal(outbox.Count);
            foreach (var entry in inflight)
            {
                entry.Value.State.PublishQueued(outbox.QueuedFor(new CallId(entry.Key)));
            }
        }

        private void FinishInflight(CallId call, Status status)
        {
            inflight.Remove(call.Value, out var finished);
            if (finished != null)
            {
                Close(finished);
            }
            outbox.Discard(call);
            var sent = FailCall(call, status);
            if (finished != null)
            {
                chain.Outbound(finished.Entered, finished.Context, sent);
            }
        }

        private Status FailCall(CallId call, Status status)
        {
            var sent = Sendable(status);
            outbox.PushTerminal(call, BodyFrame(call, Kind.Error, registry.ErrorId, sent.Encode()));
            return sent;
        }

        private Status Sendable(Status status) =>
            status.HasUnencodedDetail
                ? Status.Internal($"{status.Name} \"{status.Message}\" carried a detail model that no detail codec of the method encodes")
                : status.DetailsId == 0 || registry.DeclaresDetails(status.DetailsId)
                    ? status
                    : Status.Internal(
                        $"{status.Name} \"{status.Message}\" carried details model 0x{status.DetailsId:X8}, which the registry does not declare");

        private Outgoing BodyFrame(CallId call, Kind kind, uint messageId, ReadOnlySpan<byte> body) =>
            new Outgoing(messageId, Wire.Encode(new Header(kind, call), null, body));

        private Outgoing ControlFrame(CallId call, Kind kind) =>
            new Outgoing(registry.VoidId, Wire.Encode(new Header(kind, call), null, ReadOnlySpan<byte>.Empty));

        private static Answer Invoke(Handler handler, Responder responder, byte[] body)
        {
            try
            {
                return handler(responder, body);
            }
            catch (RpcException refused)
            {
                return Answer.Fail(refused.Status);
            }
            catch (Exception crashed)
            {
                return Answer.Fail(Status.Internal(crashed.Message));
            }
        }

        private static void Close(Inflight running)
        {
            running.State.Close();
            running.Context.Cancel();
        }

        private static Status FromTheCaller(string kind) =>
            Status.InvalidArgument($"{kind} came from the caller; only the callee streams");

        private static TimeSpan? Earlier(TimeSpan? current, TimeSpan? candidate)
        {
            if (!candidate.HasValue)
            {
                return current;
            }
            return !current.HasValue || candidate.Value < current.Value ? candidate : current;
        }

        private static uint MillisOf(TimeSpan budget)
        {
            if (budget <= TimeSpan.Zero)
            {
                return 1;
            }
            double millis = Math.Max(1, Math.Floor(budget.TotalMilliseconds));
            return millis >= uint.MaxValue ? uint.MaxValue : (uint)millis;
        }

        private sealed class Pending
        {
            public Pending(Method method, TimeSpan? deadline)
            {
                RequestId = method.RequestId;
                ReplyId = method.ReplyId;
                Shape = method.Shape;
                Deadline = deadline;
            }

            public uint RequestId { get; }

            public uint ReplyId { get; }

            public Shape Shape { get; }

            public TimeSpan? Deadline { get; }
        }

        private sealed class Inflight
        {
            public Inflight(Method method, TimeSpan? limit, TimeSpan now, CallContext context, CallState state, int entered)
            {
                ReplyId = method.ReplyId;
                Shape = method.Shape;
                Limit = limit;
                IdleLimit = method.Shape == Shape.Unary ? null : method.Policy.Idle;
                LastItem = now;
                Context = context;
                State = state;
                Entered = entered;
            }

            public uint ReplyId { get; }

            public Shape Shape { get; }

            public TimeSpan? Limit { get; }

            public TimeSpan? IdleLimit { get; }

            public TimeSpan LastItem { get; set; }

            public TimeSpan? FullSince { get; set; }

            public CallContext Context { get; }

            public CallState State { get; }

            public Task<byte[]>? Task { get; set; }

            public int Entered { get; }

            public TimeSpan? NextExpiry()
            {
                TimeSpan? idle = IdleLimit.HasValue ? LastItem + IdleLimit.Value : null;
                return Earlier(Limit, idle);
            }
        }
    }
}
