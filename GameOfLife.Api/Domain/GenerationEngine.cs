namespace GameOfLife.Api.Domain;

public sealed class GenerationEngine : IGenerationEngine
{
    public BoardState CalculateNextGeneration(
        BoardState state,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Awaiting Slice 1 RED review and approval.");
}
