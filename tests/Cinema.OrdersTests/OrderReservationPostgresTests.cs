using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Contracts;
using Cinema.Application.Jobs;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Orders.Commands.StartCheckout;
using Cinema.Application.Orders.Queries.GetOrderStatus;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Dtos;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Application.Orders.Queries.GetUserOrders;
using Cinema.Application.Common.Mappings.Orders;
using Cinema.Application.Orders.EventHandlers;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Application.Orders.Services;
using Cinema.Application.Account.Commands.SetMyDateOfBirth;
using Cinema.Application.Account.Commands.UpdateProfile;
using Cinema.Application.Services;
using Cinema.Application.Seats.Commands.LockSeat;
using Cinema.Application.Sessions.Queries.GetSessionById;
using Cinema.Application.Tickets.Queries.GetTicketDetails;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Persistence.Interceptors;
using Cinema.Infrastructure.Messaging.Consumers;
using Cinema.Orders.Application;
using Cinema.Domain.Events;
using FluentAssertions;
using MediatR;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Cinema.Application.Common.Settings;
using Npgsql;
using NSubstitute;
using Mapster;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

// Paid notifications intentionally call the existing overload without a cancellation token.
#pragma warning disable xUnit1051

namespace Cinema.OrdersTests;

// Only run with a disposable pgvector PostgreSQL database named cinema_orders_test_*.
public class OrderReservationPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payment_Facts_Use_Committed_Order_And_Stage_Without_Email(bool noEmail)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        IPriceCalculator calculator = Substitute.For<IPriceCalculator>();
        calculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(100.75m);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value, calculator: calculator);
        if (noEmail)
        {
            User user = await database.Context.Users.SingleAsync(TestContext.Current.CancellationToken);
            user.Email = null;
            await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString,
            withDomainHandlers: true, rawJson: true, retries: true);
        (await RunPaymentFactsCheckoutAsync(services, database, reserved.Value, pointsUsed: noEmail ? 25 : 0)).IsSuccess.Should().BeTrue();
        string initial;
        await using (ApplicationDbContext verify = database.NewContext())
        {
            Order order = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
            initial = await PaymentFactsBodyAsync(verify);
            using JsonDocument json = JsonDocument.Parse(initial);
            JsonElement data = json.RootElement.GetProperty("data");
            json.RootElement.GetProperty("pattern").GetString().Should().Be("TicketPurchased");
            data.GetProperty("eventId").GetGuid().Should().Be(reserved.Value);
            data.GetProperty("orderId").GetGuid().Should().Be(reserved.Value);
            data.GetProperty("userId").GetGuid().Should().Be(database.UserId);
            data.GetProperty("sessionId").GetGuid().Should().Be(database.Session.Id.Value);
            data.GetProperty("paidAt").GetDateTime().Should().Be(
                verify.Entry(order).Property<DateTime?>("PaidAt").CurrentValue!.Value);
            data.GetProperty("purchasedAt").GetDateTime().Should().Be(data.GetProperty("paidAt").GetDateTime());
            data.GetProperty("paidAmount").GetDecimal().Should().Be(noEmail ? 75.75m : 100.75m);
            data.GetProperty("pointsUsed").GetInt32().Should().Be(noEmail ? 25 : 0);
            string genericBody = await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .Where(message => message.MessageType.Contains(":TicketPurchasedEvent"))
                .Select(message => message.Body).SingleAsync(TestContext.Current.CancellationToken);
            using JsonDocument generic = JsonDocument.Parse(genericBody);
            generic.RootElement.GetProperty("timestamp").GetDateTime().Should().Be(order.PaidAt!.Value);
            (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(
                TestContext.Current.CancellationToken)).Should().Be(noEmail ? 2 : 3);
        }
        PricingItem changedPrice = await database.Context.PricingItems.SingleAsync(TestContext.Current.CancellationToken);
        changedPrice.UpdatePrice(999.99m);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        // Reconstruct through the real handler after disposing the original context/scope.
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Order committed = await context.Orders.SingleAsync(TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderPaidEvent(committed),
                TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext reloaded = database.NewContext();
        List<string> bodies = await reloaded.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
            .Where(message => message.MessageType.Contains(":NestJsTicketPurchasedEvent"))
            .Select(message => message.Body).ToListAsync(TestContext.Current.CancellationToken);
        bodies.Should().HaveCount(2);
        string[] fields = ["eventId", "orderId", "userId", "sessionId", "paidAt", "purchasedAt",
            "totalAmount", "ticketAmount", "totalPrice", "paidAmount", "foodAmount", "pointsUsed"];
        using JsonDocument original = JsonDocument.Parse(initial);
        foreach (string body in bodies)
        {
            using JsonDocument rebuilt = JsonDocument.Parse(body);
            foreach (string field in fields)
                rebuilt.RootElement.GetProperty("data").GetProperty(field).GetRawText().Should().Be(
                    original.RootElement.GetProperty("data").GetProperty(field).GetRawText(), field);
        }
    }

    [Fact]
    public async Task Payment_Facts_And_All_Outbox_Records_Roll_Back_On_Staging_Failure()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await database.Context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_loyalty_fact() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.message_type LIKE '%NestJsTicketPurchasedEvent%' THEN
                    RAISE EXCEPTION 'injected loyalty staging failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_loyalty_fact BEFORE INSERT ON outbox_message
                FOR EACH ROW EXECUTE FUNCTION reject_loyalty_fact();
            """, TestContext.Current.CancellationToken);
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString,
            withDomainHandlers: true, rawJson: true, retries: true);
        Result<Guid> result = await RunPaymentFactsCheckoutAsync(services, database, reserved.Value, pointsUsed: 25);
        result.Error.Code.Should().Be("Payment.OutcomeUnknown");
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        order.PaidAt.Should().BeNull();
        order.PaymentTransactionId.Should().BeNull();
        order.PointsUsed.Should().Be(0);
        order.PaidAmount.Should().Be(50m);
        order.Status.Should().Be(OrderStatus.PaymentOutcomeUnknown);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Reserved);
        (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(
            TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Payment_Facts_Survive_Lost_Commit_Response_And_Exact_Checkout_Replay()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        LostPaymentCommitAcknowledgement fault = new();
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString,
            withDomainHandlers: true, rawJson: true, retries: true, transactionInterceptor: fault);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("payment-facts-transaction"));
        (await RunPaymentFactsCheckoutAsync(services, database, reserved.Value, payment)).IsSuccess.Should().BeTrue();
        string original;
        await using (ApplicationDbContext verify = database.NewContext()) original = await PaymentFactsBodyAsync(verify);
        (await RunPaymentFactsCheckoutAsync(services, database, reserved.Value, payment)).IsSuccess.Should().BeTrue();
        fault.Failures.Should().Be(1);
        await payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
        await using ApplicationDbContext reloaded = database.NewContext();
        (await PaymentFactsBodyAsync(reloaded)).Should().Be(original);
        (await reloaded.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(
            TestContext.Current.CancellationToken)).Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_Payment_Facts_Checkouts_Commit_One_Charge_And_One_Fact()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString,
            withDomainHandlers: true, rawJson: true, retries: true);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return PaymentResult.Success("payment-facts-transaction");
        });
        Task<Result<Guid>> first = RunPaymentFactsCheckoutAsync(services, database, reserved.Value, payment);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            Result<Guid> second = await RunPaymentFactsCheckoutAsync(services, database, reserved.Value, payment);
            second.Error.Code.Should().Be("Order.CheckoutInProgress");
        }
        finally { release.TrySetResult(); }
        (await first).IsSuccess.Should().BeTrue();
        await payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).PaidAt.Should().NotBeNull();
        (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(
            TestContext.Current.CancellationToken)).Should().Be(3);
    }

    [Fact]
    public async Task Unknown_Payment_Has_No_Paid_Facts_Until_Authoritative_Confirmation()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString,
            withDomainHandlers: true, rawJson: true, retries: true);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns<Task<PaymentResult>>(_ => throw new TimeoutException("injected unknown payment outcome"));
        (await RunPaymentFactsCheckoutAsync(services, database, reserved.Value, payment)).Error.Code
            .Should().Be("Payment.OutcomeUnknown");
        await using (ApplicationDbContext verify = database.NewContext())
        {
            (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).PaidAt.Should().BeNull();
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(
                TestContext.Current.CancellationToken)).Should().Be(0);
        }
        DateTime beforeConfirmation = DateTime.UtcNow.AddMicroseconds(-1);
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Order order = await context.Orders.SingleAsync(TestContext.Current.CancellationToken);
            order.MarkAsPaid("confirmed-by-reconciliation");
            (await context.Tickets.SingleAsync(TestContext.Current.CancellationToken)).ConfirmPurchase(order);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext confirmed = database.NewContext();
        Order persisted = await confirmed.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.PaidAt.Should().BeOnOrAfter(beforeConfirmation);
        using JsonDocument body = JsonDocument.Parse(await PaymentFactsBodyAsync(confirmed));
        body.RootElement.GetProperty("data").GetProperty("paidAt").GetDateTime().Should().Be(persisted.PaidAt!.Value);
        await payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
    }

    private sealed class LostPaymentCommitAcknowledgement : DbTransactionInterceptor
    {
        public int Failures { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Failures == 0 && eventData.Context?.ChangeTracker.Entries<Order>()
                    .Any(entry => entry.Entity.Status == OrderStatus.Paid) == true)
            {
                Failures++;
                throw new IOException("injected lost payment commit acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Serialized_Payment_Fact_Recovers_In_New_Publisher_Despite_Email_Failure()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        await EnsureRabbitMqAvailableAsync();
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        string stagedBody;
        await using (ServiceProvider unavailable = CreateOutboxServices(database.ConnectionString,
            withDomainHandlers: true, rawJson: true, retries: true, rabbitPort: 5673))
        {
            (await RunPaymentFactsCheckoutAsync(unavailable, database, reserved.Value)).IsSuccess.Should().BeTrue();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            Func<Task> start = () => unavailable.GetRequiredService<IBusControl>().StartAsync(timeout.Token);
            await start.Should().ThrowAsync<Exception>();
            await using ApplicationDbContext verify = database.NewContext();
            stagedBody = await PaymentFactsBodyAsync(verify);
        }
        IEmailService email = Substitute.For<IEmailService>();
        email.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>())
            .Returns<Task>(_ => throw new IOException("injected downstream email outage"));
        ITicketGenerator generator = Substitute.For<ITicketGenerator>();
        generator.GenerateTicketPdf(Arg.Any<TicketPurchasedMessage>()).Returns([1, 2, 3]);
        await using ServiceProvider restarted = CreateOutboxServices(database.ConnectionString,
            rawJson: true, retries: true, captureLoyaltyFacts: true, withPurchaseConsumer: true,
            emailService: email, ticketGenerator: generator);
        ConcurrentQueue<string> received = restarted.GetRequiredService<ConcurrentQueue<string>>();
        List<IHostedService> hostedServices = restarted.GetServices<IHostedService>().ToList();
        try
        {
            foreach (IHostedService service in hostedServices)
                await service.StartAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            while (received.IsEmpty || !email.ReceivedCalls().Any())
                await Task.Delay(100, timeout.Token);
            received.Should().ContainSingle().Which.Should().Be(stagedBody);
            using JsonDocument fact = JsonDocument.Parse(received.Single());
            fact.RootElement.GetProperty("pattern").GetString().Should().Be("TicketPurchased");
            await using ApplicationDbContext verify = database.NewContext();
            Order order = await verify.Orders.SingleAsync(timeout.Token);
            order.Status.Should().Be(OrderStatus.Paid);
            fact.RootElement.GetProperty("data").GetProperty("paidAt").GetDateTime().Should().Be(order.PaidAt!.Value);
            while (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(timeout.Token) != 0)
                await Task.Delay(100, timeout.Token);
        }
        finally
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            foreach (IHostedService service in hostedServices.AsEnumerable().Reverse())
                await service.StopAsync(timeout.Token);
        }
    }

    private static Task<string> PaymentFactsBodyAsync(ApplicationDbContext context) =>
        context.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
            .Where(message => message.MessageType.Contains(":NestJsTicketPurchasedEvent"))
            .Select(message => message.Body).SingleAsync(TestContext.Current.CancellationToken);

    private static async Task<Result<Guid>> RunPaymentFactsCheckoutAsync(ServiceProvider services,
        ReservationDatabase database, Guid orderId, IPaymentService? paymentService = null, int pointsUsed = 0)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IPaymentService payment = paymentService ?? Substitute.For<IPaymentService>();
        if (paymentService is null)
            payment.ProcessPaymentAsync(Arg.Any<decimal>(), "UAH", "token", Arg.Any<CancellationToken>())
                .Returns(PaymentResult.Success("payment-facts-transaction"));
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        if (pointsUsed > 0)
        {
            loyalty.CalculateDiscountAsync(database.UserId, Arg.Any<decimal>(), Arg.Any<CancellationToken>())
                .Returns(call => (true, pointsUsed, call.ArgAt<decimal>(1) - pointsUsed));
            loyalty.DeductPointsAsync(database.UserId, pointsUsed, orderId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((true, 0, ""));
        }
        OrderCheckoutOrchestrator checkout = new(context, loyalty, payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(),
            scope.ServiceProvider.GetRequiredService<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
        return await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], orderId, false, pointsUsed > 0, "token", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Paid_Transition_And_Purchase_Outbox_Record_Commit_Or_Roll_Back_Together()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString);

        await StagePaidTransitionAsync(services, reserved.Value, database.UserId, commit: false);
        await using (ApplicationDbContext verify = database.NewContext())
        {
            (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Pending);
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        }

        await StagePaidTransitionAsync(services, reserved.Value, database.UserId, commit: true);
        await using (ApplicationDbContext verify = database.NewContext())
        {
            (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
            (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Committed_Purchase_Outbox_Message_Delivers_When_RabbitMq_Starts_Later()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        await EnsureRabbitMqAvailableAsync();
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using (ServiceProvider unavailableBroker = CreateOutboxServices(database.ConnectionString, rabbitPort: 5673))
        {
            await StagePaidTransitionAsync(unavailableBroker, reserved.Value, database.UserId, commit: true);
            using CancellationTokenSource connectionTimeout = new(TimeSpan.FromSeconds(2));
            Func<Task> connect = () => unavailableBroker.GetRequiredService<IBusControl>()
                .StartAsync(connectionTimeout.Token);
            await connect.Should().ThrowAsync<Exception>();
            connectionTimeout.IsCancellationRequested.Should().BeTrue();
        }

        await using (ApplicationDbContext verify = database.NewContext())
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString, capturePurchaseEvents: true);
        ConcurrentQueue<TicketPurchasedEvent> received = services.GetRequiredService<ConcurrentQueue<TicketPurchasedEvent>>();
        List<IHostedService> hostedServices = services.GetServices<IHostedService>().ToList();
        foreach (IHostedService service in hostedServices)
            await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while (true)
            {
                await using ApplicationDbContext verifyDelivery = database.NewContext();
                if (await verifyDelivery.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                        .CountAsync(timeout.Token) == 0)
                    break;
                await Task.Delay(200, timeout.Token);
            }

            using CancellationTokenSource receiveTimeout = new(TimeSpan.FromSeconds(20));
            while (received.IsEmpty)
                await Task.Delay(200, receiveTimeout.Token);
            received.Should().ContainSingle();
            TicketPurchasedEvent delivered = received.Single();
            delivered.UserId.Should().Be(database.UserId);
            delivered.OrderId.Should().Be(reserved.Value);
            delivered.TotalAmount.Should().Be(50m);
            delivered.TicketsCount.Should().Be(1);

            await using ApplicationDbContext verify = database.NewContext();
            (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
        }
        finally
        {
            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(10));
            foreach (IHostedService service in hostedServices.AsEnumerable().Reverse())
                await service.StopAsync(stopTimeout.Token);
        }
    }

    [Fact]
    public async Task Paid_Checkout_Stages_Each_Existing_Integration_Event_Once()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString, withDomainHandlers: true);
        await StagePaidTransitionAsync(services, reserved.Value, database.UserId, commit: true);

        await using ApplicationDbContext verify = database.NewContext();
        List<string> messageTypes = await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
            .Select(message => message.MessageType).ToListAsync(TestContext.Current.CancellationToken);
        messageTypes.Should().HaveCount(3);
        messageTypes.Should().ContainSingle(type => type.Contains($":{nameof(TicketPurchasedEvent)}"));
        messageTypes.Should().ContainSingle(type => type.Contains($":{nameof(TicketPurchasedMessage)}"));
        messageTypes.Should().ContainSingle(type => type.Contains($":{nameof(NestJsTicketPurchasedEvent)}"));
    }

    [Fact]
    public async Task Identity_Changes_And_Outbox_Messages_Commit_Or_Roll_Back_Together()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Guid birthdayUserId = Guid.NewGuid();
        await using (ApplicationDbContext seed = database.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = birthdayUserId,
                UserName = $"birthday-{Guid.NewGuid():N}",
                Email = $"birthday-{Guid.NewGuid():N}@cinema.test",
                SecurityStamp = Guid.NewGuid().ToString()
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString, withIdentity: true);
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
            currentUser.UserId.Returns(database.UserId);
            UpdateProfileCommandHandler handler = new(currentUser,
                scope.ServiceProvider.GetRequiredService<UserManager<User>>(),
                scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>(),
                NullLogger<UpdateProfileCommandHandler>.Instance);
            Result result = await handler.Handle(new UpdateProfileCommand("Ada", "Lovelace",
                new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)), TestContext.Current.CancellationToken);
            result.IsSuccess.Should().BeTrue();
        }

        await using (ApplicationDbContext verify = database.NewContext())
        {
            User updated = await verify.Users.SingleAsync(user => user.Id == database.UserId,
                TestContext.Current.CancellationToken);
            updated.FirstName.Should().Be("Ada");
            updated.DateOfBirth.Should().Be(new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            List<string> messageTypes = await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .Select(message => message.MessageType).ToListAsync(TestContext.Current.CancellationToken);
            messageTypes.Should().HaveCount(2);
            messageTypes.Should().ContainSingle(type => type.Contains(nameof(UserProfileUpdatedMessage)));
            messageTypes.Should().ContainSingle(type => type.Contains(nameof(NestJsUserDateOfBirthSetEvent)));
        }

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
            currentUser.UserId.Returns(database.UserId);
            ISendEndpointProvider unavailablePublisher = Substitute.For<ISendEndpointProvider>();
            unavailablePublisher.GetSendEndpoint(Arg.Any<Uri>())
                .Returns(Task.FromException<ISendEndpoint>(new InvalidOperationException("test staging failure")));
            UpdateProfileCommandHandler handler = new(currentUser,
                scope.ServiceProvider.GetRequiredService<UserManager<User>>(), unavailablePublisher,
                NullLogger<UpdateProfileCommandHandler>.Instance);
            Result failed = await handler.Handle(new UpdateProfileCommand("Changed", "Name", null),
                TestContext.Current.CancellationToken);
            failed.IsFailure.Should().BeTrue();
            failed.Error.Code.Should().Be("User.MessageStagingFailed");
        }

        await using (ApplicationDbContext verify = database.NewContext())
        {
            (await verify.Users.SingleAsync(user => user.Id == database.UserId,
                TestContext.Current.CancellationToken)).FirstName.Should().Be("Ada");
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        }

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
            currentUser.UserId.Returns(birthdayUserId);
            SetMyDateOfBirthCommandHandler handler = new(currentUser,
                scope.ServiceProvider.GetRequiredService<UserManager<User>>(),
                scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>(),
                NullLogger<SetMyDateOfBirthCommandHandler>.Instance);
            (await handler.Handle(new SetMyDateOfBirthCommand(new DateTime(1992, 2, 2)),
                TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (ApplicationDbContext verify = database.NewContext())
        {
            (await verify.Users.SingleAsync(user => user.Id == birthdayUserId,
                TestContext.Current.CancellationToken)).DateOfBirth.Should().BeNull();
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        }

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
            currentUser.UserId.Returns(birthdayUserId);
            SetMyDateOfBirthCommandHandler handler = new(currentUser,
                scope.ServiceProvider.GetRequiredService<UserManager<User>>(),
                scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>(),
                NullLogger<SetMyDateOfBirthCommandHandler>.Instance);
            (await handler.Handle(new SetMyDateOfBirthCommand(new DateTime(1992, 2, 2)),
                TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }

        await using (ApplicationDbContext verify = database.NewContext())
        {
            (await verify.Users.SingleAsync(user => user.Id == birthdayUserId,
                TestContext.Current.CancellationToken)).DateOfBirth.Should().NotBeNull();
            (await verify.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
                .CountAsync(TestContext.Current.CancellationToken)).Should().Be(3);
        }
    }

    [Fact]
    public async Task Consumer_Outbox_Deduplicates_MessageId_Without_Dropping_Distinct_Messages()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        await EnsureRabbitMqAvailableAsync();
        IEmailService email = Substitute.For<IEmailService>();
        ITicketGenerator generator = Substitute.For<ITicketGenerator>();
        generator.GenerateTicketPdf(Arg.Any<TicketPurchasedMessage>()).Returns([1, 2, 3]);
        await using ServiceProvider services = CreateOutboxServices(database.ConnectionString, withPurchaseConsumer: true,
            emailService: email, ticketGenerator: generator);
        List<IHostedService> hostedServices = services.GetServices<IHostedService>().ToList();
        foreach (IHostedService service in hostedServices)
            await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Guid messageId = Guid.NewGuid();
            TicketPurchasedMessage message = new(Guid.NewGuid(), "buyer@cinema.test", "Buyer", "Movie",
                DateTime.UtcNow.AddDays(1), "https://cinema.test/ticket", database.UserId, 50m);
            IBus bus = services.GetRequiredService<IBus>();
            await bus.Publish(message, context => context.MessageId = messageId, TestContext.Current.CancellationToken);
            await WaitForEmailCountAsync(email, 1);
            await WaitForInboxCountAsync(database, 1);
            await bus.Publish(message, context => context.MessageId = messageId, TestContext.Current.CancellationToken);
            await Task.Delay(2000, TestContext.Current.CancellationToken);

            email.ReceivedCalls().Should().HaveCount(1);
            await bus.Publish(message, context => context.MessageId = Guid.NewGuid(),
                TestContext.Current.CancellationToken);
            await WaitForEmailCountAsync(email, 2);
            await WaitForInboxCountAsync(database, 2);
            email.ReceivedCalls().Should().HaveCount(2);
        }
        finally
        {
            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(10));
            foreach (IHostedService service in hostedServices.AsEnumerable().Reverse())
                await service.StopAsync(stopTimeout.Token);
        }
    }

    private static async Task EnsureRabbitMqAvailableAsync()
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync("localhost", 5672, timeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            throw new InvalidOperationException(
                "RabbitMQ integration tests require a broker at localhost:5672. " +
                "Set CINEMA_TEST_RABBITMQ_USERNAME and CINEMA_TEST_RABBITMQ_PASSWORD if it does not use guest/guest.",
                exception);
        }
    }

    private static async Task WaitForEmailCountAsync(IEmailService email, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (email.ReceivedCalls().Count() < count)
            await Task.Delay(200, timeout.Token);
    }

    private static async Task WaitForInboxCountAsync(ReservationDatabase database, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using ApplicationDbContext verify = database.NewContext();
            int delivered = await verify.Set<MassTransit.EntityFrameworkCoreIntegration.InboxState>()
                .CountAsync(timeout.Token);
            if (delivered == count)
                return;
            await Task.Delay(200, timeout.Token);
        }
    }

    private static ServiceProvider CreateOutboxServices(string connectionString, bool withDomainHandlers = false,
        bool withPurchaseConsumer = false, IEmailService? emailService = null, ITicketGenerator? ticketGenerator = null,
        ushort rabbitPort = 5672, bool capturePurchaseEvents = false, bool withIdentity = false, bool rawJson = false, bool retries = false, DbTransactionInterceptor? transactionInterceptor = null, bool captureLoyaltyFacts = false)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(15);
            options.StopTimeout = TimeSpan.FromSeconds(10);
        });
        if (withIdentity)
            services.AddIdentityCore<User>().AddEntityFrameworkStores<ApplicationDbContext>();
        if (withDomainHandlers)
        {
            services.AddOrdersApplication();
            services.Configure<FrontendSettings>(settings => settings.BaseUrl = "https://cinema.test");
            services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
            services.AddScoped<DispatchDomainEventsInterceptor>();
            services.AddScoped<IPostCommitDomainEventDispatcher>(provider =>
                provider.GetRequiredService<DispatchDomainEventsInterceptor>());
            services.AddScoped(_ => Substitute.For<ISeatLockingService>());
            services.AddScoped(_ => Substitute.For<ITicketNotifier>());
        }
        if (withPurchaseConsumer)
        {
            services.AddSingleton(emailService!);
            services.AddSingleton(ticketGenerator!);
        }
        if (capturePurchaseEvents)
            services.AddSingleton(new ConcurrentQueue<TicketPurchasedEvent>());
        if (captureLoyaltyFacts)
            services.AddSingleton(new ConcurrentQueue<string>());
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            options.UseNpgsql(connectionString, npgsql => { npgsql.UseVector(); if (retries) npgsql.EnableRetryOnFailure(3); }).UseSnakeCaseNamingConvention();
            if (withDomainHandlers)
                options.AddInterceptors(provider.GetRequiredService<DispatchDomainEventsInterceptor>());
            if (transactionInterceptor is not null) options.AddInterceptors(transactionInterceptor);
        });
        services.AddMassTransit(registration =>
        {
            if (captureLoyaltyFacts) registration.AddConsumer<CapturedLoyaltyFactConsumer>();
            if (withPurchaseConsumer)
                registration.AddConsumer<TicketPurchasedConsumer, TicketPurchasedConsumerDefinition>();
            if (capturePurchaseEvents)
                registration.AddConsumer<CapturedPurchaseEventConsumer>();
            registration.AddEntityFrameworkOutbox<ApplicationDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UseBusOutbox();
            });
            registration.UsingRabbitMq((context, bus) =>
            {
                if (rawJson) bus.UseRawJsonSerializer();
                bus.Host("localhost", rabbitPort, "/", host =>
                {
                    host.Username(Environment.GetEnvironmentVariable("CINEMA_TEST_RABBITMQ_USERNAME") ?? "guest");
                    host.Password(Environment.GetEnvironmentVariable("CINEMA_TEST_RABBITMQ_PASSWORD") ?? "guest");
                });
                if (captureLoyaltyFacts)
                    bus.ReceiveEndpoint("loyalty_ticket_purchased", endpoint =>
                        endpoint.ConfigureConsumer<CapturedLoyaltyFactConsumer>(context));
                if (withPurchaseConsumer)
                    bus.ConfigureEndpoints(context);
                if (capturePurchaseEvents)
                    bus.ReceiveEndpoint($"phase3-purchase-{Guid.NewGuid():N}", endpoint =>
                    {
                        endpoint.AutoDelete = true;
                        endpoint.Durable = false;
                        endpoint.ConfigureConsumer<CapturedPurchaseEventConsumer>(context);
                    });
            });
        });
        return services.BuildServiceProvider();
    }

    private static async Task StagePaidTransitionAsync(ServiceProvider services, Guid orderId, Guid userId, bool commit)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IPublishEndpoint publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        Order order = await context.Orders.SingleAsync(candidate => candidate.Id == new EntityId<Order>(orderId),
            TestContext.Current.CancellationToken);
        Ticket ticket = await context.Tickets.SingleAsync(TestContext.Current.CancellationToken);
        order.MarkAsPaid("outbox-test-transaction");
        ticket.ConfirmPurchase(order);
        await publisher.Publish(new TicketPurchasedEvent(userId, orderId, order.TotalAmount, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (commit)
        {
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            if (scope.ServiceProvider.GetService<IPostCommitDomainEventDispatcher>() is { } dispatcher)
                await dispatcher.DispatchAfterCommitAsync(TestContext.Current.CancellationToken);
        }
        else
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reservation_Only_Creates_Pending_Order_Without_Checkout_Or_Loyalty()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(database.Session.Id.Value, database.Seat.Id.Value,
            database.UserId, Arg.Any<CancellationToken>()).Returns(true);
        IPriceCalculator prices = Substitute.For<IPriceCalculator>();
        prices.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(50m);

        ReserveOrderCommandHandler handler = new(new OrderReservationService(database.Context, prices),
            currentUser, locks, database.Context);
        Result<ReservedOrderDto> result = await handler.Handle(
            new ReserveOrderCommand(database.Session.Id.Value, [database.Seat.Id.Value]),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAmount.Should().Be(50m);
        result.Value.Status.Should().Be(nameof(OrderStatus.Pending));
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        order.Id.Value.Should().Be(result.Value.Id);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Reserved);
    }

    [Fact]
    public async Task Existing_Order_Checkout_Command_Uses_Owner_And_Atomic_Claim()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reservation = await database.ReserveAsync(database.Seat.Id.Value);
        reservation.IsSuccess.Should().BeTrue();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("transaction-1"));
        await using ApplicationDbContext context = database.NewContext();
        OrderCheckoutOrchestrator orchestrator = CreateCheckout(context, payment);
        StartCheckoutCommandHandler handler = new(currentUser, orchestrator);
        StartCheckoutCommand command = new(reservation.Value, "token", false, false);

        currentUser.UserId.Returns(Guid.NewGuid());
        (await handler.Handle(command, TestContext.Current.CancellationToken)).Error.Code.Should().Be("Order.NotFound");
        await payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, default);

        currentUser.UserId.Returns(database.UserId);
        Result<Guid> result = await handler.Handle(command, TestContext.Current.CancellationToken);
        result.IsSuccess.Should().BeTrue();
        (await handler.Handle(command, TestContext.Current.CancellationToken)).Value.Should().Be(reservation.Value);
        await payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
        GetOrderStatusQueryHandler status = new(verify, currentUser);
        (await status.Handle(new GetOrderStatusQuery(reservation.Value), TestContext.Current.CancellationToken))
            .Value.Status.Should().Be(nameof(OrderStatus.Paid));
    }

    [Fact]
    public async Task Missing_Lock_Releases_Other_Owned_Seats_Before_Reservation()
    {
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid firstSeat = Guid.NewGuid();
        Guid missingSeat = Guid.NewGuid();
        currentUser.UserId.Returns(userId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(sessionId, firstSeat, userId, Arg.Any<CancellationToken>()).Returns(true);
        locks.ValidateAndExtendLockAsync(sessionId, missingSeat, userId, Arg.Any<CancellationToken>()).Returns(false);
        IOrderReservationService reservation = Substitute.For<IOrderReservationService>();
        IOrderCheckoutOrchestrator checkout = Substitute.For<IOrderCheckoutOrchestrator>();
        CreateOrderCommandHandler handler = new(reservation, currentUser, locks, checkout,
            NullLogger<CreateOrderCommandHandler>.Instance);

        Result<Guid> result = await handler.Handle(new CreateOrderCommand
        {
            SessionId = sessionId, SeatIds = [firstSeat, missingSeat], PaymentToken = "token"
        }, TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.LockExpired");
        await locks.Received(1).UnlockSeatsAsync(sessionId, Arg.Is<IEnumerable<Guid>>(ids =>
            ids.Contains(firstSeat) && ids.Contains(missingSeat)), userId, Arg.Any<CancellationToken>());
        await reservation.DidNotReceiveWithAnyArgs().ReserveOrderAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Reservation_Exception_Releases_Owned_Lock_And_Propagates_Error()
    {
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        currentUser.UserId.Returns(userId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(sessionId, seatId, userId, Arg.Any<CancellationToken>()).Returns(true);
        IOrderReservationService reservation = Substitute.For<IOrderReservationService>();
        reservation.ReserveOrderAsync(userId, sessionId, Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Result<Guid>>(new InvalidOperationException("database unavailable")));
        CreateOrderCommandHandler handler = new(reservation, currentUser, locks,
            Substitute.For<IOrderCheckoutOrchestrator>(), NullLogger<CreateOrderCommandHandler>.Instance);

        Func<Task> reserve = () => handler.Handle(new CreateOrderCommand
        {
            SessionId = sessionId, SeatIds = [seatId], PaymentToken = "token"
        }, TestContext.Current.CancellationToken);

        await reserve.Should().ThrowAsync<InvalidOperationException>();
        await locks.Received(1).UnlockSeatsAsync(sessionId, Arg.Is<IEnumerable<Guid>>(ids =>
            ids.Single() == seatId), userId, Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task Lock_Validator_Rejects_Inactive_And_Sold_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        LockSeatCommandValidator validator = new(database.Context);
        LockSeatCommand request = new(database.Session.Id.Value, database.Seat.Id.Value);
        (await validator.ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid.Should().BeTrue();

        database.Seat.SetStatus(SeatStatus.Maintenance);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await validator.ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid.Should().BeFalse();

        database.Seat.SetStatus(SeatStatus.Active);
        OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await validator.ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Reservation_Rejects_Duplicate_And_Foreign_Hall_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> duplicate = await database.ReserveAsync([database.Seat.Id.Value, database.Seat.Id.Value]);
        duplicate.IsFailure.Should().BeTrue();

        Hall otherHall = Hall.Create(EntityId<Hall>.New(), $"Other hall {Guid.NewGuid():N}");
        Seat foreignSeat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active,
            otherHall.Id, database.Seat.SeatTypeId);
        otherHall.ApplyLayout([foreignSeat]);
        database.Context.Halls.Add(otherHall);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Result<Guid> foreign = await database.ReserveAsync(foreignSeat.Id.Value);
        foreign.IsFailure.Should().BeTrue();
        (await database.Context.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_Session_Cannot_Be_Locked_Or_Reserved()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        database.Session.Cancel(DateTime.UtcNow);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        LockSeatCommandValidator validator = new(database.Context);
        (await validator.ValidateAsync(new LockSeatCommand(database.Session.Id.Value, database.Seat.Id.Value),
            TestContext.Current.CancellationToken)).IsValid.Should().BeFalse();
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Session.NotAvailable");
    }

    [Fact]
    public async Task Competing_Reservations_Create_Only_One_Active_Ticket()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid>[] results = await Task.WhenAll(
            database.ReserveAsync(database.Seat.Id.Value),
            database.ReserveAsync(database.Seat.Id.Value));

        results.Count(result => result.IsSuccess).Should().Be(1);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(ticket => ticket.TicketStatus == TicketStatus.Reserved,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Ticket_Details_Use_Persisted_Order_Ownership_And_Preserve_Original_Dto()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Guid ticketId = (await OrderTicketFixture.ForOrder(database.Context, order)
            .SingleAsync(TestContext.Current.CancellationToken)).Id.Value;
        new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddApplication();

        HistoryCommandCounter baselineCounter = new();
        await using ApplicationDbContext baselineContext = database.NewContext(interceptor: baselineCounter);
        Ticket baselineTicket = await baselineContext.Tickets.AsNoTracking()
            .Include(ticket => ticket.Session!).ThenInclude(session => session.Movie)
            .Include(ticket => ticket.Session!).ThenInclude(session => session.Hall)
            .Include(ticket => ticket.Seat!).ThenInclude(seat => seat.SeatType)
            .SingleAsync(ticket => ticket.Id == new EntityId<Ticket>(ticketId), TestContext.Current.CancellationToken);
        await using ApplicationDbContext ownershipContext = database.NewContext();
        (await ownershipContext.Orders.SingleAsync(o => o.Id == baselineTicket.OrderId, TestContext.Current.CancellationToken))
            .UserId.Should().Be(database.UserId);
        TicketDto baselineDto = baselineTicket.Adapt<TicketDto>();
        baselineCounter.Commands.Should().ContainSingle();

        HistoryCommandCounter counter = new();
        await using ApplicationDbContext queryContext = database.NewContext(interceptor: counter);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        GetTicketDetailsQueryHandler handler = new(queryContext, currentUser);

        Result<TicketDto> owner = await handler.Handle(new GetTicketDetailsQuery(ticketId),
            TestContext.Current.CancellationToken);
        owner.IsSuccess.Should().BeTrue();
        owner.Value.Should().Be(baselineDto);
        counter.Commands.Should().ContainSingle();
        counter.Commands[0].Should().Contain("tickets").And.Contain("orders").And.Contain("user_id");

        counter.Commands.Clear();
        currentUser.UserId.Returns(Guid.NewGuid());
        Result<TicketDto> stranger = await handler.Handle(new GetTicketDetailsQuery(ticketId),
            TestContext.Current.CancellationToken);
        stranger.IsFailure.Should().BeTrue();
        stranger.Error.Code.Should().Be("Ticket.NotFound");
        counter.Commands.Should().ContainSingle();

        counter.Commands.Clear();
        currentUser.UserId.Returns(database.UserId);
        Result<TicketDto> missing = await handler.Handle(new GetTicketDetailsQuery(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        missing.Error.Code.Should().Be("Ticket.NotFound");
        counter.Commands.Should().ContainSingle();

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        counter.Commands.Clear();
        Result<TicketDto> hidden = await handler.Handle(new GetTicketDetailsQuery(ticketId),
            TestContext.Current.CancellationToken);
        hidden.IsSuccess.Should().BeTrue();
        counter.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task History_Batches_Tickets_And_Matches_Original_Projection()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat secondSeat = await database.AddSeatAsync();
        Order first = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat, secondSeat],
            new() { [database.Seat.Id] = 50m, [secondSeat.Id] = 60m });
        Session foreignSession = Session.Create(EntityId<Session>.New(),
            database.Session.StartTime.AddDays(1), database.Session.StartTime.AddDays(1).AddHours(2),
            database.Session.MovieId, database.Session.HallId, database.Session.PricingId);
        Order second = Order.New(EntityId<Order>.New(), 20m, database.UserId, foreignSession.Id);
        User foreignUser = new() { Id = Guid.NewGuid(), UserName = $"foreign-{Guid.NewGuid():N}" };
        Order foreign = OrderTicketFixture.CreateAndTrack(database.Context, foreignUser.Id, foreignSession, [secondSeat],
            new() { [secondSeat.Id] = 70m });
        database.Context.Users.Add(foreignUser);
        database.Context.Sessions.Add(foreignSession);
        database.Context.Orders.Add(second);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        TypeAdapterConfig original = new();
        new TicketMappingConfig().Register(original);
        original.NewConfig<Order, OrderDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.TotalAmount, src => src.TotalAmount)
            .Map(dest => dest.CreatedAt, src => src.BookingDate)
            .Map(dest => dest.Status, src => src.Status.ToString())
            .Ignore(dest => dest.Tickets);
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddApplication();
        services.AddOrdersApplication();
        await using ApplicationDbContext baselineContext = database.NewContext();
        List<OrderDto> baseline = await baselineContext.Orders.AsNoTracking()
            .Where(order => order.UserId == database.UserId)
            .OrderByDescending(order => order.BookingDate)
            .ProjectToType<OrderDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        foreach (OrderDto dto in baseline)
            dto.Tickets = await baselineContext.Tickets.AsNoTracking()
                .Where(ticket => ticket.OrderId == new EntityId<Order>(dto.Id))
                .ProjectToType<TicketDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        HistoryCommandCounter counter = new();
        await using ApplicationDbContext queryContext = database.NewContext(interceptor: counter);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);

        Result<OrderHistoryVm> mine = await new GetMyOrdersQueryHandler(queryContext, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        mine.IsSuccess.Should().BeTrue();
        mine.Value.ActiveOrders.Should().HaveCount(2);
        mine.Value.ActiveOrders.Select(order => order.Id).Should().Equal(first.Id.Value, second.Id.Value);
        mine.Value.ActiveOrders.Single(order => order.Id == first.Id.Value).Tickets.Should().HaveCount(2);
        mine.Value.ActiveOrders.Single(order => order.Id == second.Id.Value).Tickets.Should().BeEmpty();
        counter.Commands.Should().HaveCount(3);
        counter.Commands.Last().Should().Contain("tickets").And.Contain("order_id");

        counter.Commands.Clear();
        Result<List<OrderDto>> admin = await new GetUserOrdersQueryHandler(queryContext)
            .Handle(new GetUserOrdersQuery(database.UserId), TestContext.Current.CancellationToken);
        counter.Commands.Should().HaveCount(2);
        admin.Value.Should().BeEquivalentTo(baseline);
        admin.Value.Select(order => order.Id).Should().Equal(baseline.Select(order => order.Id));
        admin.Value.Select(order => order.Id).Should().NotContain(foreign.Id.Value);

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext filtered = database.NewContext();
        List<OrderDto> filteredBaseline = await filtered.Orders.AsNoTracking()
            .Where(order => order.UserId == database.UserId)
            .ProjectToType<OrderDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        foreach (OrderDto dto in filteredBaseline)
            dto.Tickets = await filtered.Tickets.AsNoTracking()
                .Where(ticket => ticket.OrderId == new EntityId<Order>(dto.Id))
                .ProjectToType<TicketDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        Result<List<OrderDto>> filteredHistory = await new GetUserOrdersQueryHandler(filtered)
            .Handle(new GetUserOrdersQuery(database.UserId), TestContext.Current.CancellationToken);
        filteredHistory.Value.Should().HaveCount(2);
        filteredHistory.Value.Single(order => order.Id == first.Id.Value).Tickets.Should().HaveCount(2);
        filteredHistory.Value.Single(order => order.Id == second.Id.Value).Tickets.Should().BeEmpty();
    }

    [Fact]
    public async Task Required_Session_Filter_Drops_Include_But_History_Preserves_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session,
            [database.Seat], new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext read = database.NewContext();
        (await read.Orders.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await read.Orders.AsNoTracking().Include(item => item.Session)
            .ToListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        Result<OrderHistoryVm> history = await new GetMyOrdersQueryHandler(read, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        history.Value.ActiveOrders.Concat(history.Value.PastOrders)
            .Select(item => item.Id).Should().Contain(order.Id.Value);
    }

    private sealed class HistoryCommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Paid_Event_Queries_Persisted_Tickets_During_SaveChanges()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        IPublisher publisher = Substitute.For<IPublisher>();
        OrderPaidEventHandler? handler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderPaidEvent> notification
                ? handler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        await using ApplicationDbContext context = database.NewContext(publisher);
        handler = new(context, notifier, locks, NullLogger<OrderPaidEventHandler>.Instance);
        Order order = await context.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken);
        context.ChangeTracker.Entries<Ticket>().Should().BeEmpty();

        order.MarkAsPaid("paid-test");
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await locks.Received(1).UnlockSeatAsync(database.Session.Id.Value, database.Seat.Id.Value,
            database.UserId, TestContext.Current.CancellationToken);
        await locks.Received(1).UnlockSeatAsync(database.Session.Id.Value, second.Id.Value,
            database.UserId, TestContext.Current.CancellationToken);
        await notifier.Received(1).NotifyOrderCompleted(database.UserId, reserved.Value);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Paid_Event_Includes_Added_Ticket_Before_Insert()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        IPublisher publisher = Substitute.For<IPublisher>();
        OrderPaidEventHandler? handler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderPaidEvent> notification
                ? handler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        await using ApplicationDbContext context = database.NewContext(publisher);
        handler = new(context, notifier, locks, NullLogger<OrderPaidEventHandler>.Instance);
        Order order = OrderTicketFixture.CreateAndTrack(context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        order.MarkAsPaid("paid-on-insert");

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await locks.Received(1).UnlockSeatAsync(database.Session.Id.Value, database.Seat.Id.Value,
            database.UserId, TestContext.Current.CancellationToken);
        await notifier.Received(1).NotifyOrderCompleted(database.UserId, order.Id.Value);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.SingleAsync(t => t.OrderId == order.Id, TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Cancellation_Loads_Tracked_Tickets_And_Dispatches_Seat_Side_Effects()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();

        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        IPublisher publisher = Substitute.For<IPublisher>();
        OrderCancelledEventHandler? eventHandler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderCancelledDomainEvent> notification
                ? eventHandler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        DispatchDomainEventsInterceptor dispatcher = new(publisher);
        await using ApplicationDbContext cancellation = database.NewContext(domainInterceptor: dispatcher);
        eventHandler = new(cancellation, locks, notifier,
            NullLogger<OrderCancelledEventHandler>.Instance);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();

        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment,
                Substitute.For<ILoyaltyService>(), dispatcher)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        Order trackedOrder = cancellation.ChangeTracker.Entries<Order>().Single().Entity;
        List<Ticket> releasedTickets = await OrderTicketFixture.ForOrder(cancellation, trackedOrder)
            .ToListAsync(TestContext.Current.CancellationToken);
        releasedTickets.Should().HaveCount(2);
        releasedTickets.Should().OnlyContain(t => t.OrderId == trackedOrder.Id && t.TicketStatus == TicketStatus.Released);
        await locks.Received(1).UnlockSeatsAsync(database.Session.Id.Value,
            Arg.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { database.Seat.Id.Value, second.Id.Value })),
            database.UserId, Arg.Any<CancellationToken>(), false);
        await notifier.Received(1).NotifySeatUnlockedAsync(database.Session.Id.Value, database.Seat.Id.Value,
            Arg.Any<CancellationToken>());
        await notifier.Received(1).NotifySeatUnlockedAsync(database.Session.Id.Value, second.Id.Value,
            Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Tickets.Where(t => t.OrderId == new EntityId<Order>(reserved.Value))
            .ToListAsync(TestContext.Current.CancellationToken)).Should().OnlyContain(t => t.TicketStatus == TicketStatus.Released);
    }

    [Fact]
    public async Task Pending_Cancellation_Production_Retry_Commits_And_Dispatches_After_Dispose()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        await using ApplicationDbContext cancellation = database.NewContext(retries: true);
        cancellation.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
        IPaymentService payment = Substitute.For<IPaymentService>();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        IPostCommitDomainEventDispatcher dispatcher = Substitute.For<IPostCommitDomainEventDispatcher>();
        dispatcher.DispatchAfterCommitAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            cancellation.Database.CurrentTransaction.Should().BeNull();
            cancellation.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue();
            await AssertCancellationStateAsync(database, reserved.Value, OrderStatus.Cancelled, TicketStatus.Released);
        });

        Result result = await new CancelOrderCommandHandler(cancellation, CancellationUser(database), payment, loyalty, dispatcher)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await dispatcher.Received(1).DispatchAfterCommitAsync(Arg.Any<CancellationToken>());
        payment.ReceivedCalls().Should().BeEmpty();
        loyalty.ReceivedCalls().Should().BeEmpty();
        await AssertCancellationStateAsync(database, reserved.Value, OrderStatus.Cancelled, TicketStatus.Released);
    }

    [Fact]
    public async Task Pending_Cancellation_Production_Retry_Rolls_Back_Before_Commit()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await database.Context.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_ticket_release() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected ticket release failure'; END $$;
            CREATE TRIGGER reject_ticket_release BEFORE UPDATE OF ticket_status ON tickets
            FOR EACH ROW WHEN (NEW.ticket_status=4) EXECUTE FUNCTION reject_ticket_release();
            """, TestContext.Current.CancellationToken);
        await using ApplicationDbContext cancellation = database.NewContext(retries: true);
        IPaymentService payment = Substitute.For<IPaymentService>();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        IPostCommitDomainEventDispatcher dispatcher = Substitute.For<IPostCommitDomainEventDispatcher>();
        Func<Task> cancel = async () => await new CancelOrderCommandHandler(
            cancellation, CancellationUser(database), payment, loyalty, dispatcher)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        (await cancel.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.MessageText.Should().Be("injected ticket release failure");
        await AssertCancellationStateAsync(database, reserved.Value, OrderStatus.Pending, TicketStatus.Reserved);
        await dispatcher.DidNotReceiveWithAnyArgs().DispatchAfterCommitAsync(default);
        payment.ReceivedCalls().Should().BeEmpty();
        loyalty.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_Cancellation_Production_Retry_Rejects_Processing_And_Unknown(bool unknown)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        Order order = await database.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        order.StartPayment();
        if (unknown) order.MarkPaymentOutcomeUnknown();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext cancellation = database.NewContext(retries: true);
        IPaymentService payment = Substitute.For<IPaymentService>();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();

        Result result = await new CancelOrderCommandHandler(cancellation, CancellationUser(database), payment, loyalty)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.InvalidState");
        await AssertCancellationStateAsync(database, reserved.Value,
            unknown ? OrderStatus.PaymentOutcomeUnknown : OrderStatus.PaymentProcessing, TicketStatus.Reserved);
        payment.ReceivedCalls().Should().BeEmpty();
        loyalty.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_Cancellation_Production_Retry_Lost_Claim_Does_Not_Release_Checkout()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        CancellationClaimBarrier barrier = new();
        await using ApplicationDbContext cancellation = database.NewContext(interceptor: barrier, retries: true);
        await using ApplicationDbContext checkoutContext = database.NewContext(retries: true);
        IPaymentService payment = Substitute.For<IPaymentService>();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        IPostCommitDomainEventDispatcher dispatcher = Substitute.For<IPostCommitDomainEventDispatcher>();
        TaskCompletionSource paymentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PaymentResult> finishPayment = new(TaskCreationOptions.RunContinuationsAsynchronously);
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            paymentStarted.TrySetResult();
            return finishPayment.Task;
        });
        Task<Result> cancel = new CancelOrderCommandHandler(
            cancellation, CancellationUser(database), payment, loyalty, dispatcher)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);
        Task<Result<Guid>>? checkout = null;
        try
        {
            // Cancellation read Pending, but checkout wins before its conditional claim.
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            checkout = CreateCheckout(checkoutContext, payment).ProcessCheckoutAsync(
                database.UserId, reserved.Value, false, false, "token", TestContext.Current.CancellationToken);
            await paymentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            barrier.Release.TrySetResult();
            (await cancel).Error.Code.Should().Be("Order.InvalidState");
            await AssertCancellationStateAsync(database, reserved.Value, OrderStatus.PaymentProcessing, TicketStatus.Reserved);
            await dispatcher.DidNotReceiveWithAnyArgs().DispatchAfterCommitAsync(default);
            await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, default);
            loyalty.ReceivedCalls().Should().BeEmpty();
            finishPayment.TrySetResult(PaymentResult.Success("checkout-wins"));
            (await checkout).IsSuccess.Should().BeTrue();
            await AssertCancellationStateAsync(database, reserved.Value, OrderStatus.Paid, TicketStatus.Valid);
        }
        finally
        {
            barrier.Release.TrySetResult();
            finishPayment.TrySetResult(PaymentResult.Success("checkout-wins"));
            await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (checkout is not null)
                await checkout.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Pending_Cancellation_Production_Retry_Lost_Commit_Acknowledgement_Is_Not_Rollback()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        LostCancellationCommitAcknowledgement lost = new();
        await using ApplicationDbContext cancellation = database.NewContext(retries: true, transactionInterceptor: lost);
        IPostCommitDomainEventDispatcher dispatcher = Substitute.For<IPostCommitDomainEventDispatcher>();
        IPaymentService payment = Substitute.For<IPaymentService>();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        Func<Task> cancel = async () => await new CancelOrderCommandHandler(
            cancellation, CancellationUser(database), payment, loyalty, dispatcher)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        await cancel.Should().ThrowAsync<IOException>().WithMessage("injected lost commit acknowledgement");
        lost.Commits.Should().Be(1);
        await AssertCancellationStateAsync(database, reserved.Value, OrderStatus.Cancelled, TicketStatus.Released);
        await dispatcher.DidNotReceiveWithAnyArgs().DispatchAfterCommitAsync(default);
        payment.ReceivedCalls().Should().BeEmpty();
        loyalty.ReceivedCalls().Should().BeEmpty();
    }

    private static ICurrentUserService CancellationUser(ReservationDatabase database)
    {
        ICurrentUserService user = Substitute.For<ICurrentUserService>();
        user.UserId.Returns(database.UserId);
        user.IsInRole("Admin").Returns(true);
        return user;
    }

    private static async Task AssertCancellationStateAsync(
        ReservationDatabase database, Guid orderId, OrderStatus orderStatus, TicketStatus ticketStatus)
    {
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(orderId),
            TestContext.Current.CancellationToken)).Status.Should().Be(orderStatus);
        (await verify.Tickets.IgnoreQueryFilters().Where(t => t.OrderId == new EntityId<Order>(orderId))
            .ToListAsync(TestContext.Current.CancellationToken))
            .Should().NotBeEmpty().And.OnlyContain(ticket => ticket.TicketStatus == ticketStatus);
    }

    private sealed class CancellationClaimBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE orders", StringComparison.OrdinalIgnoreCase))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class LostCancellationCommitAcknowledgement : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new IOException("injected lost commit acknowledgement");
        }
    }

    [Fact]
    public async Task Cancellation_Releases_Reserved_Ticket_In_Inactive_Hall()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext cancellation = database.NewContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsInRole("Admin").Returns(true);

        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, Substitute.For<IPaymentService>(), Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        cancellation.ChangeTracker.Entries<Ticket>().Should().ContainSingle();
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Released);
    }

    [Fact]
    public async Task Paid_Cancellation_Refunds_Payment_And_Points_Once_And_Persists_Tickets()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            order.ApplyLoyaltyDiscount(10);
            order.MarkAsPaid("payment-1");
            await ConfirmTicketsAsync(paidContext, order);
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("refund-1"));
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.RefundPointsAsync(database.UserId, 10, reserved.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));
        await using (ApplicationDbContext cancellation = database.NewContext())
        {
            Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment, loyalty)
                .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);
            result.IsSuccess.Should().BeTrue();
        }
        await using (ApplicationDbContext repeated = database.NewContext())
        {
            Result result = await new CancelOrderCommandHandler(repeated, currentUser, payment, loyalty)
                .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);
            result.Error.Code.Should().Be("Order.AlreadyCancelled");
        }

        await payment.Received(1).RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>());
        await loyalty.Received(1).RefundPointsAsync(database.UserId, 10, reserved.Value,
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext(retries: true);
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Tickets.Where(ticket => ticket.OrderId == new EntityId<Order>(reserved.Value))
            .ToListAsync(TestContext.Current.CancellationToken)).Should()
            .OnlyContain(ticket => ticket.TicketStatus == TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancellation_Without_Tickets_Does_Not_Refund_Or_Change_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = Order.New(EntityId<Order>.New(), 50m, database.UserId, database.Session.Id);
        order.MarkAsPaid("payment-1");
        database.Context.Orders.Add(order);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();

        await using ApplicationDbContext cancellation = database.NewContext();
        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment,
            Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(order.Id.Value),
            TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Inactive_Hall_Purchased_Ticket_Remains_Visible_Only_To_Owner()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            order.MarkAsPaid("payment-1");
            await ConfirmTicketsAsync(paidContext, order);
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        Guid ticketId = (await database.Context.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Id.Value;
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext read = database.NewContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        Result<TicketDto> owned = await new GetTicketDetailsQueryHandler(read, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);
        owned.IsSuccess.Should().BeTrue();
        owned.Value.Id.Should().Be(ticketId);
        Result<Cinema.Application.Orders.Queries.GetMyOrders.OrderHistoryVm> history = await new GetMyOrdersQueryHandler(read, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        history.IsSuccess.Should().BeTrue();
        history.Value.ActiveOrders.Concat(history.Value.PastOrders).Single().Tickets.Should().ContainSingle();

        currentUser.UserId.Returns(Guid.NewGuid());
        Result<TicketDto> foreign = await new GetTicketDetailsQueryHandler(read, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);
        foreign.Error.Code.Should().Be("Ticket.NotFound");
        (await read.Seats.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await read.Sessions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Paid_Cancellation_Rolls_Back_Consumed_Gold_Upgrade()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            Ticket ticket = await paidContext.Tickets.SingleAsync(t => t.OrderId == order.Id,
                TestContext.Current.CancellationToken);
            order.ApplyGoldDiscount(20m);
            ticket.ApplyGoldUpgrade(30m);
            order.MarkAsPaid("payment-1");
            ticket.ConfirmPurchase(order);
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("refund-1"));
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));

        await using ApplicationDbContext cancellation = database.NewContext();
        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment, loyalty)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await loyalty.Received(1).RollbackGoldUpgradeAsync(database.UserId, reserved.Value,
            Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancellation_At_Ten_Minute_Cutoff_Leaves_Order_And_Ticket_Valid()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        DateTime cutoffStart = DateTime.UtcNow.AddMinutes(10);
        await database.Context.Sessions.Where(session => session.Id == database.Session.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.StartTime, cutoffStart),
                TestContext.Current.CancellationToken);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);

        await using ApplicationDbContext cancellation = database.NewContext();
        Result result = await new CancelOrderCommandHandler(cancellation, currentUser,
            Substitute.For<IPaymentService>(), Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.TooLate");
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Pending);
        (await verify.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Reserved);
    }

    [Fact]
    public async Task Database_Save_Failure_After_Payment_Refund_Leaves_Paid_Order_For_Reconciliation()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            order.MarkAsPaid("payment-1");
            await ConfirmTicketsAsync(paidContext, order);
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("refund-1"));
        IPublisher publisher = Substitute.For<IPublisher>();
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        OrderCancelledEventHandler? handler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderCancelledDomainEvent> notification
                ? handler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        await using ApplicationDbContext cancellation = database.NewContext(publisher, new FailOrderUpdateInterceptor());
        handler = new(cancellation, locks, notifier, NullLogger<OrderCancelledEventHandler>.Instance);

        Func<Task> cancel = async () => await new CancelOrderCommandHandler(cancellation, currentUser, payment,
            Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(reserved.Value),
            TestContext.Current.CancellationToken);

        DbUpdateException failure = (await cancel.Should().ThrowAsync<DbUpdateException>()).Which;
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
        await payment.Received(1).RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>());
        await locks.DidNotReceiveWithAnyArgs().UnlockSeatsAsync(default, default!, default, default);
        await notifier.DidNotReceiveWithAnyArgs().NotifySeatUnlockedAsync(default, default, default);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    private static async Task ConfirmTicketsAsync(ApplicationDbContext context, Order order)
    {
        List<Ticket> tickets = await context.Tickets.IgnoreQueryFilters()
            .Where(ticket => ticket.OrderId == order.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (Ticket ticket in tickets)
            ticket.ConfirmPurchase(order);
    }

    private sealed class FailOrderUpdateInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE orders", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated cancellation save failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Reservation_Persists_Multiple_Tickets_With_One_Tracked_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();

        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);

        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        order.Status.Should().Be(OrderStatus.Pending);
        order.TotalAmount.Should().Be(100m);
        order.PaidAmount.Should().Be(100m);
        List<Ticket> tickets = await OrderTicketFixture.ForOrder(verify, order)
            .ToListAsync(TestContext.Current.CancellationToken);
        tickets.Should().HaveCount(2);
        tickets.Select(t => t.SeatId).Should().BeEquivalentTo([database.Seat.Id, second.Id]);
        tickets.Should().OnlyContain(t => t.OrderId == order.Id &&
            t.SessionId == database.Session.Id && t.PriceSnapshot == 50m && t.TicketStatus == TicketStatus.Reserved);
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Gold_Checkout_Persists_Ticket_And_Order_Discount_Together()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>())
            .Returns((true, ""));
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("gold-postgres"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m,
                database.Seat.Id.Value, 50m, 30m, null)));
        OrderCheckoutOrchestrator checkout = new(checkoutContext, loyalty, payment,
            Substitute.For<ISeatLockingService>(), gold, Substitute.For<IPublishEndpoint>(),
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, Guid.NewGuid(),
            [Guid.NewGuid()], reserved.Value, true, false, "token", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().ContainSingle();
        await gold.Received(1).CalculateAsync(Arg.Is<Session>(s => s.Id == database.Session.Id),
            Arg.Is<IReadOnlyCollection<GoldUpgradeTicketPrice>>(tickets =>
                tickets.Count == 1 && tickets.Single().SeatId == database.Seat.Id.Value),
            Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(30m);
        persisted.PaidAmount.Should().Be(30m);
        Ticket persistedTicket = await OrderTicketFixture.ForOrder(verify, persisted)
            .SingleAsync(TestContext.Current.CancellationToken);
        persistedTicket.PriceSnapshot.Should().Be(30m);
        persistedTicket.IsGoldUpgraded.Should().BeTrue();
        persistedTicket.OrderId.Should().Be(persisted.Id);
    }

    [Fact]
    public async Task Checkout_Publishes_Persisted_Ticket_Count_For_Multiple_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(100m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("two-tickets"));
        IPublishEndpoint publisher = Substitute.For<IPublishEndpoint>();
        OrderCheckoutOrchestrator checkout = new(checkoutContext, Substitute.For<ILoyaltyService>(), payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(), publisher,
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, Guid.NewGuid(),
            [Guid.NewGuid()], reserved.Value, false, false, "token", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().HaveCount(2);
        await publisher.Received(1).Publish(Arg.Is<TicketPurchasedEvent>(e =>
            e.OrderId == reserved.Value && e.TotalAmount == 100m && e.TicketsCount == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gold_Checkout_With_Equal_Prices_Upgrades_One_Tracked_Ticket()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>())
            .Returns((true, ""));
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(80m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("equal-price"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m,
                database.Seat.Id.Value, 50m, 30m, null)));
        OrderCheckoutOrchestrator checkout = new(checkoutContext, loyalty, payment,
            Substitute.For<ISeatLockingService>(), gold, Substitute.For<IPublishEndpoint>(),
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value, second.Id.Value], reserved.Value, true, false, "token",
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(80m);
        persisted.PaidAmount.Should().Be(80m);
        List<Ticket> persistedTickets = await OrderTicketFixture.ForOrder(verify, persisted)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedTickets.Should().ContainSingle(t => t.IsGoldUpgraded && t.PriceSnapshot == 30m);
        persistedTickets.Should().ContainSingle(t => !t.IsGoldUpgraded && t.PriceSnapshot == 50m);
    }

    [Fact]
    public async Task Checkout_Confirms_Ticket_In_Inactive_Hall()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext checkoutContext = database.NewContext();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("filtered-ticket"));
        IPublishEndpoint publisher = Substitute.For<IPublishEndpoint>();
        OrderCheckoutOrchestrator checkout = new(checkoutContext, Substitute.For<ILoyaltyService>(), payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(), publisher,
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, false, false, "token", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().ContainSingle();
        await publisher.Received(1).Publish(Arg.Is<TicketPurchasedEvent>(e =>
            e.OrderId == reserved.Value && e.TicketsCount == 1), Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Reservation_Creates_Pending_Order_With_Reserved_Ticket_And_Rejects_Resale()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);

        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        order.Status.Should().Be(OrderStatus.Pending);
        (await OrderTicketFixture.ForOrder(verify, order).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Reserved);
        order.TotalAmount.Should().Be(50m);
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Order.SeatsSold");
        (await verify.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Declined_Payment_Releases_Tickets_And_Allows_Resale()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "test_fail_token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure("declined"));
        OrderCheckoutOrchestrator checkout = new(checkoutContext, Substitute.For<ILoyaltyService>(), payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(),
            Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, false, false, "test_fail_token",
            TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Payment.Failed");
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().ContainSingle()
            .Which.State.Should().Be(EntityState.Unchanged);
        await using ApplicationDbContext verify = database.NewContext();
        Order failed = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken);
        failed.Status.Should().Be(OrderStatus.Failed);
        (await OrderTicketFixture.ForOrder(verify, failed).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Released);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Claimed_Checkout_Blocks_Another_Charge_And_Expiration()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext aging = database.NewContext())
        {
            Order order = await aging.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            aging.Entry(order).Property(item => item.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            await aging.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        TaskCompletionSource<bool> paymentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PaymentResult> finishPayment = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            paymentStarted.TrySetResult(true);
            return finishPayment.Task;
        });
        await using ApplicationDbContext firstContext = database.NewContext();
        await using ApplicationDbContext secondContext = database.NewContext();
        OrderCheckoutOrchestrator first = CreateCheckout(firstContext, payment);
        OrderCheckoutOrchestrator second = CreateCheckout(secondContext, payment);
        Task<Result<Guid>> firstCheckout = first.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, false, false, "token", TestContext.Current.CancellationToken);
        await paymentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Result<Guid> secondResult = await second.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, false, false, "token", TestContext.Current.CancellationToken);
        secondResult.Error.Code.Should().Be("Order.CheckoutInProgress");
        await using (ApplicationDbContext jobContext = database.NewContext())
            await new CancelExpiredOrdersJob(jobContext, Substitute.For<ISeatLockingService>(), Substitute.For<ITicketNotifier>(), NullLogger<CancelExpiredOrdersJob>.Instance)
                .Process(TestContext.Current.CancellationToken);

        await using (ApplicationDbContext processing = database.NewContext())
        {
            (await processing.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.PaymentProcessing);
        }
        finishPayment.SetResult(PaymentResult.Success("single-charge"));
        (await firstCheckout).IsSuccess.Should().BeTrue();
        await payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.SingleAsync(item => item.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Claimed_Checkout_Prevents_Pending_Cancellation_From_Releasing_Ticket()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        TaskCompletionSource<bool> paymentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PaymentResult> finishPayment = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            paymentStarted.TrySetResult(true);
            return finishPayment.Task;
        });
        await using ApplicationDbContext checkoutContext = database.NewContext();
        Task<Result<Guid>> checkout = CreateCheckout(checkoutContext, payment).ProcessCheckoutAsync(
            database.UserId, database.Session.Id.Value, [database.Seat.Id.Value], reserved.Value,
            false, false, "token", TestContext.Current.CancellationToken);
        await paymentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await using ApplicationDbContext cancellationContext = database.NewContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        currentUser.IsInRole("Admin").Returns(true);
        Result cancellation = await new CancelOrderCommandHandler(cancellationContext, currentUser, payment,
            Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(reserved.Value),
                TestContext.Current.CancellationToken);

        cancellation.Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, default);
        await using (ApplicationDbContext processing = database.NewContext())
        {
            (await processing.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.PaymentProcessing);
            (await processing.Tickets.SingleAsync(item => item.OrderId == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Reserved);
        }

        finishPayment.SetResult(PaymentResult.Success("single-charge"));
        (await checkout).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.SingleAsync(item => item.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Expiration_Claimed_First_Prevents_Payment()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext aging = database.NewContext())
        {
            Order order = await aging.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            aging.Entry(order).Property(item => item.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            await aging.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (ApplicationDbContext jobContext = database.NewContext())
            await new CancelExpiredOrdersJob(jobContext, Substitute.For<ISeatLockingService>(), Substitute.For<ITicketNotifier>(), NullLogger<CancelExpiredOrdersJob>.Instance)
                .Process(TestContext.Current.CancellationToken);

        IPaymentService payment = Substitute.For<IPaymentService>();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        Result<Guid> result = await CreateCheckout(checkoutContext, payment).ProcessCheckoutAsync(
            database.UserId, database.Session.Id.Value, [database.Seat.Id.Value], reserved.Value,
            false, false, "token", TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, default);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Expired);
        (await verify.Tickets.SingleAsync(item => item.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Released);
    }

    private static OrderCheckoutOrchestrator CreateCheckout(ApplicationDbContext context, IPaymentService payment) =>
        new(context, Substitute.For<ILoyaltyService>(), payment, Substitute.For<ISeatLockingService>(),
            Substitute.For<IGoldUpgradePricingService>(), Substitute.For<IPublishEndpoint>(),
            NullLogger<OrderCheckoutOrchestrator>.Instance);

    [Fact]
    public async Task Unknown_Payment_Outcome_Persists_Reservation_And_Blocks_Retry()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns<Task<PaymentResult>>(_ => throw new TimeoutException("provider timeout"));
        await using ApplicationDbContext checkoutContext = database.NewContext();
        Result<Guid> first = await CreateCheckout(checkoutContext, payment).ProcessCheckoutAsync(
            database.UserId, database.Session.Id.Value, [database.Seat.Id.Value], reserved.Value,
            false, false, "token", TestContext.Current.CancellationToken);

        first.Error.Code.Should().Be("Payment.OutcomeUnknown");
        await using ApplicationDbContext retryContext = database.NewContext();
        Result<Guid> retry = await CreateCheckout(retryContext, payment).ProcessCheckoutAsync(
            database.UserId, database.Session.Id.Value, [database.Seat.Id.Value], reserved.Value,
            false, false, "token", TestContext.Current.CancellationToken);
        retry.Error.Code.Should().Be("Order.InvalidState");
        await payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(item => item.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.PaymentOutcomeUnknown);
        (await verify.Tickets.SingleAsync(item => item.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Reserved);
    }

    [Fact]
    public async Task Legacy_Pending_Valid_Ticket_Requires_Reconciliation()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order legacy = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session,
            [database.Seat], new() { [database.Seat.Id] = 50m });
        database.Context.Entry(legacy).Property(item => item.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IPaymentService payment = Substitute.For<IPaymentService>();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        Result<Guid> checkout = await CreateCheckout(checkoutContext, payment).ProcessCheckoutAsync(
            database.UserId, database.Session.Id.Value, [database.Seat.Id.Value], legacy.Id.Value,
            false, false, "token", TestContext.Current.CancellationToken);
        checkout.Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, default);
        await using (ApplicationDbContext jobContext = database.NewContext())
            await new CancelExpiredOrdersJob(jobContext, Substitute.For<ISeatLockingService>(), Substitute.For<ITicketNotifier>(), NullLogger<CancelExpiredOrdersJob>.Instance)
                .Process(TestContext.Current.CancellationToken);

        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(item => item.Id == legacy.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Pending);
        (await verify.Tickets.SingleAsync(item => item.OrderId == legacy.Id, TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Order.SeatsSold");
    }

    [Fact]
    public async Task Session_Occupancy_Only_Includes_Reserved_Valid_And_Used_Tickets()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat[] seats = [database.Seat, .. Enumerable.Range(2, 4).Select(number =>
            Seat.New(EntityId<Seat>.New(), "A", number, 1, number, SeatStatus.Active,
                database.Seat.HallId, database.Seat.SeatTypeId))];
        database.Context.Seats.AddRange(seats.Skip(1));
        TicketStatus[] statuses = [TicketStatus.Reserved, TicketStatus.Valid, TicketStatus.Used,
            TicketStatus.Released, TicketStatus.Refunded];
        for (int index = 0; index < seats.Length; index++)
        {
            Order order = Order.New(EntityId<Order>.New(), 50m, database.UserId, database.Session.Id);
            Ticket ticket = statuses[index] is TicketStatus.Reserved or TicketStatus.Released
                ? Ticket.Reserve(EntityId<Ticket>.New(), 50m, order.Id, database.Session.Id, seats[index].Id)
                : Ticket.New(EntityId<Ticket>.New(), 50m, TicketStatus.Valid, order.Id, database.Session.Id, seats[index].Id);
            if (statuses[index] == TicketStatus.Released)
            {
                order.MarkAsExpired();
                ticket.ReleaseReservation(order);
            }
            else if (statuses[index] is TicketStatus.Valid or TicketStatus.Used or TicketStatus.Refunded)
            {
                order.MarkAsPaid($"paid-{index}");
                if (statuses[index] == TicketStatus.Used)
                    ticket.MarkAsUsed();
                if (statuses[index] == TicketStatus.Refunded)
                    ticket.MarkAsRefunded();
            }
            database.Context.Orders.Add(order);
            database.Context.Tickets.Add(ticket);
        }
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.GetLockedSeatsBySessionAsync(database.Session.Id.Value, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());
        await using ApplicationDbContext read = database.NewContext();
        var result = await new GetSessionByIdQueryHandler(read, locks)
            .Handle(new GetSessionByIdQuery(database.Session.Id.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.OccupiedSeatIds.Should().BeEquivalentTo(seats.Take(3).Select(seat => seat.Id.Value));
    }

    [Fact]
    public async Task Expiration_Releases_All_Unpaid_Tickets_Once_And_Preserves_Paid_Orders()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> expiredId = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        expiredId.IsSuccess.Should().BeTrue();
        Seat paidSeat = Seat.New(EntityId<Seat>.New(), "A", 3, 1, 3, SeatStatus.Active,
            database.Seat.HallId, database.Seat.SeatTypeId);
        database.Context.Seats.Add(paidSeat);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Result<Guid> paidId = await database.ReserveAsync(paidSeat.Id.Value);
        paidId.IsSuccess.Should().BeTrue();
        await using (ApplicationDbContext seed = database.NewContext())
        {
            Order expired = await seed.Orders.SingleAsync(o => o.Id == new EntityId<Order>(expiredId.Value),
                TestContext.Current.CancellationToken);
            seed.Entry(expired).Property(o => o.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            Order paid = await seed.Orders.SingleAsync(o => o.Id == new EntityId<Order>(paidId.Value),
                TestContext.Current.CancellationToken);
            paid.MarkAsPaid("already-paid");
            await ConfirmTicketsAsync(seed, paid);
            seed.Entry(paid).Property(o => o.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext jobContext = database.NewContext();
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        CancelExpiredOrdersJob job = new(jobContext, locks, notifier, NullLogger<CancelExpiredOrdersJob>.Instance);

        await job.Process(TestContext.Current.CancellationToken);
        await job.Process(TestContext.Current.CancellationToken);

        await locks.Received(1).UnlockSeatsAsync(database.Session.Id.Value,
            Arg.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { database.Seat.Id.Value, second.Id.Value })),
            database.UserId, Arg.Any<CancellationToken>(), false);
        await notifier.Received(1).NotifySeatUnlockedAsync(database.Session.Id.Value, database.Seat.Id.Value,
            Arg.Any<CancellationToken>());
        await notifier.Received(1).NotifySeatUnlockedAsync(database.Session.Id.Value, second.Id.Value,
            Arg.Any<CancellationToken>());

        await using ApplicationDbContext verify = database.NewContext();
        Order cancelled = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(expiredId.Value),
            TestContext.Current.CancellationToken);
        cancelled.Status.Should().Be(OrderStatus.Expired);
        (await OrderTicketFixture.ForOrder(verify, cancelled).ToListAsync(TestContext.Current.CancellationToken))
            .Should().HaveCount(2).And.OnlyContain(t => t.TicketStatus == TicketStatus.Released);
        Order persistedPaid = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(paidId.Value),
            TestContext.Current.CancellationToken);
        persistedPaid.Status.Should().Be(OrderStatus.Paid);
        (await OrderTicketFixture.ForOrder(verify, persistedPaid).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
        (await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_And_Inactive_Seats_And_Missing_Session_Do_Not_Create_Orders()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        (await database.ReserveAsync(Guid.NewGuid())).Error.Code.Should().Be("Order.SeatsNotFound");
        database.Seat.SetStatus(SeatStatus.Maintenance);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Order.SeatsNotActive");
        database.Seat.SetStatus(SeatStatus.Active);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value, Guid.NewGuid())).Error.Code.Should().Be("Session.NotFound");
        (await database.Context.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Pricing_Failure_Rolls_Back_Reservation_And_Allows_Retry()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        IPriceCalculator failingCalculator = Substitute.For<IPriceCalculator>();
        failingCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>())
            .Returns(_ => throw new InvalidOperationException("pricing unavailable"));

        Func<Task> reserve = async () => await database.ReserveAsync(database.Seat.Id.Value, calculator: failingCalculator);
        await reserve.Should().ThrowAsync<InvalidOperationException>();
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Persistence_Failure_Rolls_Back_Order_And_Tickets_And_Allows_Retry()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        IPriceCalculator overflowingCalculator = Substitute.For<IPriceCalculator>();
        overflowingCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>())
            .Returns(999999999999999999m);

        Func<Task> reserve = async () => await database.ReserveAsync(database.Seat.Id.Value, calculator: overflowingCalculator);
        await reserve.Should().ThrowAsync<DbUpdateException>();
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Independent_Row_Lock_Causes_Nowait_Conflict()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (NpgsqlCommand command = new("SELECT id FROM seats WHERE id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", database.Seat.Id.Value);
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken); // Lock acquired before reservation starts.
        }

        Result<Guid> conflict = await database.ReserveAsync(database.Seat.Id.Value);
        conflict.Error.Code.Should().Be("Order.SeatsUnavailable");
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Checkout_Failure_After_Reservation_Leaves_Committed_Pending_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(database.Session.Id.Value, database.Seat.Id.Value, database.UserId,
            Arg.Any<CancellationToken>()).Returns(true);
        IPriceCalculator calculator = Substitute.For<IPriceCalculator>();
        calculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(50m);
        IOrderCheckoutOrchestrator checkout = Substitute.For<IOrderCheckoutOrchestrator>();
        Guid reservedOrderId = Guid.Empty;
        checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value, Arg.Any<List<Guid>>(),
                Arg.Any<Guid>(), false, false, "token", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                reservedOrderId = call.ArgAt<Guid>(3);
                return Result.Failure<Guid>(new Error("Payment.Failed", "declined"));
            });
        CreateOrderCommandHandler handler = new(new OrderReservationService(database.Context, calculator), currentUser,
            locks, checkout, NullLogger<CreateOrderCommandHandler>.Instance);

        Result<Guid> result = await handler.Handle(new CreateOrderCommand
        {
            SessionId = database.Session.Id.Value,
            SeatIds = [database.Seat.Id.Value],
            PaymentToken = "token"
        }, TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Payment.Failed");
        reservedOrderId.Should().NotBeEmpty();
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reservedOrderId),
            TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Pending);
        (await OrderTicketFixture.ForOrder(verify, persisted).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Reserved);
    }

    [Fact]
    public async Task Saving_Order_Tracks_Tickets_And_Database_Cascades_Their_Deletion()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext verify = database.NewContext();
        Ticket ticket = await verify.Tickets.AsNoTracking()
            .Include(t => t.Session).Include(t => t.Seat)
            .SingleAsync(TestContext.Current.CancellationToken);
        ticket.OrderId.Should().Be(order.Id);
        ticket.Session!.Id.Should().Be(database.Session.Id);
        ticket.Seat!.Id.Should().Be(database.Seat.Id);

        await using (NpgsqlConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand constraint = new(
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fk_tickets_orders_order_id'",
                connection);
            (await constraint.ExecuteScalarAsync(TestContext.Current.CancellationToken))
                .Should().Be("FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE");
        }

        await using (ApplicationDbContext invalid = database.NewContext())
        {
            invalid.Tickets.Add(Ticket.New(EntityId<Ticket>.New(), 50m, TicketStatus.Refunded,
                EntityId<Order>.New(), database.Session.Id, database.Seat.Id));
            Func<Task> save = () => invalid.SaveChangesAsync(TestContext.Current.CancellationToken);
            (await save.Should().ThrowAsync<DbUpdateException>()).Which.InnerException
                .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }

        await verify.Orders.Where(o => o.Id == order.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext afterDelete = database.NewContext();
        (await afterDelete.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Theory]
    [InlineData(TicketStatus.Valid)]
    [InlineData(TicketStatus.Used)]
    public async Task Partial_Index_Rejects_Valid_Or_Used_Duplicate_But_Allows_Refunded_Tickets(TicketStatus existingStatus)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order first = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Ticket original = await OrderTicketFixture.ForOrder(database.Context, first)
            .SingleAsync(TestContext.Current.CancellationToken);
        if (existingStatus == TicketStatus.Used)
        {
            original.MarkAsUsed();
            await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (ApplicationDbContext duplicateContext = database.NewContext())
        {
            Order duplicate = OrderTicketFixture.CreateAndTrack(duplicateContext, database.UserId, database.Session, [database.Seat],
                new() { [database.Seat.Id] = 50m });
            Func<Task> saveDuplicate = () => duplicateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            DbUpdateException failure = (await saveDuplicate.Should().ThrowAsync<DbUpdateException>()).Which;
            failure.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        // Refunded tickets are outside the partial index, including multiple tickets for one seat.
        Order refundedOne = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        OrderTicketFixture.TrackedForOrder(database.Context, refundedOne).Single().MarkAsRefunded();
        Order refundedTwo = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        OrderTicketFixture.TrackedForOrder(database.Context, refundedTwo).Single().MarkAsRefunded();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(3);
        (await verify.Tickets.CountAsync(t => t.TicketStatus == TicketStatus.Refunded,
            TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Reserved_Seat_Blocks_Another_Order_Until_Released_And_Keeps_History()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order first = Order.New(EntityId<Order>.New(), 50m, database.UserId, database.Session.Id);
        Ticket reservation = Ticket.Reserve(EntityId<Ticket>.New(), 50m, first.Id, database.Session.Id, database.Seat.Id);
        database.Context.Orders.Add(first);
        database.Context.Tickets.Add(reservation);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using (ApplicationDbContext duplicateContext = database.NewContext())
        {
            Order duplicate = Order.New(EntityId<Order>.New(), 50m, database.UserId, database.Session.Id);
            duplicateContext.Orders.Add(duplicate);
            duplicateContext.Tickets.Add(Ticket.Reserve(EntityId<Ticket>.New(), 50m,
                duplicate.Id, database.Session.Id, database.Seat.Id));
            Func<Task> save = () => duplicateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            DbUpdateException failure = (await save.Should().ThrowAsync<DbUpdateException>()).Which;
            failure.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        first.MarkAsExpired();
        reservation.ReleaseReservation(first);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext replacementContext = database.NewContext();
        Order replacement = Order.New(EntityId<Order>.New(), 50m, database.UserId, database.Session.Id);
        replacementContext.Orders.Add(replacement);
        replacementContext.Tickets.Add(Ticket.Reserve(EntityId<Ticket>.New(), 50m,
            replacement.Id, database.Session.Id, database.Seat.Id));
        await replacementContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        (await verify.Tickets.IgnoreQueryFilters().CountAsync(t => t.TicketStatus == TicketStatus.Released,
            TestContext.Current.CancellationToken)).Should().Be(1);
        (await verify.Orders.CountAsync(o => o.Status == OrderStatus.Expired,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Ticket_Query_Filter_Hides_Tickets_In_Inactive_Halls()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.Context.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Abandoned_Gold_Journal_Is_Recovered_From_A_Fresh_Context(bool useConfirmed, bool paymentStarted)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext interrupted = database.NewContext())
        {
            Order order = await interrupted.Orders.SingleAsync(TestContext.Current.CancellationToken);
            Ticket ticket = await interrupted.Tickets.SingleAsync(TestContext.Current.CancellationToken);
            order.StartPayment();
            order.BeginGoldCheckout(ticket.Id.Value, 50m);
            await interrupted.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (useConfirmed)
            {
                order.ConfirmGoldUse();
                order.ApplyGoldDiscount(20m);
                ticket.ApplyGoldUpgrade(30m);
                await interrupted.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            if (paymentStarted)
            {
                order.BeginGoldPayment();
                await interrupted.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            // No request cleanup runs: only the committed journal survives.
        }
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        loyalty.RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        await using (ApplicationDbContext recovery = database.NewContext())
        {
            OrderCheckoutOrchestrator worker = new(recovery, loyalty, Substitute.For<IPaymentService>(),
                Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(),
                Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
            await worker.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
            await worker.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        persisted.Status.Should().Be(paymentStarted ? OrderStatus.PaymentOutcomeUnknown : OrderStatus.Failed);
        persisted.GoldPaymentOutcomeUnknown.Should().Be(paymentStarted);
        persisted.TotalAmount.Should().Be(50m);
        persisted.PaidAmount.Should().Be(50m);
        Ticket restored = await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken);
        restored.PriceSnapshot.Should().Be(50m);
        restored.IsGoldUpgraded.Should().BeFalse();
        restored.TicketStatus.Should().Be(paymentStarted ? TicketStatus.Reserved : TicketStatus.Released);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().Be(!paymentStarted);
        await loyalty.Received(useConfirmed ? 0 : 1).UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>());
        await loyalty.Received(1).RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recovery_And_Repeated_Checkout_Cannot_Compensate_An_Active_Checkout()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        TaskCompletionSource enteredUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            enteredUse.SetResult();
            await resumeUse.Task.WaitAsync(TestContext.Current.CancellationToken);
            return (true, "");
        });
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("paid"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, database.Seat.Id.Value, 50m, 30m, null)));
        await using ApplicationDbContext activeContext = database.NewContext();
        OrderCheckoutOrchestrator active = new(activeContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
            gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
        Task<Result<Guid>> checkout = active.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, true, false, "token", TestContext.Current.CancellationToken);
        await enteredUse.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            await using ApplicationDbContext recoveryContext = database.NewContext();
            OrderCheckoutOrchestrator recovery = new(recoveryContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
                gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
            await recovery.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
            Result<Guid> duplicate = await recovery.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
                [database.Seat.Id.Value], reserved.Value, true, false, "token", TestContext.Current.CancellationToken);
            duplicate.Error.Code.Should().Be("Order.CheckoutInProgress");
            await loyalty.DidNotReceiveWithAnyArgs().RollbackGoldUpgradeAsync(default, default, TestContext.Current.CancellationToken);
        }
        finally { resumeUse.TrySetResult(); }
        (await checkout).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Paid);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Completed);
        persisted.TotalAmount.Should().Be(30m);
    }

    [Theory]
    [InlineData("use")]
    [InlineData("discount")]
    public async Task Lost_Postgres_Session_Cannot_Resume_Checkout_After_Recovery_Releases_Gold(string stage)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        TaskCompletionSource enteredUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1 && stage == "use")
            {
                enteredUse.SetResult();
                await resumeUse.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return (true, "");
        });
        loyalty.CalculateDiscountAsync(database.UserId, 30m, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            enteredUse.SetResult();
            await resumeUse.Task.WaitAsync(TestContext.Current.CancellationToken);
            return (false, 0, 30m);
        });
        loyalty.RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("must-not-charge"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, database.Seat.Id.Value, 50m, 30m, null)));
        await using ApplicationDbContext activeContext = database.NewContext(retries: true);
        OrderCheckoutOrchestrator active = new(activeContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
            gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
        Task<Result<Guid>> checkout = active.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, true, stage == "discount", "token", TestContext.Current.CancellationToken);
        await enteredUse.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            int backend = ((NpgsqlConnection)activeContext.Database.GetDbConnection()).ProcessID;
            await using NpgsqlConnection admin = new(database.ConnectionString);
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand terminate = new("SELECT pg_terminate_backend(@backend)", admin);
            terminate.Parameters.AddWithValue("backend", backend);
            await terminate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using ApplicationDbContext recoveryContext = database.NewContext(retries: true);
            OrderCheckoutOrchestrator recovery = new(recoveryContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
                gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
            await recovery.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
        }
        finally { resumeUse.TrySetResult(); }
        try { (await checkout).IsSuccess.Should().BeFalse(); }
        catch (Exception exception) when (exception is OperationCanceledException or NpgsqlException or InvalidOperationException or DbUpdateException { InnerException: NpgsqlException }) { }
        await payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Failed);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        persisted.TotalAmount.Should().Be(50m);
    }

    private sealed class ReservationDatabase : IAsyncDisposable
    {
        public string ConnectionString { get; }
        public ApplicationDbContext Context { get; }
        public Seat Seat { get; private set; } = null!;
        public Session Session { get; private set; } = null!;
        private readonly Guid _userId = Guid.NewGuid();
        public Guid UserId => _userId;
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private ReservationDatabase(string connectionString, string adminConnectionString, string databaseName)
        {
            ConnectionString = connectionString;
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            Context = NewContext();
        }

        public static async Task<ReservationDatabase> CreateAsync(PostgresSchemaTemplate template)
        {
            (string connectionString, string adminConnectionString, string databaseName) =
                await template.CreateDatabaseAsync();
            ReservationDatabase database = new(connectionString, adminConnectionString, databaseName);
            try
            {
                User user = new()
                {
                    Id = database._userId,
                    UserName = $"order-test-{Guid.NewGuid():N}",
                    Email = $"order-{Guid.NewGuid():N}@cinema.test",
                    SecurityStamp = Guid.NewGuid().ToString()
                };
                string suffix = Guid.NewGuid().ToString("N");
                Movie movie = Movie.CreateManual($"Reservation test {suffix}", "test", 90, DateTime.UtcNow.Year, MovieStatus.ComingSoon);
                Hall hall = Hall.Create(EntityId<Hall>.New(), $"Reservation test hall {suffix}");
                SeatType seatType = SeatType.New(EntityId<SeatType>.New(), $"Standard {suffix}", null);
                database.Seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
                hall.ApplyLayout([database.Seat]);
                Pricing pricing = Pricing.New(EntityId<Pricing>.New(), $"Reservation test pricing {suffix}");
                PricingItem item = PricingItem.New(EntityId<PricingItem>.New(), 50m, pricing.Id, seatType.Id, null, null, null);
                database.Session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
                    DateTime.UtcNow.AddDays(2).AddHours(2), movie.Id, hall.Id, pricing.Id);
                database.Context.Users.Add(user);
                database.Context.Movies.Add(movie);
                database.Context.Halls.Add(hall);
                database.Context.SeatTypes.Add(seatType);
                database.Context.Pricings.Add(pricing);
                database.Context.PricingItems.Add(item);
                database.Context.Sessions.Add(database.Session);
                await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public ApplicationDbContext NewContext(IPublisher? publisher = null, DbCommandInterceptor? interceptor = null,
            bool retries = false, DispatchDomainEventsInterceptor? domainInterceptor = null, DbTransactionInterceptor? transactionInterceptor = null)
        {
            DbContextOptionsBuilder<ApplicationDbContext> builder = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(ConnectionString, x => { x.UseVector(); if (retries) x.EnableRetryOnFailure(3); }).UseSnakeCaseNamingConvention();
            if (domainInterceptor is not null)
                builder.AddInterceptors(domainInterceptor);
            else if (publisher is not null)
                builder.AddInterceptors(new DispatchDomainEventsInterceptor(publisher));
            if (interceptor is not null)
                builder.AddInterceptors(interceptor);
            if (transactionInterceptor is not null)
                builder.AddInterceptors(transactionInterceptor);
            DbContextOptions<ApplicationDbContext> options = builder.Options;
            return new ApplicationDbContext(options);
        }

        public async Task<Seat> AddSeatAsync()
        {
            Seat second = Seat.New(EntityId<Seat>.New(), "A", 2, 1, 2, SeatStatus.Active,
                Seat.HallId, Seat.SeatTypeId);
            Context.Seats.Add(second);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return second;
        }

        public async Task<Result<Guid>> ReserveAsync(Guid seatId, Guid? sessionId = null, IPriceCalculator? calculator = null)
            => await ReserveAsync([seatId], sessionId, calculator);

        public async Task<Result<Guid>> ReserveAsync(List<Guid> seatIds, Guid? sessionId = null, IPriceCalculator? calculator = null)
        {
            IPriceCalculator priceCalculator = calculator ?? Substitute.For<IPriceCalculator>();
            if (calculator is null)
                priceCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(50m);
            await using ApplicationDbContext context = NewContext();
            return await new OrderReservationService(context, priceCalculator)
                .ReserveOrderAsync(_userId, sessionId ?? Session.Id.Value, seatIds, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await using NpgsqlConnection admin = new(_adminConnectionString);
            await admin.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE {_databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

public sealed class CapturedPurchaseEventConsumer(ConcurrentQueue<TicketPurchasedEvent> received)
    : IConsumer<TicketPurchasedEvent>
{
    public Task Consume(ConsumeContext<TicketPurchasedEvent> context)
    {
        received.Enqueue(context.Message);
        return Task.CompletedTask;
    }
}

public sealed class CapturedLoyaltyFactConsumer(ConcurrentQueue<string> received)
    : IConsumer<NestJsTicketPurchasedEvent>
{
    public Task Consume(ConsumeContext<NestJsTicketPurchasedEvent> context)
    {
        received.Enqueue(context.ReceiveContext.Body.GetString());
        return Task.CompletedTask;
    }
}
