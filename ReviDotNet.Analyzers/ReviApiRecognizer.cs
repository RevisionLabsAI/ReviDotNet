// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.CodeAnalysis;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Recognizes invocations that target ReviDotNet's inference or agent surfaces, so the analyzers
    /// fire on BOTH the static <c>Infer</c>/<c>Agent</c> classes and the dependency-injected services
    /// (<c>IInferService</c>/<c>IAgentService</c> and their implementations, including the
    /// <c>ReviClient.Infer</c>/<c>ReviClient.Agent</c> facade properties whose static type is the interface).
    /// </summary>
    internal static class ReviApiRecognizer
    {
        /// <summary>True when the method belongs to the inference surface (static <c>Infer</c> or <c>IInferService</c>).</summary>
        public static bool IsInferSurface(IMethodSymbol? method)
            => Matches(method?.ContainingType, "Infer", "IInferService", "InferService");

        /// <summary>True when the method belongs to the agent surface (static <c>Agent</c> or <c>IAgentService</c>).</summary>
        public static bool IsAgentSurface(IMethodSymbol? method)
            => Matches(method?.ContainingType, "Agent", "IAgentService", "AgentService");

        private static bool Matches(INamedTypeSymbol? type, string staticName, string interfaceName, string implName)
        {
            if (type == null)
                return false;

            // Direct match: the static class, the interface, or a concrete service implementation.
            if ((type.Name == staticName || type.Name == interfaceName || type.Name == implName) && InReviNamespace(type))
                return true;

            // Any type that implements the service interface (e.g. a custom IInferService).
            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (iface.Name == interfaceName && InReviNamespace(iface))
                    return true;
            }

            return false;
        }

        private static bool InReviNamespace(INamedTypeSymbol type)
        {
            string? ns = type.ContainingNamespace?.Name;
            return ns == "Revi" || ns == "ReviDotNet";
        }
    }
}
