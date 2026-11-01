namespace GameOfLife.Api.Application;

/// <summary>The boolean grid supplied for a new board.</summary>
/// <param name="Cells">The cells indexed by row, then column; true indicates a living cell. Null is rejected by upload validation.</param>
public sealed record UploadBoardRequest(bool[][]? Cells);

/// <summary>The identifier, generation, dimensions, and cells of a board.</summary>
/// <param name="BoardId">The unique board identifier.</param>
/// <param name="Generation">The absolute generation represented by the cells.</param>
/// <param name="Width">The number of columns.</param>
/// <param name="Height">The number of rows.</param>
/// <param name="Cells">The cells indexed by row, then column.</param>
public sealed record BoardResponse(Guid BoardId, long Generation, int Width, int Height, bool[][] Cells);
