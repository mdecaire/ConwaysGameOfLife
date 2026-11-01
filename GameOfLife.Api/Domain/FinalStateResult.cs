namespace GameOfLife.Api.Domain;

public enum FinalStateOutcome
{
    Stable,
    Cycle,
    Inconclusive
}

/// <summary>
/// Generation labels State on the trajectory from StartingGeneration. CycleStartGeneration
/// is the first observed generation in the verified cycle (including a period-1 stable state).
/// Cycle metadata is null for Inconclusive. IterationsUsed counts actual engine calls.
/// </summary>
public sealed record FinalStateResult(
    FinalStateOutcome Outcome,
    BoardState State,
    long StartingGeneration,
    long Generation,
    int? CycleLength,
    long? CycleStartGeneration,
    int IterationsUsed);
