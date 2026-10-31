using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BundleFixture;
using Fomoxa.Net;
using Fomoxa.Net.Transports;
using Fomoxa.Networking;
using Fomoxa.Networking.Sessions;
using Fomoxa.Networking.Standalone;
using Fomoxa.Networking.Transports;
using Fomoxa.Rpc.Directory;
using Fomoxa.Rpc.Net;
using NUnit.Framework;

namespace Fomoxa.Rpc.Tests
{
    public sealed class DirectoryPodRuntimeTest
    {
        private const double FrameSeconds = 1.0 / 30;
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

        private FomoxaRegistry registry = null!;
        private TimeSpan now;
        private List<Exception> exceptions = null!;
        private NetworkLog log = null!;
        private List<NetworkRuntime> runtimes = null!;

        [SetUp]
        public void SetUp()
        {
            registry = TestObjects.Registry();
            now = TimeSpan.Zero;
            exceptions = new List<Exception>();
            log = new NetworkLog(exceptions.Add, message => { }, message => { });
            runtimes = new List<NetworkRuntime>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (NetworkRuntime runtime in runtimes)
            {
                runtime.ClientManager.StopConnection();
                runtime.ServerManager.StopConnection();
            }

            Assert.IsEmpty(exceptions);
        }

        [Test]
        public void AClientWithATicketFromTheDirectoryJoinsThePodAndThePodReportsIt()
        {
            using DirectoryProcess directory = DirectoryProcess.Start();
            var listener = new PodListenerFactory(registry, "pod-a");
            NetworkRuntime server = Runtime(listener);
            server.ServerManager.Authenticator = new Accepting();
            ushort gamePort = FreeUdpPort();
            using var pod = new DirectoryPod(server, Settings(directory, gamePort, "pod-a"));
            pod.OnRegistered += registration => listener.Tokens = new ConnectTokens(registry, registration.TicketKey);

            pod.Start();
            Until(() => pod.IsRegistered);
            server.ServerManager.StartConnection(gamePort);
            AllocateResponse allocation = Allocate(directory, "arena:1");
            NetworkRuntime client = Runtime(new EncryptedUdpTransportFactory(registry));
            client.ClientManager.StartConnection(allocation.Address, allocation.Port, allocation.Connections.Single());
            Until(() => client.ClientManager.State == ConnectionState.Started);
            Until(() => directory.Pods().Single().Players == 1);

            Assert.AreEqual(gamePort, allocation.Port);
            Assert.AreEqual(1, server.ServerManager.PeerCount);
            Assert.AreEqual(0.1f, directory.Pods().Single().Load, 1e-6f);
        }

        [Test]
        public void TheDrainDeadlineStopsTheServerOfTheRuntime()
        {
            using DirectoryProcess directory = DirectoryProcess.Start(drainSeconds: 1);
            var listener = new PodListenerFactory(registry, "pod-a");
            NetworkRuntime server = Runtime(listener);
            ushort gamePort = FreeUdpPort();
            using var pod = new DirectoryPod(server, Settings(directory, gamePort, "pod-a"));
            pod.OnRegistered += registration => listener.Tokens = new ConnectTokens(registry, registration.TicketKey);
            int deadlines = 0;
            pod.OnDrainDeadline += () => deadlines++;
            pod.Start();
            Until(() => pod.IsRegistered);
            server.ServerManager.StartConnection(gamePort);
            Until(() => server.ServerManager.State == ServerState.Started);

            Task drain = pod.RequestDrain();
            Until(() => drain.IsCompleted && pod.IsDraining);
            Until(() => deadlines == 1);

            Assert.AreEqual(ServerState.Stopped, server.ServerManager.State);
        }

        private static DirectoryPodSettings Settings(DirectoryProcess directory, ushort gamePort, string target) =>
            new DirectoryPodSettings
            {
                DirectoryAddress = "127.0.0.1",
                DirectoryPort = directory.Port,
                Key = Encoding.UTF8.GetBytes(DirectoryProcess.PodKey),
                Address = "127.0.0.1",
                Port = gamePort,
                Target = target,
                Version = "1.0.0",
                MaxPlayers = 10,
                RetryDelay = TimeSpan.FromMilliseconds(100),
            };

        private static AllocateResponse Allocate(DirectoryProcess directory, string roomKey)
        {
            using RpcConnection backend = directory.Backend();
            Task<AllocateResponse> allocation = new DirectoryClient(backend).AllocateAsync(new AllocateRequest { RoomKey = roomKey, Version = "1.0.0", Tickets = 1 });
            Assert.IsTrue(allocation.Wait(Patience), "Allocate did not finish in time");
            return allocation.Result;
        }

        private static ushort FreeUdpPort()
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return (ushort)((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        }

        private NetworkRuntime Runtime(ITransportFactory factory)
        {
            NetworkRuntime runtime = StandaloneRuntime.Create(registry, new NetworkSettings(), factory, new StandalonePrefabs(), new StandaloneBehaviours(), new NoSceneFiles(), () => now, log);
            runtimes.Add(runtime);
            return runtime;
        }

        private void Until(Func<bool> done)
        {
            TimeSpan until = MonotonicClock.Now + Patience;
            while (!done())
            {
                Assert.IsTrue(MonotonicClock.Now < until, "the condition did not hold in time");
                now += TimeSpan.FromSeconds(FrameSeconds);
                foreach (NetworkRuntime runtime in runtimes)
                {
                    runtime.BeginFrame(now, FrameSeconds);
                }

                foreach (NetworkRuntime runtime in runtimes)
                {
                    runtime.EndFrame();
                }

                Thread.Sleep(2);
            }
        }

        private sealed class PodListenerFactory : IConnectionTransportFactory
        {
            private readonly FomoxaRegistry registry;
            private readonly string target;
            private readonly EncryptedUdpTransportFactory client;

            public PodListenerFactory(FomoxaRegistry registry, string target)
            {
                this.registry = registry;
                this.target = target;
                client = new EncryptedUdpTransportFactory(registry);
            }

            public ConnectTokens? Tokens { get; set; }

            public int FrameBudget => client.FrameBudget;

            public bool RequiresConnection => client.RequiresConnection;

            public bool CanUseConnection(ReadOnlyMemory<byte> connection) => client.CanUseConnection(connection);

            public IListenerTransport CreateListener(ushort port, out ushort boundPort) =>
                new EncryptedUdpTransportFactory(registry, Tokens, target).CreateListener(port, out boundPort);

            public ITransportConnector CreateConnector(string address, ushort port) => client.CreateConnector(address, port);

            public ITransportConnector CreateConnector(string address, ushort port, ReadOnlyMemory<byte> connection) =>
                client.CreateConnector(address, port, connection);
        }

        private sealed class Accepting : IServerAuthenticator
        {
            public void RegisterServer(ServerAuthentication authentication)
            {
            }

            public void OnServerAuthenticating(ServerAuthenticationContext context) => context.Accept();
        }

        private sealed class NoSceneFiles : ISceneFiles
        {
            public bool Knows(uint sceneId) => false;

            public byte[] Read(uint sceneId) => throw new InvalidOperationException($"no scene file 0x{sceneId:X8}");
        }
    }
}
