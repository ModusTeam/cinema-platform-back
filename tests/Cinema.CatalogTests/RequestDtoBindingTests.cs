using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cinema.Api.Controllers;
using Cinema.Api.Controllers.Admin;
using Cinema.Api.Infrastructure.ExceptionHandling;
using Cinema.Application.AdminLoyalty.Commands;
using Cinema.Application.AdminLoyalty.Validators;
using Cinema.Application.Common.Behaviours;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Seats.Commands.UpdateSeatStatus;
using Cinema.Domain.Shared;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class RequestDtoBindingTests
{
    private const string SessionId = "63f4b838-c734-4486-9794-7735d040e32b";
    private const string SeatId = "6af05b6e-31c4-4c3f-8356-057274eea746";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(typeof(CreateOrderRequest), "{\"seatIds\":[],\"paymentToken\":\"test\"}")]
    [InlineData(typeof(ReserveOrderRequest), "{\"seatIds\":[]}")]
    [InlineData(typeof(LoyaltyDiscountPreviewRequest), "{\"seatIds\":[]}")]
    [InlineData(typeof(CheckoutPreviewRequest), "{\"seatIds\":[]}")]
    [InlineData(typeof(UpdateSeatStatusRequest), "{}")]
    [InlineData(typeof(ModifyPointsRequestDto), "{\"points\":25,\"reason\":\"test reason\"}")]
    [InlineData(typeof(ModifyPointsRequestDto), "{\"userId\":\"63f4b838-c734-4486-9794-7735d040e32b\",\"reason\":\"test reason\"}")]
    public void Required_value_fields_cannot_be_silently_defaulted(Type dto, string json)
    {
        Action deserialize = () => JsonSerializer.Deserialize(json, dto, JsonOptions);
        deserialize.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(typeof(CreateOrderRequest))]
    [InlineData(typeof(StartCheckoutRequest))]
    [InlineData(typeof(CheckoutPreviewRequest))]
    public void Checkout_flags_remain_optional_and_preserve_explicit_values(Type dto)
    {
        string json = $$"""{"sessionId":"{{SessionId}}","seatIds":["{{SeatId}}"],"paymentToken":"test"} """;
        object? request = JsonSerializer.Deserialize(json, dto, JsonOptions);
        JsonElement omitted = JsonSerializer.SerializeToElement(request, dto, JsonOptions);
        omitted.GetProperty("useLoyaltyPoints").GetBoolean().Should().BeFalse();
        omitted.GetProperty("applyGoldUpgrade").GetBoolean().Should().BeFalse();

        string explicitJson = $$"""{"sessionId":"{{SessionId}}","seatIds":["{{SeatId}}"],"paymentToken":"test","useLoyaltyPoints":true,"applyGoldUpgrade":true} """;
        JsonElement supplied = JsonSerializer.SerializeToElement(JsonSerializer.Deserialize(explicitJson, dto, JsonOptions), dto, JsonOptions);
        supplied.GetProperty("useLoyaltyPoints").GetBoolean().Should().BeTrue();
        supplied.GetProperty("applyGoldUpgrade").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"seatIds\":[]}")]
    [InlineData("{\"sessionId\":\"invalid\",\"seatIds\":[]}")]
    [InlineData("{\"sessionId\":\"00000000-0000-0000-0000-000000000000\",\"seatIds\":[]}")]
    public async Task Reservation_invalid_input_returns_HTTP_400(string json)
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client, "/api/orders/reservations", json);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    [InlineData("{\"status\":999}", HttpStatusCode.BadRequest)]
    [InlineData("{\"status\":0}", HttpStatusCode.BadRequest)]
    [InlineData("{\"status\":1}", HttpStatusCode.NoContent)]
    public async Task Seat_status_must_be_explicit_and_defined(string json, HttpStatusCode expected)
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.PatchAsync($"/api/seats/{SeatId}/status",
            new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"useLoyaltyPoints\":false,\"applyGoldUpgrade\":false")]
    public async Task Optional_flags_preserve_false_default_through_HTTP(string flags)
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client, "/api/orders",
            $$"""{"sessionId":"{{SessionId}}","seatIds":["{{SeatId}}"],"paymentToken":"test"{{flags}}} """);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CreateOrderCommand command = app.Services.GetRequiredService<AcceptedCommands>().Last!;
        command.SessionId.Should().Be(Guid.Parse(SessionId));
        command.UseLoyaltyPoints.Should().BeFalse();
        command.ApplyGoldUpgrade.Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"reason\":\"test reason\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"userId\":\"invalid\",\"points\":25,\"reason\":\"test reason\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"userId\":\"00000000-0000-0000-0000-000000000000\",\"points\":25,\"reason\":\"test reason\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"userId\":\"63f4b838-c734-4486-9794-7735d040e32b\",\"points\":0,\"reason\":\"test reason\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"userId\":\"63f4b838-c734-4486-9794-7735d040e32b\",\"points\":25,\"reason\":\"test reason\"}", HttpStatusCode.OK)]
    [InlineData("{\"userId\":\"63f4b838-c734-4486-9794-7735d040e32b\",\"points\":-25,\"reason\":\"test reason\"}", HttpStatusCode.OK)]
    public async Task Admin_points_uses_existing_validation_and_preserves_signed_adjustments(string json, HttpStatusCode expected)
    {
        await using WebApplication app = await StartAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await PostAsync(client, "/api/admin/loyalty/modify-points", json);
        response.StatusCode.Should().Be(expected);
        IAdminLoyaltyService service = app.Services.GetRequiredService<IAdminLoyaltyService>();
        if (expected == HttpStatusCode.BadRequest)
            await service.DidNotReceive().ModifyPointsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        else
            await service.Received(1).ModifyPointsAsync(Guid.Parse(SessionId), "test-admin", Arg.Any<int>(), "test reason", Arg.Any<CancellationToken>());
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static async Task<WebApplication> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
        builder.Services.AddMediatR(configuration => configuration.RegisterServicesFromAssemblyContaining<ModifyUserPointsCommand>());
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        builder.Services.AddSingleton<IValidator<CreateOrderCommand>, CreateOrderValidator>();
        builder.Services.AddSingleton<IValidator<ReserveOrderCommand>, ReserveOrderCommandValidator>();
        builder.Services.AddSingleton<IValidator<ModifyUserPointsCommand>, ModifyUserPointsCommandValidator>();
        builder.Services.AddSingleton<IValidator<UpdateSeatStatusCommand>, UpdateSeatStatusValidator>();
        builder.Services.AddSingleton<AcceptedCommands>();
        builder.Services.AddTransient<IRequestHandler<CreateOrderCommand, Result<Guid>>, AcceptOrder>();
        builder.Services.AddTransient<IRequestHandler<ReserveOrderCommand, Result<ReservedOrderDto>>, AcceptReservation>();
        builder.Services.AddTransient<IRequestHandler<UpdateSeatStatusCommand, Result>, AcceptSeatStatus>();
        IAdminLoyaltyService loyalty = Substitute.For<IAdminLoyaltyService>();
        loyalty.ModifyPointsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new AdminModifyPointsDto(call.ArgAt<Guid>(0), "BRONZE", 25, true));
        builder.Services.AddSingleton(loyalty);
        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-admin"), new Claim(ClaimTypes.Role, "Admin")], "test"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapControllers();
        try { await app.StartAsync(TestContext.Current.CancellationToken); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    public sealed class AcceptedCommands { public CreateOrderCommand? Last { get; set; } }
    public sealed class AcceptOrder(AcceptedCommands accepted) : IRequestHandler<CreateOrderCommand, Result<Guid>>
    {
        public Task<Result<Guid>> Handle(CreateOrderCommand command, CancellationToken cancellationToken)
        {
            accepted.Last = command;
            return Task.FromResult(Result.Success(Guid.NewGuid()));
        }
    }
    public sealed class AcceptReservation : IRequestHandler<ReserveOrderCommand, Result<ReservedOrderDto>>
    {
        public Task<Result<ReservedOrderDto>> Handle(ReserveOrderCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new ReservedOrderDto(Guid.NewGuid(), 100, "Pending")));
    }
    public sealed class AcceptSeatStatus : IRequestHandler<UpdateSeatStatusCommand, Result>
    {
        public Task<Result> Handle(UpdateSeatStatusCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
