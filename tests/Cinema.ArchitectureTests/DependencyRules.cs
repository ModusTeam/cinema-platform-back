using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Xunit;
using Assembly = System.Reflection.Assembly;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Cinema.ArchitectureTests;

public class DependencyRules
{
    private static readonly Assembly DomainAssembly = Assembly.Load("Cinema.Domain");
    private static readonly Assembly ApplicationAssembly = Assembly.Load("Cinema.Application");
    private static readonly Assembly InfrastructureAssembly = Assembly.Load("Cinema.Infrastructure");
    private static readonly Assembly ApiAssembly = Assembly.Load("Cinema.Api");

    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly)
        .Build();

    [Fact]
    public void Domain_Should_Not_Depend_On_Higher_Layers()
    {
        IArchRule rule = Types().That().ResideInAssembly(DomainAssembly).Should().NotDependOnAny(
                Types().That().ResideInAssembly(ApplicationAssembly)
                    .Or().ResideInAssembly(InfrastructureAssembly)
                    .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure_Or_Api()
    {
        IArchRule rule = Types().That().ResideInAssembly(ApplicationAssembly).Should().NotDependOnAny(
                Types().That().ResideInAssembly(InfrastructureAssembly)
                    .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_Api()
    {
        IArchRule rule = Types().That().ResideInAssembly(InfrastructureAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }
}
