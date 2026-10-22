using System.Reflection;
using Cinema.Api.Controllers;
using Cinema.Application.Halls.Commands.ReplaceHallLayout;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinema.ArchitectureTests;

public class HallLayoutApiTests
{
    [Theory]
    [InlineData("Hall.LayoutLocked", 409)]
    [InlineData("Hall.Inactive", 409)]
    [InlineData("Hall.NotFound", 404)]
    [InlineData("Validation.Seats", 400)]
    public async Task Layout_endpoint_maps_results_and_route_id(string code, int expectedStatus)
    {
        RecordingSender sender = new(Result.Failure(new Error(code, "Layout error")));
        using ServiceProvider services = new ServiceCollection().AddSingleton<ISender>(sender).BuildServiceProvider();
        HallsController controller = new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
        Guid hallId = Guid.NewGuid();
        var request = new ReplaceHallLayoutRequest(5, 8, [new(2, 1, Guid.NewGuid())]);

        ObjectResult response = Assert.IsType<ObjectResult>(
            await controller.ReplaceLayout(hallId, request, TestContext.Current.CancellationToken));

        Assert.Equal(expectedStatus, response.StatusCode);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal(code, problem.Extensions["code"]);
        if (expectedStatus == 409) Assert.Equal("Conflict", problem.Title);
        var command = Assert.IsType<ReplaceHallLayoutCommand>(sender.Request);
        Assert.Equal(hallId, command.HallId);
        Assert.Equal(request.Seats, command.Seats);
    }

    [Fact]
    public async Task Layout_endpoint_requires_admin_and_returns_no_content_on_success()
    {
        MethodInfo endpoint = typeof(HallsController).GetMethod(nameof(HallsController.ReplaceLayout))!;
        Assert.Equal("Admin", endpoint.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.Equal("{id:guid}/layout", endpoint.GetCustomAttribute<HttpPutAttribute>()!.Template);
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ISender>(new RecordingSender(Result.Success())).BuildServiceProvider();
        HallsController controller = new()
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
        Assert.IsType<NoContentResult>(await controller.ReplaceLayout(Guid.NewGuid(),
            new(1, 1, [new(1, 1, Guid.NewGuid())]), TestContext.Current.CancellationToken));
    }

    private sealed class RecordingSender(Result result) : ISender
    {
        public object? Request { get; private set; }
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult((TResponse)(object)result);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
