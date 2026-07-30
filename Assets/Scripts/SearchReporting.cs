using System.Collections.Generic;

/// <summary>One legal root move plus the stats its node accumulated. This is what the UI renders.</summary>
public struct MoveStat
{
    public GameCommand Move;
    public int Visits;
    public double AvgReward;
    public int Avail;
    public bool IsChosen;
}

/// <summary>Snapshot of the root taken once a move has been chosen. Fired via OnSearchDecision.</summary>
public class IsmctsReport
{
    public int Side;
    public int IterationsDone;
    public int IterationsTarget;
    public long ElapsedMs;
    public int RootVisits;
    public int NodesExpanded;
    public int ExpandedRootMoves;
    public int LegalCount;
    public List<MoveStat> Moves;   // sorted by visits desc
    public bool IsFinal;
}
