namespace GameOfLife.Api.Domain;

public sealed class GenerationEngine : IGenerationEngine
{
    public BoardState CalculateNextGeneration(
        BoardState state,
        CancellationToken cancellationToken = default)
        => new BoardState(state.Width, state.Height, new HashSet<(int X, int Y)>());
}
