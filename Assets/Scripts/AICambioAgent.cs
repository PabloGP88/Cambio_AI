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
        this.Action = action;
        this.parent = parent;
        this.Depth = parent == null ? 0 : parent.Depth + 1;
    }
}

/// <summary>One row of the root-level move table: a legal move and the stats its node
/// accumulated during search. This is the structured replacement for the old
/// "throw a canned phrase" AI sign — it's what the UI actually renders.</summary>
public struct MoveStat
{
    public GameCommand Move;
    public int Visits;
    public double AvgReward;
    public int Avail;
    public bool IsChosen;
}

/// <summary>A snapshot of the ISMCTS root, either mid-search (IsFinal = false, fired
/// periodically via OnSearchProgress) or after the decision has been made (IsFinal = true,
/// fired once via OnSearchDecision, with exactly one MoveStat flagged IsChosen).</summary>
public class IsmctsReport
{
    public int Side;
    public int IterationsDone;
    public int IterationsTarget;
    public long ElapsedMs;
    public int RootVisits;
    public int NodesExpanded;      // total tree nodes created so far this search
    public int ExpandedRootMoves;  // how many legal root moves have a node at all
    public int LegalCount;
    public List<MoveStat> Moves;   // sorted descending by visits
    public bool IsFinal;
}

/// <summary>
/// Centralized, leveled debug logging for the ISMCTS agent so it can be dialed up/down
/// without scattering `#if` blocks everywhere. Levels:
///   0 Off         - nothing.
///   1 Decision    - one summary line per ChooseMove call, plus the final children table.
///   2 Expansion   - + determinize stats and every tree expansion (new node created).
///   3 Verbose     - + every rollout's outcome and every selection step's UCB pick.
/// This is now a secondary channel (Console) — the primary "what is Ben thinking" channel
/// for the player is IsmctsReport via OnSearchProgress / OnSearchDecision.
/// </summary>
public static class MctsDebug
{
    public static bool Enabled = true;
    public static int Verbosity = 1; // 0..3, see class doc above
    private const string Tag = "[ISMCTS]";

    public static bool At(int level) => Enabled && Verbosity >= level;

    public static void Log(int level, string msg)
    {
        if (At(level)) UnityEngine.Debug.Log($"{Tag} {msg}");
    }

    public static void LogWarning(string msg) => UnityEngine.Debug.LogWarning($"{Tag} {msg}");
}

public class AICambioAgent : IAgent
{
    // --- Tuning knobs (wire these up later) ---
    public int Iterations = 5000;          // ISMCTS rollouts per decision
    public double Exploration = 1.41;      // UCT/UCB1 constant
    public int RandomSeed = 12345;
    public bool ValidateDeterminizations = true;

    // How many iterations run between live progress snapshots. Lower = smoother-looking
    // "thinking" animation but more UI churn; higher = choppier but cheaper. With the
    // default Iterations=5000 this gives 25 snapshots (25 rendered frames) per decision.
    public int ProgressReportInterval = 200;

    // --- Debug knobs --- set via MctsDebug.Enabled / MctsDebug.Verbosity (static, so any
    // caller — GameManager, a debug menu, a unit test — can flip it without a reference to
    // this agent instance). See MctsDebug's class doc for what each level shows.
    public bool DebugLogging
    {
        get => MctsDebug.Enabled;
        set => MctsDebug.Enabled = value;
    }
    public int DebugVerbosity
    {
        get => MctsDebug.Verbosity;
        set => MctsDebug.Verbosity = value;
    }

    private int _mySide;
    private readonly Random _rng;

    public int RolloutPlyCap = 0;
    public int CambioGuardScore = 15;
    public bool UseCambioGuard = true;

    // The belief layer. Per hidden slot, a distribution over what card it might be,
    // updated from observations (opening peek, look-powers, swaps, declined matches...).
    private CardBeliefs _beliefs;

    private int _nodesExpandedThisSearch;

    public event Action<IsmctsReport> OnSearchProgress;
    public event Action<IsmctsReport> OnSearchDecision;

    public AICambioAgent(int seed)
    {
        RandomSeed = seed;
        _rng = new Random(seed);
    }

    // ----------------------------------------------------------------------
    // IAgent
    // ----------------------------------------------------------------------

    public void OnNewGame(int mySide, GameState initialState)
    {
        _mySide = mySide;
        _beliefs = new CardBeliefs(mySide, initialState.HandSize, initialState.PenaltySize);

        var slolt0 = new SlotRef(mySide, Zone.Hand, 0);
        var slolt1 = new SlotRef(mySide, Zone.Hand, 1);

        _beliefs.SetKnow(slolt0, initialState.GetCard(slolt0));
        _beliefs.SetKnow(slolt1, initialState.GetCard(slolt1));

    }

    public void Observe(GameEffect effect, bool iAmActor)
    {
        // TODO: this is where "human" play comes from. Each observation tightens or
        // shifts the distributions. Examples to implement:
        //   - SlotRevealed & iAmActor      -> that slot is now KNOWN.
        //   - CardDrawn & iAmActor         -> the drawn card is known to us this turn.
        //   - MatchResolved (success)      -> matched card's rank now public via discard.
        //   - SlotsSwapped                 -> move belief mass between the two slots.
        //   - opponent DECLINES to swap a drawn high card -> Bayesian update that their
        //     swapped-out slot was probably low, etc. (the "feels human" signal).
        _beliefs?.Update(effect, iAmActor);

        MctsDebug.Log(2,
            $"Observe: {effect.Kind,-18} iAmActor={iAmActor,-5} slot={effect.Slot} slot2={effect.Slot2} " +
            $"card={(effect.Card.IsNone ? "?" : effect.Card.Id.ToString())} success={effect.Success}");
    }

    private double BelievedOwnScore(GameState pub)
    {
        double score = 0;
        int unknown = 0;
        foreach (var slot in pub.GetActiveSlots(_mySide))
        {
            if (_beliefs.Known.TryGetValue(slot, out var c))
            {
                score += c.Value;
            } else unknown++;
        }

        return score + unknown * 5.0;   // assume 5 - ish per unknown own card
    }

    public GameCommand ChooseMove(GameState publicState)
    {
        var legal = publicState.LegalMoves();

        if (UseCambioGuard && BelievedOwnScore(publicState) > CambioGuardScore)
        {
            legal = legal.Where(m => m.Type != CommandType.CallCambio).ToList();
        }

        MctsDebug.Log(1,
            $"ChooseMove: side={_mySide} phase={publicState.Phase} powerStep={publicState.PowerStep} " +
            $"legalMoves={legal.Count} knownSlots={_beliefs?.Known.Count ?? 0}");

        var chosen = legal.Count switch
        {
            0 => default,
            1 => legal[0],
            _ => RunIsmcts(publicState, legal)
        };

        if (legal.Count <= 1)
            MctsDebug.Log(1, $"ChooseMove: only {legal.Count} legal move(s) -> {chosen} (no search run)");

        return chosen;
    }

    /// <summary>Incremental version of ChooseMove: runs the search ProgressReportInterval
    /// iterations at a time, yielding a frame between chunks and firing OnSearchProgress
    /// with a live snapshot, so a coroutine caller can actually show the tree growing on
    /// screen instead of freezing for the whole search.</summary>
    public IEnumerator ChooseMoveRoutine(GameState publicState, Action<GameCommand> onDecided)
    {
        var legal = publicState.LegalMoves();

        if (UseCambioGuard && BelievedOwnScore(publicState) > CambioGuardScore)
        {
            legal = legal.Where(m => m.Type != CommandType.CallCambio).ToList();
        }

        MctsDebug.Log(1,
            $"ChooseMove: side={_mySide} phase={publicState.Phase} powerStep={publicState.PowerStep} " +
            $"legalMoves={legal.Count} knownSlots={_beliefs?.Known.Count ?? 0}");

        if (legal.Count <= 1)
        {
            var only = legal.Count == 1 ? legal[0] : default;
            MctsDebug.Log(1, $"ChooseMove: only {legal.Count} legal move(s) -> {only} (no search run)");
            onDecided(only);
            yield break;
        }

        var root = new Node(default, null);
        _nodesExpandedThisSearch = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < Iterations; i++)
        {
            GameState world = Determinize(publicState, i);
            SimulateOnce(world, root, i);

            if ((i + 1) % ProgressReportInterval == 0 || i == Iterations - 1)
            {
                OnSearchProgress?.Invoke(BuildReport(root, legal, sw.ElapsedMilliseconds, i + 1, null));
                yield return null; // let Unity render this snapshot before the next chunk
            }
        }

        sw.Stop();
        var chosen = MostVisited(root, legal);
        var finalReport = BuildReport(root, legal, sw.ElapsedMilliseconds, Iterations, chosen);
        OnSearchDecision?.Invoke(finalReport);

        if (MctsDebug.At(1)) LogTreeSummary(root, legal, chosen, sw.ElapsedMilliseconds);

        onDecided(chosen);
    }

    // ----------------------------------------------------------------------
    // ISMCTS scaffold (all TODO)
    // ----------------------------------------------------------------------

    private GameCommand RunIsmcts(GameState rootPublic, List<GameCommand> legal)
    {
         var root = new Node(default, null);
         _nodesExpandedThisSearch = 0;
         var sw = MctsDebug.At(1) ? System.Diagnostics.Stopwatch.StartNew() : null;

         for (int i = 0; i < Iterations; i++)
         {
             GameState world = Determinize(rootPublic, i);     // sample beliefs
             SimulateOnce(world, root, i);                     // select/expand/rollout/backprop
         }

         var chosen = MostVisited(root, legal);

         if (MctsDebug.At(1))
         {
             sw.Stop();
             LogTreeSummary(root, legal, chosen, sw.ElapsedMilliseconds);
         }

         return chosen;
    }

    private GameCommand MostVisited(Node root, List<GameCommand> legalAtRoot)
    {
        Node best = null;

        foreach (var move in legalAtRoot)
        {
            if (root.children.TryGetValue(move, out var child))
            {
                if (best == null || child.visits > best.visits)
                {
                    best = child;
                }
            }
        }

        if (best == null)
        {
            // Nothing ever got expanded (e.g. Iterations == 0) — fall back to random and
            // make sure that's loud, since it usually means a config problem, not real play.
            MctsDebug.LogWarning($"MostVisited: root has 0 expanded children out of {legalAtRoot.Count} legal moves — picking randomly.");
            return legalAtRoot[_rng.Next(legalAtRoot.Count)];
        }

        return best.Action;
    }

    /// <summary>Builds the structured snapshot used by both the live progress feed and the
    /// final decision report. `chosen` is null for a mid-search snapshot, and set once the
    /// search has picked a move (which flags that move's row IsChosen and marks IsFinal).</summary>
    private IsmctsReport BuildReport(Node root, List<GameCommand> legalAtRoot, long elapsedMs, int iterationsDone, GameCommand? chosen)
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
                    IsChosen = chosen.HasValue && move.Equals(chosen.Value)
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
            IsFinal = chosen.HasValue
        };
    }

    /// <summary>Dumps every root child (legal move) sorted by visit count, so you can see
    /// what the search actually explored and why it picked what it picked. Console-only
    /// mirror of the final IsmctsReport, kept for headless/log-based debugging.</summary>
    private void LogTreeSummary(Node root, List<GameCommand> legalAtRoot, GameCommand chosen, long elapsedMs)
    {
        var entries = new List<Node>();
        foreach (var move in legalAtRoot)
            if (root.children.TryGetValue(move, out var child))
                entries.Add(child);
        entries.Sort((a, b) => b.visits.CompareTo(a.visits));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[ISMCTS] === ChooseMove result (side={_mySide}, {Iterations} iters in {elapsedMs}ms) ===");
        sb.AppendLine($"[ISMCTS] root visits={root.visits}  expanded {entries.Count}/{legalAtRoot.Count} legal moves");

        foreach (var node in entries)
        {
            string mark = node.Action.Equals(chosen) ? "  <== CHOSEN" : "";
            sb.AppendLine($"[ISMCTS]   {node.Action,-30} visits={node.visits,4}  avgReward={node.AvgReward:F3}  avail={node.avail}{mark}");
        }

        int unexpanded = legalAtRoot.Count - entries.Count;
        if (unexpanded > 0)
            sb.AppendLine($"[ISMCTS]   ({unexpanded} legal move(s) never got a single visit — raise Iterations if this is large)");

        UnityEngine.Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Produce one fully-specified world consistent with what the AI knows. Clones the
    /// public state, samples values for every hidden slot from the beliefs, and writes
    /// them in. Each ISMCTS iteration calls this with a different seed.
    /// </summary>
    private GameState Determinize(GameState publicState, int iteration)
    {
        GameState world = publicState.Clone(RandomSeed + iteration);

        List<SlotRef> hidden = _beliefs.HiddenSlots(world);
        List<int> known = _beliefs.KnowIds(world);

        List<int> pool = world.UnseenCardIds(known);

        Shuffle(pool);

        List<int> slotsId = pool.GetRange(0, hidden.Count);
        world.OverwriteHidden(hidden, slotsId);

        List<int> pileIds = pool.GetRange(hidden.Count, pool.Count - hidden.Count);
        world.SetDrawPile(pileIds);

        if (ValidateDeterminizations && !world.IsCardSetWorking())
        {
            throw new InvalidOperationException(
                $"Determinized world inconsistent — belief/pool leak. " +
                $"hidden={hidden.Count}, pool={pool.Count}, known={known.Count}, " +
                $"pile={pileIds.Count}, realPile={publicState.DrawPileCount}");
        }

        MctsDebug.Log(2,
            $"iter={iteration} determinize: hidden={hidden.Count} known={known.Count} " +
            $"pool={pool.Count} pile={pileIds.Count}");


        return world;
    }
    private void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
    private void SimulateOnce(GameState world, Node root, int iteration)
    {

        Node node = root;
        var path = new List<Node> { root };

        //Selection and expansion

        while (!world.IsTerminal)
        {
            List<GameCommand> legal = world.LegalMoves();

            if (legal.Count == 0)
            {
                MctsDebug.Log(3, $"iter={iteration} depth={node.Depth} SAFETY BREAK: 0 legal moves mid-tree");
                break; // safety break
            }

            int side = world.ActiveSide;

            GameCommand? untried = FirstUntried(node, legal);

            if (untried.HasValue)
            {
                // EXPANSION

                world.Apply(untried.Value);
                var child = new Node(untried.Value, node);
                _nodesExpandedThisSearch++;

                node.children[untried.Value] = child;
                path.Add(child);
                node = child;

                MctsDebug.Log(2,
                    $"iter={iteration} EXPAND  depth={child.Depth,2} side={side} action={untried.Value} " +
                    $"(sibling {node.parent.children.Count}/{legal.Count} tried)");

                break;
            }

            // Selection
            Node chosen = null;
            double bestUcp = double.NegativeInfinity;

            foreach (var move in legal)
            {
                Node c = node.children[move];
                c.avail++;
                double u = Ucb(c, side);

                if (u > bestUcp)
                {
                    bestUcp = u;
                    chosen = c;
                }
            }

            MctsDebug.Log(3,
                $"iter={iteration} SELECT  depth={node.Depth,2} side={side} -> {chosen.Action} " +
                $"(ucb={bestUcp:F3}, visits={chosen.visits}, avgReward={chosen.AvgReward:F3})");

            world.Apply(chosen.Action);
            path.Add(chosen);
            node = chosen;

        }

        // Rollout

        double reward = Rollout(world, iteration, node.Depth);

        // Backpropa

        foreach (var n in path)
        {
            n.visits++;
            n.reward += reward;
        }

        MctsDebug.Log(3,
            $"iter={iteration} BACKPROP reward={reward:F3} across {path.Count} node(s), " +
            $"leaf action={path[^1].Action} leaf depth={path[^1].Depth}");

    }

    private GameCommand? FirstUntried(Node node, List<GameCommand> legal)
    {
        foreach (var move in legal)
            if (!node.children.ContainsKey(move)) return move;
        return null;
    }

    private double Ucb(Node child, int chooser)
    {
        double exploit = child.reward / child.visits;

        // The opponent picks the move WORST for the AI = best for itself.
        if (chooser != GameState.AISide) exploit = 1.0 - exploit;
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

        MctsDebug.Log(3,
            $"iter={iteration} ROLLOUT from treeDepth={startDepth} ran {plies} random plies, " +
            $"terminal={world.IsTerminal} scoreP={world.Score(GameState.PlayerSide)} scoreAI={world.Score(GameState.AISide)} " +
            $"-> reward={result:F3}");

        return result;
    }

    private double Evaluate(GameState world)
    {
        if (world.IsTerminal)
        {
            int w = world.WinnerSide();
            if (w == GameState.AISide) return 1.0;
            if (w < 0) return 0.5;            // draw
            return 0.0;
        }

        // Non-terminal leaf: blend (a) am I ahead of the opponent, with
        // (b) is my OWN hand low in absolute terms. (b) is what makes the search
        // prefer improving the hand over ending the game at an even position.

        int ai  = world.Score(GameState.AISide);
        int opp = world.Score(GameState.OpponentOf(GameState.AISide));
        double rel = 0.5 + 0.5 * Math.Tanh((opp - ai) / 8.0);
        double abs = 0.5 - 0.5 * Math.Tanh((ai - 14) / 8.0);

        return 0.5 * rel + 0.5 * abs;
    }

}


public class CardBeliefs
{
    private readonly int _mySide;
    private readonly int _handSize;
    private readonly int _penaltySize;

    private readonly Dictionary<SlotRef, Card> _known = new();


    public CardBeliefs(int mySide, int handSize, int penaltySize)
    {
        _mySide = mySide;
        _handSize = handSize;
        _penaltySize = penaltySize;

    }

    public void SetKnow(SlotRef s, Card card)
    {
        if  (s.IsNone || card.IsNone) return;

        _known[s] = card;
    }

    public void SwapKnow(SlotRef s0, SlotRef s1)
    {
        bool knownA = _known.TryGetValue(s0, out var cardA);
        bool knownB = _known.TryGetValue(s1, out var cardB);

        if (knownA)
        {
            _known[s1] = cardA;
        }
        else
        {
            _known.Remove(s1);
        }

        if (knownB)
        {
            _known[s0] = cardB;
        }
        else
        {
            _known.Remove(s0);
        }
    }
    public IReadOnlyDictionary<SlotRef, Card> Known => _known;
    public void Update(GameEffect effect, bool iAmActor)
    {
        // TODO: apply the observation to the distributions (see AICambioAgent.Observe).

        switch (effect.Kind)
        {
            case EffectKind.SlotRevealed:
                if (iAmActor)
                {
                    SetKnow(effect.Slot, effect.Card);
                }
                break;
            case EffectKind.SlotsSwapped:
                if (effect.Slot2.IsNone)
                {
                    if (iAmActor)
                    {
                        SetKnow(effect.Slot, effect.Card);
                    } else _known.Remove(effect.Slot);
                }
                else
                {
                    SwapKnow(effect.Slot, effect.Slot2);
                }
                break;
            case EffectKind.MatchResolved:
                if (effect.Slot.IsNone) break;

                if (effect.Success)
                {
                    _known.Remove(effect.Slot);
                }
                else
                {
                    SetKnow(effect.Slot, effect.Card);
                }
                break;
            case EffectKind.InformedTradeReady:
                if (iAmActor)
                {
                    SetKnow(effect.Slot, effect.Card);
                    SetKnow(effect.Slot2, effect.Card2);
                }
                break;
        }
    }

    /// <summary>Every active slot of both players that the AI is NOT certain of.</summary>

    public List<SlotRef> HiddenSlots(GameState world)
    {
        var hidden = new List<SlotRef>();

        foreach (var side in new[]
                 {
                     GameState.PlayerSide, GameState.AISide
                 })
        {
            foreach (var slot in world.GetActiveSlots(side))
            {
                if (!_known.ContainsKey(slot))
                {
                    hidden.Add(slot);
                }
            }
        }

        return hidden;
    }

    /// <summary>Ids the AI knows, restricted to still-active slots. Excluded from the unseen .</summary>
    public List<int> KnowIds(GameState world)
    {
        var ids = new List<int>(_known.Count);

        foreach (var key in _known)
        {
            if (world.IsActive(key.Key))
            {
                ids.Add(key.Value.Id);
            }
        }

        return ids;
    }

    public List<int> SampleHidden(GameState world, Random rng)
    {
        // TODO: belief-weighted sample of card ids to fill HiddenSlots() + draw pile,
        // drawn from world.UnseenCardIds(known). Uniform for now / when unimplemented.
        return new List<int>();
    }
}