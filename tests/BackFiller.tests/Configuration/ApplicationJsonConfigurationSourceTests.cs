using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Configuration
{
    public sealed class ApplicationJsonConfigurationSourceTests
    {
        [Fact]
        public void Production_assembly_names_the_configuration_files()
        {
            Assert.Equal("VectorNNTP.BackFiller", typeof(ApplicationJsonConfiguration).Assembly.GetName().Name);
            Assert.Equal("VectorNNTP.RabbitMQ.json", ApplicationJsonConfiguration.SharedRabbitMqFileName);

            var root = RepositoryFiles.Root();
            Assert.True(File.Exists(RelativePath.Under(root, "src", "BackFiller", "VectorNNTP.BackFiller.json")));
            Assert.True(File.Exists(RelativePath.Under(root, "src", "BackFiller", "VectorNNTP.RabbitMQ.json")));
            Assert.False(File.Exists(RelativePath.Under(root, "src", "BackFiller", "appsettings.json")));
            Assert.False(File.Exists(RelativePath.Under(root, "src", "BackFiller", "RabbitMq.json")));
        }

        [Fact]
        public void Host_defaults_are_removed_and_entry_assembly_files_are_registered()
        {
            var builder = new ConfigurationBuilder();
            builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
            builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
            builder.AddJsonFile("VectorNNTP.BackFiller.settings.json", optional: true, reloadOnChange: true);
            builder.AddEnvironmentVariables();

            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production");

            var primary = ApplicationJsonConfiguration.PrimaryJsonFileName();
            var environment = ApplicationJsonConfiguration.EnvironmentJsonFileName("Production");
            var files = FileSources(builder).ToArray();
            var actual = files.Select(static source => source.Path ?? string.Empty).ToArray();
            Assert.Equal(
                new[] { primary, environment, ApplicationJsonConfiguration.SharedRabbitMqFileName },
                actual);
            Assert.False(files[0].Optional);
            Assert.False(files[0].ReloadOnChange);
            Assert.True(files[1].Optional);
            Assert.True(files[1].ReloadOnChange);
            Assert.True(files[2].Optional);
            Assert.True(files[2].ReloadOnChange);
            Assert.IsType<EnvironmentVariablesConfigurationSource>(builder.Sources[^1]);
        }

        [Fact]
        public void Entry_assembly_files_are_added_when_the_host_registered_no_json_sources()
        {
            var builder = new ConfigurationBuilder();
            builder.AddEnvironmentVariables();

            ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
            ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production");

            var files = FileSources(builder).ToArray();
            Assert.Equal(3, files.Length);
            Assert.All(files, static source => Assert.True(source.Optional));
            Assert.All(files, static source => Assert.True(source.ReloadOnChange));
            Assert.IsType<EnvironmentVariablesConfigurationSource>(builder.Sources[^1]);
        }

        [Fact]
        public void Missing_optional_files_do_not_fail_configuration_build()
        {
            var directory = Directory.CreateTempSubdirectory("bf-json-missing-");
            try
            {
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(builder, "Production");

                var configuration = builder.Build();
                Assert.Null(configuration["BackFiller:ServerId"]);
                Assert.Null(configuration["RabbitMQ:Hosts:0"]);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        [Fact]
        public void Missing_required_primary_file_fails_configuration_build()
        {
            var directory = Directory.CreateTempSubdirectory("bf-json-required-");
            try
            {
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");

                Assert.Throws<FileNotFoundException>(() => builder.Build());
            }
            finally
            {
                directory.Delete(true);
            }
        }

        [Fact]
        public void Invalid_primary_json_fails_configuration_build()
        {
            var directory = Directory.CreateTempSubdirectory("bf-json-invalid-");
            try
            {
                File.WriteAllText(
                    Path.Combine(directory.FullName, ApplicationJsonConfiguration.PrimaryJsonFileName()),
                    "{");
                var builder = new ConfigurationBuilder();
                builder.SetBasePath(directory.FullName);
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                builder.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: false);
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(builder, "Production");

                var exception = Assert.Throws<InvalidDataException>(() => builder.Build());
                Assert.IsType<FormatException>(exception.InnerException);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        [Fact]
        public void Generic_host_builder_does_not_keep_appsettings_sources()
        {
            var directory = Directory.CreateTempSubdirectory("bf-host-json-");
            try
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [],
                    ContentRootPath = directory.FullName,
                });
                ApplicationJsonConfiguration.UseEntryAssemblyJsonFiles(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);
                ApplicationJsonConfiguration.AddSharedRabbitMqJsonFile(
                    builder.Configuration,
                    builder.Environment.EnvironmentName);

                var paths = FileSources(builder.Configuration).Select(static source => source.Path).ToArray();
                Assert.DoesNotContain(
                    paths,
                    static path => string.Equals(path, "appsettings.json", StringComparison.OrdinalIgnoreCase)
                        || (path?.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) ?? false)
                        || (path?.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase) ?? false));
                Assert.Contains(ApplicationJsonConfiguration.PrimaryJsonFileName(), paths);
                Assert.Contains(
                    ApplicationJsonConfiguration.EnvironmentJsonFileName(builder.Environment.EnvironmentName),
                    paths);
                Assert.Contains(ApplicationJsonConfiguration.SharedRabbitMqFileName, paths);
            }
            finally
            {
                directory.Delete(true);
            }
        }

        private static IEnumerable<FileConfigurationSource> FileSources(IConfigurationBuilder configuration)
        {
            foreach (var source in configuration.Sources)
            {
                if (source is FileConfigurationSource file)
                {
                    yield return file;
                }
            }
        }
    }
}
