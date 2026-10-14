using System.Text;
using RSM.Safety.Redis;

namespace RSM.Safety.Tests;

public class RedisWhitelistTests
{
    [Theory]
    [InlineData("PING")]
    [InlineData("ping")]
    [InlineData("AUTH", "secret")]
    [InlineData("AUTH", "user", "secret")]
    [InlineData("INFO")]
    [InlineData("INFO", "memory")]
    [InlineData("DBSIZE")]
    [InlineData("SLOWLOG", "GET")]
    [InlineData("SLOWLOG", "GET", "128")]
    [InlineData("slowlog", "len")]
    [InlineData("LATENCY", "LATEST")]
    [InlineData("CLIENT", "LIST")]
    [InlineData("CONFIG", "GET", "maxmemory")]
    [InlineData("CONFIG", "GET", "maxmemory*")]
    [InlineData("CONFIG", "GET", "maxmemory-policy")]
    public void Allows_read_only_commands(params string[] args) =>
        FluentActions.Invoking(() => RedisCommandWhitelist.Validate(args)).Should().NotThrow();

    [Theory]
    [InlineData("SET", "k", "v")]
    [InlineData("GET", "k")]
    [InlineData("DEL", "k")]
    [InlineData("FLUSHDB")]
    [InlineData("FLUSHALL")]
    [InlineData("CONFIG", "SET", "maxmemory", "1")]
    [InlineData("CONFIG", "GET", "requirepass")]
    [InlineData("CONFIG", "GET", "*")]
    [InlineData("CONFIG", "REWRITE")]
    [InlineData("SHUTDOWN")]
    [InlineData("DEBUG", "SEGFAULT")]
    [InlineData("CLIENT", "KILL", "ID", "1")]
    [InlineData("CLIENT", "PAUSE", "1000")]
    [InlineData("SAVE")]
    [InlineData("BGSAVE")]
    [InlineData("BGREWRITEAOF")]
    [InlineData("EVAL", "return 1", "0")]
    [InlineData("SCRIPT", "FLUSH")]
    [InlineData("SLAVEOF", "host", "1")]
    [InlineData("REPLICAOF", "host", "1")]
    [InlineData("MODULE", "LOAD", "x.so")]
    [InlineData("KEYS", "*")]
    [InlineData("SLOWLOG", "RESET")]
    [InlineData("SLOWLOG", "GET", "100000")]
    [InlineData("LATENCY", "RESET")]
    [InlineData("INFO", "all\r\nFLUSHALL")]
    [InlineData("PING", "extra")]
    [InlineData("MULTI")]
    public void Rejects_everything_else(params string[] args) =>
        FluentActions.Invoking(() => RedisCommandWhitelist.Validate(args)).Should().Throw<RedisCommandRejectedException>();

    [Fact]
    public void Rejects_crlf_injection_in_any_argument() =>
        FluentActions.Invoking(() => RedisCommandWhitelist.Validate(["AUTH", "pw\r\nFLUSHALL"])).Should().Throw<RedisCommandRejectedException>();

    [Fact]
    public void Rejection_message_never_contains_auth_secret()
    {
        var act = () => RedisCommandWhitelist.Validate(["AUTH", "a", "b", "topsecret"]);

        act.Should().Throw<RedisCommandRejectedException>().Which.Message.Should().NotContain("topsecret");
    }
}

public class RespTests
{
    [Fact]
    public void Encodes_commands_as_bulk_string_arrays() =>
        Encoding.UTF8.GetString(Resp.Encode(["INFO", "server"])).Should().Be("*2\r\n$4\r\nINFO\r\n$6\r\nserver\r\n");

    [Fact]
    public void Encodes_utf8_lengths_in_bytes() =>
        Encoding.UTF8.GetString(Resp.Encode(["AUTH", "رمز"])).Should().Contain("$6\r\n");

    [Theory]
    [InlineData("+PONG\r\n", RespKind.SimpleString, "PONG")]
    [InlineData("-ERR unknown command 'INFO'\r\n", RespKind.Error, "ERR unknown command 'INFO'")]
    [InlineData("$5\r\nhello\r\n", RespKind.BulkString, "hello")]
    [InlineData("$-1\r\n", RespKind.Null, null)]
    public async Task Decodes_replies(string wire, RespKind kind, string? text)
    {
        var value = await Resp.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(wire)), CancellationToken.None);

        value.Kind.Should().Be(kind);
        value.Text.Should().Be(text);
    }

    [Fact]
    public async Task Decodes_nested_arrays_and_integers()
    {
        var value = await Resp.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes("*2\r\n:42\r\n*1\r\n+x\r\n")), CancellationToken.None);

        value.Items!.Should().HaveCount(2);
        value.Items![0].Integer.Should().Be(42);
        value.Items![1].Items![0].Text.Should().Be("x");
    }

    [Fact]
    public async Task Rejects_oversized_bulk_strings()
    {
        var act = () => Resp.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes("$999999999999\r\n")), CancellationToken.None);

        await act.Should().ThrowAsync<RespProtocolException>();
    }

    [Fact]
    public void Parses_info_sections()
    {
        var info = RedisInfo.Parse("# Server\r\nredis_version:5.0.14\r\nuptime_in_seconds:10\r\n\r\n# Memory\r\nused_memory:1024\r\n");

        info["Server"]["redis_version"].Should().Be("5.0.14");
        info["Memory"]["used_memory"].Should().Be("1024");
    }
}

public class RespWhitelistClientTests
{
    [Fact]
    public async Task Sends_whitelisted_command_and_reads_reply()
    {
        var stream = new FakeDuplexStream("+PONG\r\n");
        await using var client = new RespWhitelistClient(stream);

        var reply = await client.ExecuteAsync(CancellationToken.None, "ping");

        reply.Text.Should().Be("PONG");
        stream.Written.Should().Be("*1\r\n$4\r\nPING\r\n");
    }

    [Fact]
    public async Task Rejected_commands_never_reach_the_socket()
    {
        var stream = new FakeDuplexStream("+OK\r\n");
        await using var client = new RespWhitelistClient(stream);

        var act = () => client.ExecuteAsync(CancellationToken.None, "FLUSHALL");

        await act.Should().ThrowAsync<RedisCommandRejectedException>();
        stream.Written.Should().BeEmpty();
    }

    private sealed class FakeDuplexStream(string response) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.UTF8.GetBytes(response));
        private readonly MemoryStream _output = new();

        public string Written => Encoding.UTF8.GetString(_output.ToArray());

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    }
}
