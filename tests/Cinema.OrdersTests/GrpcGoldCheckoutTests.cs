using Cinema.Infrastructure.Grpc.Loyalty;
using Cinema.Infrastructure.Options;
using Cinema.Infrastructure.Services;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class GrpcGoldCheckoutTests
{
    [Theory]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Lost_Use_Response_Is_Reconciled_With_The_Same_Identity(StatusCode status)
    {
        Guid userId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        LoyaltyService.LoyaltyServiceClient client = Substitute.For<LoyaltyService.LoyaltyServiceClient>();
        List<UseGoldUpgradeRequest> requests = [];
        client.UseGoldUpgradeAsync(Arg.Any<UseGoldUpgradeRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.Arg<UseGoldUpgradeRequest>());
                Task<UseGoldUpgradeResponse> response = requests.Count == 1
                    ? Task.FromException<UseGoldUpgradeResponse>(new RpcException(new Status(status, "response lost")))
                    : Task.FromResult(new UseGoldUpgradeResponse { Success = true });
                return new AsyncUnaryCall<UseGoldUpgradeResponse>(response, Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });
        GrpcLoyaltyService service = new(client, NullLogger<GrpcLoyaltyService>.Instance,
            Options.Create(new LoyaltySettings { ApiKey = "test-key" }));

        var result = await service.UseGoldUpgradeAsync(userId, orderId, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(request => request.UserId == userId.ToString() && request.OrderId == orderId.ToString());
    }
}
