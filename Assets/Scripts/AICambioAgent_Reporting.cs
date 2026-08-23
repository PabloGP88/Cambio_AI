using System.Collections.Generic;

/* turns the finished search and current beliefs into the structs the UI and telemetry
   consume: IsmctsReport for per-decision search stats, BeliefReport for per-slot
   belief-vs-truth rows and the cambio-guard decision variables, and a verbose console
   dump of the root's children */
public partial class AICambioAgent
{
    private IsmctsReport BuildReport(Node root, List<GameCommand> legalAtRoot, long elapsedMs, int iterationsDone, GameCommand chosen)
    {
        var moves = new List<MoveStat>(legalAtRoot.Count);
        foreach (var move in legalAtRoot)
        {
            if (root.children.TryGetValue(move, out var child))
            {
                moves.Add(new MoveStat
                {
                    Move = move,
                    Visits = child.visits,
                    AvgReward = child.AvgReward,
                    Avail = child.avail,
                    IsChosen = move.Equals(chosen)
                });
            }
        }
        moves.Sort((a, b) => b.Visits.CompareTo(a.Visits));

        return new IsmctsReport
        {
            Side = _mySide,
            IterationsDone = iterationsDone,
            IterationsTarget = Iterations,
            ElapsedMs = elapsedMs,
            RootVisits = root.visits,
            NodesExpanded = _nodesExpandedThisSearch,
            ExpandedRootMoves = moves.Count,
            LegalCount = legalAtRoot.Count,
            Moves = moves,
            IsFinal = true
        };
    }

    private BeliefReport BuildBeliefReport(GameState pub, GameCommand chosen)
    {
        int oppSide = GameState.OpponentOf(_mySide);

        // match how the cambio shift is applied elsewhere: has the opponent called cambio
        bool oppCambio = pub.CambioCalled &&
                         (oppSide == GameState.PlayerSide ? pub.PlayerCalledCambio
                                                          : !pub.PlayerCalledCambio);

        // build the current unseen-pool histogram once: it's the shared prior for every
        // hidden slot, and lets us report the believed mean value per slot, E[value]
        List<int> poolIds = pub.UnseenCardIds(_beliefs.KnowIds(pub));
        double poolMean = CambioMath.PoolHistogram(poolIds, _poolHist);

        var rows = new List<BeliefSlotRow>();
        int knownOwn = 0, knownOpp = 0, hidden = 0;

        foreach (int side in new[] { GameState.PlayerSide, GameState.AISide })
        {
            foreach (var slot in pub.GetActiveSlots(side))
            {
                bool known = _beliefs.Known.ContainsKey(slot);
                if (known) { if (side == _mySide) knownOwn++; else knownOpp++; }
                else hidden++;


                double tiltRaw = 0.0, tiltEff = 0.0;
                if (!known)
                {
                    _beliefs.FillLogLik(slot, _logLbuf);
                    tiltRaw = poolMean - CambioMath.ExpectedValue(_logLbuf, _poolHist);

                    FillEffLogLik(slot, oppSide, oppCambio, _logLbuf);
                    tiltEff = poolMean - CambioMath.ExpectedValue(_logLbuf, _poolHist);
                }

                Card truth = pub.GetCard(slot);
                rows.Add(new BeliefSlotRow
                {
                    Slot       = slot,
                    IsOpponent = side != _mySide,
                    Known      = known,
                    OppKnows   = _beliefs.OppKnows(slot),
                    TiltRaw    = tiltRaw,   // believed-value shift from beliefs alone
                    TiltEff    = tiltEff,   // believed-value shift the search actually consumed
                    TrueValue  = truth.Value,
                    TrueNumber = truth.Number
                });
            }
        }

        return new BeliefReport
        {
            Side   = _mySide,
            Phase  = pub.Phase,
            Step   = pub.PowerStep,
            Chosen = chosen,
            BayesianOn = UseBayesianLayer,

            BelievedOwnScore = BelievedOwnScore(pub),
            ActualOwnScore   = pub.Score(_mySide),
            ActualOppScore   = pub.Score(oppSide),

            OppGlobalTilt = _beliefs.OppGlobalTilt,
            OppTurnCount  = _beliefs.OppTurnCount,

            HiddenCount   = hidden,
            KnownOwnCount = knownOwn,
            KnownOppCount = knownOpp,

            GuardEvaluated = _guardEvaluated,
            GuardMeanOwn   = _guardMeanOwn,
            GuardMeanOpp   = _guardMeanOpp,
            GuardPAhead    = _guardPAhead,
            Slots = rows
        };
    }

    private void LogTreeSummary(Node root, List<GameCommand> legalAtRoot, GameCommand chosen, long elapsedMs)
    {
        var entries = new List<Node>();
        foreach (var move in legalAtRoot)
            if (root.children.TryGetValue(move, out var child)) entries.Add(child);
        entries.Sort((a, b) => b.visits.CompareTo(a.visits));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[ISMCTS] === ChooseMove (side={_mySide}, {Iterations} iters, {_failedDeterminizations} skipped, {elapsedMs}ms) ===");
        sb.AppendLine($"[ISMCTS] root visits={root.visits}  expanded {entries.Count}/{legalAtRoot.Count} legal moves");
        foreach (var node in entries)
        {
            string mark = node.Action.Equals(chosen) ? "  <== CHOSEN" : "";
            sb.AppendLine($"[ISMCTS]   {node.Action,-30} visits={node.visits,4}  avg={node.AvgReward:F3}  avail={node.avail}{mark}");
        }
        int unexpanded = legalAtRoot.Count - entries.Count;
        if (unexpanded > 0)
            sb.AppendLine($"[ISMCTS]   ({unexpanded} legal move(s) never visited — raise Iterations if large)");

        UnityEngine.Debug.Log(sb.ToString());
    }
}
