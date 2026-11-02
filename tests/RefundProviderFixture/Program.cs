using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.RefundProviderFixture;
string connection = Environment.GetEnvironmentVariable("REFUND_FIXTURE_CONNECTION") ?? throw new InvalidOperationException("Fixture connection required");
await DurableRefundProvider.InitializeAsync(connection, CancellationToken.None);
RefundProviderRequest request = JsonSerializer.Deserialize<RefundProviderRequest>(Environment.GetEnvironmentVariable("REFUND_FIXTURE_REQUEST")!)!;
DurableRefundProvider provider = new(connection);
if (args.Single() == "submit-lost")
{
    await provider.SubmitAsync(request, CancellationToken.None);
    Environment.Exit(93); // Process dies after durable provider commit, before response.
}
Console.WriteLine(JsonSerializer.Serialize(await provider.LookupAsync(request, CancellationToken.None)));
