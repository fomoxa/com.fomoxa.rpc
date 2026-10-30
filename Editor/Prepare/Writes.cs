using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Fomoxa.Rpc.Prepare
{
    public sealed class Writes
    {
        private readonly string root;
        private readonly string emptyModels;
        private readonly string systemModels;
        private readonly SortedDictionary<string, string> planned = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, Header?> owned = new SortedDictionary<string, Header?>(StringComparer.Ordinal);

        public Writes(string project) : this(project, ModelFiles.RpcModels, ModelFiles.SystemModelsCopy)
        {
        }

        public Writes(string root, string emptyModels, string systemModels)
        {
            this.root = root;
            this.emptyModels = emptyModels.Replace('\\', '/').TrimEnd('/');
            this.systemModels = systemModels.Replace('\\', '/').TrimEnd('/');
        }

        public static List<EmptyModel> EmptyModels(IEnumerable<Service> services)
        {
            var models = new List<EmptyModel>();
            var errors = new List<Diagnostic>();
            foreach (var service in services)
            {
                foreach (var method in service.Methods.Where(method => method.HasNoModelParameter))
                {
                    string name = method.Model ?? service.Name + method.Name;
                    var first = models.FirstOrDefault(model => model.Name == name);
                    if (first != null)
                    {
                        errors.Add(Diagnostic.Error("FRPC001",
                            $"{first.Service}.{first.Method} and {service.Name}.{method.Name} share the request model {name}\n"
                            + "  a method is identified by the message id of its request model (01-design §3.3)\n"
                            + "  give one of them its own request model"));
                        continue;
                    }
                    models.Add(new EmptyModel(name, service.Name, method.Name, service.Namespace));
                }
            }
            if (errors.Count > 0)
            {
                throw new PrepareException(errors);
            }
            return models.OrderBy(model => model.Name, StringComparer.Ordinal).ToList();
        }

        public void CopySystemModels(string source)
        {
            Own(systemModels, ModelFiles.CopyMarker);
            foreach (string relative in List(source, "").Where(path => path.EndsWith(".cs", StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(Path.Combine(source, relative)).Replace("\r\n", "\n");
                planned[systemModels + "/" + relative] = ModelFiles.CopyMarker + "\n" + text;
            }
        }

        public void EmptyModels(IEnumerable<EmptyModel> models)
        {
            Own(emptyModels, ModelFiles.Marker);
            foreach (var model in models)
            {
                planned[emptyModels + "/" + model.Name + ".cs"] = Render(model);
            }
        }

        public void AddEmptyModels(IEnumerable<EmptyModel> models)
        {
            Own(emptyModels, ModelFiles.Marker);
            foreach (string path in owned.Keys.Where(path => path.StartsWith(emptyModels + "/", StringComparison.Ordinal)))
            {
                planned[path] = File.ReadAllText(Path.Combine(root, path));
            }
            foreach (var model in models)
            {
                planned[emptyModels + "/" + model.Name + ".cs"] = Render(model);
            }
        }

        public List<Diagnostic> Renames(IReadOnlyList<EmptyModel> models)
        {
            var warnings = new List<Diagnostic>();
            var taken = new List<string>();
            var old = owned.Values.Where(header => header != null).Select(header => header!).ToList();
            foreach (var header in old)
            {
                if (models.Any(model => model.Name == header.Model))
                {
                    continue;
                }
                var renamed = models.FirstOrDefault(model =>
                    model.Service == header.Service && !taken.Contains(model.Name) && old.All(earlier => earlier.Model != model.Name));
                if (renamed == null)
                {
                    continue;
                }
                taken.Add(renamed.Name);
                warnings.Add(Diagnostic.Warning("FRPC020",
                    $"renaming {header.Service}.{header.Method} to {renamed.Service}.{renamed.Method} changes its message id\n"
                    + $"  {header.Model}.rpc becomes {renamed.Name}.rpc; peers built before the rename answer UNIMPLEMENTED\n"
                    + $"  to keep the id, write [Unary(Model = \"{header.Model}\")]"));
            }
            return warnings;
        }

        public (List<string> Written, List<string> Removed) Apply()
        {
            var written = new List<string>();
            var removed = new List<string>();
            foreach (var (relative, text) in planned)
            {
                string full = Path.Combine(root, relative);
                if (File.Exists(full) && File.ReadAllText(full) == text)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
                written.Add(relative);
            }
            foreach (string relative in owned.Keys.Where(relative => !planned.ContainsKey(relative)))
            {
                File.Delete(Path.Combine(root, relative));
                removed.Add(relative);
            }
            string models = Path.Combine(root, emptyModels);
            if (Directory.Exists(models) && !Directory.EnumerateFileSystemEntries(models).Any())
            {
                Directory.Delete(models);
            }
            return (written, removed);
        }

        private void Own(string directory, string marker)
        {
            var errors = new List<Diagnostic>();
            foreach (string relative in List(Path.Combine(root, directory), "").Where(relative => relative.EndsWith(".cs", StringComparison.Ordinal)))
            {
                string path = directory + "/" + relative;
                string text = File.ReadAllText(Path.Combine(root, path));
                if (text.Split('\n')[0].TrimEnd('\r') != marker)
                {
                    errors.Add(Diagnostic.Error("FRPC030",
                        $"{path} has no generated-file marker\n"
                        + $"  the file was written by hand or edited; move it out of {directory}/ or delete it"));
                    continue;
                }
                owned[path] = Header.Of(text);
            }
            if (errors.Count > 0)
            {
                throw new PrepareException(errors);
            }
        }

        private static string Render(EmptyModel model)
        {
            string header = $"{ModelFiles.Marker}\n// DO NOT EDIT MANUALLY\n// service: {model.Service}\n// method: {model.Method}\n// model: {model.Name}\n\nusing Fomoxa;\n\n";
            string body = $"[Network]\n[Codec(\"rpc\")]\npublic class {model.Name}\n{{\n}}\n";
            if (model.Namespace == null)
            {
                return header + body;
            }
            string indented = string.Join("\n", body.TrimEnd('\n').Split('\n').Select(line => "    " + line)) + "\n";
            return header + $"namespace {model.Namespace}\n{{\n{indented}}}\n";
        }

        private static List<string> List(string root, string relative)
        {
            var found = new List<string>();
            string directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory))
            {
                return found;
            }
            foreach (string entry in Directory.GetFileSystemEntries(directory).OrderBy(entry => entry, StringComparer.Ordinal))
            {
                string path = relative == "" ? Path.GetFileName(entry) : relative + "/" + Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    found.AddRange(List(root, path));
                }
                else
                {
                    found.Add(path);
                }
            }
            return found;
        }

        private sealed record Header(string Service, string Method, string Model)
        {
            public static Header? Of(string text)
            {
                string? Field(string key) => text.Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.StartsWith($"// {key}: ", StringComparison.Ordinal))
                    .Select(line => line.Substring(key.Length + 5))
                    .FirstOrDefault();
                string? service = Field("service");
                string? method = Field("method");
                string? model = Field("model");
                return service == null || method == null || model == null ? null : new Header(service, method, model);
            }
        }
    }
}
