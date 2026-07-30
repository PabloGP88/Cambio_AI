using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;

public sealed class Node
{
    public readonly GameCommand Action;
    public readonly Node parent;
    public readonly Dictionary<GameCommand, Node> children = new();
    public readonly int Depth;

    public int visits;
    public int avail;
    public double reward;
    

    public double AvgReward => visits > 0 ? reward / visits : 0.0;

    public Node(GameCommand action, Node parent)
    {
        Action = action;
        this.parent = parent;
        Depth = parent == null ? 0 : parent.Depth + 1;
    }
}

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


/// <summary>
/// Leveled console logging for the search. 0 off, 1 per-decision summary, 2 + expansions
/// and determinize stats, 3 + every selection / rollout. Flip via MctsDebug.Verbosity.
/// NOTE: always guard hot-path calls with `if (MctsDebug.At(n))` so the interpolated
/// string isn't built when the level is disabled.
/// </summary>
public static class MctsDebug
{
    public static bool Enabled = true;
    public static int Verbosity = 1;
    private const string Tag = "[ISMCTS]";

    public static bool At(int level) => Enabled && Verbosity >= level;
    public static void Log(int level, string msg) { if (At(level)) UnityEngine.Debug.Log($"{Tag} {msg}"); }
    public static void LogWarning(string msg) => UnityEngine.Debug.LogWarning($"{Tag} {msg}");
}

public class AICambioAgent : IAgent
{
    // --- Search tuning ---
    public int Iterations = 4000;
    public double Exploration = 1.41;
    public int RandomSeed = 12345;

    // Random plies to play out past the tree leaf before evaluating. 0 = evaluate the leaf
    // directly (MCTS with a value function). Non-zero gives a shallow random playout.
    public int RolloutPlyCap = 0;

    // Yield to Unity every N iterations inside ChooseMoveRoutine so a big search doesn't
    // freeze a frame. 0 = never yield (whole search runs in one frame, matches old behaviour).
    public int IterationsPerYield = 0;

    // Drop CallCambio from the root move set while we believe our own hand is still too high.
    // Baseline (Bayesian off) uses this absolute own-score cap. The Bayesian path ignores it
    // and instead compares believed own vs opponent score distributions (see below).
    public int CambioGuardScore = 10;
    public bool UseCambioGuard = true;

    // --- Bayesian Cambio guard (only used when UseBayesianLayer is true) ---
    // We permit CallCambio only when we believe we finish ahead by at least CambioMargin points,
    // with probability >= CambioConfidence. Calling ends our turn and hands the opponent exactly
    // one more turn, so CambioMargin also absorbs the improvement a competent opponent squeezes
    // from that final draw (don't call when only marginally ahead — they can catch up).
    public double CambioMargin     = 2.0;   // points of believed lead required (own < opp - margin)
    public double CambioConfidence = 0.60;  // P(we're ahead by the margin) needed to allow the call

    // On a belief/pool inconsistency, skip that determinization instead of crashing the turn.
    public bool ValidateDeterminizations = true;
    
    // switch from using or not the belief layer (baseline = uniform determinizer)
    public bool UseBayesianLayer = true;

    public bool DebugLogging { get => MctsDebug.Enabled; set => MctsDebug.Enabled = value; }
    public int DebugVerbosity { get => MctsDebug.Verbosity; set => MctsDebug.Verbosity = value; }

    private int _mySide;
    private readonly Random _rng;
    private CardBeliefs _beliefs;

    
    // Stats for graphs
    
    private bool   _guardEvaluated;
    private double _guardMeanOwn, _guardMeanOpp, _guardPAhead;
    
    private int _nodesExpandedThisSearch;
    private int _failedDeterminizations;

    public event Action<IsmctsReport> OnSearchDecision;
    public event Action<BeliefReport> OnBeliefSnapshot;
    public AICambioAgent(int seed)
    {
        RandomSeed = seed;
        _rng = new Random(seed);
    }

    public double UnknownOwnPrior = 5.889;
    // ---------------------------------------------------------------- IAgent

    public void OnNewGame(int mySide, GameState initialState)
    {
        _mySide = mySide;
        _beliefs = new CardBeliefs(mySide, initialState.HandSize, initialState.PenaltySize);

        // The AI peeks its first two hand cards, exactly like the human's opening peek.
        var slot0 = new SlotRef(mySide, Zone.Hand, 0);
        var slot1 = new SlotRef(mySide, Zone.Hand, 1);
        _beliefs.SetKnow(slot0, initialState.GetCard(slot0));
        _beliefs.SetKnow(slot1, initialState.GetCard(slot1));
    }

    public void Observe(GameEffect effect, bool iAmActor)
    {
        _beliefs?.Update(effect, iAmActor);

        if (MctsDebug.At(2))
            MctsDebug.Log(2, $"Observe: {effect.Kind,-18} iAmActor={iAmActor,-5} slot={effect.Slot} " +
                             $"card={(effect.Card.IsNone ? "?" : effect.Card.Id.ToString())} success={effect.Success}");
    }

    public GameCommand ChooseMove(GameState publicState)
    {
        var legal = LegalForSearch(publicState);
        if (legal.Count == 0) return default;
        if (legal.Count == 1)
        {
            OnBeliefSnapshot?.Invoke(BuildBeliefReport(publicState, legal[0])); 
            return legal[0];
        }

        var root = NewRoot();
        var sw = MctsDebug.At(1) ? System.Diagnostics.Stopwatch.StartNew() : null;

        for (int i = 0; i < Iterations; i++)
            RunOneIteration(root, publicState, i);

        var chosen = MostVisited(root, legal);
        if (sw != null) { sw.Stop(); LogTreeSummary(root, legal, chosen, sw.ElapsedMilliseconds); }

        OnBeliefSnapshot?.Invoke(BuildBeliefReport(publicState, chosen));         
        return chosen;
    }

    public IEnumerator ChooseMoveRoutine(GameState publicState, Action<GameCommand> onDecided)
    {
        var legal = LegalForSearch(publicState);
        if (legal.Count <= 1)
        {
            var only = legal.Count == 1 ? legal[0] : default;
            OnBeliefSnapshot?.Invoke(BuildBeliefReport(publicState, only));   
            onDecided(only);
            yield break;
        }

        var root = NewRoot();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < Iterations; i++)
        {
            RunOneIteration(root, publicState, i);
            if (IterationsPerYield > 0 && (i + 1) % IterationsPerYield == 0)
                yield return null;
        }

        sw.Stop();
        var chosen = MostVisited(root, legal);
        OnSearchDecision?.Invoke(BuildReport(root, legal, sw.ElapsedMilliseconds, Iterations, chosen));
        if (MctsDebug.At(1)) LogTreeSummary(root, legal, chosen, sw.ElapsedMilliseconds);

        OnBeliefSnapshot?.Invoke(BuildBeliefReport(publicState, chosen));  
        onDecided(chosen);
    }

    // ---------------------------------------------------------------- Search

    private Node NewRoot()
    {
        _nodesExpandedThisSearch = 0;
        _failedDeterminizations = 0;
        return new Node(default, null);
    }

    private void RunOneIteration(Node root, GameState publicState, int i)
    {
        GameState world = Determinize(publicState, i);
        if (world == null) { _failedDeterminizations++; return; }
        SimulateOnce(world, root, i);
    }

    /// <summary>Legal moves at the root, minus a too-early Cambio if the guard forbids it.
    /// Baseline uses the absolute own-score cap; the Bayesian layer uses a relative
    /// own-vs-opponent score-distribution test (see BayesianCambioOk).</summary>
    private List<GameCommand> LegalForSearch(GameState state)
    {
        _guardEvaluated = false; 
        var legal = state.LegalMoves();

        // Only pay for the guard when CallCambio is actually on the table this decision.
        if (UseCambioGuard && legal.Count > 1)
        {
            bool hasCambio = false;
            for (int i = 0; i < legal.Count; i++)
                if (legal[i].Type == CommandType.CallCambio) { hasCambio = true; break; }

            if (hasCambio)
            {
                bool allowCambio = UseBayesianLayer
                    ? BayesianCambioOk(state)                       // relative, distribution-based
                    : BelievedOwnScore(state) <= CambioGuardScore;  // old absolute cap (baseline)

                if (!allowCambio)
                {
                    var filtered = legal.Where(m => m.Type != CommandType.CallCambio).ToList();
                    if (filtered.Count > 0) legal = filtered;       // never filter down to zero moves
                }
            }
        }

        /*if (legal.Count > 1)   // This removes blind-matching
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

    /// <summary>Distribution-based Cambio guard. Estimates believed own and opponent END scores
    /// under the same deck-coherent posterior, treats their difference D = own - opp as
    /// Normal(E[own]-E[opp], Var[own]+Var[opp]) (independence across the two hands is a
    /// mean-field approximation), and permits the call only when
    ///   P(D &lt; -CambioMargin) >= CambioConfidence.
    /// The margin folds in the opponent's one guaranteed final turn.</summary>
    private bool BayesianCambioOk(GameState pub)
    {
        int oppSide = GameState.OpponentOf(_mySide);

        const bool oppCambio = false;

        PoolHistogram(pub.UnseenCardIds(_beliefs.KnowIds(pub)), _poolHist);

        var (mOwn, vOwn) = BelievedScoreDist(pub, _mySide, oppSide, oppCambio, _poolHist);
        var (mOpp, vOpp) = BelievedScoreDist(pub, oppSide, oppSide, oppCambio, _poolHist);

        double meanD = mOwn - mOpp; // want this well below zero
        double sdD = Math.Sqrt(vOwn + vOpp) + 1e-9;

        // P(own - opp < -margin)
        double pAhead = NormalCdf((-CambioMargin - meanD) / sdD);
    
        _guardMeanOwn = mOwn; _guardMeanOpp = mOpp; 
        _guardPAhead  = pAhead; _guardEvaluated = true; 

    if (MctsDebug.At(1))
            MctsDebug.Log(1, $"CambioGuard[bayes]: E[own]={mOwn:F2}±{Math.Sqrt(vOwn):F2}  " +
                             $"E[opp]={mOpp:F2}±{Math.Sqrt(vOpp):F2}  margin={CambioMargin}  " +
                             $"P(ahead)={pAhead:F3} (need>={CambioConfidence}) -> {(pAhead >= CambioConfidence ? "ALLOW" : "block")}");

        return pAhead >= CambioConfidence;
    }

    /// <summary>Believed mean and variance of a side's total score. Known slots contribute
    /// their exact value with zero variance; hidden slots contribute E[value] and Var[value]
    /// from the deck-coherent posterior P(v) ∝ poolHist[v]·exp(effLogL[v]). With the Bayesian
    /// layer off both sides' hidden slots fall back to the flat pool posterior (unused here,
    /// since this is only called on the Bayesian path).</summary>
    private (double mean, double variance) BelievedScoreDist(
        GameState pub, int side, int oppSide, bool oppCambio, double[] poolHist)
    {
        double mean = 0, variance = 0;
        foreach (var slot in pub.GetActiveSlots(side))
        {
            if (_beliefs.Known.TryGetValue(slot, out var c))
            {
                mean += c.Value;                        // certain -> contributes no variance
            }
            else
            {
                FillEffLogLik(slot, oppSide, oppCambio, _logLbuf);
                var (m, v) = MomentsOf(_logLbuf, poolHist);
                mean     += m;
                variance += v;
            }
        }
        return (mean, variance);
    }

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

    // Extra low-lean applied to the opponent's slots once THEY have called cambio: you rarely
    // call from behind, so their hand is probably low. Applied as a log-linear likelihood
    // factor exp(-CambioShift * value) on top of whatever CardBeliefs already believes.
    public double CambioShift = 0.25;

    private readonly double[] _ew     = new double[12];  // exp(logL) weight buffer, index = Value+1
    private readonly double[] _logLbuf = new double[12]; // scratch log-likelihood vector
    private readonly double[] _poolHist = new double[12];// unseen-pool value histogram (report only)

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

    private void SimulateOnce(GameState world, Node root, int iteration)
    {
        Node node = root;
        var path = new List<Node> { root };

        while (!world.IsTerminal)
        {
            List<GameCommand> legal = world.LegalMoves();
            if (legal.Count == 0) break;

            int side = world.ActiveSide;

            // Single pass: bump availability for every legal move that already has a node
            // (ISMCTS: it was "available" this descent), and remember the first untried move.
            GameCommand? untried = null;
            foreach (var move in legal)
            {
                if (node.children.TryGetValue(move, out var c)) c.avail++;
                else if (!untried.HasValue) untried = move;
            }

            // Expansion
            if (untried.HasValue)
            {
                world.Apply(untried.Value);
                var child = new Node(untried.Value, node);
                _nodesExpandedThisSearch++;
                node.children[untried.Value] = child;
                path.Add(child);
                node = child;

                if (MctsDebug.At(2))
                    MctsDebug.Log(2, $"iter={iteration} EXPAND depth={child.Depth} side={side} action={untried.Value}");
                break;
            }

            // Selection
            Node chosen = null;
            double bestUcb = double.NegativeInfinity;
            foreach (var move in legal)
            {
                Node c = node.children[move];
                double u = Ucb(c, side);
                if (u > bestUcb) { bestUcb = u; chosen = c; }
            }

            if (MctsDebug.At(3))
                MctsDebug.Log(3, $"iter={iteration} SELECT depth={node.Depth} side={side} -> {chosen.Action} " +
                                 $"ucb={bestUcb:F3} visits={chosen.visits} avg={chosen.AvgReward:F3}");

            world.Apply(chosen.Action);
            path.Add(chosen);
            node = chosen;
        }

        double reward = Rollout(world, iteration, node.Depth);

        foreach (var n in path)
        {
            n.visits++;
            n.reward += reward;
        }

        if (MctsDebug.At(3))
            MctsDebug.Log(3, $"iter={iteration} BACKPROP reward={reward:F3} across {path.Count} nodes");
    }

    private double Ucb(Node child, int chooser)
    {
        double exploit = child.reward / child.visits;
        if (chooser != GameState.AISide) exploit = 1.0 - exploit;   // opponent minimises AI reward
        double explore = Exploration * Math.Sqrt(Math.Log(child.avail) / child.visits);
        return exploit + explore;
    }

    private double Rollout(GameState world, int iteration, int startDepth)
    {
        int plies = 0;
        while (!world.IsTerminal && plies < RolloutPlyCap)
        {
            List<GameCommand> legal = world.LegalMoves();
            if (legal.Count == 0) break;
            world.Apply(legal[_rng.Next(legal.Count)]);
            plies++;
        }

        double result = Evaluate(world);

        if (MctsDebug.At(3))
            MctsDebug.Log(3, $"iter={iteration} ROLLOUT from depth={startDepth} ran {plies} plies, " +
                             $"terminal={world.IsTerminal} -> reward={result:F3}");
        return result;
    }

    private const double EvalTempo = 8.0;         // softness of the tanh
    private const double EvalTargetScore = 14.0;  // AI hand score we treat as ok
    private const double PenaltyAversion = 0.3;

    private double Evaluate(GameState world)
    {
        if (world.IsTerminal)
        {
            int w = world.WinnerSide();
            if (w == GameState.AISide) return 1.0;
            if (w < 0) return 0.5;
            return 0.0;
        }

        int ai  = world.Score(GameState.AISide);
        int opp = world.Score(GameState.OpponentOf(GameState.AISide));
        double rel = 0.5 + 0.5 * Math.Tanh((opp - ai) / EvalTempo);
        double abs = 0.5 - 0.5 * Math.Tanh((ai - EvalTargetScore) / EvalTempo);

        // Linear, un-saturated cost for penalty cards the AI is carrying.
        double aiPenalty = 0;
        foreach (var s in world.GetActiveSlots(GameState.AISide))
            if (s.Zone == Zone.Penalty) aiPenalty += world.GetCard(s).Value;

        double blended = 0.5 * rel + 0.5 * abs - PenaltyAversion * aiPenalty;
        return blended < 0 ? 0 : blended > 1 ? 1 : blended;
    }

    private void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private GameCommand MostVisited(Node root, List<GameCommand> legalAtRoot)
    {
        Node best = null;
        foreach (var move in legalAtRoot)
            if (root.children.TryGetValue(move, out var child))
                if (best == null || child.visits > best.visits) best = child;

        if (best == null)
        {
            MctsDebug.LogWarning($"MostVisited: no expanded children out of {legalAtRoot.Count} legal moves " +
                                 $"({_failedDeterminizations} determinizations skipped) — picking randomly.");
            return legalAtRoot[_rng.Next(legalAtRoot.Count)];
        }
        return best.Action;
    }

    // ---------------------------------------------------------------- Reporting

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

        // Match how the cambio shift is applied elsewhere: "has the OPPONENT called cambio".
        bool oppCambio = pub.CambioCalled &&
                         (oppSide == GameState.PlayerSide ? pub.PlayerCalledCambio
                                                          : !pub.PlayerCalledCambio);

        // Build the current unseen-pool histogram once: it's the shared prior for every
        // hidden slot, and lets us report the believed MEAN value per slot (E[value]).
        List<int> poolIds = pub.UnseenCardIds(_beliefs.KnowIds(pub));
        double poolMean = PoolHistogram(poolIds, _poolHist);

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
                    tiltRaw = poolMean - ExpectedValue(_logLbuf, _poolHist);

                    FillEffLogLik(slot, oppSide, oppCambio, _logLbuf);
                    tiltEff = poolMean - ExpectedValue(_logLbuf, _poolHist);
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

            GuardEvaluated = _guardEvaluated,   // <-- add these four
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
    
    public SlotRef SnapOwn(GameState pub)
    {
        Card top = pub.TopDiscard;
        if (top.IsNone) return SlotRef.None;
        foreach (var kv in _beliefs.Known)
        {
            if (kv.Key.Side != _mySide) continue;
            if (!pub.IsActive(kv.Key)) continue;
            if (kv.Value.Number == top.Number) return kv.Key;
        }
        return SlotRef.None;
    }

    // ---------------------------------------------------------------- Belief -> sampling glue

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

    private static int ValueIdx(int cardId) => new Card(cardId).Value + 1;   // -1..10 -> 0..11

    private static double Spread(double[] logL)
    {
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        for (int i = 0; i < logL.Length; i++)
        {
            if (logL[i] < mn) mn = logL[i];
            if (logL[i] > mx) mx = logL[i];
        }
        return mx - mn;
    }

    /// <summary>Believed mean and variance of a slot's value under the deck-coherent posterior
    /// P(v) ∝ poolHist[v] · exp(logL[v]). Values outside the current pool get zero weight, so
    /// beliefs stay consistent with what is physically left in the deck. A max-subtract keeps
    /// exp() from under/overflowing for peaked likelihoods.</summary>
    private static (double mean, double variance) MomentsOf(double[] logL, double[] poolHist)
    {
        double maxLog = double.NegativeInfinity;
        for (int b = 0; b < 12; b++)
            if (poolHist[b] > 0 && logL[b] > maxLog) maxLog = logL[b];
        if (double.IsNegativeInfinity(maxLog)) return (0.0, 0.0);   // empty pool

        double num = 0, num2 = 0, den = 0;
        for (int v = -1; v <= 10; v++)
        {
            double w = poolHist[v + 1] * Math.Exp(logL[v + 1] - maxLog);
            num  += v * w;
            num2 += (double)v * v * w;
            den  += w;
        }
        if (den <= 0) return (0.0, 0.0);
        double mean = num / den;
        double variance = num2 / den - mean * mean;
        return (mean, variance < 0 ? 0.0 : variance);   // clamp tiny negative from rounding
    }

    /// <summary>E[value] for a slot; thin wrapper over MomentsOf for telemetry.</summary>
    private static double ExpectedValue(double[] logL, double[] poolHist) => MomentsOf(logL, poolHist).mean;

    /// <summary>Standard normal CDF (Zelen &amp; Severo / A&amp;S 26.2.17, |error| &lt; 7.5e-8).</summary>
    private static double NormalCdf(double z)
    {
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double d = 0.3989422804014327 * Math.Exp(-z * z / 2.0);
        double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 +
                   t * (-1.821255978 + t * 1.330274429))));
        return z >= 0 ? 1.0 - p : p;
    }

    private static double PoolHistogram(List<int> pool, double[] hist12)
    {
        Array.Clear(hist12, 0, hist12.Length);
        double sum = 0;
        foreach (int id in pool)
        {
            int val = new Card(id).Value;
            hist12[val + 1] += 1.0;
            sum += val;
        }
        return pool.Count > 0 ? sum / pool.Count : 0.0;
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
        double PeakOf(SlotRef s) { FillEffLogLik(s, oppSide, oppCambio, _logLbuf); return Spread(_logLbuf); }
        hidden.Sort((a, b) => PeakOf(b).CompareTo(PeakOf(a)));

        var assigned = new int[hidden.Count];

        for (int k = 0; k < hidden.Count; k++)
        {
            FillEffLogLik(hidden[k], oppSide, oppCambio, _logLbuf);

            int pick;
            if (Spread(_logLbuf) < 1e-9 || pool.Count == 1)
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
                for (int i = 0; i < pool.Count; i++) total += _ew[ValueIdx(pool[i])];

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
                        acc += _ew[ValueIdx(pool[i])];
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
    
}

public class CardBeliefs
{
    private readonly int _mySide;
    private readonly int _handSize;
    private readonly int _penaltySize;
    private readonly int _oppSide;

    private const int Buckets = 12;                 // Card.Value in [-1..10] -> index Value+1 in [0..11]

    // --- Likelihood tuning (every term is a multiplicative factor in probability space) ---
    // Opponent kept a drawn card, discarding a card worth d: they'd only keep it if it beat d,
    // so the new (hidden) card v is likely < d. Modelled as sigmoid(SwapBeta·(d - v)).
    public double SwapBeta = 0.35;    // sharpness of that keep-sigmoid
    public double SwapBias = 0.05;    // small blanket lean-low for choosing to keep a draw at all

    // A slot the opponent KNOWS and has kept across turns is probably fine for them: a mild,
    // capped lean-low that grows with the number of turns it survived.
    public double KeepLogLik  = 0.03;
    public int    KeepTurnCap = 6;

    // A plain low face-up discard is weak evidence the opponent's whole hand is low: a global
    // log-linear lean-low applied to all their slots, capped.
    public double DiscardSlope = 0.02;
    public double TypicalValue = 6.0;
    public double GlobalCap    = 0.6;

    private readonly Dictionary<SlotRef, double[]> _logL = new();   // per-slot accumulated log-likelihood
    private readonly HashSet<SlotRef> _oppKnows = new();
    private readonly Dictionary<SlotRef, int> _oppKnownSince = new();
    private int _oppTurnCount;
    private double _oppGlobalLowSlope;                              // global lean-low slope (opp slots)

    private readonly Dictionary<SlotRef, Card> _known = new();

    // Stats for graphs / telemetry
    public double OppGlobalTilt => _oppGlobalLowSlope;
    public int    OppTurnCount  => _oppTurnCount;
    public bool   OppKnows(SlotRef s) => _oppKnows.Contains(s);

    public CardBeliefs(int mySide, int handSize, int penaltySize)
    {
        _mySide = mySide;
        _handSize = handSize;
        _penaltySize = penaltySize;
        _oppSide = GameState.OpponentOf(mySide);
        var o0 = new SlotRef(_oppSide, Zone.Hand, 0);
        var o1 = new SlotRef(_oppSide, Zone.Hand, 1);
        _oppKnows.Add(o0); _oppKnownSince[o0] = 0;
        if (handSize > 1)
        {
            _oppKnows.Add(o1); _oppKnownSince[o1] = 0;
        }
    }

    public IReadOnlyDictionary<SlotRef, Card> Known => _known;

    public void SetKnow(SlotRef s, Card card)
    {
        if (s.IsNone || card.IsNone) return;
        _known[s] = card;
    }

    /// <summary>Move known-ness with the cards when two slots swap contents.</summary>
    public void SwapKnow(SlotRef s0, SlotRef s1)
    {
        bool knownA = _known.TryGetValue(s0, out var cardA);
        bool knownB = _known.TryGetValue(s1, out var cardB);

        if (knownB) _known[s0] = cardB; else _known.Remove(s0);
        if (knownA) _known[s1] = cardA; else _known.Remove(s1);
    }

    /// <summary>Fill a slot's total log-likelihood over value buckets (index = Card.Value + 1).
    /// All-zero == flat == "fall back to the deck prior". Known slots return flat (we're certain,
    /// so no shaping is needed — the determinizer pins them to their exact card anyway).</summary>
    public void FillLogLik(SlotRef s, double[] outLogL)
    {
        Array.Clear(outLogL, 0, outLogL.Length);
        if (_known.ContainsKey(s)) return;

        if (_logL.TryGetValue(s, out var stored))
            for (int b = 0; b < Buckets; b++) outLogL[b] += stored[b];

        // keep-survival: opp knows this slot and hasn't replaced it -> mild lean-low.
        if (_oppKnows.Contains(s) && _oppKnownSince.TryGetValue(s, out var since))
        {
            int survived = _oppTurnCount - since;
            if (survived > KeepTurnCap) survived = KeepTurnCap;
            if (survived > 0)
            {
                double a = KeepLogLik * survived;
                for (int v = -1; v <= 10; v++) outLogL[v + 1] += -a * v;
            }
        }

        // global "opp hand running low" lean (opponent slots only).
        if (s.Side == _oppSide && _oppGlobalLowSlope != 0.0)
            for (int v = -1; v <= 10; v++) outLogL[v + 1] += -_oppGlobalLowSlope * v;
    }

    public void Update(GameEffect effect, bool iAmActor)
    {
        switch (effect.Kind)
        {
            case EffectKind.CardDrawn:
                if (!iAmActor)
                {
                    _oppTurnCount++;
                }
                break;
            
            case EffectKind.SlotRevealed:
                // Only learn it if WE looked (LookOwn / LookOpponent)
                if (iAmActor) SetKnow(effect.Slot, effect.Card);
                break;

            case EffectKind.SlotsSwapped:
                if (effect.Slot2.IsNone)
                {
                    // Swap-drawn-into-slot: single slot changed.
                    if (iAmActor)
                    {
                        SetKnow(effect.Slot, effect.Card);   // we know what we placed
                        ClearSlotMeta(effect.Slot);
                    }
                    else
                    {
                        // Opponent kept an (unseen) drawn card, discarding the displaced one.
                        _known.Remove(effect.Slot);
                        ClearSlotMeta(effect.Slot);
                        SetSwapInLikelihood(effect.Slot, effect.Card2.Value);  // Card2 = displaced (public)
                    }
                }
                else
                {
                    SwapKnow(effect.Slot, effect.Slot2);
                    SwapLogL(effect.Slot, effect.Slot2);
                }
                break;

            case EffectKind.MatchResolved:
                if (effect.Slot.IsNone) break;                         // drawn-card match, no slot
                    
                if (effect.Success)
                {
                    _known.Remove(effect.Slot);        // card left the slot
                    ClearSlotMeta(effect.Slot);
                }
                else 
                {
                    SetKnow(effect.Slot, effect.Card);                // failed match reveals it to everyone
                }
                
                break;
            
            case EffectKind.DrawnDiscarded:

                if (!iAmActor)
                {
                    var excess = TypicalValue - effect.Card.Value;   // low discard = strong signal

                    if (excess > 0)
                    {
                        _oppGlobalLowSlope += DiscardSlope * excess;
                        if (_oppGlobalLowSlope > GlobalCap)
                        {
                            _oppGlobalLowSlope = GlobalCap;
                        }
                    }
                }
                
                break;
            
            case EffectKind.InformedTradeReady:
                if (iAmActor)
                {
                    SetKnow(effect.Slot, effect.Card);                 // opponent slot we looked at
                    SetKnow(effect.Slot2, effect.Card2);               // own slot
                    ClearSlotMeta(effect.Slot);
                    ClearSlotMeta(effect.Slot2);
                }
                break;
        }
    }

    /// <summary>Likelihood of the new hidden card given the opponent kept it over a displaced
    /// card worth d: P(kept | value=v) ∝ sigmoid(SwapBeta·(d - v)) — high when v is well below
    /// d — times a small blanket lean-low. Stored as a log-likelihood vector.</summary>
    private void SetSwapInLikelihood(SlotRef s, int displacedValue)
    {
        var vec = new double[Buckets];
        for (int v = -1; v <= 10; v++)
        {
            double keep = Sigmoid(SwapBeta * (displacedValue - v));
            vec[v + 1] = Math.Log(keep + 1e-9) - SwapBias * v;
        }
        _logL[s] = vec;
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    /// <summary>Every active slot of both players the AI is NOT certain of.</summary>
    public List<SlotRef> HiddenSlots(GameState world)
    {
        var hidden = new List<SlotRef>();
        foreach (var side in new[] { GameState.PlayerSide, GameState.AISide })
            foreach (var slot in world.GetActiveSlots(side))
                if (!_known.ContainsKey(slot)) hidden.Add(slot);
        return hidden;
    }

    /// <summary>Ids the AI knows, restricted to still-active slots (excluded from the unseen pool).</summary>
    public List<int> KnowIds(GameState world)
    {
        var ids = new List<int>(_known.Count);
        foreach (var kv in _known)
            if (world.IsActive(kv.Key)) ids.Add(kv.Value.Id);
        return ids;
    }
    
    private void ClearSlotMeta(SlotRef s)
    {
        _logL.Remove(s);
        _oppKnows.Remove(s);
        _oppKnownSince.Remove(s);
    }

    private void SwapLogL(SlotRef a, SlotRef b)
    {
        bool hasA = _logL.TryGetValue(a, out var la);
        bool hasB = _logL.TryGetValue(b, out var lb);
        if (hasB) _logL[a] = lb; else _logL.Remove(a);
        if (hasA) _logL[b] = la; else _logL.Remove(b);

        bool ka = _oppKnows.Contains(a), kb = _oppKnows.Contains(b);
        _oppKnownSince.TryGetValue(a, out var sa);
        _oppKnownSince.TryGetValue(b, out var sb);
        if (kb) { _oppKnows.Add(a); _oppKnownSince[a] = sb; } else ClearOppKnown(a);
        if (ka) { _oppKnows.Add(b); _oppKnownSince[b] = sa; } else ClearOppKnown(b);
    }
    private void ClearOppKnown(SlotRef s) { _oppKnows.Remove(s); _oppKnownSince.Remove(s); }
}