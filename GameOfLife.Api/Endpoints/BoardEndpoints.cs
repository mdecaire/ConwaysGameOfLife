using GameOfLife.Api.Application;

namespace GameOfLife.Api.Endpoints;

public static class BoardEndpoints
{
    /// <summary>Maps the board upload endpoint, returning a created response after persistence.</summary>
    /// <param name="app">The application on which to register the endpoint.</param>
    public static void MapBoardEndpoints(this WebApplication app)
    {
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
            return Results.Json(new BoardResponse(
                boardId, 0, state.Width, state.Height, cells),
                statusCode: StatusCodes.Status201Created);
        });
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

