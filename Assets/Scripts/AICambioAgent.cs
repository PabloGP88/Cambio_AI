using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

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
    public int CambioGuardScore = 10;
    public bool UseCambioGuard = true;

    // On a belief/pool inconsistency, skip that determinization instead of crashing the turn.
    public bool ValidateDeterminizations = true;
    
    // switch from using or not bayerisan
    public bool UseBayesianLayer = true;

    public bool DebugLogging { get => MctsDebug.Enabled; set => MctsDebug.Enabled = value; }
    public int DebugVerbosity { get => MctsDebug.Verbosity; set => MctsDebug.Verbosity = value; }

    private int _mySide;
    private readonly Random _rng;
    private CardBeliefs _beliefs;

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

    /// <summary>Legal moves at the root, minus a too-early Cambio if the guard is on.</summary>
    private List<GameCommand> LegalForSearch(GameState state)
    {
        var legal = state.LegalMoves();

        if (UseCambioGuard && legal.Count > 1 && BelievedOwnScore(state) > CambioGuardScore)
        {
            var filtered = legal.Where(m => m.Type != CommandType.CallCambio).ToList();
            if (filtered.Count > 0) legal = filtered;   // never filter down to zero moves
        }

        if (MctsDebug.At(1))
            MctsDebug.Log(1, $"ChooseMove: side={_mySide} phase={state.Phase} powerStep={state.PowerStep} " +
                             $"legal={legal.Count} known={_beliefs?.Known.Count ?? 0}");
        return legal;
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

    public double CambioShift = 0.25;
    private readonly double[] _ew = new double[12]; 

    private GameState Determinize(GameState publicState, int iteration)
    {
        GameState world = publicState.Clone(RandomSeed + iteration);

        List<SlotRef> hidden = _beliefs.HiddenSlots(world);
        List<int> known = _beliefs.KnowIds(world);

        // TODO(bayesian): replace this uniform pool with a belief-weighted sample.
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
    private const double EvalTargetScore = 14.0;  // AI hand score we treat as "fine"

    private double Evaluate(GameState world)
    {
        if (world.IsTerminal)
        {
            int w = world.WinnerSide();
            if (w == GameState.AISide) return 1.0;
            if (w < 0) return 0.5;   // draw
            return 0.0;
        }

        // Non-terminal leaf: blend (a) am I ahead of the opponent with (b) is my own hand
        // low in absolute terms. (b) makes the search prefer improving its hand over ending
        // the game at an even position.
        int ai = world.Score(GameState.AISide);
        int opp = world.Score(GameState.OpponentOf(GameState.AISide));
        double rel = 0.5 + 0.5 * Math.Tanh((opp - ai) / EvalTempo);
        double abs = 0.5 - 0.5 * Math.Tanh((ai - EvalTargetScore) / EvalTempo);
        return 0.5 * rel + 0.5 * abs;
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

        // Match how EffTilt is called elsewhere: "has the OPPONENT called cambio".
        bool oppCambio = pub.CambioCalled &&
                         (oppSide == GameState.PlayerSide ? pub.PlayerCalledCambio
                                                          : !pub.PlayerCalledCambio);

        var rows = new List<BeliefSlotRow>();
        int knownOwn = 0, knownOpp = 0, hidden = 0;

        foreach (int side in new[] { GameState.PlayerSide, GameState.AISide })
        {
            foreach (var slot in pub.GetActiveSlots(side))
            {
                bool known = _beliefs.Known.ContainsKey(slot);
                if (known) { if (side == _mySide) knownOwn++; else knownOpp++; }
                else hidden++;

                Card truth = pub.GetCard(slot);
                rows.Add(new BeliefSlotRow
                {
                    Slot       = slot,
                    IsOpponent = side != _mySide,
                    Known      = known,
                    OppKnows   = _beliefs.OppKnows(slot),
                    TiltRaw    = _beliefs.TiltFor(slot),
                    TiltEff    = EffTilt(slot, oppSide, oppCambio),
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
    
    private double EffTilt(SlotRef s, int oppSide, bool oppCambio)
    {
        if (!UseBayesianLayer) return 0.0;
        
        double t = _beliefs.TiltFor(s);
        if (oppCambio && s.Side == oppSide) t += CambioShift;
        return t;
    }

    private static int ValueIdx(int cardId) => new Card(cardId).Value + 1;   // -1..10 -> 0..11

    /// <summary>Belief-weighted assignment of hidden slots to distinct pool cards
    /// (weighted sampling without replacement), remainder becomes the draw pile.</summary>
    private void AssignHidden(GameState world, List<SlotRef> hidden, List<int> pool, bool oppCambio)
    {
        int oppSide = GameState.OpponentOf(_mySide);

        // Peaky slots pick from the full pool first: reduces the sequential-WOR bias.
        hidden.Sort((a, b) =>
            Math.Abs(EffTilt(b, oppSide, oppCambio)).CompareTo(
                Math.Abs(EffTilt(a, oppSide, oppCambio))));

        var assigned = new int[hidden.Count];

        for (int k = 0; k < hidden.Count; k++)
        {
            double theta = EffTilt(hidden[k], oppSide, oppCambio);
            int pick;

            if (theta == 0.0 || pool.Count == 1)
            {
                pick = _rng.Next(pool.Count);                       // uniform fast path
            }
            else
            {
                for (int v = -1; v <= 10; v++) _ew[v + 1] = Math.Exp(-theta * v);

                double total = 0;
                for (int i = 0; i < pool.Count; i++) total += _ew[ValueIdx(pool[i])];

                double r = _rng.NextDouble() * total, acc = 0;
                pick = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    acc += _ew[ValueIdx(pool[i])];
                    if (r <= acc) { pick = i; break; }
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

/// <summary>
/// The AI's certain knowledge of card positions. Right now it is binary: a slot is either
/// KNOWN (exact card) or hidden. The Bayesian layer will sit on top, turning "hidden" into a
/// distribution and feeding a weighted sample into AICambioAgent.Determinize.
/// </summary>
public class CardBeliefs
{
    private readonly int _mySide;
    private readonly int _handSize;
    private readonly int _penaltySize;
    
    
    // 0.03
    // 6
    // 0.10
    // 0.02
    // 0.02
    // 6.0
    // 0.6
    

    public double KeepAlpha      = 0.03;   // per surviving opp turn, known-and-kept slot
    public int    KeepTurnCap    = 6;
    public double SwapInBase     = 0.10;   // base low-tilt when opp keeps a hidden draw
    public double DisplacedAlpha = 0.02;   // per point of the card they threw away
    public double DiscardAlpha   = 0.02;   // per point below typical of a plain discard
    public double TypicalValue   = 6.0;    // ~pool mean
    public double GlobalCap      = 0.6;   // clamp on accumulated global shift


    private readonly int _oppSide;
    private readonly Dictionary<SlotRef, double> _tilt = new();
    private readonly HashSet<SlotRef> _oppKnows = new();
    private readonly Dictionary<SlotRef, int> _oppKnownSince = new();
    private int _oppTurnCount;
    private double _oppGlobalTilt;

    private readonly Dictionary<SlotRef, Card> _known = new();
    
    // Stats for graph
    public double OppGlobalTilt => _oppGlobalTilt;
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
                        _known.Remove(effect.Slot);
                        ClearSlotMeta(effect.Slot);
                        _tilt[effect.Slot] = SwapInBase + DisplacedAlpha * effect.Card2.Value;
                    }
                }
                else
                {
                    SwapKnow(effect.Slot, effect.Slot2);
                    SwapTilt(effect.Slot, effect.Slot2);
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
                        _oppGlobalTilt += DiscardAlpha * excess;
                        if (_oppGlobalTilt > GlobalCap)
                        {
                            _oppGlobalTilt = GlobalCap;
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
    
    public double TiltFor(SlotRef s)
    {
        double theta = _tilt.TryGetValue(s, out var t) ? t : 0.0;
        if (s.Side == _oppSide) theta += _oppGlobalTilt;

        if (_oppKnows.Contains(s) && _oppKnownSince.TryGetValue(s, out var since))
        {
            int survived = _oppTurnCount - since;
            if (survived > KeepTurnCap) survived = KeepTurnCap;
            if (survived > 0) theta += KeepAlpha * survived;
        }
        return theta;
    }
    
    private void ClearSlotMeta(SlotRef s)
    {
        _tilt.Remove(s);
        _oppKnows.Remove(s);
        _oppKnownSince.Remove(s);
    }

    private void SwapTilt(SlotRef a, SlotRef b)
    {
        bool hasA = _tilt.TryGetValue(a, out var ta);
        bool hasB = _tilt.TryGetValue(b, out var tb);
        if (hasB) _tilt[a] = tb; else _tilt.Remove(a);
        if (hasA) _tilt[b] = ta; else _tilt.Remove(b);

        bool ka = _oppKnows.Contains(a), kb = _oppKnows.Contains(b);
        _oppKnownSince.TryGetValue(a, out var sa);
        _oppKnownSince.TryGetValue(b, out var sb);
        if (kb) { _oppKnows.Add(a); _oppKnownSince[a] = sb; } else ClearOppKnown(a);
        if (ka) { _oppKnows.Add(b); _oppKnownSince[b] = sa; } else ClearOppKnown(b);
    }
    private void ClearOppKnown(SlotRef s) { _oppKnows.Remove(s); _oppKnownSince.Remove(s); }
}

