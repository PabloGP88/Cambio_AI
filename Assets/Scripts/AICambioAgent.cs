using System;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Belief-guided ISMCTS agent for Cambio. This file holds the agent's public surface:
/// configuration, per-search state, the IAgent lifecycle
/// (OnNewGame / Observe / ChooseMove / ChooseMoveRoutine) and the SnapOwn helper.
/// The rest is split across partials by concern:
///   AICambioAgent.Search.cs       tree policy — iterate, select, expand, rollout, evaluate
///   AICambioAgent.Determinize.cs  belief-weighted world sampling per iteration
///   AICambioAgent.Cambio.cs       the "don't call Cambio too early" guard (baseline + Bayesian)
///   AICambioAgent.Reporting.cs    IsmctsReport / BeliefReport builders + console tree dumps
/// Pure helpers live in CambioMath; belief bookkeeping in CardBeliefs.
/// </summary>
public partial class AICambioAgent : IAgent
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
    // and instead compares believed own vs opponent score distributions (see AICambioAgent.Cambio.cs).
    public int CambioGuardScore = 10;
    public bool UseCambioGuard = true;

    // --- Bayesian Cambio guard (only used when UseBayesianLayer is true) ---
    // We permit CallCambio only when we believe we finish ahead by at least CambioMargin points,
    // with probability >= CambioConfidence. Calling ends our turn and hands the opponent exactly
    // one more turn, so CambioMargin also absorbs the improvement a competent opponent squeezes
    // from that final draw (don't call when only marginally ahead — they can catch up).
    public double CambioMargin     = 2.0;   // points of believed lead required (own < opp - margin)
    public double CambioConfidence = 0.60;  // P(we're ahead by the margin) needed to allow the call

    // Extra low-lean applied to the opponent's slots once THEY have called cambio: you rarely
    // call from behind, so their hand is probably low. Applied as a log-linear likelihood
    // factor exp(-CambioShift * value) on top of whatever CardBeliefs already believes.
    public double CambioShift = 0.25;

    // On a belief/pool inconsistency, skip that determinization instead of crashing the turn.
    public bool ValidateDeterminizations = true;

    // Switch the belief layer on/off (baseline = uniform determinizer, flat guard).
    public bool UseBayesianLayer = true;

    // Flat per-unknown own-slot value prior used by the baseline guard and belief reporting.
    public double UnknownOwnPrior = 5.889;

    public bool DebugLogging { get => MctsDebug.Enabled; set => MctsDebug.Enabled = value; }
    public int DebugVerbosity { get => MctsDebug.Verbosity; set => MctsDebug.Verbosity = value; }

    private int _mySide;
    private readonly Random _rng;
    private CardBeliefs _beliefs;

    // --- Per-decision stats surfaced through the reports ---
    private bool   _guardEvaluated;
    private double _guardMeanOwn, _guardMeanOpp, _guardPAhead;

    private int _nodesExpandedThisSearch;
    private int _failedDeterminizations;

    // --- Scratch buffers (single-threaded; reused within a decision) ---
    private readonly double[] _ew      = new double[12];  // exp(logL) weight buffer, index = Value+1
    private readonly double[] _logLbuf = new double[12];  // scratch log-likelihood vector
    private readonly double[] _poolHist = new double[12]; // unseen-pool value histogram

    public event Action<IsmctsReport> OnSearchDecision;
    public event Action<BeliefReport> OnBeliefSnapshot;

    public AICambioAgent(int seed)
    {
        RandomSeed = seed;
        _rng = new Random(seed);
    }

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

    // ---------------------------------------------------------------- Public decision helpers

    /// <summary>If we're certain of one of our own active cards whose rank matches the top
    /// discard, return that slot so the caller can snap it; otherwise SlotRef.None.</summary>
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
}
