using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using System.Xml.Linq;
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
    private static readonly Assembly CatalogDomainAssembly = Assembly.Load("Cinema.Catalog.Domain");
    private static readonly Assembly CatalogApplicationAssembly = Assembly.Load("Cinema.Catalog.Application");
    private static readonly Assembly CatalogInfrastructureAssembly = Assembly.Load("Cinema.Catalog.Infrastructure");
    private static readonly Assembly SchedulingDomainAssembly = Assembly.Load("Cinema.Scheduling.Domain");
    private static readonly Assembly SchedulingApplicationAssembly = Assembly.Load("Cinema.Scheduling.Application");
    private static readonly Assembly SchedulingInfrastructureAssembly = Assembly.Load("Cinema.Scheduling.Infrastructure");
    private static readonly Assembly BookingApplicationAssembly = Assembly.Load("Cinema.Booking.Application");
    private static readonly Assembly BookingInfrastructureAssembly = Assembly.Load("Cinema.Booking.Infrastructure");
    private static readonly Assembly OrdersDomainAssembly = Assembly.Load("Cinema.Orders.Domain");
    private static readonly Assembly TicketsDomainAssembly = Assembly.Load("Cinema.Tickets.Domain");
    private static readonly Assembly IdentityDomainAssembly = Assembly.Load("Cinema.Identity.Domain");
    private static readonly Assembly OrdersApplicationAssembly = Assembly.Load("Cinema.Orders.Application");
    private static readonly Assembly OrdersInfrastructureAssembly = Assembly.Load("Cinema.Orders.Infrastructure");
    private static readonly Assembly TicketsInfrastructureAssembly = Assembly.Load("Cinema.Tickets.Infrastructure");

    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly,
            CatalogDomainAssembly, CatalogApplicationAssembly, CatalogInfrastructureAssembly,
            SchedulingDomainAssembly, SchedulingApplicationAssembly, SchedulingInfrastructureAssembly,
            BookingApplicationAssembly, BookingInfrastructureAssembly, OrdersDomainAssembly, TicketsDomainAssembly,
            OrdersApplicationAssembly, IdentityDomainAssembly, OrdersInfrastructureAssembly, TicketsInfrastructureAssembly)
        .Build();

    [Fact]
    public void Orders_And_Tickets_Domains_Have_Exactly_One_Owner_For_Each_Extracted_Type()
    {
        Assembly[] projectAssemblies =
        [
            DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly,
            CatalogDomainAssembly, CatalogApplicationAssembly, CatalogInfrastructureAssembly,
            SchedulingDomainAssembly, SchedulingApplicationAssembly, SchedulingInfrastructureAssembly,
            BookingApplicationAssembly, BookingInfrastructureAssembly, OrdersDomainAssembly,
            TicketsDomainAssembly, OrdersApplicationAssembly, IdentityDomainAssembly,
            OrdersInfrastructureAssembly, TicketsInfrastructureAssembly, Assembly.Load("Cinema.SharedKernel")
        ];

        (System.Type Type, Assembly Owner)[] extractedTypes =
        [
            (typeof(Cinema.Domain.Entities.Order), OrdersDomainAssembly),
            (typeof(Cinema.Domain.Enums.OrderStatus), OrdersDomainAssembly),
            (typeof(Cinema.Domain.Events.OrderPaidEvent), OrdersDomainAssembly),
            (typeof(Cinema.Domain.Events.OrderFailedDomainEvent), OrdersDomainAssembly),
            (typeof(Cinema.Domain.Events.OrderCancelledDomainEvent), OrdersDomainAssembly),
            (typeof(Cinema.Domain.Entities.Ticket), TicketsDomainAssembly),
            (typeof(Cinema.Domain.Enums.TicketStatus), TicketsDomainAssembly),
            (typeof(Cinema.Domain.Entities.User), IdentityDomainAssembly),
            (typeof(Cinema.Domain.Entities.RefreshToken), IdentityDomainAssembly)
        ];

        foreach ((System.Type type, Assembly owner) in extractedTypes)
        {
            Assert.Equal(owner, type.Assembly);
            Assert.Equal([owner], projectAssemblies.Where(assembly => assembly.GetType(type.FullName!) is not null));
        }
    }

    [Fact]
    public void Extracted_Domains_Have_Only_Allowed_Project_Dependencies()
    {
        string[] ordersDependencies = ["Cinema.Scheduling.Domain", "Cinema.SharedKernel"];
        string[] ticketsDependencies = ["Cinema.Orders.Domain", "Cinema.Scheduling.Domain", "Cinema.SharedKernel"];

        Assert.Equal(ordersDependencies, GetProjectReferences(OrdersDomainAssembly));
        Assert.Equal(ticketsDependencies, GetProjectReferences(TicketsDomainAssembly));
        Assert.Equal(ordersDependencies, GetCompiledProjectReferences(OrdersDomainAssembly));
        Assert.Equal(ticketsDependencies, GetCompiledProjectReferences(TicketsDomainAssembly));
        Assert.Empty(GetProjectReferences(IdentityDomainAssembly));
        Assert.Empty(GetCompiledProjectReferences(IdentityDomainAssembly));
    }

    [Fact]
    public void Identity_Domain_Should_Not_Depend_On_Other_Project_Assemblies()
    {
        IArchRule rule = Types().That().ResideInAssembly(IdentityDomainAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(DomainAssembly)
                .Or().ResideInAssembly(OrdersDomainAssembly)
                .Or().ResideInAssembly(TicketsDomainAssembly)
                .Or().ResideInAssembly(ApplicationAssembly)
                .Or().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Orders_And_Tickets_Configurations_Have_Only_Their_Module_Owners()
    {
        Assert.Equal(OrdersInfrastructureAssembly,
            typeof(Cinema.Orders.Infrastructure.Persistence.Configurations.OrderConfiguration).Assembly);
        Assert.Equal(TicketsInfrastructureAssembly,
            typeof(Cinema.Tickets.Infrastructure.Persistence.Configurations.TicketConfiguration).Assembly);
        Assert.Null(InfrastructureAssembly.GetType("Cinema.Infrastructure.Persistence.Configurations.OrderConfiguration"));
        Assert.Null(InfrastructureAssembly.GetType("Cinema.Infrastructure.Persistence.Configurations.TicketConfiguration"));
        Assert.DoesNotContain(InfrastructureAssembly.GetTypes(), type =>
            typeof(Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Cinema.Domain.Entities.Order>)
                .IsAssignableFrom(type));
        Assert.DoesNotContain(InfrastructureAssembly.GetTypes(), type =>
            typeof(Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Cinema.Domain.Entities.Ticket>)
                .IsAssignableFrom(type));
    }

    [Fact]
    public void Extracted_Infrastructure_Dependencies_Remain_Directed()
    {
        Assert.Equal(["Cinema.Identity.Domain", "Cinema.Orders.Domain", "Cinema.Scheduling.Infrastructure"],
            GetProjectReferences(OrdersInfrastructureAssembly));
        Assert.Equal(["Cinema.Orders.Domain", "Cinema.Scheduling.Domain", "Cinema.Tickets.Domain"],
            GetProjectReferences(TicketsInfrastructureAssembly));

        Assembly[] domains = [OrdersDomainAssembly, TicketsDomainAssembly, IdentityDomainAssembly];
        foreach (Assembly domain in domains)
        {
            Assert.DoesNotContain(OrdersInfrastructureAssembly.GetName().Name, GetProjectReferences(domain));
            Assert.DoesNotContain(TicketsInfrastructureAssembly.GetName().Name, GetProjectReferences(domain));
        }
    }

    [Fact]
    public void Domain_Projects_Do_Not_Reference_Infrastructure()
    {
        Assembly[] domains =
        [
            DomainAssembly, CatalogDomainAssembly, SchedulingDomainAssembly,
            OrdersDomainAssembly, TicketsDomainAssembly, IdentityDomainAssembly,
            Assembly.Load("Cinema.SharedKernel")
        ];

        foreach (Assembly domain in domains)
        {
            Assert.DoesNotContain(GetProjectReferences(domain),
                reference => reference.EndsWith(".Infrastructure", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Shared_Context_Remains_The_Only_Context_And_Migration_Owner()
    {
        Assembly[] infrastructureAssemblies =
        [
            InfrastructureAssembly, CatalogInfrastructureAssembly, SchedulingInfrastructureAssembly,
            BookingInfrastructureAssembly, OrdersInfrastructureAssembly, TicketsInfrastructureAssembly
        ];

        Assert.Equal([typeof(Cinema.Infrastructure.Persistence.ApplicationDbContext)],
            infrastructureAssemblies.SelectMany(assembly => assembly.GetTypes())
                .Where(type => !type.IsAbstract && typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(type)));

        Assert.Equal([InfrastructureAssembly], infrastructureAssemblies
            .Where(assembly => assembly.GetTypes().Any(type =>
                typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(type) ||
                typeof(Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot).IsAssignableFrom(type))));
    }

    private static string[] GetCompiledProjectReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("Cinema.", StringComparison.Ordinal))
            .OrderBy(name => name)
            .ToArray();

    private static string[] GetProjectReferences(Assembly assembly)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cinema.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        string project = Directory.GetFiles(Path.Combine(directory.FullName, "src"),
                $"{assembly.GetName().Name}.csproj", SearchOption.AllDirectories).Single();
        return XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
            .OrderBy(name => name)
            .ToArray();
    }

    [Fact]
    public void Project_References_Should_Have_No_Cycles()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cinema.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        string[] projects = Directory.GetFiles(directory.FullName, "*.csproj", SearchOption.AllDirectories);
        Dictionary<string, string[]> references = projects.ToDictionary(
            project => Path.GetFullPath(project),
            project => XDocument.Load(project).Descendants("ProjectReference")
                .Select(reference => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(project)!,
                    reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar))))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);

        void Visit(string project)
        {
            Assert.True(visiting.Add(project), $"Circular project reference involving {project}");
            foreach (string dependency in references[project])
            {
                if (!visited.Contains(dependency)) Visit(dependency);
            }
            visiting.Remove(project);
            visited.Add(project);
        }

        foreach (string project in references.Keys)
        {
            if (!visited.Contains(project)) Visit(project);
        }
    }

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

    [Fact]
    public void Catalog_Domain_Should_Not_Depend_On_Application_Infrastructure_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(CatalogDomainAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(CatalogApplicationAssembly)
                .Or().ResideInAssembly(CatalogInfrastructureAssembly)
                .Or().ResideInAssembly(DomainAssembly)
                .Or().ResideInAssembly(ApplicationAssembly)
                .Or().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Catalog_Application_Should_Not_Depend_On_Infrastructure_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(CatalogApplicationAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(CatalogInfrastructureAssembly)
                .Or().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Catalog_Infrastructure_Should_Not_Depend_On_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(CatalogInfrastructureAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Legacy_Domain_And_Application_Should_Not_Depend_On_Catalog_Implementation()
    {
        IArchRule rule = Types().That().ResideInAssembly(DomainAssembly)
            .Or().ResideInAssembly(ApplicationAssembly).Should().NotDependOnAny(
                Types().That().ResideInAssembly(CatalogApplicationAssembly)
                    .Or().ResideInAssembly(CatalogInfrastructureAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Scheduling_Domain_Should_Not_Depend_On_Legacy_Or_Higher_Layers()
    {
        IArchRule rule = Types().That().ResideInAssembly(SchedulingDomainAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(DomainAssembly)
                .Or().ResideInAssembly(ApplicationAssembly)
                .Or().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly)
                .Or().ResideInAssembly(SchedulingApplicationAssembly)
                .Or().ResideInAssembly(SchedulingInfrastructureAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Scheduling_Application_Should_Not_Depend_On_Infrastructure_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(SchedulingApplicationAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(SchedulingInfrastructureAssembly)
                .Or().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Scheduling_Infrastructure_Should_Not_Depend_On_Legacy_Infrastructure_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(SchedulingInfrastructureAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Catalog_Should_Not_Depend_On_Scheduling()
    {
        IArchRule rule = Types().That().ResideInAssembly(CatalogDomainAssembly)
            .Or().ResideInAssembly(CatalogApplicationAssembly)
            .Or().ResideInAssembly(CatalogInfrastructureAssembly).Should().NotDependOnAny(
                Types().That().ResideInAssembly(SchedulingDomainAssembly)
                    .Or().ResideInAssembly(SchedulingApplicationAssembly)
                    .Or().ResideInAssembly(SchedulingInfrastructureAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Legacy_Layers_Should_Not_Depend_On_Scheduling_Implementation()
    {
        IArchRule rule = Types().That().ResideInAssembly(DomainAssembly)
            .Or().ResideInAssembly(ApplicationAssembly).Should().NotDependOnAny(
                Types().That().ResideInAssembly(SchedulingApplicationAssembly)
                    .Or().ResideInAssembly(SchedulingInfrastructureAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Booking_Application_Should_Not_Depend_On_Implementation_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(BookingApplicationAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(BookingInfrastructureAssembly)
                .Or().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Booking_Infrastructure_Should_Not_Depend_On_Legacy_Infrastructure_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(BookingInfrastructureAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Catalog_And_Scheduling_Domains_Should_Not_Depend_On_Booking()
    {
        IArchRule rule = Types().That().ResideInAssembly(CatalogDomainAssembly)
            .Or().ResideInAssembly(SchedulingDomainAssembly).Should().NotDependOnAny(
                Types().That().ResideInAssembly(BookingApplicationAssembly)
                    .Or().ResideInAssembly(BookingInfrastructureAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Orders_Application_Should_Not_Depend_On_Infrastructure_Or_Host()
    {
        IArchRule rule = Types().That().ResideInAssembly(OrdersApplicationAssembly).Should().NotDependOnAny(
            Types().That().ResideInAssembly(InfrastructureAssembly)
                .Or().ResideInAssembly(CatalogInfrastructureAssembly)
                .Or().ResideInAssembly(SchedulingInfrastructureAssembly)
                .Or().ResideInAssembly(BookingInfrastructureAssembly)
                .Or().ResideInAssembly(ApiAssembly));
        rule.Check(Architecture);
    }

    [Fact]
    public void Existing_Domains_And_Booking_Should_Not_Depend_On_Orders_Application()
    {
        IArchRule rule = Types().That().ResideInAssembly(CatalogDomainAssembly)
            .Or().ResideInAssembly(SchedulingDomainAssembly)
            .Or().ResideInAssembly(DomainAssembly)
            .Or().ResideInAssembly(BookingApplicationAssembly)
            .Or().ResideInAssembly(BookingInfrastructureAssembly)
            .Or().ResideInAssembly(ApplicationAssembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(OrdersApplicationAssembly));
        rule.Check(Architecture);
    }
}
