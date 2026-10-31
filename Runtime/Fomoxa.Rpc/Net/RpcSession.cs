using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Fomoxa.Net;
using Fomoxa.Net.Transports;

namespace Fomoxa.Rpc.Net
{
    public sealed record PlainMessage(uint MessageId, byte[] Payload);

    public readonly struct Wait : IEquatable<Wait>
    {
        public Wait(TimeSpan timeout, bool writable)
        {
            Timeout = timeout;
            Writable = writable;
        }

        public TimeSpan Timeout { get; }

        public bool Writable { get; }

        public bool Equals(Wait other) => Timeout == other.Timeout && Writable == other.Writable;

        public override bool Equals(object? obj) => obj is Wait other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Timeout, Writable);

        public static bool operator ==(Wait left, Wait right) => left.Equals(right);

        public static bool operator !=(Wait left, Wait right) => !left.Equals(right);

        public override string ToString() => $"Wait {{ Timeout = {Timeout}, Writable = {Writable} }}";
    }

    public sealed class WaitPolicy
    {
        public TimeSpan IdleCap { get; init; } = TimeSpan.FromMilliseconds(250);

        public TimeSpan WorkPoll { get; init; } = TimeSpan.FromTicks(2_000);

        public TimeSpan ListenerPoll { get; init; } = TimeSpan.FromMilliseconds(10);

        public static WaitPolicy Default { get; } = new WaitPolicy();

        public Wait Plan(RpcCore core, bool netCongested, TimeSpan now)
        {
            var timeout = IdleCap;
            if (core.HasRunnableWork() && WorkPoll < timeout)
            {
                timeout = WorkPoll;
            }
            var deadline = core.NextDeadline();
            if (deadline.HasValue)
            {
                var untilDeadline = deadline.Value - now;
                if (untilDeadline < timeout)
                {
                    timeout = untilDeadline > TimeSpan.Zero ? untilDeadline : TimeSpan.Zero;
                }
            }
            return new Wait(timeout, netCongested || core.HasUnsentFrames);
        }

        public bool Block(IReadiness readiness, Wait plan, Waker waker)
        {
            if (plan.Writable && !readiness.ReportsWritable)
            {
                return readiness.WaitReady(false, plan.Timeout < WorkPoll ? plan.Timeout : WorkPoll, waker);
            }
            return readiness.WaitReady(plan.Writable, plan.Timeout, waker);
        }
    }

    public sealed class RpcSession : IDisposable
    {
        private readonly ITransport transport;
        private readonly IReadiness? readiness;
        private readonly FomoxaConnection connection;
        private readonly List<PlainMessage> plain = new List<PlainMessage>();

        public RpcSession(
            ITransport transport,
            Schema schema,
            Registry registry,
            FomoxaRole role,
            TimeSpan now,
            SessionConfig? netConfig = null,
            RpcConfig? config = null)
        {
            registry.RequireDeclaredIn(id => schema.Message(id) != null);
            this.transport = transport;
            readiness = ReadinessOf(transport);
            Core = new RpcCore(registry, role == FomoxaRole.Client ? Role.Client : Role.Server, config);
            connection = new FomoxaConnection(transport, schema, netConfig ?? new SessionConfig(), role, now);
        }

        public RpcCore Core { get; }

        public ITransport Transport => transport;

        public bool IsReady => Core.IsReady;

        public bool IsClosed => connection.IsClosed;

        public SessionState State => connection.State;

        public HandshakeFailure? Refusal { get; private set; }

        public CallResult Call(uint requestId, ReadOnlySpan<byte> body, CallOptions options, TimeSpan now) =>
            Core.Call(requestId, body, options, now);

        public void Tick(TimeSpan now)
        {
            var events = connection.Tick(now);
            for (int index = 0; index < events.Count; index++)
            {
                var seen = events[index];
                switch (seen.Kind)
                {
                    case FomoxaEventKind.Ready:
                        Core.OnReady();
                        break;

                    case FomoxaEventKind.Message:
                        if (!Core.OnMessage(seen.MessageId, seen.Payload, now))
                        {
                            plain.Add(new PlainMessage(seen.MessageId, seen.CopyPayload()));
                        }
                        break;

                    case FomoxaEventKind.HandshakeFailed:
                        Refusal = seen.Failure;
                        Core.OnSessionEnd();
                        break;

                    case FomoxaEventKind.Disconnected:
                        Core.OnSessionEnd();
                        break;
                }
            }
            Core.Tick(now);
            Core.Drain(frame => DeliveryOf(connection.Send(frame.MessageId, frame.Payload)));
        }

        public List<Outcome> TakeOutcomes() => Core.TakeOutcomes();

        public List<PlainMessage> TakePlain()
        {
            var taken = new List<PlainMessage>(plain);
            plain.Clear();
            return taken;
        }

        public SendStatus SendPlain(uint messageId, ReadOnlySpan<byte> payload) =>
            Core.Registry.IsPlain(messageId) ? connection.Send(messageId, payload) : SendStatus.NotReady;

        public bool Wait(WaitPolicy policy, TimeSpan now, Waker waker, TimeSpan? due = null)
        {
            var plan = policy.Plan(Core, connection.IsCongested, now);
            if (due.HasValue && due.Value - now < plan.Timeout)
            {
                plan = new Wait(due.Value > now ? due.Value - now : TimeSpan.Zero, plan.Writable);
            }
            if (readiness == null)
            {
                waker.Sleep(plan.Timeout < policy.WorkPoll ? plan.Timeout : policy.WorkPoll);
                return false;
            }
            return policy.Block(readiness, plan, waker);
        }

        public void Close()
        {
            connection.Close();
            Core.OnSessionEnd();
        }

        public void Dispose()
        {
            Close();
            connection.Dispose();
        }

        private static IReadiness? ReadinessOf(ITransport transport) => ShellReadiness.For(transport);

        internal static TcpTransport ConnectTcp(string host, int port, TimeSpan timeout)
        {
            var address = Resolve(host);
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var connecting = socket.BeginConnect(new IPEndPoint(address, port), null, null);
                if (!connecting.AsyncWaitHandle.WaitOne(timeout))
                {
                    throw new SocketException((int)SocketError.TimedOut);
                }
                socket.EndConnect(connecting);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            return new TcpTransport(socket);
        }

        private static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out var parsed))
            {
                return parsed;
            }
            var addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }
            return addresses[0];
        }

        private static Delivery DeliveryOf(SendStatus status) => status switch
        {
            SendStatus.Congested => Delivery.Rejected,
            SendStatus.NotReady => Delivery.Rejected,
            _ => Delivery.Accepted,
        };
    }
}
