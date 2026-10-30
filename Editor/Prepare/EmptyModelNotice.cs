using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fomoxa.Rpc.Prepare
{
    public static class EmptyModelNotice
    {
        private static readonly Regex Line = new Regex(
            @"(?<service>[A-Za-z0-9_]+)\.(?<method>[A-Za-z_][A-Za-z0-9_]*) needs the empty model (?<model>[A-Za-z_][A-Za-z0-9_.]*), which has no rpc codec yet");

        public static string Format(EmptyModel model) =>
            $"{model.Service}.{model.Method} needs the empty model {Qualified(model)}, which has no rpc codec yet";

        public static List<EmptyModel> Parse(IEnumerable<string> messages)
        {
            var models = new List<EmptyModel>();
            foreach (string message in messages)
            {
                foreach (Match match in Line.Matches(message))
                {
                    string qualified = match.Groups["model"].Value;
                    int dot = qualified.LastIndexOf('.');
                    string name = dot < 0 ? qualified : qualified.Substring(dot + 1);
                    string? space = dot < 0 ? null : qualified.Substring(0, dot);
                    if (models.All(model => model.Name != name))
                    {
                        models.Add(new EmptyModel(name, match.Groups["service"].Value, match.Groups["method"].Value, space));
                    }
                }
            }
            return models;
        }

        private static string Qualified(EmptyModel model) => model.Namespace == null ? model.Name : model.Namespace + "." + model.Name;
    }
}
