using System;
using System.Collections;
using System.Collections.Generic;

public partial class AICambioAgent : IAgent
{

    public int Iterations = 4000;
    public double Exploration = 1.41;
    public int RandomSeed = 12345;
    
    public int RolloutPlyCap = 0;


    public int IterationsPerYield = 0;
    
    public int CambioGuardScore = 10;
    public bool UseCambioGuard = true;


    public double CambioMargin     = 2.0;   
    public double CambioConfidence = 0.60;  


    public double CambioShift = 0.25;

    // on a belief or pool inconsistency, skip that determinization instead of crashing the turn
    public bool ValidateDeterminizations = true;

    // switch the belief layer on or off
    public bool UseBayesianLayer = true;

    // flat per-unknown own-slot value prior used by the baseline guard and belief reporting
    public double UnknownOwnPrior = 5.889;

    public bool DebugLogging { get => MctsDebug.Enabled; set => MctsDebug.Enabled = value; }
    public int DebugVerbosity { get => MctsDebug.Verbosity; set => MctsDebug.Verbosity = value; }

    private int _mySide;
    private readonly Random _rng;
    private CardBeliefs _beliefs;

    // per-decision stats surfaced through the reports
    private bool   _guardEvaluated;
    private double _guardMeanOwn, _guardMeanOpp, _guardPAhead;

    private int _nodesExpandedThisSearch;
    private int _failedDeterminizations;

    // scratch buffers, single-threaded and reused within a decision; index = Value + 1
    private readonly double[] _ew      = new double[12];  // exp(logL) weight buffer
    private readonly double[] _logLbuf = new double[12];  // scratch log-likelihood vector
    private readonly double[] _poolHist = new double[12]; // unseen-pool value histogram

    public event Action<IsmctsReport> OnSearchDecision;
    public event Action<BeliefReport> OnBeliefSnapshot;

    public AICambioAgent(int seed)
    {
        RandomSeed = seed;
        _rng = new Random(seed);
    }

    // IAgent

    public void OnNewGame(int mySide, GameState initialState)
    {
        _mySide = mySide;
        _beliefs = new CardBeliefs(mySide, initialState.HandSize, initialState.PenaltySize);

        // the AI peeks its first two hand cards, exactly like the human's opening peek
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

    // public decision helpers

    /* if we're certain of one of our own active cards whose rank matches the top discard,
       return that slot so the caller can snap it; otherwise SlotRef.None */
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
