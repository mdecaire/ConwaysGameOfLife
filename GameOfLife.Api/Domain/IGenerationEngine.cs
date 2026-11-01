namespace GameOfLife.Api.Domain;

public interface IGenerationEngine
{
    BoardState CalculateNextGeneration(
        BoardState state,
        CancellationToken cancellationToken = default);
}
