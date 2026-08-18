using System;

namespace Kylin.DI
{
    internal static class ResolverAuthorityGuard
    {
        internal static bool IsResolverType(Type type)
        {
            return type != null && typeof(IScope).IsAssignableFrom(type);
        }

        internal static void ThrowIfRegistrationType(Type type, string role)
        {
            if (!IsResolverType(type)) return;

            throw new InvalidOperationException(
                $"[KDI] {role} {type.Name} exposes resolver authority and cannot be registered. " +
                "Declare concrete dependencies with [Inject] and use IInstantiator for dynamic Unity object creation.");
        }

        internal static void ThrowIfResolverInstance(object instance, string source)
        {
            if (!(instance is IScope)) return;

            throw new InvalidOperationException(
                $"[KDI] {source} returned or registered an IScope resolver capability. " +
                "Scopes cannot become services, aliases, factory results, or injected values.");
        }

        internal static Scope RequireConcreteScope(IScope scope, string operation)
        {
            if (scope is Scope concreteScope)
                return concreteScope;

            throw new NotSupportedException(
                $"[KDI] {operation} requires a KDI Scope so its work can share the activation/lifetime ledger.");
        }
    }
}
