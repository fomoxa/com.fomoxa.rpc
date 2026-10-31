using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fomoxa.Networking;
using Fomoxa.Rpc.Prepare;
using Fomoxa.Unity.Editor;
using UnityEditor;

namespace Fomoxa.Rpc.Editor
{
    [InitializeOnLoad]
    public sealed class FRpcSourceStep : IFomoxaSourceStep
    {
        public const string CoreVersion = "0.2.0";
        public const string EmptyModelsFolder = "FRpcModels";
        public const string SystemModelsCopy = "FomoxaRpcSystemModels";

        private readonly string? systemModelsSource;
        private readonly Func<IReadOnlyList<Service>> services;

        static FRpcSourceStep()
        {
            FomoxaSourceSteps.Register(new FRpcSourceStep(SystemModelsSourceForThisEditor(), FRpcServices.InThisEditor));
        }

        public FRpcSourceStep(string? systemModelsSource, Func<IReadOnlyList<Service>> services)
        {
            this.systemModelsSource = systemModelsSource;
            this.services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public static string? SystemModelsSourceForThisEditor()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FRpcSourceStep).Assembly);
            return package == null ? null : Path.Combine(package.resolvedPath, "Runtime", "Fomoxa.Rpc", "SystemModels");
        }

        public static string EmptyModelsFor(string generatedFolder) =>
            generatedFolder.Replace('\\', '/').TrimEnd('/') + "/" + EmptyModelsFolder;

        public FomoxaSourceResult Prepare(FomoxaCompilation compilation)
        {
            if (NetworkRuntime.Version != CoreVersion)
            {
                return FomoxaSourceResult.Failure($"com.fomoxa.rpc needs com.fomoxa.networking {CoreVersion}; the project has {NetworkRuntime.Version}");
            }

            if (systemModelsSource == null || !Directory.Exists(systemModelsSource))
            {
                return FomoxaSourceResult.Failure($"the system models of com.fomoxa.rpc are missing: {systemModelsSource ?? "package not found"}");
            }

            string emptyModels = EmptyModelsFor(compilation.GeneratedFolder);
            try
            {
                var writes = new Writes(compilation.ProjectRoot, emptyModels, SystemModelsCopy);
                writes.CopySystemModels(systemModelsSource);
                var warnings = new List<Diagnostic>();
                if (compilation.Failed)
                {
                    writes.AddEmptyModels(EmptyModelNotice.Parse(compilation.Errors));
                }
                else
                {
                    List<EmptyModel> models = Writes.EmptyModels(services());
                    writes.EmptyModels(models);
                    warnings.AddRange(writes.Renames(models));
                }

                writes.Apply();
                var folders = new List<string>();
                if (Directory.Exists(Path.Combine(compilation.ProjectRoot, emptyModels)))
                {
                    folders.Add(emptyModels);
                }

                folders.Add(SystemModelsCopy);
                return FomoxaSourceResult.Success(folders, warnings.Count == 0 ? null : string.Join("\n", warnings.Select(warning => warning.ToString())));
            }
            catch (PrepareException failed)
            {
                return FomoxaSourceResult.Failure(failed.Message);
            }
            catch (IOException failed)
            {
                return FomoxaSourceResult.Failure($"the fRPC step could not write its files: {failed.Message}");
            }
        }
    }
}
