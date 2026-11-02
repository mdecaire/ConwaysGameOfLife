using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameOfLife.Api.Application;
using Xunit;

namespace GameOfLife.IntegrationTests;

public sealed class KestrelResourceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("*/*")]
    [InlineData("text/html")]
    public async Task ErrorNegotiationPreservesStatusesAndSupportedJsonProblems(string? accept)
    {
        using var fixture = new KestrelFixture();
        using var client = fixture.OpenClient();
        if (accept is not null) client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        // Binding and body-limit errors use status-code middleware; missing boards
        // use the endpoint's explicit Results.Problem response.
        using var invalid = await client.PostAsync("/api/boards",
            new StringContent("{", Encoding.UTF8, "application/json"));
        using var missing = await client.GetAsync($"/api/boards/{Guid.NewGuid()}/states-ahead?stepsAhead=0");
        using var oversized = await client.PostAsync("/api/boards",
            new StringContent("""{"cells":[[true]]}""".PadRight(257), Encoding.UTF8, "application/json"));
        await BoardFixture.AssertProblem(missing, HttpStatusCode.NotFound);
        if (accept != "text/html")
        {
            await BoardFixture.AssertProblem(invalid, HttpStatusCode.BadRequest);
            await BoardFixture.AssertProblem(oversized, HttpStatusCode.RequestEntityTooLarge);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            Assert.NotEqual("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
            Assert.NotEqual("application/problem+json", oversized.Content.Headers.ContentType?.MediaType);
        }
        // Minimal APIs return JSON without imposing a new 406 contract.
        using var valid = await client.PostAsJsonAsync("/api/boards", new { cells = new[] { new[] { true } } });
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        Assert.Equal("application/json", valid.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, fixture.Count("Boards"));
    }

    [Theory]
    [InlineData(65537, 1, false)]
    [InlineData(1024, 1024, false)]
    [InlineData(1024, 1024, true)]
    public async Task LargeBoardsRoundTripEveryCellThroughHttpAndSqlite(int width, int height, bool dense)
    {
        var cells = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat(dense, width).ToArray()).ToArray();
        // Asymmetric markers expose row/column transposition, even on square boards.
        cells[0][width - 2] = !dense;
        cells[height - 1][3] = !dense;
        if (height > 1) cells[17][31] = !dense;
        using var fixture = new KestrelFixture(16 * 1024 * 1024);
        using var client = fixture.OpenClient();
        using var upload = await client.PostAsJsonAsync("/api/boards", new { cells });
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var created = (await upload.Content.ReadFromJsonAsync<BoardResponse>())!;
        AssertCells(created);
        using var current = await client.GetAsync($"/api/boards/{created.BoardId}/states-ahead?stepsAhead=0");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        AssertCells((await current.Content.ReadFromJsonAsync<BoardResponse>())!);
        using var initial = await client.GetAsync($"/api/boards/{created.BoardId}/generations/0");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        AssertCells((await initial.Content.ReadFromJsonAsync<BoardResponse>())!);
        // A new store/connection independently reads persisted coordinates.
        var saved = fixture.Store.Find(created.BoardId)!.Value;
        Assert.Equal(0, saved.Generation);
        Assert.Equal(width, saved.State.Width);
        Assert.Equal(height, saved.State.Height);
        Assert.Equal(cells.Sum(row => row.Count(alive => alive)), saved.State.LivingCells.Count);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Assert.Equal(cells[y][x], saved.State.LivingCells.Contains((x, y)));

        void AssertCells(BoardResponse board)
        {
            Assert.Equal(width, board.Width);
            Assert.Equal(height, board.Height);
            Assert.Equal(0, board.Generation);
            Assert.Equal(height, board.Cells.Length);
            for (var y = 0; y < height; y++) Assert.Equal(cells[y], board.Cells[y]);
        }
    }

    [Theory]
    [InlineData(255, 201)]
    [InlineData(256, 201)]
    [InlineData(257, 413)]
    public async Task KnownLengthLimitUsesBytesBeforeBindingOrPersistence(int size, int expectedStatus)
    {
        using var fixture = new KestrelFixture();
        using var client = fixture.OpenClient();
        // A multibyte JSON property value makes character-count limits incorrect.
        const string json = """{"cells":[[false,true,false],[true,false,false]],"note":"é"}""";
        var bytes = Encoding.UTF8.GetBytes(json + new string(' ', size - Encoding.UTF8.GetByteCount(json)));
        Assert.Equal(size, bytes.Length);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/boards")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var board = (await response.Content.ReadFromJsonAsync<BoardResponse>())!;
            Assert.Equal(3, board.Width);
            Assert.Equal(2, board.Height);
            Assert.Equal(new[] { false, true, false }, board.Cells[0]);
            Assert.Equal(new[] { true, false, false }, board.Cells[1]);
            Assert.NotNull(fixture.Store.Find(board.BoardId));
            Assert.Equal(size, fixture.BytesRead);
            Assert.Equal(1, fixture.Count("Boards"));
        }
        else
        {
            await BoardFixture.AssertProblem(response, HttpStatusCode.RequestEntityTooLarge);
            Assert.Equal(0, fixture.Count("Boards"));
            Assert.Equal(0, fixture.BytesRead);
        }
        Assert.Equal(expectedStatus, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(64, -1)]
    [InlineData(64, 0)]
    [InlineData(64, 1)]
    [InlineData(256, -1)]
    [InlineData(256, 0)]
    [InlineData(256, 1)]
    public async Task ChunkedLimitIncludesTheFramesActuallyWritten(int chunkSize, int allowance)
    {
        var bytes = Encoding.UTF8.GetBytes("""{"cells":[[true,false]]}""".PadRight(256));
        // Each WriteAsync below produces one HTTP/1.1 chunk: hex length, CRLF,
        // data, CRLF; the terminating zero chunk is "0\r\n\r\n".
        var wireSize = Encoding.ASCII.GetByteCount("0\r\n\r\n");
        foreach (var chunk in bytes.Chunk(chunkSize))
            wireSize += chunk.Length + chunk.Length.ToString("X").Length + 4;
        using var fixture = new KestrelFixture(wireSize + allowance);
        using var client = fixture.OpenClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/boards")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ChunkedContent(bytes, chunkSize)
        };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        if (allowance >= 0)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var board = (await response.Content.ReadFromJsonAsync<BoardResponse>())!;
            Assert.Equal(2, board.Width);
            Assert.Equal(1, board.Height);
            Assert.Equal(new[] { true, false }, board.Cells.Single());
            Assert.True(fixture.Store.Find(board.BoardId)!.Value.State.LivingCells.SetEquals(new[] { (0, 0) }));
            Assert.Equal(1, fixture.Count("Boards"));
            Assert.Equal(bytes.Length, fixture.BytesRead);
        }
        else
        {
            await BoardFixture.AssertProblem(response, HttpStatusCode.RequestEntityTooLarge);
            Assert.Equal(0, fixture.Count("Boards"));
        }
    }

    [Fact]
    public async Task StreamedOversizedGridStopsBeforeCompleteBodyReachesBinding()
    {
        var json = JsonSerializer.Serialize(new { cells = new[] { new bool[100] } });
        var bytes = Encoding.UTF8.GetBytes(json.PadRight(1024));
        using var fixture = new KestrelFixture();
        using var client = fixture.OpenClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/boards")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ChunkedContent(bytes, 64)
        };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        await BoardFixture.AssertProblem(response, HttpStatusCode.RequestEntityTooLarge);
        // Even the grid (before trailing padding) is longer than the bounded prefix.
        Assert.True(Encoding.UTF8.GetByteCount(json) > 256);
        Assert.InRange(fixture.BytesRead, 0, 256);
        Assert.Equal(0, fixture.Count("Boards"));
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] bytes;
        private readonly int chunkSize;
        public ChunkedContent(byte[] bytes, int chunkSize)
        {
            this.bytes = bytes;
            this.chunkSize = chunkSize;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (var chunk in bytes.Chunk(chunkSize)) await stream.WriteAsync(chunk);
        }
    }
}
