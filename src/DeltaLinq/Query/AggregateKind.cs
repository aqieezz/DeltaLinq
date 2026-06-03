namespace DeltaLinq;

/// <summary>Scalar terminal operators translated to SQL aggregates / EXISTS / COUNT.</summary>
internal enum AggregateKind
{
    Count,
    LongCount,
    Any,
    Sum,
    Min,
    Max,
    Average
}
