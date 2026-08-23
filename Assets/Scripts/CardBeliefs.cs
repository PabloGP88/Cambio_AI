using System;
using System.Collections.Generic;

// per-slot belief state for the AI
public class CardBeliefs
{
    private readonly int _mySide;
    private readonly int _handSize;
    private readonly int _penaltySize;
    private readonly int _oppSide;

    private const int Buckets = 12;                 // Card.Value from -1 to 10 maps to index Value+1, 0 to 11

    // likelihood tuning; every term is a multiplicative factor in probability space
    
    public double SwapBeta = 0.35;    // sharpness of that keep-sigmoid
    public double SwapBias = 0.05;    // small blanket lean-low for choosing to keep a draw at all

    // a slot the opponent knows and has kept across turns
    public double KeepLogLik  = 0.03;
    public int    KeepTurnCap = 6;

    // a plain low face-up discard is weak evidence 
    public double DiscardSlope = 0.02;
    public double TypicalValue = 6.0;
    public double GlobalCap    = 0.6;

    private readonly Dictionary<SlotRef, double[]> _logL = new();   // per-slot accumulated log-likelihood
    private readonly HashSet<SlotRef> _oppKnows = new();
    private readonly Dictionary<SlotRef, int> _oppKnownSince = new();
    private int _oppTurnCount;
    private double _oppGlobalLowSlope;                              // global lean-low slope for opponent slots

    private readonly Dictionary<SlotRef, Card> _known = new();

    // stats for graphs and telemetry
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

    // move known-ness with the cards when two slots swap contents
    public void SwapKnow(SlotRef s0, SlotRef s1)
    {
        bool knownA = _known.TryGetValue(s0, out var cardA);
        bool knownB = _known.TryGetValue(s1, out var cardB);

        if (knownB) _known[s0] = cardB; else _known.Remove(s0);
        if (knownA) _known[s1] = cardA; else _known.Remove(s1);
    }
    
    public void FillLogLik(SlotRef s, double[] outLogL)
    {
        Array.Clear(outLogL, 0, outLogL.Length);
        if (_known.ContainsKey(s)) return;

        if (_logL.TryGetValue(s, out var stored))
            for (int b = 0; b < Buckets; b++) outLogL[b] += stored[b];

        // keep-survival
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

        // global running low lean
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
                // via LookOwn or LookOpponent
                if (iAmActor) SetKnow(effect.Slot, effect.Card);
                break;

            case EffectKind.SlotsSwapped:
                if (effect.Slot2.IsNone)
                {
                    // swap-drawn-into-slot
                    if (iAmActor)
                    {
                        SetKnow(effect.Slot, effect.Card);   // we know what we placed
                        ClearSlotMeta(effect.Slot);
                    }
                    else
                    {
                        // opponent kept an unseen drawn card
                        _known.Remove(effect.Slot);
                        ClearSlotMeta(effect.Slot);
                        SetSwapInLikelihood(effect.Slot, effect.Card2.Value);  // Card2 = displaced, public
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

    /* likelihood of the new hidden card given the opponent kept it over a displaced card worth
       high when v is well below
       d, times a small blanket lean-low. stored as a log-likelihood vector */
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

    // every active slot of both players the AI is not certain of
    public List<SlotRef> HiddenSlots(GameState world)
    {
        var hidden = new List<SlotRef>();
        foreach (var side in new[] { GameState.PlayerSide, GameState.AISide })
            foreach (var slot in world.GetActiveSlots(side))
                if (!_known.ContainsKey(slot)) hidden.Add(slot);
        return hidden;
    }

    // ids the AI knows, restricted to still-active slots; excluded from the unseen pool
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
