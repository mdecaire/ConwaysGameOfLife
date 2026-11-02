using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api.Application;
using GameOfLife.Api.Infrastructure;
using Xunit;
using static GameOfLife.IntegrationTests.BoardFixture;

namespace GameOfLife.IntegrationTests;

public sealed class BoardReadTests
{
    [Fact]
    public async Task PreviewZeroReturnsReopenedCurrentSnapshotWithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], 1);
        var before = fixture.StoredValues();

        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");

        await AssertBoard(response, fixture.Id, 1, Phases[1]);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    public async Task PreviewCalculatesOffsetFromCurrentAndDoesNotPersist(int currentGeneration, int steps)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[currentGeneration], currentGeneration);
        var before = fixture.StoredValues();

        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead={steps}");

        await AssertBoard(response, fixture.Id, 2, Phases[2]);
        Assert.Equal(steps, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData("?stepsAhead=-1")]
    [InlineData("?stepsAhead=abc")]
    [InlineData("?stepsAhead=1.5")]
    [InlineData("?stepsAhead=9223372036854775808")]
    [InlineData("?stepsAhead=")]
    [InlineData("")]
    public async Task InvalidPreviewOffsetReturnsProblemWithoutCalculationOrWrites(string query)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], 1);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead{query}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public async Task PreviewRejectsAdditionOverflowBeforeEngineButAllowsZeroAtMaximum()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], long.MaxValue);
        var before = fixture.StoredValues();
        using var zero = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
        await AssertBoard(zero, fixture.Id, long.MaxValue, Phases[1]);
        using var overflow = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=1");
        await AssertProblem(overflow, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public async Task AbsoluteZeroReturnsOriginalAfterProgressAndReopenWithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[2], 2);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/0");
        await AssertBoard(response, fixture.Id, 0, Phases[0]);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task AbsolutePositiveAlwaysReconstructsFromOriginal(int currentGeneration, int target)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[currentGeneration], currentGeneration);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/{target}");
        await AssertBoard(response, fixture.Id, target, Phases[target]);
        Assert.Equal(target, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task InvalidAbsoluteGenerationReturnsProblemWithoutCalculationOrWrites(string target)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], 1);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/{target}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData("states-ahead?stepsAhead=0")]
    [InlineData("states-ahead?stepsAhead=2")]
    [InlineData("generations/0")]
    [InlineData("generations/2")]
    public async Task MissingBoardReturnsNotFoundWithoutEngineWork(string route)
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.GetAsync($"/api/boards/{Guid.NewGuid()}/{route}");
        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Theory]
    [InlineData("states-ahead?stepsAhead=0")]
    [InlineData("generations/0")]
    public async Task InvalidBoardIdUsesStandardBinding(string route)
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.GetAsync($"/api/boards/invalid/{route}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task UploadLocationResolvesToLiveCurrentStateAfterProgressAndReopen()
    {
        using var fixture = new BoardFixture();
        var cells = Enumerable.Range(0, 6).Select(y => Enumerable.Range(0, 7)
            .Select(x => Phases[0].Contains((x, y))).ToArray()).ToArray();
        Uri location;
        Guid id;
        using (var upload = await fixture.Client.PostAsJsonAsync("/api/boards", new { cells }))
        {
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
            var board = (await upload.Content.ReadFromJsonAsync<BoardResponse>())!;
            id = board.BoardId;
            Assert.NotNull(upload.Headers.Location);
            location = upload.Headers.Location;
            Assert.Equal($"/api/boards/{id}/states-ahead?stepsAhead=0", location.OriginalString);
        }
        using (var original = await fixture.Client.GetAsync(location))
            await AssertBoard(original, id, 0, Phases[0]);
        fixture.SetCurrent(Phases[1], 1, id);
        fixture.Reopen();
        using var current = await fixture.Client.GetAsync(location);
        await AssertBoard(current, id, 1, Phases[1]);
        using var initial = await fixture.Client.GetAsync($"/api/boards/{id}/generations/0");
        await AssertBoard(initial, id, 0, Phases[0]);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task PreviewKeepsItsSnapshotWhenStorageChangesDuringCalculation()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        fixture.Engine.AfterCalculation = _ =>
        {
            if (fixture.Engine.Calls == 1) fixture.SetCurrent(Phases[1], 99);
        };
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=2");
        await AssertBoard(response, fixture.Id, 2, Phases[2]);
        Assert.Equal(2, fixture.Engine.Calls);
        var stored = fixture.StoredValues();
        Assert.Equal(99, stored.Generation);
        var codec = new BoardStateCodec();
        Assert.True(codec.Decode(7, 6, stored.Initial).LivingCells.SetEquals(Phases[0]));
        Assert.True(codec.Decode(7, 6, stored.Current).LivingCells.SetEquals(Phases[1]));
    }

    [Theory]
    [InlineData("states-ahead?stepsAhead=10001")]
    [InlineData("generations/10001")]
    public async Task RetrievalDoesNotEnforceTheOldProvisionalTenThousandCap(string route)
    {
        using var fixture = new BoardFixture();
        fixture.Seed([], [], 0);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/{route}");
        await AssertBoard(response, fixture.Id, 10001, []);
        Assert.Equal(10001, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    /// <param name="absolute">True to test absolute reconstruction; false to test relative preview.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStopsRetrievalBeforeAnotherEngineCallAndLeavesStorageUnchanged(bool absolute)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        using var cancellation = new CancellationTokenSource();
        fixture.Engine.AfterCalculation = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
        };
        var service = new BoardService(new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec()), fixture.Engine);
        // The largest representable target is accepted; cancellation stops work after one call.
        Assert.Throws<OperationCanceledException>(() => absolute
            ? service.GetGeneration(fixture.Id, long.MaxValue, cancellation.Token)
            : service.GetNStatesAhead(fixture.Id, long.MaxValue, cancellation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());

        Assert.Throws<OperationCanceledException>(() => absolute
            ? service.GetGeneration(fixture.Id, 0, cancellation.Token)
            : service.GetNStatesAhead(fixture.Id, 0, cancellation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

}
