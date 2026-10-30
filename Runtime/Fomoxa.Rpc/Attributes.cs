using System;

namespace Fomoxa.Rpc
{
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
    public sealed class ServiceAttribute : Attribute
    {
        public ServiceAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    public abstract class RpcMethodAttribute : Attribute
    {
        public bool Idempotent { get; set; }

        public string? Deadline { get; set; }

        public string? Requires { get; set; }

        public string? Idle { get; set; }

        public bool Parallel { get; set; }

        public string? Model { get; set; }

        public Type[]? Details { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class UnaryAttribute : RpcMethodAttribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ServerStreamAttribute : RpcMethodAttribute
    {
    }
}
