using System;
using System.Collections.Generic;

/// <summary>
/// Belief-weighted determinization. For each search iteration we clone the public state and
/// fill every hidden slot by sampling the unseen pool proportional to that slot's effective
/// belief, leaving the remainder as an uninformed draw pile. With the Bayesian layer off,
/// every belief is flat and this reduces to a uniform determinizer — the null control.
/// </summary>
public partial class AICambioAgent
{
    private GameState Determinize(GameState publicState, int iteration)
    {
        GameState world = publicState.Clone(RandomSeed + iteration);

        List<SlotRef> hidden = _beliefs.HiddenSlots(world);
        List<int> known = _beliefs.KnowIds(world);

        // The unseen pool IS the Bayesian prior: sampling a hidden slot proportional to
        // exp(logL(value)) over these cards yields the deck-coherent posterior
        //   P(value=v) ∝ N_pool(v) · exp(logL(v)).
        List<int> pool = world.UnseenCardIds(known);

        if (pool.Count < hidden.Count)
        {
            if (MctsDebug.At(1))
                MctsDebug.LogWarning($"Determinize skipped iter={iteration}: pool={pool.Count} < hidden={hidden.Count} (belief/pool leak).");
            return null;
        }

        bool oppCambio = world.CambioCalled && world.PlayerCalledCambio;
        AssignHidden(world, hidden, pool, oppCambio);

        if (ValidateDeterminizations && !world.IsCardSetWorking())
        {
            if (MctsDebug.At(1))
                MctsDebug.LogWarning($"Determinize skipped iter={iteration}: inconsistent card set " +
                                     $"(hidden={hidden.Count}, pool={pool.Count}, known={known.Count}).");
            return null;
        }

        if (MctsDebug.At(2))
            MctsDebug.Log(2, $"iter={iteration} determinize: hidden={hidden.Count} known={known.Count} pool={pool.Count}");
        return world;
    }

    /// <summary>Fill a 12-bucket effective log-likelihood vector (index = Card.Value + 1)
    /// for the given slot — the belief the search should sample from. Baseline (Bayesian
    /// off) is always flat, which reproduces the old uniform determinizer exactly. The
    /// cambio nudge lives here (not in CardBeliefs) so it can be toggled with the layer.</summary>
    private void FillEffLogLik(SlotRef s, int oppSide, bool oppCambio, double[] outLogL)
    {
        if (!UseBayesianLayer) { Array.Clear(outLogL, 0, outLogL.Length); return; }

        _beliefs.FillLogLik(s, outLogL);

        if (oppCambio && s.Side == oppSide)
            for (int v = -1; v <= 10; v++) outLogL[v + 1] += -CambioShift * v;
    }

    /// <summary>Belief-weighted assignment of hidden slots to distinct pool cards
    /// (weighted sampling without replacement). Each slot draws a pool card with weight
    /// exp(logL(value)); summed over the pool this reproduces the deck-coherent posterior
    /// P(v) ∝ N_pool(v)·exp(logL(v)). The leftover pool becomes the (uninformed) draw pile.</summary>
    private void AssignHidden(GameState world, List<SlotRef> hidden, List<int> pool, bool oppCambio)
    {
        int oppSide = GameState.OpponentOf(_mySide);

        // Peaky (confident) slots pick from the full pool first: reduces sequential-WOR bias.
        // Peakiness = spread of the effective log-likelihood; flat slots (no signal) sort last.
        double PeakOf(SlotRef s) { FillEffLogLik(s, oppSide, oppCambio, _logLbuf); return CambioMath.Spread(_logLbuf); }
        hidden.Sort((a, b) => PeakOf(b).CompareTo(PeakOf(a)));

        var assigned = new int[hidden.Count];

        for (int k = 0; k < hidden.Count; k++)
        {
            FillEffLogLik(hidden[k], oppSide, oppCambio, _logLbuf);

            int pick;
            if (CambioMath.Spread(_logLbuf) < 1e-9 || pool.Count == 1)
            {
                pick = _rng.Next(pool.Count);                       // flat belief -> uniform fast path
            }
            else
            {
                // exp with a max-subtract for numerical stability (offset cancels in the ratio).
                double maxLog = double.NegativeInfinity;
                for (int b = 0; b < 12; b++) if (_logLbuf[b] > maxLog) maxLog = _logLbuf[b];
                for (int b = 0; b < 12; b++) _ew[b] = Math.Exp(_logLbuf[b] - maxLog);

                double total = 0;
                for (int i = 0; i < pool.Count; i++) total += _ew[CambioMath.ValueIdx(pool[i])];

                if (total <= 0)
                {
                    pick = _rng.Next(pool.Count);                   // degenerate guard
                }
                else
                {
                    double r = _rng.NextDouble() * total, acc = 0;
                    pick = pool.Count - 1;
                    for (int i = 0; i < pool.Count; i++)
                    {
                        acc += _ew[CambioMath.ValueIdx(pool[i])];
                        if (r <= acc) { pick = i; break; }
                    }
                }
            }

            assigned[k] = pool[pick];
            int last = pool.Count - 1;                              // O(1) swap-remove
            pool[pick] = pool[last];
            pool.RemoveAt(last);
        }

        world.OverwriteHidden(hidden, assigned);
        Shuffle(pool);                                             // draw pile: genuinely uninformed
        world.SetDrawPile(pool);
    }

    private void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
