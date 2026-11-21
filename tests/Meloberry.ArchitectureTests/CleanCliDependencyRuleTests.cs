using System.Reflection;
using Meloberry.Application;
using Meloberry.Cli.Commands;
using Meloberry.Domain.Common;
using Meloberry.Infrastructure;
using Xunit;

namespace Meloberry.ArchitectureTests;

public sealed class CleanCliDependencyRuleTests
{
    [Fact]
    public void Domain_DependsOnNoOtherLayerOrThirdPartyPackages()
    {
        var domainAssembly = typeof(Result).Assembly;
        var referencedNames = domainAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referencedNames, n =>
            n.StartsWith("Meloberry.Application", StringComparison.Ordinal) ||
            n.StartsWith("Meloberry.Infrastructure", StringComparison.Ordinal) ||
            n.StartsWith("mby", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) ||
            n.StartsWith("Markdig", StringComparison.Ordinal));

        AssertNoConsoleUsage(domainAssembly);
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrCliOrSqliteOrConsole()
    {
        var appAssembly = typeof(Meloberry.Application.DependencyInjection).Assembly;
        var referencedNames = appAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(referencedNames, n =>
            n.StartsWith("Meloberry.Infrastructure", StringComparison.Ordinal) ||
            n.StartsWith("mby", StringComparison.Ordinal) ||
            n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
            n.StartsWith("Markdig", StringComparison.Ordinal));

        AssertNoConsoleUsage(appAssembly);
    }

    [Fact]
    public void CliCommands_DoNotDependOnInfrastructureOrSqliteOrSystemConsoleDirectly()
    {
        var cliAssembly = typeof(ApplyCliCommand).Assembly;
        var commandTypes = cliAssembly.GetTypes()
            .Where(t => string.Equals(t.Namespace, "Meloberry.Cli.Commands", StringComparison.Ordinal) ||
                        (t.DeclaringType?.Namespace == "Meloberry.Cli.Commands"))
            .ToList();

        Assert.NotEmpty(commandTypes);

        foreach (var type in commandTypes)
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var param in ctor.GetParameters())
                {
                    Assert.False(IsForbiddenInfraType(param.ParameterType),
                        $"CLI Command '{type.FullName}' injects forbidden type '{param.ParameterType.FullName}'.");
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.False(IsForbiddenInfraType(field.FieldType),
                    $"CLI Command '{type.FullName}' holds forbidden field '{field.FieldType.FullName}'.");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                // Check method signature types
                Assert.False(IsForbiddenInfraType(method.ReturnType),
                    $"CLI Command '{type.FullName}.{method.Name}' returns forbidden type.");
            }
        }
    }

    [Fact]
    public void DomainEntities_HaveNoPublicConstructorsAndNoPublicPropertySetters()
    {
        var domainAssembly = typeof(Result).Assembly;
        var entityTypes = domainAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsSealed == false &&
                        (t.Namespace?.StartsWith("Meloberry.Domain.Sessions", StringComparison.Ordinal) == true ||
                         t.Namespace?.StartsWith("Meloberry.Domain.Quizzes", StringComparison.Ordinal) == true ||
                         t.Namespace?.StartsWith("Meloberry.Domain.Questions", StringComparison.Ordinal) == true))
            .Where(t => !t.IsNested && !typeof(Delegate).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(entityTypes);

        foreach (var entity in entityTypes)
        {
            var publicCtors = entity.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.True(publicCtors.Length == 0, $"Entity '{entity.FullName}' must not expose public constructors.");

            foreach (var prop in entity.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.SetMethod is not null)
                {
                    Assert.False(prop.SetMethod.IsPublic,
                        $"Entity property '{entity.FullName}.{prop.Name}' must not have a public setter.");
                }
            }
        }
    }

    private static bool IsForbiddenInfraType(Type type) =>
        type.Assembly == typeof(MeloberryStorageOptions).Assembly ||
        type.FullName?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true ||
        type == typeof(Console);

    private static void AssertNoConsoleUsage(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Assert.NotEqual(typeof(Console), field.FieldType);

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                Assert.NotEqual(typeof(Console), prop.PropertyType);
        }
    }
}
