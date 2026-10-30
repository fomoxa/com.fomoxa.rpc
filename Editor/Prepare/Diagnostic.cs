using System;
using System.Collections.Generic;
using System.Linq;

namespace Fomoxa.Rpc.Prepare
{
    public sealed record Diagnostic(string Severity, string Code, string Message)
    {
        public static Diagnostic Error(string code, string message) => new Diagnostic("error", code, message);

        public static Diagnostic Warning(string code, string message) => new Diagnostic("warning", code, message);

        public override string ToString() => $"{Severity} {Code}: {Message}";
    }

    public sealed class PrepareException : Exception
    {
        public PrepareException(IReadOnlyList<Diagnostic> diagnostics)
            : base(string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString())))
        {
            Diagnostics = diagnostics;
        }

        public PrepareException(Diagnostic diagnostic) : this(new[] { diagnostic })
        {
        }

        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public IEnumerable<string> Codes => Diagnostics.Select(diagnostic => diagnostic.Code);
    }
}
