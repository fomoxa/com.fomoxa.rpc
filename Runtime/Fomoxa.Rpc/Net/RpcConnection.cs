using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Fomoxa.Net;
using Fomoxa.Net.Transports;

namespace Fomoxa.Rpc.Net
{
    public enum ConnectFailure
    {
        Refused,
        Closed,
        TimedOut,
        Rejected,
    }

    public sealed class ConnectException : Exception
    {
        public ConnectException(ConnectFailure failure, HandshakeFailure? refusal, string message) : base(message)
        {
            Failure = failure;
            Refusal = refusal;
        }

        public ConnectException(Status rejection)
            : base($"the authentication was rejected: {rejection}")
        {
            Failure = ConnectFailure.Rejected;
            Status = rejection;
        }

        public ConnectFailure Failure { get; }

        public HandshakeFailure? Refusal { get; }

        public Status? Status { get; }
    }

    public sealed class ConnectOptions
    {
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

        public SessionConfig? Net { get; init; }

        public RpcConfig? Config { get; init; }

        public WaitPolicy Policy { get; init; } = WaitPolicy.Default;

        public Action<PlainMessage>? Plain { get; init; }

        public Credential? Credential { get; init; }

        public RetryPolicy? Retry { get; init; }

        public Func<ITransport?>? Reconnect { get; init; }
    }

    public sealed class CallEvent
    {
        private CallEvent(Outcome? outcome, CallError refusal)
        {
            Outcome = outcome;
            Refusal = refusal;
        }

        public Outcome? Outcome { get; }

        public CallError Refusal { get; }

        public bool IsRefused => Outcome == null;

        internal static CallEvent Of(Outcome outcome) => new CallEvent(outcome, CallError.None);

        internal static CallEvent Refused(CallError refusal) => new CallEvent(null, refusal);

        public override string ToString() => Outcome?.ToString() ?? $"refused: {Refusal}";
    }

    public sealed class RpcCall
    {
        private readonly BlockingCollection<CallEvent> events = new BlockingCollection<CallEvent>();
        private readonly long token;
        private readonly WeakReference<Inbox> inbox;
        private readonly Action<RpcCall>? watcher;
        private CancellationTokenRegistration cancellation;

        internal RpcCall(long token, Inbox inbox, Action<RpcCall>? watcher)
        {
            this.token = token;
            this.inbox = new WeakReference<Inbox>(inbox);
            this.watcher = watcher;
        }

        public bool IsFinished => events.IsCompleted;

        public bool TryNext(out CallEvent next)
        {
            if (events.TryTake(out var taken))
            {
                next = taken;
                return true;
            }
            next = null!;
            return false;
        }

        public CallEvent? Next() => events.TryTake(out var next, Timeout.Infinite) ? next : null;

        public CallEvent? Next(TimeSpan timeout)
        {
            if (events.TryTake(out var next, timeout))
            {
                return next;
            }
            if (events.IsCompleted)
            {
                return null;
            }
            throw new TimeoutException($"no outcome within {timeout}");
        }

        public void Cancel()
        {
            if (inbox.TryGetTarget(out var target))
            {
                target.Push(new CancelOrder(token));
            }
        }

        internal void CancelWhen(CancellationToken token)
        {
            if (token.CanBeCanceled)
            {
                cancellation = token.Register(Cancel);
            }
        }

        internal void Deliver(Outcome outcome)
        {
            if (events.IsAddingCompleted)
            {
                return;
            }
            events.Add(CallEvent.Of(outcome));
            if (outcome.TerminatesCall)
            {
                End();
            }
            watcher?.Invoke(this);
        }

        internal void Refuse(CallError refusal)
        {
            if (events.IsAddingCompleted)
            {
                return;
            }
            events.Add(CallEvent.Refused(refusal));
            End();
            watcher?.Invoke(this);
        }

        internal void Finish()
        {
            if (events.IsAddingCompleted)
            {
                return;
            }
            End();
            watcher?.Invoke(this);
        }

        private void End()
        {
            events.CompleteAdding();
            cancellation.Dispose();
        }
    }

    public sealed class RpcConnection : IDisposable, IAsyncDisposable
    {
        private readonly Schema schema;
        private readonly Registry registry;
        private readonly ConnectOptions options;
        private readonly bool driven;
        private readonly RpcConfig config;
        private readonly object gate = new object();
        private readonly Task<RpcConnection> opened;
        private Session current;
        private Session? incoming;
        private List<Action<bool>>? reopening;
        private bool closed;
        private bool ticking;
        private long nextToken;
        private TimeSpan lastTick = MonotonicClock.Now;
        private List<Action> deferred = new List<Action>();

        private RpcConnection(Schema schema, Registry registry, ConnectOptions options, bool driven, Func<RpcConnection, Session> first)
        {
            this.schema = schema;
            this.registry = registry;
            this.options = options;
            this.driven = driven;
            config = options.Config ?? new RpcConfig();
            current = first(this);
            opened = current.Ready.Task;
        }

        public bool IsOpen
        {
            get
            {
                var session = Current;
                return session.Driver?.Phase == Phase.Ready && !session.Inbox.IsClosed;
            }
        }

        public bool IsDriven => driven;

        public Task<RpcConnection> Opened => opened;

        internal TimeSpan Now => driven ? lastTick : MonotonicClock.Now;

        internal RpcConfig Config => config;

        internal RetryPolicy Retry => options.Retry ?? RetryPolicy.Never;

        internal bool HasEnded => Current.HasEnded;

        internal bool CanReopen => options.Reconnect != null;

        private Session Current
        {
            get
            {
                lock (gate)
                {
                    return current;
                }
            }
        }

        public static Task<RpcConnection> Connect(string address, Schema schema, Registry registry, ConnectOptions? options = null)
        {
            options ??= new ConnectOptions();
            var (host, port) = Address.Split(address);
            var timeout = options.Timeout;
            return Open(() => RpcSession.ConnectTcp(host, port, timeout), schema, registry, options);
        }

        public static Task<RpcConnection> Open(ITransport transport, Schema schema, Registry registry, ConnectOptions? options = null) =>
            Open(() => transport, schema, registry, options ?? new ConnectOptions());

        public static RpcConnection Driven(ITransport transport, Schema schema, Registry registry, ConnectOptions? options = null) =>
            new RpcConnection(schema, registry, options ?? new ConnectOptions(), true, connection => connection.DrivenSession(transport, false));

        public RpcCall Call(uint requestId, ReadOnlySpan<byte> body, CallOptions options) =>
            Start(requestId, body, options, TimeSpan.Zero, null);

        public Task<TResponse> Unary<TRequest, TResponse>(
            UnaryMethod<TRequest, TResponse> method,
            TRequest request,
            CallOptions? options = null)
        {
            var chosen = options ?? CallOptions.Default;
            var done = new TaskCompletionSource<TResponse>(
                IsDriven ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
            var body = method.Request.Encode(request);
            var policy = chosen.Retry ?? Retry;
            if (policy.MaxAttempts > 1)
            {
                new Retrying<TRequest, TResponse>(this, method, body, chosen, policy, done).Start();
                return done.Task;
            }
            Start(method.Method.RequestId, body, chosen, TimeSpan.Zero, call => Defer(() =>
            {
                if (call.TryNext(out var next))
                {
                    Typed.Settle(done, next, method, chosen.Cancellation);
                }
                else if (call.IsFinished)
                {
                    done.TrySetException(new RpcException(Status.Unavailable()));
                }
            }));
            return done.Task;
        }

        public IAsyncEnumerable<TItem> ServerStream<TRequest, TItem>(
            ServerStreamMethod<TRequest, TItem> method,
            TRequest request,
            CallOptions? options = null) =>
            Items(method, request, options ?? CallOptions.Default, default);

        public void Tick(TimeSpan now)
        {
            if (!driven)
            {
                throw new InvalidOperationException("a self-driven connection drives itself");
            }
            if (ticking)
            {
                return;
            }
            ticking = true;
            lastTick = now;
            var stepped = current;
            var opening = incoming;
            try
            {
                Step(stepped, now);
                if (opening != null)
                {
                    Step(opening, now);
                }
            }
            finally
            {
                ticking = false;
            }
            SettleDriven(stepped);
            if (opening != null)
            {
                SettleDriven(opening);
            }
            RunDeferred();
        }

        public void Close()
        {
            Session session;
            Session? opening;
            lock (gate)
            {
                closed = true;
                session = current;
                opening = incoming;
            }
            session.Inbox.Push(new CloseOrder());
            opening?.Inbox.Push(new CloseOrder());
        }

        internal RpcCall Start(uint requestId, ReadOnlySpan<byte> body, CallOptions options, TimeSpan delay, Action<RpcCall>? watcher) =>
            StartOn(Current, requestId, body, options, delay, watcher);

        internal void Defer(Action settle)
        {
            if (driven && ticking)
            {
                deferred.Add(settle);
                return;
            }
            settle();
        }

        internal void Reopen(Action<bool> then)
        {
            lock (gate)
            {
                if (closed || options.Reconnect == null)
                {
                    then(false);
                    return;
                }
                if (!current.HasEnded)
                {
                    then(true);
                    return;
                }
                if (reopening != null)
                {
                    reopening.Add(then);
                    return;
                }
                reopening = new List<Action<bool>> { then };
            }
            if (!driven)
            {
                var spawned = Spawn(options.Reconnect, true);
                lock (gate)
                {
                    incoming = spawned;
                }
                return;
            }
            ITransport? transport;
            try
            {
                transport = options.Reconnect();
            }
            catch (Exception)
            {
                transport = null;
            }
            if (transport == null)
            {
                Reopened(null, false);
                return;
            }
            try
            {
                var session = DrivenSession(transport, true);
                lock (gate)
                {
                    incoming = session;
                }
            }
            catch (Exception)
            {
                Reopened(null, false);
            }
        }

        private RpcCall StartOn(Session session, uint requestId, ReadOnlySpan<byte> body, CallOptions options, TimeSpan delay, Action<RpcCall>? watcher)
        {
            long token = Interlocked.Increment(ref nextToken);
            var call = new RpcCall(token, session.Inbox, watcher);
            var refusal = session.Inbox.PushCall(new CallOrder(token, requestId, body.ToArray(), options, call, delay));
            if (refusal != CallError.None)
            {
                call.Refuse(refusal);
                return call;
            }
            call.CancelWhen(options.Cancellation);
            return call;
        }

        private void Step(Session session, TimeSpan now)
        {
            var driver = session.Driver!;
            if (driver.IsShutDown)
            {
                return;
            }
            session.OpensBy ??= now + options.Timeout;
            bool running = driver.Step(now);
            if (running && driver.Phase == Phase.Opening && now >= session.OpensBy)
            {
                session.TimedOut = new ConnectException(ConnectFailure.TimedOut, null, "the session was not ready before the timeout");
                running = false;
            }
            if (!running)
            {
                driver.ShutDown();
            }
        }

        private void RunDeferred()
        {
            while (deferred.Count > 0)
            {
                var due = deferred;
                deferred = new List<Action>();
                foreach (var settle in due)
                {
                    settle();
                }
            }
        }

        private async IAsyncEnumerable<TItem> Items<TRequest, TItem>(
            ServerStreamMethod<TRequest, TItem> method,
            TRequest request,
            CallOptions options,
            [EnumeratorCancellation] CancellationToken enumeration)
        {
            var arrived = new Signal(IsDriven);
            var call = Start(method.Method.RequestId, method.Request.Encode(request), options, TimeSpan.Zero, _ => Defer(arrived.Raise));
            using var stop = enumeration.Register(call.Cancel);
            bool finished = false;
            try
            {
                while (true)
                {
                    if (!call.TryNext(out var next))
                    {
                        if (call.IsFinished)
                        {
                            finished = true;
                            throw new RpcException(Status.Unavailable());
                        }
                        await arrived.Wait();
                        continue;
                    }
                    var outcome = next.Outcome;
                    if (outcome == null)
                    {
                        finished = true;
                        throw new RpcException(Typed.Refused(next.Refusal));
                    }
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Item:
                            if (!Typed.TryDecode(method.Item, outcome.Body, out var item, out var error))
                            {
                                throw new RpcException(Typed.Undecodable(method.Method, "item", error));
                            }
                            yield return item;
                            break;

                        case OutcomeKind.End:
                            finished = true;
                            yield break;

                        default:
                            finished = true;
                            throw Typed.Failure(outcome, method.Details, options.Cancellation, enumeration);
                    }
                }
            }
            finally
            {
                if (!finished)
                {
                    call.Cancel();
                }
            }
        }

        public void Dispose()
        {
            Close();
            if (driven)
            {
                if (!ticking)
                {
                    Tick(MonotonicClock.Now);
                }
                return;
            }
            Session session;
            Session? opening;
            lock (gate)
            {
                session = current;
                opening = incoming;
            }
            foreach (var thread in new[] { session.Thread, opening?.Thread })
            {
                if (thread != null && Thread.CurrentThread != thread)
                {
                    thread.Join();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            if (driven)
            {
                Dispose();
                return default;
            }
            Close();
            return new ValueTask(Current.Finished.Task);
        }

        private void SettleDriven(Session session)
        {
            if (session.TimedOut != null)
            {
                Resolve(session, session.TimedOut);
            }
            else
            {
                Settle(session);
            }
            if (session.Driver!.IsShutDown)
            {
                session.Finished.TrySetResult(true);
            }
        }

        private void Settle(Session session)
        {
            switch (session.Driver!.Phase)
            {
                case Phase.Ready when options.Credential == null:
                    Resolve(session, null);
                    break;

                case Phase.Ready when !session.Authenticating:
                    session.Authenticating = true;
                    var credential = options.Credential;
                    StartOn(session, credential.Method.RequestId, credential.Body, CallOptions.Default, TimeSpan.Zero,
                        call => Defer(() => Authenticated(session, call)));
                    break;

                case Phase.Refused:
                    Resolve(session, new ConnectException(
                        ConnectFailure.Refused, session.Driver.Refusal, $"the handshake failed: {session.Driver.Refusal}"));
                    break;

                case Phase.Closed:
                    Resolve(session, new ConnectException(ConnectFailure.Closed, null, "the peer closed the session"));
                    break;
            }
        }

        private void Authenticated(Session session, RpcCall call)
        {
            if (!call.TryNext(out var next))
            {
                if (call.IsFinished)
                {
                    Resolve(session, new ConnectException(ConnectFailure.Closed, null, "the peer closed the session"));
                }
                return;
            }
            var outcome = next.Outcome;
            if (outcome?.Kind == OutcomeKind.Response)
            {
                Resolve(session, null);
                return;
            }
            var status = outcome == null
                ? Typed.Refused(next.Refusal)
                : outcome.Status ?? Status.Internal($"{options.Credential!.Method.Name} received {outcome.Kind}");
            Resolve(session, new ConnectException(status));
        }

        private void Resolve(Session session, ConnectException? failure)
        {
            bool resolved = failure == null ? session.Ready.TrySetResult(this) : session.Ready.TrySetException(failure);
            if (!resolved)
            {
                return;
            }
            session.GiveUp?.Dispose();
            if (failure != null)
            {
                session.Inbox.Push(new CloseOrder());
            }
            if (session.Replaces)
            {
                Reopened(session, failure == null);
            }
        }

        private void Reopened(Session? session, bool ready)
        {
            List<Action<bool>>? waiting;
            lock (gate)
            {
                if (ready && closed)
                {
                    ready = false;
                }
                if (ready)
                {
                    current = session!;
                }
                if (incoming == session)
                {
                    incoming = null;
                }
                waiting = reopening;
                reopening = null;
            }
            if (!ready)
            {
                session?.Inbox.Push(new CloseOrder());
            }
            foreach (var then in waiting ?? new List<Action<bool>>())
            {
                Defer(() => then(ready));
            }
        }

        private Session DrivenSession(ITransport transport, bool replaces)
        {
            var built = new RpcSession(transport, schema, registry, FomoxaRole.Client, MonotonicClock.Now, options.Net, options.Config);
            var inbox = new Inbox(null);
            return new Session(inbox, null, TaskCreationOptions.None, replaces)
            {
                Driver = new Driver(built, inbox, options.Policy, options.Plain, null),
            };
        }

        private Session Spawn(Func<ITransport?> transport, bool replaces)
        {
            var inbox = new Inbox(new Waker());
            Session session = null!;
            var thread = new Thread(() =>
            {
                Driver driver;
                try
                {
                    var built = transport() ?? throw new ConnectException(ConnectFailure.Closed, null, "the application gave no transport");
                    var rpc = new RpcSession(built, schema, registry, FomoxaRole.Client, MonotonicClock.Now, options.Net, options.Config);
                    driver = new Driver(rpc, inbox, options.Policy, options.Plain, Driver.Pool);
                }
                catch (Exception failed)
                {
                    foreach (var order in inbox.Close())
                    {
                        if (order is CallOrder call)
                        {
                            call.Call.Refuse(CallError.NotReady);
                        }
                    }
                    inbox.Dispose();
                    if (session.Ready.TrySetException(failed) && replaces)
                    {
                        Reopened(session, false);
                    }
                    session.Finished.TrySetResult(true);
                    return;
                }
                driver.PhaseChanged += _ => Settle(session);
                session.Driver = driver;
                try
                {
                    driver.Run();
                }
                finally
                {
                    session.Finished.TrySetResult(true);
                }
            })
            {
                IsBackground = true,
                Name = "frpc-connection",
            };
            session = new Session(inbox, thread, TaskCreationOptions.RunContinuationsAsynchronously, replaces);
            session.GiveUp = new Timer(_ => Resolve(session,
                new ConnectException(ConnectFailure.TimedOut, null, "the session was not ready before the timeout")));
            session.GiveUp.Change(options.Timeout, Timeout.InfiniteTimeSpan);
            thread.Start();
            return session;
        }

        private static Task<RpcConnection> Open(Func<ITransport> transport, Schema schema, Registry registry, ConnectOptions options)
        {
            registry.RequireDeclaredIn(id => schema.Message(id) != null);
            return new RpcConnection(schema, registry, options, false, connection => connection.Spawn(transport, false)).Opened;
        }

        private sealed class Session
        {
            private Driver? driver;

            public Session(Inbox inbox, Thread? thread, TaskCreationOptions completion, bool replaces)
            {
                Inbox = inbox;
                Thread = thread;
                Replaces = replaces;
                Ready = new TaskCompletionSource<RpcConnection>(completion);
                Finished = new TaskCompletionSource<bool>(completion);
            }

            public Inbox Inbox { get; }

            public Thread? Thread { get; }

            public bool Replaces { get; }

            public TaskCompletionSource<RpcConnection> Ready { get; }

            public TaskCompletionSource<bool> Finished { get; }

            public Driver? Driver
            {
                get => Volatile.Read(ref driver);
                set => Volatile.Write(ref driver, value);
            }

            public Timer? GiveUp { get; set; }

            public bool Authenticating { get; set; }

            public TimeSpan? OpensBy { get; set; }

            public ConnectException? TimedOut { get; set; }

            public bool HasEnded =>
                Inbox.IsClosed || Driver is { } running && (running.IsShutDown || running.Phase == Phase.Closed || running.Phase == Phase.Refused);
        }
    }

    internal static class Address
    {
        public static (string Host, int Port) Split(string address)
        {
            int colon = address.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(address.Substring(colon + 1), out int port))
            {
                throw new FormatException($"'{address}' is not host:port");
            }
            string host = address.Substring(0, colon);
            if (host.StartsWith("[") && host.EndsWith("]"))
            {
                host = host.Substring(1, host.Length - 2);
            }
            return (host, port);
        }
    }
}
