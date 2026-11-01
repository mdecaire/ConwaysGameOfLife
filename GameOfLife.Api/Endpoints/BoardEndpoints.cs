using GameOfLife.Api.Application;

namespace GameOfLife.Api.Endpoints;

public static class BoardEndpoints
{
    /// <summary>Maps durable upload, stateful advancement, and read-only generation endpoints.</summary>
    /// <param name="app">The application on which to register the endpoint.</param>
    public static void MapBoardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/boards/{id}/final-state", (
            Guid id, HttpRequest request, BoardService service, CancellationToken cancellationToken) =>
        {
            if (request.Query.ContainsKey("maxIterations"))
                return Results.Problem(statusCode: 400, title: "Unsupported iteration budget");
            try
            {
                var result = service.FinalState(id, cancellationToken);
                return result is null
                    ? Results.Problem(statusCode: 404, title: "Board not found")
                    : Results.Ok(result);
            }
            catch (FinalStateInconclusiveException)
            {
                return Results.Problem(statusCode: 422, title: "Final state not proven",
                    detail: "The internal generation calculation limit was reached.");
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.Problem(statusCode: 400, title: "Invalid generation",
                    detail: "The generation exceeds the supported integer range.");
            }
        });

        app.MapGet("/api/boards/{id}/preview", (
            Guid id, long stepsAhead, BoardService service, CancellationToken cancellationToken) =>
            ExecuteBoard(() => service.Preview(id, stepsAhead, cancellationToken)));

        app.MapGet("/api/boards/{id}/generations/{generationNumber}", (
            Guid id, long generationNumber, BoardService service, CancellationToken cancellationToken) =>
            ExecuteBoard(() => service.GetGeneration(id, generationNumber, cancellationToken)));

        app.MapPost("/api/boards/{id}/next", (
            Guid id, BoardService service, CancellationToken cancellationToken) =>
            ExecuteBoard(() => service.Next(id, cancellationToken)));

        app.MapPost("/api/boards", (
            UploadBoardRequest request, BoardService service, CancellationToken cancellationToken) =>
        {
            var validationError = ValidateCells(request.Cells);
            if (validationError is not null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid board",
                    detail: validationError);
            }

            var cells = request.Cells!;
            var (boardId, state) = service.Upload(cells, cancellationToken);
            return Results.Created($"/api/boards/{boardId}/preview?stepsAhead=0",
                new BoardResponse(boardId, 0, state.Width, state.Height, cells));
        });
    }

    /// <summary>Maps a board operation's result, validation failure, or optimistic conflict to an HTTP response.</summary>
    /// <param name="execute">The operation returning a board response, or null when the board is missing.</param>
    /// <returns>A successful board response or a 400, 404, or 409 ProblemDetails response.</returns>
    /// <remarks>Unexpected exceptions and cancellation propagate to the existing middleware.</remarks>
    private static IResult ExecuteBoard(Func<BoardResponse?> execute)
    {
        try
        {
            var board = execute();
            return board is null
                ? Results.Problem(statusCode: 404, title: "Board not found")
                : Results.Ok(board);
        }
        catch (BoardConflictException)
        {
            return Results.Problem(statusCode: 409, title: "Board changed",
                detail: "The board changed while the next generation was being calculated.");
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid generation",
                detail: "The generation must be nonnegative and within the supported integer range.");
        }
    }

    /// <summary>Validates that the grid has nonnull rows and nonzero, rectangular dimensions.</summary>
    /// <param name="cells">The grid to validate, indexed by row, then column.</param>
    /// <returns>The first validation error, or null when the grid is valid.</returns>
    private static string? ValidateCells(bool[][]? cells)
    {
        if (cells is null)
        {
            return "The cells grid must not be null.";
        }

        if (cells.Length == 0)
        {
            return "The cells grid must contain at least one row.";
        }

        if (cells.Any(row => row is null))
        {
            return "The cells grid must not contain null rows.";
        }

        var width = cells[0].Length;
        if (width == 0)
        {
            return "The cells grid must contain at least one column.";
        }

        if (cells.Any(row => row.Length != width))
        {
            return "All rows must have the same length.";
        }

        return null;
    }
}

