namespace GameOfLife.Api.Domain;

public interface IGenerationEngine
{
    /// <summary>Calculates the next finite, nonwrapping generation without changing the input.</summary>
    /// <param name="state">The current board dimensions and living cells.</param>
    /// <param name="cancellationToken">The token used to observe cancellation during calculation.</param>
    /// <returns>A new board state with unchanged dimensions.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was observed during calculation.</exception>
    BoardState CalculateNextGeneration(
        BoardState state,
        CancellationToken cancellationToken = default);
}
