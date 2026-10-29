using System.Net;
using Cinema.Api.Controllers;
using Cinema.Domain.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Cinema.BookingTests;
public class SessionBusyHttpTests
{
    [Fact]
    public async Task Session_busy_result_is_a_retryable_HTTP_conflict()
    {
        using TestServer server=new(new WebHostBuilder()
            .ConfigureServices(services=>services.AddControllers().AddApplicationPart(typeof(LifecycleBusyProofController).Assembly))
            .Configure(app=>{app.UseRouting();app.UseEndpoints(endpoints=>endpoints.MapControllers());}));
        using HttpClient client=server.CreateClient();
        using HttpResponseMessage response=await client.GetAsync("/proof/lifecycle-busy",TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("Session.Busy");
    }
}
[Route("proof/lifecycle-busy")]
public class LifecycleBusyProofController : ApiController
{
    [HttpGet]
    public ActionResult Get() => HandleResult(Result.Failure(new Error("Session.Busy","Session is being modified. Retry later.")));
}
