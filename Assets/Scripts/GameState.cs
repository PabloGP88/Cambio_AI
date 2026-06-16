using System;
using System.Collections.Generic;
using System.Linq;

public enum GamePhase
{
    Dealing,
    DrawingCard,
    CardDrawn,
    SelectingSwapSlot,
    DiscardingDrawn,   // transient (never rests here)
    UsingPower,
    CambioCalled,      // transient
    GameOver
}

public enum PowerStep
{
    None,
    LookingOwn,
    LookingOpponent,
    SelectingPowerSwapSource,
    SelectingPowerSwapTarget,
    SelectingTradeOpponent,
    SelectingTradeOwn,
    ConfirmingTrade
}

/// <summary>Observable consequence of applying a command. Drives view events + AI observation.</summary>
public enum EffectKind
{
    CardDrawn,           // Side = drawer, Card = drawn card, Bool1 = actorIsPlayer
    SlotRevealed,        // Slot (Side/Index/Zone), Card, Bool1 = actorIsPlayer (who learned it)
    MatchResolved,       // Slot, Card, Bool1 = success, Bool2 = byPlayer
    PenaltyAdded,        // Slot (the new penalty slot), Card, Bool1 = forPlayer
    SlotsSwapped,        // Slot = a, Slot2 = b
    GiveDone,
    InformedTradeReady,  // Card = opponent card, Card2 = own card
    GameOver
}

public struct GameEffect
{
    public EffectKind Kind;
    public SlotRef Slot;
    public SlotRef Slot2;
    public Card Card;
    public Card Card2;
    public bool Success;
    public bool ByPlayer;
}

public struct MoveResult
{
    public bool Ok;
    public List<GameEffect> Effects;
    public static MoveResult Fail() => new MoveResult { Ok = false, Effects = null };
}

/// <summary>
/// The entire game of Cambio as plain data + pure logic. Knows nothing about Unity,
/// sprites, clicks, or animation. It is:
///   - the single source of truth for the live game (owned by GameManager), and
///   - the unit the AI clones, determinizes, and steps during DISMASTS search.
///
/// Design contract for the AI:
///   * Apply() and LegalMoves() depend ONLY on public structure (phase, which slots
///     are active, counts, the visible discard top). They never branch on a hidden
///     card's *value*. So the AI may safely call LegalMoves() on the real state.
///   * Hidden card *values* must come from the AI's belief layer via Determinize().
///     The AI must not read face-down card values out of the live state — that's
///     cheating. (The masking helper AsInformationSet() makes this enforceable.)
/// </summary>
public class GameState
{
    public const int PlayerSide = 0;
    public const int AISide = 1;
    
    public int HandSize
    {
        get; 
        private set;
    }

    public int PenaltySize
    {
        get; 
        private set;
    }

    // The game, the layout
    private Card[][] _hand;       // [side][index]
    private Card[][] _penalty;    // [side][index]
    private List<Card> _drawPile; // end of list = top of deck
    private List<Card> _discard;  // end of list = top of discard

    // The current state of the game
    private GamePhase _phase;
    private PowerStep _powerStep;
    private bool _isPlayerTurn;
    private Card _drawn; // card just picked up
    private CardPower _activePower; // if there's a power, which one is currently being used

    
    // Endgame to set final turn
    private bool _cambioCalled;
    private int _cambioCallerSide;
    private int _finalRoundTurnsLeft;
    
    // flags
    private bool _matchedThisTurn;
    private bool _awaitingGiveCard;
    private SlotRef _matchReceiver;     // opponent slot to be filled by the giver
    private bool _awaitingPeekConfirm;  // a look-power peeked; only FinishPeeking is legal now
    
    private SlotRef _powerSource;
    private SlotRef _tradeOpponent;
    private SlotRef _tradeOwn;

    private Random _rng; // have random shuffle each time

    // Public reads for the AI and the PLayerInput, this is so they dont know the actual value of the cards
    public GamePhase Phase => _phase;
    public PowerStep PowerStep => _powerStep;
    public bool IsPlayerTurn => _isPlayerTurn;
    public int ActiveSide => _isPlayerTurn ? PlayerSide : AISide;
    public int OpponentSide => _isPlayerTurn ? AISide : PlayerSide;
    public Card Drawn => _drawn;
    public CardPower ActivePower => _activePower;
    public bool CambioCalled => _cambioCalled;
    public bool PlayerCalledCambio => _cambioCallerSide == PlayerSide;
    public int FinalRoundTurnsLeft => _finalRoundTurnsLeft;
    public bool AwaitingGiveCard => _awaitingGiveCard;
    public bool GiveByPlayer => _isPlayerTurn; // matcher is always the active side in this ruleset
    public bool AwaitingPeekConfirm => _awaitingPeekConfirm;
    public bool MatchedThisTurn => _matchedThisTurn;
    public bool IsTerminal => _phase == GamePhase.GameOver;
    public Card TopDiscard => _discard.Count > 0 ? _discard[^1] : Card.None;
    public int DrawPileCount => _drawPile.Count;
    public int DiscardCount => _discard.Count;

    // ----------------------------------------------------------------------
    // Construction
    // ----------------------------------------------------------------------

    /// <param name="shuffledIds">A pre-shuffled ordering of every physical card id.</param>
    /// <param name="handSize"></param>
    /// <param name="penaltySize"></param>
    /// <param name="seed"></param>
    public GameState(IReadOnlyList<int> shuffledIds, int handSize, int penaltySize, int seed)
    {
        HandSize = handSize;
        PenaltySize = penaltySize;
        _rng = new Random(seed);

        _drawPile = new List<Card>(shuffledIds.Count);
        
        foreach (var id in shuffledIds)
        {
            // Create draw pile
            _drawPile.Add(new Card(id));
        }
        // Empty discard
        _discard = new List<Card>();

        _hand = new[] { NewSlots(handSize), NewSlots(handSize) };
        _penalty = new[] { NewSlots(penaltySize), NewSlots(penaltySize) };

        // Deal alternating-free: player then AI (matches original DealInitialHands).
        for (var i = 0; i < handSize; i++) _hand[PlayerSide][i] = DrawNoReshuffle();
        for (var i = 0; i < handSize; i++) _hand[AISide][i] = DrawNoReshuffle();
        
        // penalties start empty (None)

        _phase = GamePhase.Dealing;
        _isPlayerTurn = true;
        _drawn = Card.None;
        _powerStep = PowerStep.None;
        _matchReceiver = SlotRef.None;
        _powerSource = SlotRef.None;
        _tradeOpponent = SlotRef.None;
        _tradeOwn = SlotRef.None;
    }

    private GameState() { } // for Clone

    private static Card[] NewSlots(int n)
    {
        var a = new Card[n];
        for (int i = 0; i < n; i++) a[i] = Card.None;
        return a;
    }

    // Always start play is players turn
    public void StartPlay()
    {
        if (_phase != GamePhase.Dealing) return;
        _phase = GamePhase.DrawingCard;
        _isPlayerTurn = true;
    }


    /// <summary>Deep copy for search. Pass a fresh seed so determinizations diverge.</summary>
    public GameState Clone(int seed)
    {
        var game = new GameState
        {
            HandSize = HandSize,
            PenaltySize = PenaltySize,
            _hand = new[] { (Card[])_hand[0].Clone(), (Card[])_hand[1].Clone() },
            _penalty = new[] { (Card[])_penalty[0].Clone(), (Card[])_penalty[1].Clone() },
            _drawPile = new List<Card>(_drawPile),
            _discard = new List<Card>(_discard),
            _phase = _phase,
            _powerStep = _powerStep,
            _isPlayerTurn = _isPlayerTurn,
            _drawn = _drawn,
            _activePower = _activePower,
            _cambioCalled = _cambioCalled,
            _cambioCallerSide = _cambioCallerSide,
            _finalRoundTurnsLeft = _finalRoundTurnsLeft,
            _matchedThisTurn = _matchedThisTurn,
            _awaitingGiveCard = _awaitingGiveCard,
            _matchReceiver = _matchReceiver,
            _awaitingPeekConfirm = _awaitingPeekConfirm,
            _powerSource = _powerSource,
            _tradeOpponent = _tradeOpponent,
            _tradeOwn = _tradeOwn,
            _rng = new Random(seed)
        };
        
        return game;
    }

    // ----------------------------------------------------------------------
    // Slot access
    // ----------------------------------------------------------------------

    public Card GetCard(SlotRef slotRef)
    {
        if (slotRef.IsNone) return Card.None;
        var cards = slotRef.Zone == Zone.Hand ? _hand[slotRef.Side] : _penalty[slotRef.Side];
        
        if (slotRef.Index < 0 || slotRef.Index >= cards.Length) return Card.None;
        
        return cards[slotRef.Index];
    }

    private void SetCard(SlotRef slotRef, Card card)
    {
        var arr = slotRef.Zone == Zone.Hand ? _hand[slotRef.Side] : _penalty[slotRef.Side];
        arr[slotRef.Index] = card;
    }

    /// <summary>A slot is "active" iff it currently holds a card.</summary>
    public bool IsActive(SlotRef s) => !GetCard(s).IsNone;

    
    // This method is really importat since this is where the address is generated, it uses yield retunr so whenever its called it remmeebrs its last state
    private IEnumerable<SlotRef> ActiveSlotsOf(int side)
    {
        for (int i = 0; i < _hand[side].Length; i++)
            if (!_hand[side][i].IsNone) yield return new SlotRef(side, Zone.Hand, i);
        for (int i = 0; i < _penalty[side].Length; i++)
            if (!_penalty[side][i].IsNone) yield return new SlotRef(side, Zone.Penalty, i);
    }


    // Legal moves — the action space the AI picks from
    public List<GameCommand> LegalMoves()
    {
        var moves = new List<GameCommand>();
        if (_phase == GamePhase.GameOver || _phase == GamePhase.Dealing) return moves;

        // to determine legal moves for each possible game phase
        switch (_phase)
        {
            case GamePhase.DrawingCard:
                if (_awaitingGiveCard)
                {
                    foreach (var s in ActiveSlotsOf(ActiveSide))
                        moves.Add(GameCommand.Give(s));
                }
                else
                {
                    if (CanDraw()) moves.Add(GameCommand.DrawFromDeck());
                    if (_discard.Count > 0) moves.Add(GameCommand.DrawFromDiscard());
                    if (!_cambioCalled) moves.Add(GameCommand.CallCambio());
                    if (!_matchedThisTurn && _discard.Count > 0)
                    {
                        // Option to match all available cards from the player or AI
                        
                        foreach (var s in ActiveSlotsOf(PlayerSide)) moves.Add(GameCommand.Match(s));
                        foreach (var s in ActiveSlotsOf(AISide)) moves.Add(GameCommand.Match(s));
                    }
                }
                break;

            case GamePhase.CardDrawn:
                // No Moves
                break;
            
            case GamePhase.SelectingSwapSlot:
                if (_phase == GamePhase.CardDrawn)
                {
                    moves.Add(GameCommand.DiscardDrawn());
                }

                foreach (var s in ActiveSlotsOf(ActiveSide))
                {
                    // All available slots can be swaped with
                    moves.Add(GameCommand.SwapDrawnInto(s));
                }
                
                break;

            case GamePhase.UsingPower:
                if (_awaitingPeekConfirm)
                {
                    moves.Add(GameCommand.FinishPeeking());
                    break;
                }
                
                // Internal switch for each power in _powerStep
                switch (_powerStep)
                {
                    case PowerStep.LookingOwn:
                        // None
                        break;
                    case PowerStep.SelectingPowerSwapSource:
                        // None
                        break;
                    case PowerStep.SelectingTradeOwn:
                        foreach (var s in ActiveSlotsOf(ActiveSide))
                        {
                            // Setting all available cards as targets for the power
                            moves.Add(GameCommand.UsePowerOn(s));
                        }
                        break;
                    case PowerStep.LookingOpponent:
                        // None
                        break;
                    case PowerStep.SelectingPowerSwapTarget:
                        // None
                        break;
                    case PowerStep.SelectingTradeOpponent:
                        foreach (var s in ActiveSlotsOf(OpponentSide)) moves.Add(GameCommand.UsePowerOn(s));
                        break;
                    case PowerStep.ConfirmingTrade:
                        moves.Add(GameCommand.ConfirmTrade());
                        break;
                }
                break;
        }
        return moves;
    }

    private bool CanDraw() => _drawPile.Count > 0 || _discard.Count > 1;


    // Apply is what Ai and player input calls, it does a do per possible actions and only executes the valid ones
    public MoveResult Apply(GameCommand cmd)
    {
        var fx = new List<GameEffect>();
        bool ok = cmd.Type switch
        {
            CommandType.DrawFromDeck      => DoDraw(fromDiscard: false, fx),
            CommandType.DrawFromDiscard   => DoDraw(fromDiscard: true, fx),
            CommandType.DiscardDrawn      => DoDiscardDrawn(fx),
            CommandType.SwapDrawnIntoSlot => DoSwapDrawn(cmd.Slot, fx),
            CommandType.UsePowerOnSlot    => DoUsePower(cmd.Slot, fx),
            CommandType.AttemptMatch      => DoAttemptMatch(cmd.Slot, fx),
            CommandType.GiveCard          => DoGiveCard(cmd.Slot, fx),
            CommandType.ConfirmTrade      => DoConfirmTrade(fx),
            CommandType.FinishPeeking     => DoFinishPeeking(fx),
            CommandType.CallCambio        => DoCallCambio(fx),
            _ => false
        };
        return new MoveResult
        {
            Ok = ok,
            Effects = fx 
        };
    }

    private bool DoDraw(bool fromDiscard, List<GameEffect> fx)
    {
        if (_phase != GamePhase.DrawingCard || _awaitingGiveCard) return false;
        Card c = fromDiscard ? DrawFromDiscard() : DrawCard();
        if (c.IsNone) return false;
        _drawn = c;
        _phase = GamePhase.CardDrawn;
        fx.Add(new GameEffect { Kind = EffectKind.CardDrawn, Slot = new SlotRef(ActiveSide, Zone.Hand, -1), Card = c, Success = _isPlayerTurn });
        return true;
    }

    private bool DoDiscardDrawn(List<GameEffect> fx)
    {
        if (_phase != GamePhase.CardDrawn || _drawn.IsNone) return false;


        Card top = TopDiscard;
        if (!top.IsNone && _drawn.Number == top.Number)
        {
            Card matched = _drawn;
            Discard(matched);
            _drawn = Card.None;
            _activePower = CardPower.None;          // matched cards never fire a power

            fx.Add(new GameEffect
            {
                Kind  = EffectKind.MatchResolved,
                Slot  = SlotRef.None,               // the drawn card lives in no slot
                Card  = matched,
                Success = true,                        // success
                ByPlayer = _isPlayerTurn                // byPlayer
            });

            EndTurn(fx);
            return true;
        }

        Discard(_drawn);
        _activePower = _drawn.Power;
        _drawn = Card.None;

        if (_activePower != CardPower.None) BeginPower(_activePower);
        else EndTurn(fx);
        return true;
    }

    private bool DoSwapDrawn(SlotRef s, List<GameEffect> fx)
    {
        if (_phase != GamePhase.CardDrawn && _phase != GamePhase.SelectingSwapSlot) return false;
        if (s.Side != ActiveSide || !IsActive(s)) return false;

        Card displaced = GetCard(s);
        SetCard(s, _drawn);
        Discard(displaced);
        _drawn = Card.None;
        EndTurn(fx);
        return true;
    }

    private bool DoUsePower(SlotRef s, List<GameEffect> fx)
    {
        if (_phase != GamePhase.UsingPower || _awaitingPeekConfirm) return false;
        if (!IsActive(s)) return false;

        switch (_powerStep)
        {
            case PowerStep.LookingOwn:
                if (s.Side != ActiveSide) return false;
                _awaitingPeekConfirm = true;
                fx.Add(Reveal(s));
                return true;

            case PowerStep.LookingOpponent:
                if (s.Side != OpponentSide) return false;
                _awaitingPeekConfirm = true;
                fx.Add(Reveal(s));
                return true;

            case PowerStep.SelectingPowerSwapSource:
                if (s.Side != ActiveSide) return false;
                _powerSource = s;
                _powerStep = PowerStep.SelectingPowerSwapTarget;
                return true;

            case PowerStep.SelectingPowerSwapTarget:
                if (s.Side != OpponentSide) return false;
                SwapSlots(_powerSource, s, fx);
                _powerSource = SlotRef.None;
                EndTurn(fx);
                return true;

            case PowerStep.SelectingTradeOpponent:
                if (s.Side != OpponentSide) return false;
                _tradeOpponent = s;
                _powerStep = PowerStep.SelectingTradeOwn;
                return true;

            case PowerStep.SelectingTradeOwn:
                if (s.Side != ActiveSide) return false;
                _tradeOwn = s;
                _powerStep = PowerStep.ConfirmingTrade;
                fx.Add(new GameEffect
                {
                    Kind = EffectKind.InformedTradeReady,
                    Slot = _tradeOpponent,
                    Slot2 = _tradeOwn,
                    Card = GetCard(_tradeOpponent),
                    Card2 = GetCard(_tradeOwn)
                });
                return true;
        }
        return false;
    }

    private bool DoAttemptMatch(SlotRef s, List<GameEffect> fx)
    {
        if (_phase != GamePhase.DrawingCard || _matchedThisTurn || _awaitingGiveCard) return false;
        if (!IsActive(s)) return false;
        Card top = TopDiscard;
        if (top.IsNone) return false;

        Card c = GetCard(s);
        bool success = c.Number == top.Number;

        if (!success)
        {
            // Faithful to original: failed match just adds a penalty; the turn's
            // match flag is NOT consumed, so another attempt is technically legal.
            ApplyPenalty(ActiveSide, fx);
            fx.Add(new GameEffect { Kind = EffectKind.MatchResolved, Slot = s, Card = c, Success = false, ByPlayer = _isPlayerTurn });
            return true;
        }

        _matchedThisTurn = true;
        bool matchersOwn = s.Side == ActiveSide;
        Discard(c);
        SetCard(s, Card.None);
        fx.Add(new GameEffect { Kind = EffectKind.MatchResolved, Slot = s, Card = c, Success = true, ByPlayer = _isPlayerTurn });

        if (!matchersOwn)
        {
            // Matched an opponent card -> you must hand one of yours into the gap.
            _awaitingGiveCard = true;
            _matchReceiver = s;
        }
        return true;
    }

    private bool DoGiveCard(SlotRef s, List<GameEffect> fx)
    {
        if (!_awaitingGiveCard) return false;
        if (s.Side != ActiveSide || !IsActive(s)) return false;

        Card given = GetCard(s);
        SetCard(s, Card.None);
        SetCard(_matchReceiver, given);

        var receiver = _matchReceiver;
        _awaitingGiveCard = false;
        _matchReceiver = SlotRef.None;

        fx.Add(new GameEffect { Kind = EffectKind.SlotsSwapped, Slot = s, Slot2 = receiver });
        fx.Add(new GameEffect { Kind = EffectKind.GiveDone });
        // No EndTurn: the active player must still draw this turn.
        return true;
    }

    private bool DoConfirmTrade(List<GameEffect> fx)
    {
        if (_phase != GamePhase.UsingPower || _powerStep != PowerStep.ConfirmingTrade) return false;
        SwapSlots(_tradeOpponent, _tradeOwn, fx);
        _tradeOpponent = SlotRef.None;
        _tradeOwn = SlotRef.None;
        EndTurn(fx);
        return true;
    }

    private bool DoFinishPeeking(List<GameEffect> fx)
    {
        if (_phase != GamePhase.UsingPower || !_awaitingPeekConfirm) return false;
        EndTurn(fx);
        return true;
    }

    private bool DoCallCambio(List<GameEffect> fx)
    {
        if (_phase != GamePhase.DrawingCard || _awaitingGiveCard || _cambioCalled) return false;
        _cambioCalled = true;
        _cambioCallerSide = ActiveSide;
        _finalRoundTurnsLeft = 1;
        _phase = GamePhase.CambioCalled;
        EndTurn(fx);
        return true;
    }

    // ----------------------------------------------------------------------
    // Shared mechanics
    // ----------------------------------------------------------------------

    private void BeginPower(CardPower power)
    {
        _powerStep = power switch
        {
            CardPower.LookOwnCard      => PowerStep.LookingOwn,
            CardPower.LookOpponentCard => PowerStep.LookingOpponent,
            CardPower.BlindSwap        => PowerStep.SelectingPowerSwapSource,
            CardPower.LookAndSwap      => PowerStep.SelectingTradeOpponent,
            _                          => PowerStep.None
        };
        _phase = GamePhase.UsingPower;
    }

    private void EndTurn(List<GameEffect> fx)
    {
        _drawn = Card.None;
        _activePower = CardPower.None;
        _powerStep = PowerStep.None;
        _matchedThisTurn = false;
        _awaitingGiveCard = false;
        _awaitingPeekConfirm = false;
        _matchReceiver = SlotRef.None;
        _powerSource = SlotRef.None;
        _tradeOpponent = SlotRef.None;
        _tradeOwn = SlotRef.None;

        if (_cambioCalled && ActiveSide != _cambioCallerSide)
        {
            _finalRoundTurnsLeft--;
            if (_finalRoundTurnsLeft <= 0)
            {
                _phase = GamePhase.GameOver;
                fx.Add(new GameEffect { Kind = EffectKind.GameOver });
                return;
            }
        }

        _isPlayerTurn = !_isPlayerTurn;
        _phase = GamePhase.DrawingCard;
    }

    private void SwapSlots(SlotRef a, SlotRef b, List<GameEffect> fx)
    {
        Card ca = GetCard(a);
        SetCard(a, GetCard(b));
        SetCard(b, ca);
        fx.Add(new GameEffect { Kind = EffectKind.SlotsSwapped, Slot = a, Slot2 = b });
    }

    private void ApplyPenalty(int side, List<GameEffect> fx)
    {
        int idx = -1;
        for (int i = 0; i < _penalty[side].Length; i++)
            if (_penalty[side][i].IsNone) { idx = i; break; }
        if (idx < 0) return;

        Card pen = DrawCard();
        if (pen.IsNone) return;

        _penalty[side][idx] = pen;
        fx.Add(new GameEffect
        {
            Kind = EffectKind.PenaltyAdded,
            Slot = new SlotRef(side, Zone.Penalty, idx),
            Card = pen,
            Success = side == PlayerSide
        });
    }

    private GameEffect Reveal(SlotRef s) => new GameEffect
    {
        Kind = EffectKind.SlotRevealed,
        Slot = s,
        Card = GetCard(s),
        Success = _isPlayerTurn // the active side is the one learning the card
    };

    // ----------------------------------------------------------------------
    // Deck plumbing (with discard reshuffle — fixes the null-draw crash)
    // ----------------------------------------------------------------------

    private Card DrawNoReshuffle()
    {
        if (_drawPile.Count == 0) return Card.None;
        Card c = _drawPile[_drawPile.Count - 1];
        _drawPile.RemoveAt(_drawPile.Count - 1);
        return c;
    }

    private Card DrawCard()
    {
        if (_drawPile.Count == 0)
        {
            if (_discard.Count <= 1) return Card.None;
            // Keep the top discard; shuffle the rest back into the draw pile.
            Card top = _discard[_discard.Count - 1];
            for (int i = 0; i < _discard.Count - 1; i++) _drawPile.Add(_discard[i]);
            _discard.Clear();
            _discard.Add(top);
            Shuffle(_drawPile);
        }
        return DrawNoReshuffle();
    }

    private Card DrawFromDiscard()
    {
        if (_discard.Count == 0) return Card.None;
        Card c = _discard[_discard.Count - 1];
        _discard.RemoveAt(_discard.Count - 1);
        return c;
    }

    private void Discard(Card c)
    {
        if (!c.IsNone) _discard.Add(c);
    }

    private void Shuffle(List<Card> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ----------------------------------------------------------------------
    // Scoring / queries
    // ----------------------------------------------------------------------

    public int Score(int side)
    {
        var total = _hand[side].Where(c => !c.IsNone).Sum(c => c.Value);
        total += _penalty[side].Where(c => !c.IsNone).Sum(c => c.Value);
        return total;
    }

    public int WinnerSide()
    {
        int p = Score(PlayerSide), a = Score(AISide);
        if (p < a) return PlayerSide;
        if (a < p) return AISide;
        return -1; // draw
    }

    // ----------------------------------------------------------------------
    // DISMASTS support
    // ----------------------------------------------------------------------

    /// <summary>
    /// Ids that are not currently face-up in the discard pile and not "known" to the
    /// observer. Used by the belief/determinization layer to know what can fill the
    /// hidden slots and the draw pile. (Belief weighting lives in the AI; this just
    /// reports the raw multiset of unseen cards given a known-id set.)
    /// </summary>
    public List<int> UnseenCardIds(HashSet<int> knownIds)
    {
        var present = new HashSet<int>();
        foreach (var c in _discard) present.Add(c.Id);
        if (knownIds != null) foreach (var id in knownIds) present.Add(id);

        var result = new List<int>();
        for (int id = 0; id < Card.DeckSize; id++)
            if (!present.Contains(id)) result.Add(id);
        return result;
    }

    /// <summary>
    /// Overwrite every slot/draw-pile card the observer does NOT know with a sampled
    /// id drawn from <paramref name="sampledHidden"/> (order = consumption order).
    /// The AI builds <paramref name="sampledHidden"/> from its beliefs, then calls this
    /// on a Clone to produce one fully-specified determinized world to search.
    ///
    /// NOTE: This is intentionally a thin hook. The "which slots are known" decision is
    /// the belief layer's job (see AICambioAgent). Implemented minimally here.
    /// </summary>
    public void OverwriteHidden(IReadOnlyCollection<SlotRef> hiddenSlots, IReadOnlyList<int> sampledHidden)
    {
        int k = 0;
        foreach (var s in hiddenSlots)
        {
            if (k >= sampledHidden.Count) break;
            SetCard(s, new Card(sampledHidden[k++]));
        }
        // Remaining sampled ids (if any) repopulate the hidden draw pile order, etc.
        // Left as a deliberate extension point for the belief layer.
    }
}
