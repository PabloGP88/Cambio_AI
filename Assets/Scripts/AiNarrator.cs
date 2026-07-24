using System.Collections.Generic;

public static class AiNarrator
{
    private const string Me = "Eva";
    
    private static string Pos(SlotRef s)
    {
        if (s.IsNone) return "a card";
        if (s.Zone == Zone.Penalty) return $"penalty card {s.Index + 1}";

        // Screen positions (what a playtester sees). AI hand is at the top, player's at
        // the bottom, so each side indexes its 2x2 grid from its own "near" row:
        //
        //   AI:  0 1        Player:  2 3
        //        2 3                 0 1
        //
        if (s.Side == GameState.AISide)
            return s.Index switch
            {
                0 => "top-left",
                1 => "top-right",
                2 => "bottom-left",
                3 => "bottom-right",
                _ => $"card {s.Index + 1}"
            };

        return s.Index switch
        {
            0 => "bottom-left",
            1 => "bottom-right",
            2 => "top-left",
            3 => "top-right",
            _ => $"card {s.Index + 1}"
        };
    }

    private static string PowerName(CardPower p) => p switch
    {
        CardPower.LookOwnCard      => "peek at one of his own cards",
        CardPower.LookOpponentCard => "peek at one of yours",
        CardPower.BlindSwap        => "blind-swap two cards",
        CardPower.LookAndSwap      => "look at one of yours and swap",
        _                          => "use a power"
    };

    /// <summary>Build one commentary line for a just-applied AI command. Returns null
    /// when there's nothing worth announcing (e.g. an intermediate power selection, so
    /// the previous line stays on screen).</summary>
    public static string Describe(CommandType type, List<GameEffect> effects, GameState stateAfter)
    {
        switch (type)
        {
            case CommandType.DrawFromDeck:
                return $"{Me} drew a card from the deck.";

            case CommandType.CallCambio:
                return $"{Me} called Cambio, devastating";

            case CommandType.DiscardDrawn:
                if (HasMatchNoSlot(effects))
                    return $"{Me} discarded a matching card.";
                if (stateAfter != null && stateAfter.Phase == GamePhase.UsingPower)
                    return $"{Me} played a power card — about to {PowerName(stateAfter.ActivePower)}...";
                return $"{Me} discarded the card he drew.";

            case CommandType.SwapDrawnIntoSlot:
            {
                SlotRef s = FirstSingleSwap(effects);
                return s.IsNone ? null : $"{Me} swapped the drawn card into his {Pos(s)}.";
            }

            case CommandType.UsePowerOnSlot:
            {
                // A look-power reveals a slot; a completed swap-power produces a cross-side swap.
                SlotRef looked = FirstReveal(effects);
                if (!looked.IsNone)
                    return looked.Side == GameState.AISide
                        ? $"{Me} looked at his {Pos(looked)} card."
                        : $"{Me} looked at your {Pos(looked)} card.";

                var (his, yours) = FirstCrossSwap(effects);
                if (!his.IsNone)
                    return $"{Me} swapped his {Pos(his)} with your {Pos(yours)}.";

                return null; // intermediate pick (choosing which cards to swap) — stay quiet
            }

            case CommandType.ConfirmTrade:
            {
                var (his, yours) = FirstCrossSwap(effects);
                return his.IsNone ? null : $"{Me} swapped his {Pos(his)} with your {Pos(yours)}.";
            }

            case CommandType.AttemptMatch:
            {
                var m = FirstSlotMatch(effects);
                if (m == null) return null;
                var (slot, success) = m.Value;
                if (!success)
                    return $"{Me} tried to snap but missed...he is so chopped.";
                return slot.Side == GameState.AISide
                    ? $"{Me} matched his {Pos(slot)}! Get good brah"
                    : $"{Me} snapped your {Pos(slot)}! You are so slow";
            }

            case CommandType.GiveCard:
                return $"{Me} handed you one of his cards.";

            case CommandType.FinishPeeking:
            default:
                return null; // nothing to add — keep the previous line on screen
        }
    }
    
    private static SlotRef FirstReveal(List<GameEffect> fx)
    {
        if (fx != null)
            foreach (var e in fx)
                if (e.Kind == EffectKind.SlotRevealed) return e.Slot;
        return SlotRef.None;
    }

    private static SlotRef FirstSingleSwap(List<GameEffect> fx)
    {
        if (fx != null)
            foreach (var e in fx)
                if (e.Kind == EffectKind.SlotsSwapped && e.Slot2.IsNone) return e.Slot;
        return SlotRef.None;
    }

    private static (SlotRef his, SlotRef yours) FirstCrossSwap(List<GameEffect> fx)
    {
        if (fx != null)
            foreach (var e in fx)
                if (e.Kind == EffectKind.SlotsSwapped && !e.Slot2.IsNone)
                {
                    bool firstIsAi = e.Slot.Side == GameState.AISide;
                    return firstIsAi ? (e.Slot, e.Slot2) : (e.Slot2, e.Slot);
                }
        return (SlotRef.None, SlotRef.None);
    }

    private static bool HasMatchNoSlot(List<GameEffect> fx)
    {
        if (fx != null)
            foreach (var e in fx)
                if (e.Kind == EffectKind.MatchResolved && e.Slot.IsNone) return true;
        return false;
    }

    private static (SlotRef slot, bool success)? FirstSlotMatch(List<GameEffect> fx)
    {
        if (fx != null)
            foreach (var e in fx)
                if (e.Kind == EffectKind.MatchResolved && !e.Slot.IsNone) return (e.Slot, e.Success);
        return null;
    }
}