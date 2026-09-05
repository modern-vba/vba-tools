using System.Reflection;
using VbaDev.App.Build;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookMaterializerContractTests
{
    [Fact]
    public void ProjectBuildAndPublishUseOneInternalSealedClosedMaterializationBoundary()
    {
        var applicationAssembly = typeof(BuildCommand).Assembly;
        var materializer = applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookMaterializer",
            throwOnError: false);
        var intent = applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookMaterializationIntent",
            throwOnError: false);
        var result = applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookMaterializationResult",
            throwOnError: false);

        Assert.NotNull(materializer);
        Assert.False(materializer.IsPublic);
        Assert.True(materializer.IsSealed);
        Assert.NotNull(intent);
        Assert.False(intent.IsPublic);
        Assert.True(intent.IsAbstract);
        Assert.All(
            intent.GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.NotNull(result);
        Assert.False(result.IsPublic);
        Assert.True(result.IsSealed);

        var closedIntents = intent.GetNestedTypes(BindingFlags.NonPublic)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(closedIntents, type => type.Name == "ProjectBuild");
        Assert.Contains(closedIntents, type => type.Name == "Publish");
        Assert.All(closedIntents, type =>
        {
            Assert.True(type.IsSealed);
            Assert.True(intent.IsAssignableFrom(type));
            Assert.DoesNotContain(
                type.GetConstructors(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
            Assert.DoesNotContain(
                type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic),
                property => typeof(Delegate).IsAssignableFrom(property.PropertyType));
        });

        var materializationEntrypoint = Assert.Single(
            materializer.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly),
            method => method.Name == "MaterializeAsync");
        Assert.Equal(intent, materializationEntrypoint.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(Task<>).MakeGenericType(result),
            materializationEntrypoint.ReturnType);
        Assert.Null(applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookGenerationPipeline",
            throwOnError: false));
        Assert.Null(applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookOutputProfile",
            throwOnError: false));
        Assert.Null(applicationAssembly.GetType(
            "VbaDev.App.Workbooks.IWorkbookBuildAutomation",
            throwOnError: false));
        Assert.Null(applicationAssembly.GetType(
            "VbaDev.App.Workbooks.SynchronousWorkbookGenerationAutomation",
            throwOnError: false));
    }
}
