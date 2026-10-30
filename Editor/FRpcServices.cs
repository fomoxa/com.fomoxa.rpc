using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Fomoxa.Rpc.Prepare;
using UnityEditor;

namespace Fomoxa.Rpc.Editor
{
    public static class FRpcServices
    {
        public static IReadOnlyList<Service> InThisEditor() => Of(TypeCache.GetTypesWithAttribute<ServiceAttribute>());

        public static IReadOnlyList<Service> Of(IEnumerable<Type> types) =>
            types.Where(type => type.IsInterface && type.GetCustomAttribute<ServiceAttribute>() != null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Select(ServiceOf)
                .ToList();

        private static Service ServiceOf(Type type) =>
            new Service(
                type.GetCustomAttribute<ServiceAttribute>()!.Name,
                string.IsNullOrEmpty(type.Namespace) ? null : type.Namespace,
                type.Assembly.GetName().Name + ": " + type.FullName,
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .OrderBy(method => method.MetadataToken)
                    .Select(MethodOf)
                    .Where(method => method != null)
                    .Select(method => method!)
                    .ToList());

        private static Prepare.Method? MethodOf(MethodInfo declared)
        {
            RpcMethodAttribute? attribute = declared.GetCustomAttribute<RpcMethodAttribute>();
            if (attribute == null)
            {
                return null;
            }

            var requests = new List<string>();
            string? response = null;
            string? item = null;
            foreach (ParameterInfo parameter in declared.GetParameters())
            {
                Type type = parameter.ParameterType;
                if (type == typeof(CallContext))
                {
                    continue;
                }

                if (IsGeneric(type, typeof(Reply<>)))
                {
                    response = type.GetGenericArguments()[0].Name;
                }
                else if (IsGeneric(type, typeof(ItemStream<>)))
                {
                    item = type.GetGenericArguments()[0].Name;
                }
                else
                {
                    requests.Add(type.Name);
                }
            }

            Type returned = declared.ReturnType;
            if (IsGeneric(returned, typeof(Task<>)))
            {
                response = returned.GetGenericArguments()[0].Name;
            }
            else if (returned != typeof(void) && returned != typeof(Task))
            {
                response = returned.Name;
            }

            return new Prepare.Method(
                declared.Name,
                attribute is UnaryAttribute ? Prepare.Kind.Unary : Prepare.Kind.ServerStream,
                attribute.Model,
                requests,
                response,
                item,
                (attribute.Details ?? Array.Empty<Type>()).Select(detail => detail.Name).ToList());
        }

        private static bool IsGeneric(Type type, Type definition) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == definition;
    }
}
