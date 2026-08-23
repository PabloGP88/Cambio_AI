using System;
using System.Collections.Generic;
using System.Linq;

public partial class AICambioAgent
{
    // legal moves at the root, minus a too-early Cambio if the guard forbids it 
    private List<GameCommand> LegalForSearch(GameState state)
    {
        _guardEvaluated = false;
        var legal = state.LegalMoves();

        // only pay for the guard when CallCambio is actually on the table this decision
        if (UseCambioGuard && legal.Count > 1)
        {
            bool hasCambio = false;
            for (int i = 0; i < legal.Count; i++)
                if (legal[i].Type == CommandType.CallCambio) { hasCambio = true; break; }

            if (hasCambio)
            {
                bool allowCambio = UseBayesianLayer
                    ? BayesianCambioOk(state)                       // relative, distribution-based
                    : BelievedOwnScore(state) <= CambioGuardScore;  // old absolute cap

                if (!allowCambio)
                {
                    var filtered = legal.Where(m => m.Type != CommandType.CallCambio).ToList();
                    if (filtered.Count > 0) legal = filtered;       // never filter down to zero moves
                }
            }
        }

        /*if (legal.Count > 1)   
        {
            Card top = state.TopDiscard;
            var filtered = legal.Where(m =>
                m.Type != CommandType.AttemptMatch ||              
                (!top.IsNone &&
                 _beliefs.Known.TryGetValue(m.Slot, out var c) &&   
                 c.Number == top.Number)).ToList();                 
            if (filtered.Count > 0) legal = filtered;
        }
        */

        if (MctsDebug.At(1))
            MctsDebug.Log(1, $"ChooseMove: side={_mySide} phase={state.Phase} powerStep={state.PowerStep} " +
                             $"legal={legal.Count} known={_beliefs?.Known.Count ?? 0}");
        return legal;
    }

    // distribution-based Cambio guard
    private bool BayesianCambioOk(GameState pub)
    {
        int oppSide = GameState.OpponentOf(_mySide);

        const bool oppCambio = false;

        CambioMath.PoolHistogram(pub.UnseenCardIds(_beliefs.KnowIds(pub)), _poolHist);

        var (mOwn, vOwn) = BelievedScoreDist(pub, _mySide, oppSide, oppCambio, _poolHist);
        var (mOpp, vOpp) = BelievedScoreDist(pub, oppSide, oppSide, oppCambio, _poolHist);

        double meanD = mOwn - mOpp; // want this well below zero
        double sdD = Math.Sqrt(vOwn + vOpp) + 1e-9;

        // P(own - opp < -margin)
        double pAhead = CambioMath.NormalCdf((-CambioMargin - meanD) / sdD);

        _guardMeanOwn = mOwn; _guardMeanOpp = mOpp;
        _guardPAhead  = pAhead; _guardEvaluated = true;

        if (MctsDebug.At(1))
            MctsDebug.Log(1, $"CambioGuard[bayes]: E[own]={mOwn:F2}±{Math.Sqrt(vOwn):F2}  " +
                             $"E[opp]={mOpp:F2}±{Math.Sqrt(vOpp):F2}  margin={CambioMargin}  " +
                             $"P(ahead)={pAhead:F3} (need>={CambioConfidence}) -> {(pAhead >= CambioConfidence ? "ALLOW" : "block")}");

        return pAhead >= CambioConfidence;
    }


    private (double mean, double variance) BelievedScoreDist(
        GameState pub, int side, int oppSide, bool oppCambio, double[] poolHist)
    {
        double mean = 0, variance = 0;
        foreach (var slot in pub.GetActiveSlots(side))
        {
            if (_beliefs.Known.TryGetValue(slot, out var c))
            {
                mean += c.Value;                        // certain, so contributes no variance
            }
            else
            {
                FillEffLogLik(slot, oppSide, oppCambio, _logLbuf);
                var (m, v) = CambioMath.MomentsOf(_logLbuf, poolHist);
                mean     += m;
                variance += v;
            }
        }
        return (mean, variance);
    }

    /* flat-prior believed own score used by the baseline guard: known slots at their exact
       value, every unknown own slot at UnknownOwnPrior */
    private double BelievedOwnScore(GameState pub)
    {
        double score = 0;
        int unknown = 0;
        foreach (var slot in pub.GetActiveSlots(_mySide))
        {
            if (_beliefs.Known.TryGetValue(slot, out var c)) score += c.Value;
            else unknown++;
        }
        return score + unknown * UnknownOwnPrior;
    }
}
