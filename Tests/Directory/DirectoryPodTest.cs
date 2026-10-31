using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fomoxa.Net;
using Fomoxa.Rpc.Directory;
using Fomoxa.Rpc.Net;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Fomoxa.Rpc.Tests
{
    public sealed class DirectoryPodTest
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

        private readonly List<IDisposable> owned = new List<IDisposable>();
        private readonly List<DirectoryPod> pods = new List<DirectoryPod>();

        [TearDown]
        public void TearDown()
        {
            foreach (DirectoryPod pod in pods)
            {
                pod.Dispose();
            }

            pods.Clear();
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                owned[index].Dispose();
            }

            owned.Clear();
        }

        [Test]
        public void APodRegistersAndItsTicketKeyIsThePodKeyOfTheDirectory()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            var registrations = new List<DirectoryRegistration>();
            Probe pod = NewPod(directory, "10.0.0.1", 7001);
            pod.Pod.OnRegistered += registrations.Add;

            pod.Pod.Start();
            Until(() => pod.Pod.IsRegistered, pod.Pod);

            Assert.AreNotEqual(0u, pod.Pod.PodId);
            CollectionAssert.AreEqual(PodKey(pod.Pod.PodId), pod.Pod.TicketKey);
            DirectoryRegistration registration = registrations.Single();
            Assert.AreEqual(pod.Pod.PodId, registration.PodId);
            Assert.AreEqual(0u, registration.PreviousPodId);
            Assert.IsFalse(registration.ChangedPodId);
            PodState state = ReadPod(directory, pod.Pod.PodId);
            Assert.AreEqual("10.0.0.1", state.Address);
            Assert.AreEqual(7001, state.Port);
            Assert.AreEqual("1.0.0", state.Version);
            Assert.AreEqual("local", state.Region);
            Assert.AreEqual(10u, state.MaxPlayers);
            Assert.AreEqual("arena", state.Labels.Single(label => label.Name == "map").Value);
        }

        [Test]
        public void APodReportsPlayersRoomsAndItsLoadOnTheIntervalOfTheDirectory()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            Probe pod = Registered(directory, "10.0.0.1", 7001);
            pod.Players = 3;
            Pump(TimeSpan.FromSeconds(1.5), pod.Pod);

            PodState state = ReadPod(directory, pod.Pod.PodId);
            Assert.AreEqual(3u, state.Players);
            Assert.AreEqual(0.3f, state.Load, 1e-6f);

            pod.Pod.Load = () => 0.75f;
            Pump(TimeSpan.FromSeconds(1.5), pod.Pod);

            Assert.AreEqual(0.75f, ReadPod(directory, pod.Pod.PodId).Load, 1e-6f);
        }

        [Test]
        public void AnAcquiredRoomIsKeptAndReleasingItRemovesItAtTheDirectory()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            Probe pod = Registered(directory, "10.0.0.1", 7001);

            Settle(pod.Pod.AcquireRoom("arena:1"), pod.Pod);

            CollectionAssert.AreEqual(new[] { "arena:1" }, pod.Pod.Rooms);
            Assert.AreEqual(1u, ReadPod(directory, pod.Pod.PodId).Rooms);
            Assert.AreEqual(7001, Allocate(directory, "arena:1").Port);

            Settle(pod.Pod.ReleaseRoom("arena:1"), pod.Pod);

            CollectionAssert.IsEmpty(pod.Pod.Rooms);
            Assert.AreEqual(0u, ReadPod(directory, pod.Pod.PodId).Rooms);
        }

        [Test]
        public void AcquiringBeforeTheRegistrationFailsUnavailableAndRecordsNothing()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            Probe pod = NewPod(directory, "10.0.0.1", 7001);

            Status status = Failure(pod.Pod.AcquireRoom("arena:1"), pod.Pod);

            Assert.AreEqual(StatusCode.Unavailable, status.Code);
            CollectionAssert.IsEmpty(pod.Pod.Rooms);
        }

        [Test]
        public void ARoomTheDirectoryRefusesIsNotRecorded()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            Probe first = Registered(directory, "10.0.0.1", 7001);
            Probe second = Registered(directory, "10.0.0.2", 7002);
            Settle(first.Pod.AcquireRoom("arena:1"), first.Pod, second.Pod);

            Status status = Failure(second.Pod.AcquireRoom("arena:1"), first.Pod, second.Pod);

            Assert.AreEqual(StatusCode.FailedPrecondition, status.Code);
            CollectionAssert.IsEmpty(second.Pod.Rooms);
            Assert.AreEqual(7001, Allocate(directory, "arena:1").Port);
        }

        [Test]
        public void AfterTheDirectoryRestartsThePodRegistersAgainWithItsIdAndRooms()
        {
            ushort port = DirectoryProcess.FreePort();
            DirectoryProcess directory = DirectoryProcess.Start(port);
            var registrations = new List<DirectoryRegistration>();
            Probe pod = Registered(directory, "10.0.0.1", 7001);
            pod.Pod.OnRegistered += registrations.Add;
            Settle(pod.Pod.AcquireRoom("arena:1"), pod.Pod);
            uint podId = pod.Pod.PodId;

            directory.Dispose();
            Until(() => !pod.Pod.IsRegistered, pod.Pod);
            directory = Own(DirectoryProcess.Start(port));
            Until(() => pod.Pod.IsRegistered, pod.Pod);

            Assert.AreEqual(podId, pod.Pod.PodId);
            Assert.AreEqual(podId, registrations.Single().PreviousPodId);
            Assert.IsFalse(registrations.Single().ChangedPodId);
            Assert.AreEqual(1u, ReadPod(directory, podId).Rooms);
            Assert.AreEqual(7001, Allocate(directory, "arena:1").Port);
        }

        [Test]
        public void APodWhoseIdWasTakenRegistersWithANewIdAndReportsTheNewKey()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            var registrations = new List<DirectoryRegistration>();
            Probe pod = Registered(directory, "10.0.0.1", 7001);
            uint taken = pod.Pod.PodId;
            pod.Pod.Stop();
            Until(() => !directory.Pods().Any(state => state.PodId == taken));
            using RpcConnection other = directory.PodConnection();
            Settled(other.Unary(DirectoryMethods.RegisterPod, new RegisterPod { PodId = taken, Address = "10.0.0.9", Port = 7009, Version = "1.0.0", MaxPlayers = 4 }));
            pod.Pod.OnRegistered += registrations.Add;

            pod.Pod.Start();
            Until(() => pod.Pod.IsRegistered, pod.Pod);

            DirectoryRegistration registration = registrations.Single();
            Assert.AreNotEqual(taken, registration.PodId);
            Assert.AreEqual(taken, registration.PreviousPodId);
            Assert.IsTrue(registration.ChangedPodId);
            CollectionAssert.AreEqual(PodKey(registration.PodId), registration.TicketKey);
            Assert.AreEqual("10.0.0.9", ReadPod(directory, taken).Address);
        }

        [Test]
        public void ADrainFromTheOperatorReachesThePodAndTheDeadlineStopsTheServer()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start(drainSeconds: 1));
            Probe pod = Registered(directory, "10.0.0.1", 7001);
            var drains = new List<DateTimeOffset>();
            int deadlines = 0;
            pod.Pod.OnDrain += drains.Add;
            pod.Pod.OnDrainDeadline += () => deadlines++;

            using (RpcConnection admin = directory.Admin())
            {
                Settled(new DirectoryAdminClient(admin).DrainAsync(new DrainPod { PodId = pod.Pod.PodId }));
            }

            Until(() => drains.Count == 1, pod.Pod);
            Assert.IsTrue(pod.Pod.IsDraining);
            Assert.AreEqual(drains[0], pod.Pod.DrainDeadline);
            Assert.AreEqual(0, pod.ServerStops);
            Assert.AreEqual(StatusCode.FailedPrecondition, Failure(pod.Pod.AcquireRoom("arena:9"), pod.Pod).Code);

            Until(() => deadlines == 1, pod.Pod);

            Assert.AreEqual(1, pod.ServerStops);
            Pump(TimeSpan.FromMilliseconds(200), pod.Pod);
            Assert.AreEqual(1, pod.ServerStops);
            Assert.AreEqual(1, drains.Count);
        }

        [Test]
        public void APodThatRegistersAgainDuringADrainIsToldOnce()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            Probe pod = Registered(directory, "10.0.0.1", 7001);
            var drains = new List<DateTimeOffset>();
            pod.Pod.OnDrain += drains.Add;
            Settle(pod.Pod.RequestDrain(), pod.Pod);
            Until(() => drains.Count == 1, pod.Pod);
            uint podId = pod.Pod.PodId;

            pod.Pod.Stop();
            pod.Pod.Start();
            Until(() => pod.Pod.IsRegistered, pod.Pod);
            Pump(TimeSpan.FromMilliseconds(300), pod.Pod);

            Assert.AreEqual(podId, pod.Pod.PodId);
            Assert.IsTrue(ReadPod(directory, podId).Draining);
            Assert.AreEqual(1, drains.Count);
        }

        [Test]
        public void ADrainingPodRelocatesItsRoomToAnotherPod()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            Probe leaving = Registered(directory, "10.0.0.1", 7001);
            Probe staying = Registered(directory, "10.0.0.2", 7002);
            Settle(leaving.Pod.AcquireRoom("arena:1"), leaving.Pod, staying.Pod);
            var drains = new List<DateTimeOffset>();
            leaving.Pod.OnDrain += drains.Add;

            Settle(leaving.Pod.RequestDrain(), leaving.Pod, staying.Pod);
            Until(() => drains.Count == 1, leaving.Pod, staying.Pod);
            AllocateResponse moved = Settle(leaving.Pod.Relocate("arena:1", 2, new byte[] { 4, 2 }), leaving.Pod, staying.Pod);

            Assert.AreEqual("10.0.0.2", moved.Address);
            Assert.AreEqual(7002, moved.Port);
            Assert.AreEqual(2, moved.Connections.Count);
            Assert.AreEqual(7002, Allocate(directory, "arena:1").Port);
            Assert.IsTrue(ReadPod(directory, leaving.Pod.PodId).Draining);
        }

        [Test]
        public void AWrongKeyIsReportedAndThePodKeepsTrying()
        {
            DirectoryProcess directory = Own(DirectoryProcess.Start());
            var errors = new List<Exception>();
            Probe pod = NewPod(directory, "10.0.0.1", 7001, Encoding.UTF8.GetBytes("not-the-pod-key-of-the-directory"));
            pod.Pod.OnError += errors.Add;

            pod.Pod.Start();
            Until(() => errors.Count >= 2, pod.Pod);

            Assert.IsFalse(pod.Pod.IsRegistered);
            Assert.IsInstanceOf<ConnectException>(errors[0]);
            Assert.AreEqual(StatusCode.Unauthenticated, ((ConnectException)errors[0]).Status!.Code);
        }

        private static byte[] PodKey(uint podId)
        {
            byte[] info = Encoding.ASCII.GetBytes("fomoxa directory pod key").Concat(BitConverter.GetBytes(podId)).ToArray();
            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(DirectoryProcess.RootKey, null, info));
            var key = new byte[32];
            hkdf.GenerateBytes(key, 0, key.Length);
            return key;
        }

        private static PodState ReadPod(DirectoryProcess directory, uint podId) => directory.Pods().Single(state => state.PodId == podId);

        private static AllocateResponse Allocate(DirectoryProcess directory, string roomKey)
        {
            using RpcConnection backend = directory.Backend();
            return Settled(new DirectoryClient(backend).AllocateAsync(new AllocateRequest { RoomKey = roomKey, Version = "1.0.0", Tickets = 1 }));
        }

        private static T Settled<T>(Task<T> task)
        {
            Assert.IsTrue(task.Wait(Patience), "the call did not finish in time");
            return task.Result;
        }

        private static void Pump(TimeSpan duration, params DirectoryPod[] pods)
        {
            TimeSpan until = MonotonicClock.Now + duration;
            while (MonotonicClock.Now < until)
            {
                foreach (DirectoryPod pod in pods)
                {
                    pod.Tick();
                }

                Thread.Sleep(5);
            }
        }

        private static void Until(Func<bool> done, params DirectoryPod[] pods)
        {
            TimeSpan until = MonotonicClock.Now + Patience;
            while (!done())
            {
                Assert.IsTrue(MonotonicClock.Now < until, "the condition did not hold in time");
                foreach (DirectoryPod pod in pods)
                {
                    pod.Tick();
                }

                Thread.Sleep(5);
            }
        }

        private static void Settle(Task task, params DirectoryPod[] pods)
        {
            Until(() => task.IsCompleted, pods);
            task.GetAwaiter().GetResult();
        }

        private static T Settle<T>(Task<T> task, params DirectoryPod[] pods)
        {
            Until(() => task.IsCompleted, pods);
            return task.GetAwaiter().GetResult();
        }

        private static Status Failure(Task task, params DirectoryPod[] pods)
        {
            Until(() => task.IsCompleted, pods);
            Assert.IsTrue(task.IsFaulted, "the call succeeded");
            return ((RpcException)task.Exception!.InnerException!).Status;
        }

        private T Own<T>(T disposable) where T : IDisposable
        {
            owned.Add(disposable);
            return disposable;
        }

        private Probe Registered(DirectoryProcess directory, string address, ushort port)
        {
            Probe probe = NewPod(directory, address, port);
            probe.Pod.Start();
            Until(() => probe.Pod.IsRegistered, probe.Pod);
            return probe;
        }

        private Probe NewPod(DirectoryProcess directory, string address, ushort port, byte[]? key = null)
        {
            var settings = new DirectoryPodSettings
            {
                DirectoryAddress = "127.0.0.1",
                DirectoryPort = directory.Port,
                Key = key ?? Encoding.UTF8.GetBytes(DirectoryProcess.PodKey),
                Address = address,
                Port = port,
                Target = $"pod-{port}",
                Version = "1.0.0",
                Region = "local",
                Labels = { ["map"] = "arena" },
                MaxPlayers = 10,
                RetryDelay = TimeSpan.FromMilliseconds(100),
            };
            var probe = new Probe();
            probe.Pod = new DirectoryPod(settings, () => probe.Players, () => probe.ServerStops++, () => MonotonicClock.Now, () => DateTimeOffset.UtcNow);
            pods.Add(probe.Pod);
            return probe;
        }

        private sealed class Probe
        {
            public DirectoryPod Pod { get; set; } = null!;

            public int Players { get; set; }

            public int ServerStops { get; set; }
        }
    }
}
