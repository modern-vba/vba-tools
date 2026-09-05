using System.Reflection;
using VbaDev.App.Build;
using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookMaterializerContractTests
{
    private const BindingFlags DeclaredInstanceMembers =
        BindingFlags.Instance |
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.DeclaredOnly;

    [Fact]
    public void WorkbookOutputsUseExactlyFourDataOnlyClosedIntentsAndOneProductionEntrypoint()
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
        Assert.Equal(
            ["ExplicitImport", "ProjectBuild", "Publish", "SourceSnapshotBuild"],
            closedIntents.Select(type => type.Name).ToArray());
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
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly),
                property => typeof(Delegate).IsAssignableFrom(property.PropertyType));
            Assert.All(
                type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly),
                property => Assert.Null(property.SetMethod));
            Assert.All(
                type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly),
                field =>
                {
                    Assert.True(field.IsInitOnly);
                    Assert.False(typeof(Delegate).IsAssignableFrom(field.FieldType));
                });
            Assert.DoesNotContain(
                type.GetMethods(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly),
                method => !method.IsSpecialName);
        });

        var declaredMaterializerMethods = materializer.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            declaredMaterializerMethods,
            method => method.Name == "MaterializeCapturedSnapshotCompatibilityAsync");
        var materializationEntrypoint = Assert.Single(
            declaredMaterializerMethods,
            method =>
                !method.IsPrivate &&
                method.Name.StartsWith("Materialize", StringComparison.Ordinal));
        Assert.Equal("MaterializeAsync", materializationEntrypoint.Name);
        Assert.Collection(
            materializationEntrypoint.GetParameters(),
            parameter => Assert.Equal(intent, parameter.ParameterType),
            parameter => Assert.Equal(typeof(CancellationToken), parameter.ParameterType));
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

    [Fact]
    public void ExplicitImportOwnsConcreteAdmittedSourceAndTargetPath()
    {
        var applicationAssembly = typeof(BuildCommand).Assembly;
        var intent = applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookMaterializationIntent",
            throwOnError: false);

        Assert.NotNull(intent);
        var explicitImport = intent.GetNestedType(
            "ExplicitImport",
            BindingFlags.NonPublic);
        Assert.NotNull(explicitImport);
        var constructorParameters = Assert.Single(
                explicitImport.GetConstructors(DeclaredInstanceMembers))
            .GetParameters();
        Assert.Collection(
            constructorParameters,
            parameter => Assert.Equal(typeof(AdmittedVbaSourceSet), parameter.ParameterType),
            parameter => Assert.Equal(typeof(string), parameter.ParameterType));
        Assert.Contains(
            explicitImport.GetProperties(DeclaredInstanceMembers),
            property => property.PropertyType == typeof(AdmittedVbaSourceSet));
        Assert.Contains(
            explicitImport.GetProperties(DeclaredInstanceMembers),
            property => property.PropertyType == typeof(string));
    }

    [Fact]
    public void SourceSnapshotBuildOwnsTheConcreteBuildSourceSnapshotCapture()
    {
        var applicationAssembly = typeof(BuildCommand).Assembly;
        var intent = applicationAssembly.GetType(
            "VbaDev.App.Build.WorkbookMaterializationIntent",
            throwOnError: false);
        var capture = applicationAssembly.GetType(
            "VbaDev.App.Build.BuildSourceSnapshotCapture",
            throwOnError: false);

        Assert.NotNull(intent);
        Assert.NotNull(capture);
        Assert.True(capture.IsClass);
        Assert.True(capture.IsSealed);
        Assert.False(capture.IsAbstract);

        var sourceSnapshotBuild = intent.GetNestedType(
            "SourceSnapshotBuild",
            BindingFlags.NonPublic);
        Assert.NotNull(sourceSnapshotBuild);
        Assert.Contains(
            Assert.Single(sourceSnapshotBuild.GetConstructors(DeclaredInstanceMembers))
                .GetParameters(),
            parameter => parameter.ParameterType == capture);
        var captureProperty = Assert.Single(
            sourceSnapshotBuild.GetProperties(DeclaredInstanceMembers),
            property => property.PropertyType == capture);
        Assert.Null(captureProperty.SetMethod);
    }

    [Fact]
    public void SupersededSnapshotMaterializationAdaptersAreAbsentFromProduction()
    {
        var applicationAssembly = typeof(BuildCommand).Assembly;
        var supersededMembers = new List<string>();
        var supersededMethods = new[]
        {
            (TypeName: "VbaDev.App.Build.BuildCommand", MethodName: "RunCapturedSnapshotAsync"),
            (TypeName: "VbaDev.App.Build.WorkbookOutputCommand", MethodName: "RunCapturedSnapshotBuildAsync"),
            (TypeName: "VbaDev.App.Build.WorkbookMaterializer", MethodName: "MaterializeCapturedSnapshotCompatibilityAsync")
        };

        foreach (var (typeName, methodName) in supersededMethods)
        {
            var type = applicationAssembly.GetType(typeName, throwOnError: false);
            Assert.NotNull(type);
            if (type.GetMethods(DeclaredInstanceMembers)
                .Any(method => method.Name == methodName))
            {
                supersededMembers.Add($"{typeName}.{methodName}");
            }
        }

        const string borrowedSourceInputTypeName =
            "VbaDev.App.Build.BorrowedWorkbookGenerationSourceInput";
        if (applicationAssembly.GetType(
                borrowedSourceInputTypeName,
                throwOnError: false) is not null)
        {
            supersededMembers.Add(borrowedSourceInputTypeName);
        }

        Assert.Empty(supersededMembers);
    }
}
