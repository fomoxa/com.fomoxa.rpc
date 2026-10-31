using System.Linq;
using Fomoxa.Rpc.Directory;
using FomoxaDirectoryCodecs;
using NUnit.Framework;

namespace Fomoxa.Rpc.Tests
{
    public sealed class DirectorySchemaTest
    {
        private const ulong GatewayDirectoryModelFingerprint = 0x89B3F912AD5AFF5A;

        [Test]
        public void TheSchemaFingerprintEqualsTheRustDirectoryModel()
        {
            Assert.AreEqual(GatewayDirectoryModelFingerprint, Handshake.FomoxaSchemaFingerprint);
            Assert.AreEqual(GatewayDirectoryModelFingerprint, NetSchema.Build().Fingerprint);
        }

        [Test]
        public void ThePodSideDeclaresEveryDirectoryMethod()
        {
            CollectionAssert.AreEquivalent(
                new[] { "Allocate", "Relocate", "RegisterPod", "ReportLoad", "AcquireRoom", "ReleaseRoom", "WatchDrain", "RequestDrain", "WatchPods" },
                DirectoryMethods.All.Select(method => method.Name.Split('.').Last()));
            CollectionAssert.AreEquivalent(new[] { "Drain", "ReadPods" }, DirectoryAdminMethods.All.Select(method => method.Name.Split('.').Last()));
        }

        [Test]
        public void ARegistrationRoundTripsThroughItsCodec()
        {
            var registration = new RegisterPod
            {
                PodId = 7,
                Address = "10.0.0.7",
                Port = 7777,
                Target = "pod-7",
                Version = "1.2.3",
                Region = "ap-southeast",
                Labels = { new DirectoryLabel { Name = "map", Value = "arena" } },
                MaxPlayers = 64,
                Rooms = { "arena:1", "arena:2" },
            };

            RegisterPod decoded = DirectoryMethods.RegisterPod.Request.Decode(DirectoryMethods.RegisterPod.Request.Encode(registration));

            Assert.AreEqual(7u, decoded.PodId);
            Assert.AreEqual("10.0.0.7", decoded.Address);
            Assert.AreEqual(7777, decoded.Port);
            Assert.AreEqual("arena", decoded.Labels.Single().Value);
            CollectionAssert.AreEqual(new[] { "arena:1", "arena:2" }, decoded.Rooms);
        }
    }
}
