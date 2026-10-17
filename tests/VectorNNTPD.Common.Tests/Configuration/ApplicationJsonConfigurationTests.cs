using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Configuration
{
    /// <summary>
    /// Locks entry-assembly JSON file names and the host configuration sources that replace <c>appsettings</c>.
    /// </summary>
    public sealed class ApplicationJsonConfigurationTests
    {
        /// <summary>
        /// Asserts that the primary and Development JSON names are the entry assembly's simple name plus <c>.json</c> or <c>.Development.json</c>, and that the primary name does not contain <c>.exe</c>.
        /// </summary>
        [Fact]
        public void File_names_are_derived_from_the_entry_assembly_short_name()
        {
            var name = Assembly.GetEntryAssembly()?.GetName()?.Name;
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name + ".json", ApplicationJsonConfiguration.PrimaryJsonFileName());
            Assert.Equal(name + ".Development.json", ApplicationJsonConfiguration.EnvironmentJsonFileName("Development"));
            Assert.DoesNotContain(".exe", ApplicationJsonConfiguration.PrimaryJsonFileName(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asserts that a Development host loads the entry-assembly primary and environment JSON, keeps the primary probe and Development shared values, and does not retain <c>appsettings</c> or <c>.settings.</c> file sources.
        /// </summary>
        [Fact]
        public void Host_loads_entry_assembly_json_and_does_not_fall_back_to_appsettings()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-host-json-");
            try
            {
                var name = Assembly.GetEntryAssembly()!.GetName().Name!;
                Write(root.FullName, "appsettings.json", "legacy");
                Write(root.FullName, name + ".json", "primary", "base");
                Write(root.FullName, name + ".Development.json", shared: "development");
                Write(root.FullName, name + ".settings.json", "settings");
                Write(root.FullName, name + ".settings.Development.json", shared: "settings-development");

                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    ContentRootPath = root.FullName,
                    EnvironmentName = Environments.Development,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);

                Assert.Equal("primary", builder.Configuration["VectorNntpConfigProbe"]);
                Assert.Equal("development", builder.Configuration["VectorNntpConfigShared"]);
                Assert.DoesNotContain(
                    builder.Configuration.Sources.OfType<FileConfigurationSource>(),
                    source => source.Path is "appsettings.json"
                        or "appsettings.Development.json"
                        || (source.Path?.Contains(".settings.", StringComparison.OrdinalIgnoreCase) ?? false));
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        /// <summary>
        /// Asserts that missing entry-assembly JSON is optional and that a preloaded <c>appsettings.json</c> probe is absent after <see cref="ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles"/>.
        /// </summary>
        [Fact]
        public void Missing_entry_assembly_json_stays_optional_and_ignores_a_leftover_appsettings_file()
        {
            var root = Directory.CreateTempSubdirectory("vnntp-host-json-missing-");
            try
            {
                Write(root.FullName, "appsettings.json", "legacy");
                var builder = new ConfigurationBuilder()
                    .SetBasePath(root.FullName)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Development");

                var configuration = builder.Build();
                Assert.Null(configuration["VectorNntpConfigProbe"]);
            }
            finally
            {
                TryDelete(root.FullName);
            }
        }

        /// <summary>
        /// Writes one JSON object containing the optional probe and shared marker properties used by these facts.
        /// </summary>
        /// <param name="directory">Directory that receives the file.</param>
        /// <param name="fileName">File name, including extension, relative to <paramref name="directory"/>.</param>
        /// <param name="marker">When set, the <c>VectorNntpConfigProbe</c> value to write.</param>
        /// <param name="shared">When set, the <c>VectorNntpConfigShared</c> value to write.</param>
        private static void Write(string directory, string fileName, string? marker = null, string? shared = null)
        {
            var markerLine = marker is null ? string.Empty : $""" "VectorNntpConfigProbe": "{marker}" """;
            var sharedLine = shared is null ? string.Empty : $""" "VectorNntpConfigShared": "{shared}" """;
            var body = marker is null
                ? $$"""{ {{sharedLine}} }"""
                : shared is null
                    ? $$"""{ {{markerLine}} }"""
                    : $$"""{ {{markerLine}}, {{sharedLine}} }""";
            File.WriteAllText(Path.Combine(directory, fileName), body);
        }

        /// <summary>
        /// Deletes <paramref name="path"/> recursively. <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are ignored.
        /// </summary>
        /// <param name="path">Temporary directory to delete.</param>
        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
