using System;
using System.Collections.Generic;
using System.Threading;
using Fomoxa.Net;

namespace Fomoxa.Rpc.Net
{
    internal abstract class Order
    {
    }

    internal sealed class CallOrder : Order
    {
        public CallOrder(long token, uint requestId, byte[] body, CallOptions options, RpcCall call, TimeSpan delay)
        {
            Token = token;
            RequestId = requestId;
            Body = body;
            Options = options;
            Call = call;
            Delay = delay;
        }

        public long Token { get; }

        public uint RequestId { get; }

        public byte[] Body { get; }

        public CallOptions Options { get; }

        public RpcCall Call { get; }

        public TimeSpan Delay { get; }

        public CallOrder WithoutDelay() => new CallOrder(Token, RequestId, Body, Options, Call, TimeSpan.Zero);
    }

    internal sealed class CancelOrder : Order
    {
        public CancelOrder(long token)
        {
            Token = token;
        }

        public long Token { get; }
    }

    internal sealed class CloseOrder : Order
    {
    }

    internal sealed class Inbox : IDisposable
    {
        public const int MaxOrders = 1024;

        private readonly object gate = new object();
        private List<Order> orders = new List<Order>();
        private int calls;
        private bool closed;

        public Inbox(Waker? waker)
        {
            Waker = waker;
        }

        public Waker? Waker { get; }

        public bool IsClosed
        {
            get
            {
                lock (gate)
                {
                    return closed;
                }
            }
        }

        public CallError PushCall(CallOrder order)
        {
            lock (gate)
            {
                if (closed)
                {
                    return CallError.NotReady;
                }
                if (calls >= MaxOrders)
                {
                    return CallError.Congested;
                }
                calls++;
                orders.Add(order);
            }
            Waker?.Wake();
            return CallError.None;
        }

        public void Push(Order order)
        {
            lock (gate)
            {
                if (closed)
                {
                    return;
                }
                orders.Add(order);
            }
            Waker?.Wake();
        }

        public List<Order> Take()
        {
            lock (gate)
            {
                var taken = orders;
                orders = new List<Order>();
                calls = 0;
                return taken;
            }
        }

        public List<Order> Close()
        {
            lock (gate)
            {
                closed = true;
                return Take();
            }
        }

        public void Dispose() => Waker?.Dispose();
    }

    internal enum Phase
    {
        Opening,
        Ready,
        Refused,
        Closed,
    }

    internal sealed class Driver
    {
        private readonly RpcSession session;
        private readonly Inbox inbox;
        private readonly WaitPolicy policy;
        private readonly Action<PlainMessage>? plain;
        private readonly Dictionary<uint, (long Token, RpcCall Call)> calls = new Dictionary<uint, (long, RpcCall)>();
        private readonly Dictionary<long, CallId> tokens = new Dictionary<long, CallId>();
        private readonly List<(TimeSpan Due, CallOrder Order)> delayed = new List<(TimeSpan, CallOrder)>();
        private bool shutDown;

        public Driver(RpcSession session, Inbox inbox, WaitPolicy policy, Action<PlainMessage>? plain, Action<Action>? executor)
        {
            this.session = session;
            this.inbox = inbox;
            this.policy = policy;
            this.plain = plain;
            if (inbox.Waker != null)
            {
                var waker = new WeakReference<Waker>(inbox.Waker);
                session.Core.SetWaker(() =>
                {
                    if (waker.TryGetTarget(out var target))
                    {
                        target.Wake();
                    }
                });
            }
            if (executor != null)
            {
                session.Core.SetExecutor(executor);
            }
        }

        public event Action<Phase>? PhaseChanged;

        public Phase Phase { get; private set; } = Phase.Opening;

        public HandshakeFailure? Refusal => session.Refusal;

        public bool IsShutDown => shutDown;

        public TimeSpan? NextDue
        {
            get
            {
                TimeSpan? next = null;
                foreach (var (due, _) in delayed)
                {
                    if (next == null || due < next)
                    {
                        next = due;
                    }
                }
                return next;
            }
        }

        public void Run()
        {
            var waker = inbox.Waker ?? throw new InvalidOperationException("a driving thread needs a waker");
            session.Core.SetClock(() => MonotonicClock.Now);
            try
            {
                while (Step(MonotonicClock.Now))
                {
                    session.Wait(policy, MonotonicClock.Now, waker, NextDue);
                }
            }
            finally
            {
                ShutDown();
            }
        }

        public bool Step(TimeSpan now)
        {
            inbox.Waker?.Clear();
            bool closing = TakeOrders(now);
            session.Tick(now);
            foreach (var message in session.TakePlain())
            {
                plain?.Invoke(message);
            }
            RouteOutcomes();
            Report();
            return !closing && Phase != Phase.Closed && Phase != Phase.Refused;
        }

        private bool TakeOrders(TimeSpan now)
        {
            bool closing = false;
            var orders = inbox.Take();
            for (int index = delayed.Count - 1; index >= 0; index--)
            {
                if (delayed[index].Due <= now)
                {
                    orders.Add(delayed[index].Order);
                    delayed.RemoveAt(index);
                }
            }
            foreach (var order in orders)
            {
                switch (order)
                {
                    case CallOrder waiting when waiting.Delay > TimeSpan.Zero:
                        delayed.Add((now + waiting.Delay, waiting.WithoutDelay()));
                        break;

                    case CallOrder call:
                        var opened = session.Call(call.RequestId, call.Body, call.Options, now);
                        if (opened.IsOk)
                        {
                            tokens[call.Token] = opened.Call;
                            calls[opened.Call.Value] = (call.Token, call.Call);
                        }
                        else
                        {
                            call.Call.Refuse(opened.Error);
                        }
                        break;

                    case CancelOrder cancel:
                        if (tokens.TryGetValue(cancel.Token, out var id))
                        {
                            session.Core.Cancel(id);
                        }
                        for (int index = delayed.Count - 1; index >= 0; index--)
                        {
                            if (delayed[index].Order.Token == cancel.Token)
                            {
                                delayed[index].Order.Call.Finish();
                                delayed.RemoveAt(index);
                            }
                        }
                        break;

                    case CloseOrder:
                        closing = true;
                        break;
                }
            }
            return closing;
        }

        private void RouteOutcomes()
        {
            foreach (var outcome in session.TakeOutcomes())
            {
                if (!calls.TryGetValue(outcome.Call.Value, out var open))
                {
                    continue;
                }
                open.Call.Deliver(outcome);
                if (outcome.TerminatesCall)
                {
                    calls.Remove(outcome.Call.Value);
                    tokens.Remove(open.Token);
                }
            }
        }

        private void Report()
        {
            var phase = session.Refusal.HasValue
                ? Phase.Refused
                : session.IsClosed ? Phase.Closed : session.IsReady ? Phase.Ready : Phase;
            if (phase == Phase.Ready && Phase == Phase.Ready)
            {
                return;
            }
            if (phase != Phase)
            {
                Phase = phase;
                PhaseChanged?.Invoke(phase);
            }
        }

        public void ShutDown()
        {
            if (shutDown)
            {
                return;
            }
            shutDown = true;
            session.Close();
            RouteOutcomes();
            foreach (var order in inbox.Close())
            {
                if (order is CallOrder call)
                {
                    call.Call.Refuse(CallError.NotReady);
                }
            }
            foreach (var (_, order) in delayed)
            {
                order.Call.Refuse(CallError.NotReady);
            }
            delayed.Clear();
            foreach (var open in calls.Values)
            {
                open.Call.Finish();
            }
            calls.Clear();
            tokens.Clear();
            session.Dispose();
            if (Phase != Phase.Closed && Phase != Phase.Refused)
            {
                Phase = Phase.Closed;
                PhaseChanged?.Invoke(Phase.Closed);
            }
            inbox.Dispose();
        }

        internal static void Pool(Action job) => ThreadPool.UnsafeQueueUserWorkItem(_ => job(), null);
    }
}
