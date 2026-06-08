using System;
using System.Collections.Generic;

/// <summary>
/// AI opponent for Cambio. Intended to become an Information-Set Monte Carlo Tree Search
/// agent with a Bayesian belief layer that makes its hidden-card guesses (and therefore
/// its play) feel human.
///
/// THIS IS A STUB. Nothing here is real ISMCTS yet — it is the skeleton + references so
/// the rest of the game is already shaped correctly around it. Today ChooseMove falls
/// back to a uniform-random legal move so the game is playable end to end. Replace the
/// body of RunIsmcts() and the belief methods to bring it to life; the surface around it
/// (GameState.Clone / LegalMoves / Apply / OverwriteHidden, IAgent, GameManager loop)
/// will not need to change.
///
/// The pipeline this is built for:
///   1. Observe(...)            -> keep beliefs current as cards are seen/moved/declined.
///   2. ChooseMove(state):
///        a. LegalMoves(state)                          (action space)
///        b. for each ISMCTS iteration:
///             - Determinize(): sample a concrete world consistent with beliefs
///             - run one MCTS playout in that determinized clone
///        c. return the most-visited root action
/// </summary>
public class AICambioAgent : IAgent
{
    // --- Tuning knobs (wire these up later) ---
    public int Iterations = 1000;          // ISMCTS rollouts per decision
    public double Exploration = 1.41;      // UCT/UCB1 constant
    public int RandomSeed = 12345;

    private int _mySide;
    private readonly Random _rng;

    // The belief layer. Per hidden slot, a distribution over what card it might be,
    // updated from observations (opening peek, look-powers, swaps, declined matches...).
    private CardBeliefs _beliefs;

    public AICambioAgent(int seed = 12345)
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
        // TODO: seed beliefs with the AI's own opening peek (its first two cards).
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
        // var tree = new IsmctsNode(legal);
        // for (int i = 0; i < Iterations; i++)
        // {
        //     GameState world = Determinize(rootPublic, i);   // sample beliefs
        //     SimulateOnce(world, tree);                       // select/expand/rollout/backprop
        // }
        // return tree.MostVisitedAction();
        throw new NotImplementedException("ISMCTS not implemented yet.");
    }

    /// <summary>
    /// Produce one fully-specified world consistent with what the AI knows. Clones the
    /// public state, samples values for every hidden slot from the beliefs, and writes
    /// them in. Each ISMCTS iteration calls this with a different seed.
    /// </summary>
    private GameState Determinize(GameState publicState, int iteration)
    {
        GameState world = publicState.Clone(RandomSeed + iteration);
        // List<SlotRef> hidden = _beliefs.HiddenSlots();
        // List<int> sample = _beliefs.SampleHidden(world, _rng);   // belief-weighted draw
        // world.OverwriteHidden(hidden, sample);
        return world;
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

/// <summary>
/// Bayesian belief over hidden cards. Stub. The intended representation is, per hidden
/// slot, a probability distribution over remaining card ids/ranks, renormalised as the
/// pool of unseen cards shrinks. Determinization samples from these; observations update
/// them. This is the layer that should make the AI guess like a person rather than an
/// omniscient solver.
/// </summary>
public class CardBeliefs
{
    private readonly int _mySide;
    private readonly int _handSize;
    private readonly int _penaltySize;

    // TODO: e.g. Dictionary<SlotRef, double[ rankOrId ]> distributions;
    //       HashSet<SlotRef> known; (slots the AI has actually seen)

    public CardBeliefs(int mySide, int handSize, int penaltySize)
    {
        _mySide = mySide;
        _handSize = handSize;
        _penaltySize = penaltySize;
    }

    public void Update(GameEffect effect, bool iAmActor)
    {
        // TODO: apply the observation to the distributions (see AICambioAgent.Observe).
    }

    public List<SlotRef> HiddenSlots()
    {
        // TODO: every active slot whose value the AI is not certain of.
        return new List<SlotRef>();
    }

    public List<int> SampleHidden(GameState world, Random rng)
    {
        // TODO: belief-weighted sample of card ids to fill HiddenSlots() + draw pile,
        // drawn from world.UnseenCardIds(known). Uniform for now / when unimplemented.
        return new List<int>();
    }
}
