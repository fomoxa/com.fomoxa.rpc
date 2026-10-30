using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fomoxa.Net;
using Fomoxa.Net.Transports;

namespace Fomoxa.Rpc.Net
{
    public sealed class MemoryTransport : ITransport
    {
        private readonly Pipe inbound;
        private readonly Pipe outbound;

        private MemoryTransport(Pipe inbound, Pipe outbound)
        {
            this.inbound = inbound;
            this.outbound = outbound;
        }

        public TransportKind Kind => TransportKind.Stream;

        public static (MemoryTransport Near, MemoryTransport Far) Pair()
        {
            var one = new Pipe();
            var other = new Pipe();
            return (new MemoryTransport(one, other), new MemoryTransport(other, one));
        }

        public SendOutcome Send(ReadOnlySpan<byte> bytes)
        {
            lock (outbound)
            {
                if (outbound.Closed)
                {
                    return SendOutcome.Closed;
                }
                foreach (byte value in bytes)
                {
                    outbound.Bytes.Enqueue(value);
                }
                return SendOutcome.Ok;
            }
        }

        public ReceiveOutcome Receive(Span<byte> buffer)
        {
            lock (inbound)
            {
                if (inbound.Bytes.Count == 0)
                {
                    return inbound.Closed ? ReceiveOutcome.Closed : ReceiveOutcome.WouldBlock;
                }
                int count = Math.Min(buffer.Length, inbound.Bytes.Count);
                for (int index = 0; index < count; index++)
                {
                    buffer[index] = inbound.Bytes.Dequeue();
                }
                return ReceiveOutcome.Ok(count);
            }
        }

        public void CloseGracefully() => Close();

        public void Dispose() => Close();

        private void Close()
        {
            lock (outbound)
            {
                outbound.Closed = true;
            }
            lock (inbound)
            {
                inbound.Closed = true;
            }
        }

        private sealed class Pipe
        {
            public Queue<byte> Bytes { get; } = new Queue<byte>();

            public bool Closed { get; set; }
        }
    }

    public sealed class Loopback : IDisposable
    {
        private const int Rounds = 10_000;

        public Loopback(Schema schema, Registry server, Registry caller, ConnectOptions? options = null)
        {
            Now = MonotonicClock.Now;
            var (near, far) = MemoryTransport.Pair();
            Server = RpcServer.Driven(new SingleListener(far), schema, server);
            if (options?.Config != null)
            {
                Server.WithConfig(options.Config);
            }
            Connection = RpcConnection.Driven(near, schema, caller, options);
            Run(Connection.Opened);
            Connection.Opened.GetAwaiter().GetResult();
        }

        public RpcServer Server { get; }

        public RpcConnection Connection { get; }

        public TimeSpan Now { get; private set; }

        public void Tick()
        {
            Connection.Tick(Now);
            Server.Tick(Now);
        }

        public bool Run(Func<bool> done)
        {
            for (int round = 0; round < Rounds; round++)
            {
                if (done())
                {
                    return true;
                }
                Tick();
            }
            return done();
        }

        public bool Run(Task until) => Run(() => until.IsCompleted);

        public void Advance(TimeSpan by)
        {
            Now += by;
            Tick();
        }

        public void Dispose()
        {
            Connection.Dispose();
            Server.Dispose();
        }

        private sealed class SingleListener : IListenerTransport
        {
            private ITransport? waiting;

            public SingleListener(ITransport waiting)
            {
                this.waiting = waiting;
            }

            public AcceptOutcome Accept()
            {
                var accepted = waiting;
                waiting = null;
                return accepted == null ? AcceptOutcome.Pending : AcceptOutcome.Accepted(accepted);
            }

            public void Dispose()
            {
            }
        }
    }
}
