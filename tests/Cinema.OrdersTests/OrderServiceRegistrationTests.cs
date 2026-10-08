using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Services;
using Cinema.Application.Services;
using Cinema.Infrastructure;
using Cinema.Orders.Application;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class OrderServiceRegistrationTests
{
    [Fact]
    public void Host_Registers_Each_Moved_Service_Once_And_Resolves_It()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=cinema_orders_test_registration;Username=test;Password=test",
                ["ConnectionStrings:RedisConnection"] = "localhost:6379"
            })
            .Build();
        ServiceCollection services = new();
        services.AddApplication();
        services.AddOrdersApplication();
        services.AddInfrastructureServices(configuration);

        AssertRegistration<IOrderReservationService, OrderReservationService>(services);
        AssertRegistration<IOrderCheckoutOrchestrator, OrderCheckoutOrchestrator>(services);
        AssertRegistration<IGoldUpgradePricingService, GoldUpgradePricingService>(services);

        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IApplicationDbContext>());
        services.AddScoped(_ => Substitute.For<IPriceCalculator>());
        services.AddScoped(_ => Substitute.For<IPaymentService>());
        services.AddScoped(_ => Substitute.For<ILoyaltyService>());
        services.AddScoped(_ => Substitute.For<ISeatLockingService>());
        services.AddScoped(_ => Substitute.For<IPublishEndpoint>());

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOrderReservationService>().Should().BeOfType<OrderReservationService>();
        scope.ServiceProvider.GetRequiredService<IOrderCheckoutOrchestrator>().Should().BeOfType<OrderCheckoutOrchestrator>();
        scope.ServiceProvider.GetRequiredService<IGoldUpgradePricingService>().Should().BeOfType<GoldUpgradePricingService>();
    }

    private static void AssertRegistration<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        ServiceDescriptor registration = services.Single(x => x.ServiceType == typeof(TService));
        registration.ImplementationType.Should().Be(typeof(TImplementation));
        registration.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}
