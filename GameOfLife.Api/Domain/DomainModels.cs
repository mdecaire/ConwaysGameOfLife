namespace GameOfLife.Api.Domain;

// Data shape only; validation and generation behavior follow their approved tests.
/// <summary>The dimensions and living-cell coordinates of a finite board.</summary>
/// <param name="Width">The number of columns.</param>
/// <param name="Height">The number of rows.</param>
/// <param name="LivingCells">The living coordinates, with X indexing columns and Y indexing rows.</param>
public sealed record BoardState(
    int Width,
    int Height,
    HashSet<(int X, int Y)> LivingCells);
