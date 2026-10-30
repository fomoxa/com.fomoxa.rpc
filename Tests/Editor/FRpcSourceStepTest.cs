using System;
using System.Collections.Generic;
using System.IO;
using Fomoxa.Rpc.Prepare;
using Fomoxa.Unity.Editor;
using NUnit.Framework;

namespace Fomoxa.Rpc.Editor.Tests
{
    public sealed class FRpcSourceStepTest
    {
        private const string Generated = "Assets/Generated";
        private const string EmptyModels = Generated + "/" + FRpcSourceStep.EmptyModelsFolder;

        private static readonly Prepare.Method Say = new Prepare.Method("Say", Prepare.Kind.Unary, null, new[] { "EchoRequest" }, "EchoResponse", null, Array.Empty<string>());

        private static readonly Prepare.Method Ping = new Prepare.Method("Ping", Prepare.Kind.Unary, null, Array.Empty<string>(), null, null, Array.Empty<string>());

        private static readonly Prepare.Method Probe = new Prepare.Method("Probe", Prepare.Kind.Unary, "EchoProbeRequest", Array.Empty<string>(), null, null, Array.Empty<string>());

        private string projectRoot = "";

        private static IReadOnlyList<Service> Echo(params Prepare.Method[] methods) =>
            new[] { new Service("Echo", "Sample.Services", "Sample: Sample.Services.IEcho", methods) };

        private FomoxaSourceResult Run(bool failed, IReadOnlyList<string> errors, Func<IReadOnlyList<Service>> services) =>
            new FRpcSourceStep(FRpcSourceStep.SystemModelsSourceForThisEditor(), services)
                .Prepare(new FomoxaCompilation(projectRoot, Generated, failed, errors));

        private FomoxaSourceResult Succeeded(params Prepare.Method[] methods) => Run(false, Array.Empty<string>(), () => Echo(methods));

        private FomoxaSourceResult Failed(params string[] errors) =>
            Run(true, errors, () => throw new AssertionException("a failed compilation does not read the services"));

        private string Read(string relative) => File.ReadAllText(Path.Combine(projectRoot, relative));

        private bool Exists(string relative) => File.Exists(Path.Combine(projectRoot, relative));

        [SetUp]
        public void CreateProject()
        {
            projectRoot = Path.GetFullPath(Path.Combine("Temp", "FRpcSourceStepTest", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(projectRoot);
        }

        [TearDown]
        public void DeleteProject()
        {
            Directory.Delete(projectRoot, true);
        }

        [Test]
        public void TheSystemModelsOfThePackageAreFound()
        {
            Assert.IsTrue(File.Exists(Path.Combine(FRpcSourceStep.SystemModelsSourceForThisEditor(), "FRpc.cs")));
        }

        [Test]
        public void AMethodWithoutAModelGetsItsEmptyModelAfterASuccessfulCompilation()
        {
            FomoxaSourceResult result = Succeeded(Say, Ping, Probe);

            Assert.IsTrue(result.Succeeded, result.Error);
            CollectionAssert.AreEqual(new[] { EmptyModels, FRpcSourceStep.SystemModelsCopy }, result.SourceFolders);
            Assert.IsNull(result.Warnings);
            StringAssert.StartsWith(ModelFiles.Marker, Read(EmptyModels + "/EchoPing.cs"));
            StringAssert.Contains("namespace Sample.Services", Read(EmptyModels + "/EchoPing.cs"));
            StringAssert.Contains("public class EchoProbeRequest", Read(EmptyModels + "/EchoProbeRequest.cs"));
            Assert.IsFalse(Exists(EmptyModels + "/EchoSay.cs"));
            StringAssert.StartsWith(ModelFiles.CopyMarker, Read(FRpcSourceStep.SystemModelsCopy + "/FRpc.cs"));
        }

        [Test]
        public void WithoutEmptyModelsOnlyTheSystemModelsJoinTheRun()
        {
            FomoxaSourceResult result = Succeeded(Say);

            Assert.IsTrue(result.Succeeded, result.Error);
            CollectionAssert.AreEqual(new[] { FRpcSourceStep.SystemModelsCopy }, result.SourceFolders);
            Assert.IsFalse(Directory.Exists(Path.Combine(projectRoot, EmptyModels)));
        }

        [Test]
        public void TheNoticeOfAFailedCompilationAddsItsModelAndKeepsTheOthers()
        {
            Succeeded(Ping);
            string ping = Read(EmptyModels + "/EchoPing.cs");

            FomoxaSourceResult result = Failed(
                "Assets/Services/Echo.cs(9,14): error FRPC003: Echo.Check needs the empty model Sample.Services.EchoCheck, which has no rpc codec yet",
                "Assets/Other.cs(1,1): error CS1002: ; expected");

            Assert.IsTrue(result.Succeeded, result.Error);
            CollectionAssert.AreEqual(new[] { EmptyModels, FRpcSourceStep.SystemModelsCopy }, result.SourceFolders);
            StringAssert.Contains("// method: Check", Read(EmptyModels + "/EchoCheck.cs"));
            StringAssert.Contains("namespace Sample.Services", Read(EmptyModels + "/EchoCheck.cs"));
            Assert.AreEqual(ping, Read(EmptyModels + "/EchoPing.cs"));
        }

        [Test]
        public void AFailedCompilationWithoutNoticesRemovesNothing()
        {
            Succeeded(Ping);

            FomoxaSourceResult result = Failed("Assets/Other.cs(1,1): error CS1002: ; expected");

            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.IsTrue(Exists(EmptyModels + "/EchoPing.cs"));
        }

        [Test]
        public void TheEmptyModelOfARemovedMethodIsDeletedAndItsMetaFileLeftToUnity()
        {
            Succeeded(Ping);
            File.WriteAllText(Path.Combine(projectRoot, EmptyModels, "EchoPing.cs.meta"), "guid: 0\n");

            FomoxaSourceResult result = Succeeded(Say);

            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.IsFalse(Exists(EmptyModels + "/EchoPing.cs"));
            Assert.AreEqual("guid: 0\n", Read(EmptyModels + "/EchoPing.cs.meta"));
        }

        [Test]
        public void ARenamedMethodWarnsThatItsIdChanges()
        {
            Succeeded(Ping);

            FomoxaSourceResult result = Succeeded(new Prepare.Method("Check", Prepare.Kind.Unary, null, Array.Empty<string>(), null, null, Array.Empty<string>()));

            Assert.IsTrue(result.Succeeded, result.Error);
            StringAssert.Contains("warning FRPC020", result.Warnings);
            StringAssert.Contains("EchoPing.rpc becomes EchoCheck.rpc", result.Warnings);
        }

        [Test]
        public void AFileWithoutTheMarkerIsRefused()
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, EmptyModels));
            File.WriteAllText(Path.Combine(projectRoot, EmptyModels, "EchoPing.cs"), "public class EchoPing { }\n");

            FomoxaSourceResult result = Succeeded(Ping);

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("error FRPC030", result.Error);
            StringAssert.Contains(EmptyModels + "/EchoPing.cs has no generated-file marker", result.Error);
        }

        [Test]
        public void TwoMethodsSharingAnEmptyModelAreRefused()
        {
            FomoxaSourceResult result = Succeeded(Probe, new Prepare.Method("Check", Prepare.Kind.Unary, "EchoProbeRequest", Array.Empty<string>(), null, null, Array.Empty<string>()));

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("error FRPC001", result.Error);
        }

        [Test]
        public void MissingSystemModelsAreAnError()
        {
            FomoxaSourceResult result = new FRpcSourceStep(Path.Combine(projectRoot, "absent"), () => Echo(Ping))
                .Prepare(new FomoxaCompilation(projectRoot, Generated, false, Array.Empty<string>()));

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("the system models of com.fomoxa.rpc are missing", result.Error);
            Assert.IsFalse(Directory.Exists(Path.Combine(projectRoot, EmptyModels)));
        }
    }
}
