using System;
using System.Collections.Generic;

public sealed class Node
{
    public readonly GameCommand Action;
    public readonly Node parent;
    public readonly Dictionary<GameCommand, Node> children = new();

    public int visits;
    public int avail;
    public double reward;

    public Node(GameCommand action, Node parent)
    {
        this.Action = action;
        this.parent = parent;
    }
}

public class AICambioAgent : IAgent
{
    // --- Tuning knobs (wire these up later) ---
    public int Iterations = 1000;          // ISMCTS rollouts per decision
    public double Exploration = 1.41;      // UCT/UCB1 constant
    public int RandomSeed = 12345;
    public bool ValidateDeterminizations = true;

    private int _mySide;
    private readonly Random _rng;

    // The belief layer. Per hidden slot, a distribution over what card it might be,
    // updated from observations (opening peek, look-powers, swaps, declined matches...).
    private CardBeliefs _beliefs;

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
    }

    public GameCommand ChooseMove(GameState publicState)
    {
        var legal = publicState.LegalMoves();
        if (legal.Count == 0) return default;

        // --- TEMPORARY placeholder so the game runs end to end. DELETE once RunIsmcts works. ---
        // Prefer non-match moves so the stub doesn't spam failed matches (and penalties)
        // while you're testing; it just draws/plays through its turn at random.
        var safe = legal.FindAll(m => m.Type != CommandType.AttemptMatch);
        var pool = safe.Count > 0 ? safe : legal;
        return pool[_rng.Next(pool.Count)];

        // --- Target implementation (uncomment & finish): ---
        // return RunIsmcts(publicState, legal);
    }

    // ----------------------------------------------------------------------
    // ISMCTS scaffold (all TODO)
    // ----------------------------------------------------------------------

    private GameCommand RunIsmcts(GameState rootPublic, List<GameCommand> legal)
    {
         var root = new Node(default, null);
        
         for (int i = 0; i < Iterations; i++)
         {
             GameState world = Determinize(rootPublic, i);     // sample beliefs
             SimulateOnce(world, root);                       // select/expand/rollout/backprop
         }
         
         return MostVisited(root, legal);
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

        return best != null ? best.Action : legalAtRoot[_rng.Next(legalAtRoot.Count)];
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
        
        List<int> slotsId = pool.GetRange(0, known.Count);
        world.OverwriteHidden(hidden, slotsId);
        
        List<int> pileIds = pool.GetRange(hidden.Count, pool.Count - known.Count);
        world.SetDrawPile(pileIds);

        if (ValidateDeterminizations && !world.IsCardSetWorking())
        {
            throw new InvalidOperationException(
                $"Determinized world inconsistent — belief/pool leak. " +
                $"hidden={hidden.Count}, pool={pool.Count}, known={known.Count}, " +
                $"pile={pileIds.Count}, realPile={publicState.DrawPileCount}");
        }
        
        // world.OverwriteHidden(hidden, sample);
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
    private void SimulateOnce(GameState world, /*IsmctsNode*/ object node)
    {
        // Standard MCTS loop on the determinized world:
        //   Selection  : descend by UCB1 over actions legal in THIS determinization.
        //   Expansion  : add an unexpanded child.
        //   Rollout    : play to terminal with a light policy (random or heuristic).
        //   Backprop   : update visit/value stats keyed by the *observable* action,
        //                so statistics pool across determinizations (the ISMCTS trick).
        throw new NotImplementedException();
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
                    SetKnow(effect.Slot2, effect.Card);
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
