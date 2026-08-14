using System;
using System.Reflection;

namespace Thalovant
{
    /// <summary>
    /// Single source of truth for the SDK version and for every user agent
    /// derived from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version is read from the assembly at runtime, so the csproj
    /// <c>&lt;Version&gt;</c> is the only place in the repository that names it.
    /// Never hard-code a version inside a user-agent literal anywhere else:
    /// <c>tests/Thalovant.Sdk.Tests/VersionTests.cs</c> enforces it, because a
    /// second hand-maintained copy is exactly how the Python SDK shipped a
    /// stale data-plane user agent for four releases.
    /// </para>
    /// <para>
    /// Works identically on <c>net8.0</c> and <c>netstandard2.1</c>: it uses
    /// only <c>System.Reflection</c> APIs present in netstandard2.0.
    /// </para>
    /// </remarks>
    public static class ThalovantSdkVersion
    {
        /// <summary>Product token shared by every Thalovant .NET SDK user agent.</summary>
        public const string Product = "ThalovantDotNetSDK";

        // Declaration order matters: static field initializers run top to
        // bottom, so Version must be assigned before UserAgent reads it.

        /// <summary>The SDK version, as declared by the csproj <c>&lt;Version&gt;</c>.</summary>
        public static readonly string Version = ResolveVersion();

        /// <summary>The user agent sent by both the control plane and the data plane.</summary>
        public static readonly string UserAgent = Product + "/" + Version;

        private static string ResolveVersion()
        {
            var assembly = typeof(ThalovantSdkVersion).GetTypeInfo().Assembly;

            // AssemblyInformationalVersionAttribute is generated from the csproj
            // <Version>. SourceLink and CI builds append "+<commit sha>" as SemVer
            // build metadata, which never belongs in a user agent.
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (informational is { Length: > 0 })
            {
                var metadata = informational.IndexOf('+');
                var version = metadata >= 0 ? informational.Substring(0, metadata) : informational;
                if (version.Length > 0)
                {
                    return version;
                }
            }

            // Fallback for assemblies whose informational version was stripped
            // (some merge/trim tooling). AssemblyName.Version is always
            // four-part; the SDK versions are three-part, so drop the revision.
            var assemblyVersion = assembly.GetName().Version;
            return assemblyVersion is null
                ? "0.0.0"
                : assemblyVersion.Major + "." + assemblyVersion.Minor + "." + assemblyVersion.Build;
        }
    }
}
