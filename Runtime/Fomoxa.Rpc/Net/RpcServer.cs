using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Fomoxa.Net;
using Fomoxa.Net.Transports;

namespace Fomoxa.Rpc.Net
{
    public interface IServerObserver
    {
        void Opened(long connection, EndPoint? peer)
        {
        }

        void Plain(long connection, PlainMessage message)
        {
        }

        void Closed(long connection)
        {
        }
    }

    public sealed class RpcServer : IDisposable
    {
        private sealed class Silent : IServerObserver
        {
        }

        private readonly Socket? listener;
        private readonly IListenerTransport? drivenListener;
        private readonly IListenerTransport? listened;
        private Waker? acceptWaker;
        private readonly Schema schema;
        private readonly Registry registry;
        private readonly List<WeakReference<Inbox>> live = new List<WeakReference<Inbox>>();
        private readonly List<(long Connection, Inbox Inbox, Driver Driver)> driven = new List<(long, Inbox, Driver)>();
        private SessionConfig netConfig = new SessionConfig();
        private RpcConfig config = new RpcConfig();
        private WaitPolicy policy = WaitPolicy.Default;
        private IServerObserver observer = new Silent();
        private Action<RpcCore>? configure;
        private Thread? accept;
        private long nextConnection;
        private volatile bool stopped;
        private bool ticking;

        private RpcServer(Socket? listener, IListenerTransport? drivenListener, Schema schema, Registry registry, IListenerTransport? listened = null)
        {
            this.listener = listener;
            this.drivenListener = drivenListener;
            this.listened = listened;
            this.schema = schema;
            this.registry = registry;
        }

        public static RpcServer Bind(string address, Schema schema, Registry registry)
        {
            registry.RequireDeclaredIn(id => schema.Message(id) != null);
            var (host, port) = Address.Split(address);
            var ip = IPAddress.Parse(host);
            var listener = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Bind(new IPEndPoint(ip, port));
                listener.Listen(128);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
            return new RpcServer(listener, null, schema, registry);
        }

        public static RpcServer Listen(IListenerTransport listener, Schema schema, Registry registry)
        {
            registry.RequireDeclaredIn(id => schema.Message(id) != null);
            return new RpcServer(null, null, schema, registry, listener);
        }

        public static RpcServer Driven(IListenerTransport listener, Schema schema, Registry registry)
        {
            registry.RequireDeclaredIn(id => schema.Message(id) != null);
            return new RpcServer(null, listener, schema, registry);
        }

        public static RpcServer Driven(string address, Schema schema, Registry registry)
        {
            registry.RequireDeclaredIn(id => schema.Message(id) != null);
            var (host, port) = Address.Split(address);
            return new RpcServer(null, new TcpListenerTransport(new IPEndPoint(IPAddress.Parse(host), port)), schema, registry);
        }

        public IPEndPoint LocalEndPoint => listener != null
            ? (IPEndPoint)listener.LocalEndPoint!
            : (drivenListener ?? listened) is TcpListenerTransport tcp
                ? tcp.LocalEndPoint
                : throw new InvalidOperationException("the listener has no IP end point");

        public bool IsStopped => stopped;

        public bool IsDriven => drivenListener != null;

        public int ConnectionCount => driven.Count;

        public RpcServer WithNetConfig(SessionConfig netConfig)
        {
            this.netConfig = netConfig;
            return this;
        }

        public RpcServer WithConfig(RpcConfig config)
        {
            this.config = config;
            return this;
        }

        public RpcServer WithPolicy(WaitPolicy policy)
        {
            this.policy = policy;
            return this;
        }

        public RpcServer Observing(IServerObserver observer)
        {
            this.observer = observer;
            return this;
        }

        public RpcServer Configuring(Action<RpcCore> configure)
        {
            this.configure = configure;
            return this;
        }

        public RpcServer Serve()
        {
            if (listener == null && listened == null)
            {
                throw new InvalidOperationException("a driven server is driven by Tick");
            }
            if (accept != null)
            {
                throw new InvalidOperationException("the server already serves");
            }
            if (listened != null)
            {
                acceptWaker = new Waker();
            }
            accept = new Thread(listened != null ? AcceptFromListener : AcceptUntilStopped) { IsBackground = true, Name = "frpc-accept" };
            accept.Start();
            return this;
        }

        public void Wait() => accept?.Join();

        public void Tick(TimeSpan now)
        {
            if (drivenListener == null)
            {
                throw new InvalidOperationException("a self-driven server drives itself");
            }
            if (ticking)
            {
                return;
            }
            ticking = true;
            try
            {
                AcceptPending(drivenListener, now);
                for (int index = driven.Count - 1; index >= 0; index--)
                {
                    var (connection, _, driver) = driven[index];
                    if (driver.Step(now))
                    {
                        continue;
                    }
                    driver.ShutDown();
                    driven.RemoveAt(index);
                    observer.Closed(connection);
                }
            }
            finally
            {
                ticking = false;
            }
        }

        public void Stop()
        {
            if (stopped)
            {
                return;
            }
            stopped = true;
            if (drivenListener != null)
            {
                drivenListener.Dispose();
                foreach (var (_, inbox, _) in driven)
                {
                    inbox.Push(new CloseOrder());
                }
                Tick(MonotonicClock.Now);
                return;
            }
            listener?.Dispose();
            acceptWaker?.Wake();
            if (accept != null && Thread.CurrentThread != accept)
            {
                accept.Join();
            }
            listened?.Dispose();
            acceptWaker?.Dispose();
            lock (live)
            {
                foreach (var weak in live)
                {
                    if (weak.TryGetTarget(out var inbox))
                    {
                        inbox.Push(new CloseOrder());
                    }
                }
                live.Clear();
            }
        }

        public void Dispose() => Stop();

        private void AcceptUntilStopped()
        {
            while (!stopped)
            {
                Socket accepted;
                try
                {
                    accepted = listener!.Accept();
                }
                catch (SocketException) when (!stopped)
                {
                    continue;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (stopped)
                {
                    accepted.Dispose();
                    return;
                }
                Launch(() => new TcpTransport(accepted), accepted.RemoteEndPoint, accepted.Dispose);
            }
        }

        private void AcceptFromListener()
        {
            var source = listened!;
            var waker = acceptWaker!;
            var readiness = ShellReadiness.For(source);
            while (!stopped)
            {
                var outcome = source.Accept();
                if (outcome.Status == AcceptStatus.Progress)
                {
                    continue;
                }
                if (outcome.Status == AcceptStatus.Error)
                {
                    return;
                }
                if (outcome.Status == AcceptStatus.Accepted && outcome.Transport != null)
                {
                    var transport = outcome.Transport;
                    if (stopped)
                    {
                        transport.Dispose();
                        return;
                    }
                    Launch(() => transport, (transport as TcpTransport)?.Socket.RemoteEndPoint, transport.Dispose);
                    continue;
                }
                if (readiness != null)
                {
                    readiness.WaitReady(policy.IdleCap, waker);
                }
                else
                {
                    waker.Sleep(policy.ListenerPoll);
                }
                waker.Clear();
            }
        }

        private void Launch(Func<ITransport> open, EndPoint? peer, Action discard)
        {
            long connection = Interlocked.Increment(ref nextConnection);
            var inbox = new Inbox(new Waker());
            lock (live)
            {
                live.RemoveAll(weak => !weak.TryGetTarget(out var target) || target.IsClosed);
                live.Add(new WeakReference<Inbox>(inbox));
            }
            var worker = new Thread(() => Serve(connection, open, peer, discard, inbox))
            {
                IsBackground = true,
                Name = $"frpc-conn-{connection}",
            };
            worker.Start();
        }

        private void Serve(long connection, Func<ITransport> open, EndPoint? peer, Action discard, Inbox inbox)
        {
            RpcSession session;
            try
            {
                session = new RpcSession(
                    open(), schema, registry, FomoxaRole.Server, MonotonicClock.Now, netConfig, config);
            }
            catch (Exception)
            {
                discard();
                inbox.Close();
                inbox.Dispose();
                return;
            }
            session.Core.Connection = connection;
            configure?.Invoke(session.Core);
            observer.Opened(connection, peer);
            var driver = new Driver(session, inbox, policy, message => observer.Plain(connection, message), Driver.Pool);
            driver.Run();
            observer.Closed(connection);
        }

        private void AcceptPending(IListenerTransport source, TimeSpan now)
        {
            while (!stopped)
            {
                var outcome = source.Accept();
                if (outcome.Status == AcceptStatus.Progress)
                {
                    continue;
                }
                if (outcome.Status != AcceptStatus.Accepted || outcome.Transport == null)
                {
                    return;
                }
                long connection = ++nextConnection;
                var transport = outcome.Transport;
                var session = new RpcSession(transport, schema, registry, FomoxaRole.Server, now, netConfig, config);
                session.Core.Connection = connection;
                configure?.Invoke(session.Core);
                var inbox = new Inbox(null);
                var driver = new Driver(session, inbox, policy, message => observer.Plain(connection, message), null);
                driven.Add((connection, inbox, driver));
                observer.Opened(connection, (transport as TcpTransport)?.Socket.RemoteEndPoint);
            }
        }
    }
}
