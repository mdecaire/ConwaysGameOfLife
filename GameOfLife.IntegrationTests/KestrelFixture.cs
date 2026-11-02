using System.Net;
using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameOfLife.IntegrationTests;

// Replays the production entry point on Kestrel; the factory's ordinary TestServer
// host only bootstraps WebApplicationFactory and receives no test requests.
internal sealed class KestrelFixture(long limit = 256, IGenerationEngine? engine = null)
    : WebApplicationFactory<Program>
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"life-kestrel-{Guid.NewGuid():N}.db");
    private IHost? kestrel;
    private IHost? bootstrap;
    public long BytesRead;
    public TaskCompletionSource RequestCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string ConnectionString => new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString();
    public SqliteBoardStore Store => new(ConnectionString, new BoardStateCodec());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Boards"] = ConnectionString }));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new ObserveReads(this));
            if (engine is not null) services.AddSingleton(engine);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var testHost = builder.Build();
        bootstrap = testHost;
        builder.ConfigureWebHost(web => web.UseSockets().UseKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = limit;
            options.Listen(IPAddress.Loopback, 0);
        }));
        kestrel = builder.Build();
        kestrel.Start();
        testHost.Start();
        return testHost;
    }

    public HttpClient OpenClient()
    {
        _ = Services;
        var address = kestrel!.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
    }

    public long Count(string table)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    public override async ValueTask DisposeAsync()
    {
        if (kestrel is not null)
        {
            await kestrel.StopAsync();
            kestrel.Dispose();
            kestrel = null;
        }
        if (bootstrap is not null) await bootstrap.StopAsync();
        await base.DisposeAsync();
        // Windows can briefly retain a file handle after both hosts have stopped.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                File.Delete(path + "-wal");
                File.Delete(path + "-shm");
                break;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(50);
            }
        }
    }

    private sealed class ObserveReads(KestrelFixture fixture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Request.Body = new ObservedStream(context.Request.Body, fixture);
                try { await nextMiddleware(context); }
                finally { fixture.RequestCompleted.TrySetResult(); }
            });
            next(app);
        };
    }

    private sealed class ObservedStream(Stream inner, KestrelFixture fixture) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer, token);
            Interlocked.Add(ref fixture.BytesRead, count);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
