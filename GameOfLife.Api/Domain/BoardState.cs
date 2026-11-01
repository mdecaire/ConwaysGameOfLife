namespace GameOfLife.Api.Domain;

// Data shape only; validation and generation behavior follow their approved tests.
public sealed record BoardState(
    int Width,
    int Height,
    HashSet<(int X, int Y)> LivingCells);
