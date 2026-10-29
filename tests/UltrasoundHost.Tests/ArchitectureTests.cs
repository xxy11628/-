using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.Tests;

/// <summary>
/// Enforces the dependency rules of docs/spec/01-plugin-conventions.md (R1 to R4) and
/// docs/spec/04-target-architecture.md (R6) so they cannot erode silently. Rules are checked twice:
/// in the project files (what is declared) and in the compiled assemblies (what is actually used).
/// </summary>
public class ArchitectureTests
{
    private const string ContractsName = "UltrasoundHost.Contracts";

    private static string SolutionRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Directory.Build.props")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("could not find the solution root from " + AppContext.BaseDirectory);
    }

    private static string[] PluginProjectFiles()
    {
        string pluginsDirectory = Path.Combine(SolutionRoot(), "src", "Plugins");
        return Directory.GetFiles(pluginsDirectory, "*.csproj", SearchOption.AllDirectories);
    }

    private static string[] ProjectReferences(string projectFile)
    {
        XDocument document = XDocument.Load(projectFile);
        return document.Descendants("ProjectReference")
            .Select(e => ((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/'))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToArray();
    }

    private static string[] PackageReferences(string projectFile)
    {
        XDocument document = XDocument.Load(projectFile);
        return document.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToArray();
    }

    private static string[] UsedSolutionAssemblies(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("UltrasoundHost.", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void There_is_at_least_one_plugin_project_to_check()
    {
        Assert.NotEmpty(PluginProjectFiles());
    }

    [Fact]
    public void R1_Contracts_references_no_project_and_only_the_DI_abstractions_package()
    {
        string project = Path.Combine(SolutionRoot(), "src", ContractsName, ContractsName + ".csproj");

        Assert.Empty(ProjectReferences(project));
        Assert.Equal(new[] { "Microsoft.Extensions.DependencyInjection.Abstractions" }, PackageReferences(project));
        Assert.Empty(UsedSolutionAssemblies(typeof(PluginManifest).Assembly));
    }

    [Fact]
    public void R2_Core_references_only_Contracts_and_no_plugin()
    {
        string project = Path.Combine(SolutionRoot(), "src", "UltrasoundHost.Core", "UltrasoundHost.Core.csproj");

        Assert.Equal(new[] { ContractsName }, ProjectReferences(project));
        Assert.All(UsedSolutionAssemblies(typeof(PluginHost).Assembly), name => Assert.Equal(ContractsName, name));
    }

    [Fact]
    public void R3_R4_Every_plugin_references_only_Contracts_never_another_plugin()
    {
        foreach (string project in PluginProjectFiles())
        {
            Assert.True(
                ProjectReferences(project).SequenceEqual(new[] { ContractsName }),
                Path.GetFileName(project) + " must reference only " + ContractsName);
        }

        foreach (Assembly assembly in new[]
        {
            typeof(SimulatorPlugin).Assembly,
            typeof(GatePeakPlugin).Assembly,
            typeof(BasicViewPlugin).Assembly,
        })
        {
            Assert.All(UsedSolutionAssemblies(assembly), name => Assert.Equal(ContractsName, name));
        }
    }

    [Fact]
    public void Every_plugin_type_has_a_unique_manifest_id()
    {
        IPlugin[] plugins = { new SimulatorPlugin(), new GatePeakPlugin(), new BasicViewPlugin() };
        string[] ids = plugins.Select(p => p.Manifest.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(plugins, p => Assert.True(p.Manifest.MinContractsVersion <= ContractsInfo.Current));
    }

    [Fact]
    public async Task The_real_plugins_load_together_and_the_session_check_passes_for_the_simulator()
    {
        var plugins = new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin(), new BasicViewPlugin() };
        await using Session session = await Session.StartAsync(plugins);

        Assert.All(session.Host.Statuses, s => Assert.Equal(PluginState.Running, s.State));
        Assert.Equal(new[] { PluginKind.Device, PluginKind.Processing, PluginKind.View }, session.Host.Statuses.Select(s => s.Kind).ToArray());

        IDevice device = await new SimulatorDeviceFactory().OpenAsync(new DeviceOpenOptions());
        await using (device)
        {
            Assert.True(session.Host.CheckSession(device.Capabilities).AllSatisfied);
        }
    }

    [Fact]
    public void R6_Application_references_only_Contracts_and_Core_and_has_no_ui_dependency()
    {
        string project = Path.Combine(SolutionRoot(), "src", "UltrasoundHost.Application", "UltrasoundHost.Application.csproj");

        Assert.Equal(new[] { ContractsName, "UltrasoundHost.Core" }, ProjectReferences(project));

        Assembly assembly = typeof(ShellSession).Assembly;
        Assert.All(UsedSolutionAssemblies(assembly), name => Assert.Contains(name, new[] { ContractsName, "UltrasoundHost.Core" }));

        string[] referenced = assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(referenced, name =>
            name.StartsWith("Presentation", StringComparison.Ordinal)
            || name.StartsWith("System.Windows", StringComparison.Ordinal)
            || name == "WindowsBase"
            || name == "System.Windows.Forms");
    }

    [Fact]
    public void Q5_source_does_not_hardcode_the_16_bit_full_scale_literal()
    {
        // 06 §C1 / 07 Q5: amplitude conversions go through AdcFormat.FullScaleCounts, never a literal.
        string sourceRoot = Path.Combine(SolutionRoot(), "src");
        foreach (string file in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            Assert.False(
                File.ReadAllText(file).Contains("32768", StringComparison.Ordinal),
                file + " hard-codes the 16-bit full-scale literal; use AdcFormat.FullScaleCounts");
        }
    }

    [Fact]
    public void R11_Application_and_shells_only_enumerate_plugin_capabilities_through_GetActive()
    {
        // R11: contribution interfaces must come from PluginHost.GetActive<T>(), never from the DI
        // container, otherwise a plugin that faulted after Register could still hand over services.
        string[] contributionInterfaces =
        {
            "IViewContribution",
            "IProcessorContribution",
            "IDeviceFactoryContribution",
            "IFrameProcessor",
            "IDeviceFactory",
        };

        string[] directories =
        {
            Path.Combine(SolutionRoot(), "src", "UltrasoundHost.Application"),
            Path.Combine(SolutionRoot(), "src", "UltrasoundHost.App.Cli"),
            Path.Combine(SolutionRoot(), "src", "UltrasoundHost.App.Wpf"),
        };

        foreach (string directory in directories)
        {
            foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                {
                    continue;
                }

                string source = File.ReadAllText(file);
                foreach (string contribution in contributionInterfaces)
                {
                    bool resolvesFromContainer = Regex.IsMatch(source, @"GetServices?\s*<\s*" + contribution + @"\s*>");
                    Assert.False(
                        resolvesFromContainer,
                        Path.GetFileName(file) + " resolves " + contribution + " from the DI container; use PluginHost.GetActive<T>()");
                }
            }
        }
    }

    [Fact]
    public void Q1_library_awaits_are_followed_by_ConfigureAwait_false()
    {
        // 07 Q1: libraries must not capture a synchronization context. WPF/CLI/test code is exempt.
        string[] libraryDirectories =
        {
            "UltrasoundHost.Contracts",
            "UltrasoundHost.Core",
            "UltrasoundHost.Application",
            "Plugins",
        };

        foreach (string name in libraryDirectories)
        {
            string directory = Path.Combine(SolutionRoot(), "src", name);
            foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                {
                    continue;
                }

                int lineNumber = 0;
                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    lineNumber = index + 1;
                    string trimmed = lines[index].Trim();
                    if (!trimmed.Contains("await ", StringComparison.Ordinal)
                        || trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith("///", StringComparison.Ordinal)
                        || trimmed.StartsWith("await using", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Await statements may span several lines; check the whole statement.
                    string statement = trimmed;
                    int end = index;
                    while (!statement.Contains(';') && end + 1 < lines.Length)
                    {
                        end++;
                        statement += " " + lines[end].Trim();
                    }

                    Assert.True(
                        statement.Contains(".ConfigureAwait(", StringComparison.Ordinal),
                        file + ":" + lineNumber + " awaits without ConfigureAwait(false) (07 Q1)");
                    index = end;
                }
            }
        }
    }

    [Fact]
    public void R7_only_App_shells_and_tests_reference_plugin_projects()
    {
        string[] rootDirectories = { Path.Combine(SolutionRoot(), "src"), Path.Combine(SolutionRoot(), "tests") };
        foreach (string root in rootDirectories)
        {
            foreach (string project in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(project);
                bool referencesPlugin = ProjectReferences(project)
                    .Any(reference => reference.StartsWith("UltrasoundHost.Plugins.", StringComparison.Ordinal));
                if (!referencesPlugin)
                {
                    continue;
                }

                Assert.True(
                    name.StartsWith("UltrasoundHost.App.", StringComparison.Ordinal) || name == "UltrasoundHost.Tests",
                    name + " references a plugin project; only App.* composition roots and tests may do that (R7)");
            }
        }
    }

    [Fact]
    public void R8_plugins_have_no_ui_framework_and_no_windows_target()
    {
        foreach (string project in PluginProjectFiles())
        {
            string name = Path.GetFileNameWithoutExtension(project);
            XDocument document = XDocument.Load(project);
            Assert.DoesNotContain(
                document.Descendants("UseWPF"),
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                document.Descendants("UseWindowsForms"),
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));

            string? targetFramework = document.Descendants("TargetFramework").Select(element => element.Value).FirstOrDefault();
            Assert.True(
                targetFramework is null || !targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase),
                name + " targets " + targetFramework + " (R8)");

            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == name);
            if (assembly is null)
            {
                continue;
            }

            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => (reference.Name ?? string.Empty).StartsWith("Presentation", StringComparison.Ordinal)
                    || reference.Name == "WindowsBase"
                    || reference.Name == "System.Windows.Forms");
        }
    }

    [Fact]
    public void R9_plugin_ids_are_prefixed_by_kind_and_unique()
    {
        // Touch the plugin assemblies so they are loaded even if no other test did.
        _ = typeof(SimulatorPlugin);
        _ = typeof(GatePeakPlugin);
        _ = typeof(BasicViewPlugin);

        Type[] pluginTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => (assembly.GetName().Name ?? string.Empty).StartsWith("UltrasoundHost.Plugins.", StringComparison.Ordinal))
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(IPlugin).IsAssignableFrom(type))
            .ToArray();

        Assert.NotEmpty(pluginTypes);

        var ids = new List<string>();
        foreach (Type type in pluginTypes)
        {
            IPlugin plugin = (IPlugin)Activator.CreateInstance(type)!;
            string expectedPrefix = plugin.Manifest.Kind switch
            {
                PluginKind.Device => "device.",
                PluginKind.Technique => "technique.",
                PluginKind.Processing => "processing.",
                PluginKind.View => "view.",
                PluginKind.Storage => "storage.",
                _ => string.Empty,
            };

            Assert.True(
                plugin.Manifest.Id.StartsWith(expectedPrefix, StringComparison.Ordinal),
                plugin.Manifest.Id + " must start with '" + expectedPrefix + "' for kind " + plugin.Manifest.Kind + " (R9)");
            ids.Add(plugin.Manifest.Id);
        }

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_project_is_classified_by_a_rule()
    {
        IEnumerable<string> projects = Directory.GetFiles(Path.Combine(SolutionRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(SolutionRoot(), "tests"), "*.csproj", SearchOption.AllDirectories));

        foreach (string project in projects)
        {
            string name = Path.GetFileNameWithoutExtension(project);
            bool known = name is "UltrasoundHost.Contracts" or "UltrasoundHost.Core" or "UltrasoundHost.Application" or "UltrasoundHost.Tests"
                || name.StartsWith("UltrasoundHost.Plugins.", StringComparison.Ordinal)
                || name.StartsWith("UltrasoundHost.App.", StringComparison.Ordinal);

            Assert.True(known, name + " is not covered by any architecture rule; classify it in docs/spec/04 and 07");
        }
    }
}
