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
            var (boardId, state) = service.Upload(request.Cells, cancellationToken);
            return Results.Json(new BoardResponse(
                boardId, 0, state.Width, state.Height, request.Cells),
                statusCode: StatusCodes.Status201Created);
        });
    }
}

