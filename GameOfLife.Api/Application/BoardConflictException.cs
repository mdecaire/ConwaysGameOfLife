namespace GameOfLife.Api.Application;

public sealed class BoardConflictException() : Exception("The board changed before the update could commit.");
