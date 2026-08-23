using System.Collections.Generic;

// one legal root move plus the stats its node accumulated; this is what the UI renders
public struct MoveStat
{
    public GameCommand Move;
    public int Visits;
    public double AvgReward;
    public int Avail;
    public bool IsChosen;
}

// snapshot of the root taken once a move has been chosen; fired via OnSearchDecision
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
    public List<MoveStat> Moves;   // sorted by visits descending
    public bool IsFinal;
}
