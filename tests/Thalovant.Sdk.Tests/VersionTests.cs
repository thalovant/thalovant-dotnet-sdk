using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// Pins every user agent to the one version the SDK assembly declares.
    /// <para>
    /// The Python SDK shipped 0.4.20 through 0.4.23 with a stale data-plane
    /// user agent because the version lived in several hand-maintained literals
    /// and the release workflow's literal replacement silently no-opped once one
    /// copy fell behind. These tests fail loudly if a version literal ever creeps
    /// back into a user agent — deliberately never asserting a literal
    /// themselves, since a test that pins a literal is just one more copy.
    /// </para>
    /// </summary>
    public class VersionTests
    {
        private const string Product = "ThalovantDotNetSDK";

        /// <summary>
        /// The version the built SDK assembly reports, resolved independently of
        /// <see cref="ThalovantSdkVersion"/> so the two derivations cross-check.
        /// </summary>
        private static string AssemblyVersion()
        {
            var assembly = typeof(ThalovantDefaults).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            Assert.False(
                string.IsNullOrEmpty(informational),
                "Thalovant.Sdk carries no AssemblyInformationalVersionAttribute; the csproj " +
                "<Version> is what generates it.");

            // SourceLink and CI builds append "+<commit sha>" build metadata.
            var metadata = informational!.IndexOf('+');
            return metadata >= 0 ? informational.Substring(0, metadata) : informational;
        }

        /// <summary>
        /// The repository root, found by walking up from the test assembly, or
        /// <c>null</c> when the tests run outside a source checkout.
        /// </summary>
        private static DirectoryInfo? RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Thalovant.Sdk", "Thalovant.Sdk.csproj")))
                {
                    return directory;
                }
                directory = directory.Parent;
            }
            return null;
        }

        private static ThalovantIdentity WssIdentity()
        {
            return new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "access-1",
                ["password"] = "p",
                ["site_id"] = "s",
                ["default_master"] = "https://hub.example.com",
                ["data_plane_endpoints"] = new JsonObject { ["wss"] = "wss://hub.example.com/ws" },
            });
        }

        [Fact]
        public void EveryUserAgentEqualsTheAssemblyVersion()
        {
            var expected = Product + "/" + AssemblyVersion();

            Assert.Equal(expected, ThalovantSdkVersion.UserAgent);
            Assert.Equal(expected, ThalovantDefaults.UserAgent);
            Assert.Equal(Product, ThalovantSdkVersion.Product);
            Assert.Equal(AssemblyVersion(), ThalovantSdkVersion.Version);

            // Control plane.
            Assert.Equal(expected, new ThalovantControlPlane().UserAgent);

            // Data plane: both the transport built directly and the one the
            // client builds for you. This is the surface that drifted in Python.
            var identity = WssIdentity();
            using (var transport = new HiveMindWssTransport(identity))
            {
                Assert.Equal(expected, transport.UserAgent);
            }
            using (var client = new ThalovantClient(identity))
            {
                Assert.Equal(expected, client.Transport.UserAgent);
            }
        }

        [Fact]
        public void CsprojVersionMatchesTheAssemblyVersion()
        {
            var root = RepositoryRoot();
            if (root is null)
            {
                // Running from a packaged/extracted test bundle, not a checkout.
                return;
            }

            var csproj = Path.Combine(root.FullName, "src", "Thalovant.Sdk", "Thalovant.Sdk.csproj");
            var declared = Regex.Match(File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");

            Assert.True(declared.Success, csproj + " does not declare a <Version>");
            Assert.Equal(AssemblyVersion(), declared.Groups[1].Value);
        }

        [Fact]
        public void NoSourceFileHardCodesAUserAgentVersion()
        {
            var root = RepositoryRoot();
            if (root is null)
            {
                return;
            }

            var sources = Path.Combine(root.FullName, "src");
            var pinned = new Regex(Regex.Escape(Product) + @"/\d");

            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
            {
                var relative = file.Substring(root.FullName.Length + 1).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                {
                    // Build output, not tracked source.
                    continue;
                }
                if (pinned.IsMatch(File.ReadAllText(file)))
                {
                    offenders.Add(relative);
                }
            }
            offenders.Sort(StringComparer.Ordinal);

            Assert.True(
                offenders.Count == 0,
                "User agents must derive from ThalovantSdkVersion.UserAgent, which reads the " +
                "csproj <Version> off the assembly, but a pinned version literal was found in: " +
                string.Join(", ", offenders));
        }
    }
}
