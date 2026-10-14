using System.Net;
using RSM.Safety.Http;

namespace RSM.Safety.Tests;

public class RabbitGetClientTests
{
    [Theory]
    [InlineData("api/overview")]
    [InlineData("/api/queues")]
    [InlineData("api/nodes")]
    public async Task Gets_only_whitelisted_paths(string path)
    {
        var handler = new RecordingHandler();
        using var client = new RabbitGetClient(new Uri("http://mq:15672"), "monitor", "pw", TimeSpan.FromSeconds(5), handler);

        await client.GetAsync(path, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/" + path.TrimStart('/'));
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Basic");
    }

    [Theory]
    [InlineData("api/queues/%2F/q1/contents")]
    [InlineData("api/definitions")]
    [InlineData("api/overview/../definitions")]
    [InlineData("http://evil/api/overview")]
    [InlineData("api/nodes?x=1")]
    public async Task Refuses_any_other_path(string path)
    {
        var handler = new RecordingHandler();
        using var client = new RabbitGetClient(new Uri("http://mq:15672/"), null, null, TimeSpan.FromSeconds(5), handler);

        var act = () => client.GetAsync(path, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestRejectedException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Refuses_non_http_schemes() =>
        FluentActions.Invoking(() => new RabbitGetClient(new Uri("file:///etc/passwd"), null, null, TimeSpan.FromSeconds(1), new RecordingHandler()))
            .Should().Throw<HttpRequestRejectedException>();

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
