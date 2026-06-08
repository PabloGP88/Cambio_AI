using System;

/// <summary>
/// Power a card carries when it is discarded. Pure game concept, no Unity.
/// </summary>
public enum CardPower { None, LookOwnCard, LookOpponentCard, BlindSwap, LookAndSwap }

/// <summary>
/// A card is just an integer id (0..53) into the physical deck. Two cards with
/// the same rank/colour but different suits are distinct ids (so the *view* can
/// resolve the right sprite) yet gameplay-identical (Value/Power depend only on
/// rank/colour). Keeping Card a small readonly struct means GameState arrays are
/// value types and clone with a single Array.Clone() — which is exactly what
/// ISMCTS determinization needs to do thousands of times per decision.
///
/// Id layout (matches the original Deck):
///   0..12   suit 0 (black)   ranks 1..13
///   13..25  suit 1 (red)     ranks 1..13
///   26..38  suit 2 (red)     ranks 1..13
///   39..51  suit 3 (black)   ranks 1..13
///   52,53   jokers (rank 0)
/// </summary>
[Serializable]
public readonly struct Card : IEquatable<Card>
{
    public readonly int Id;

    public Card(int id) { Id = id; }

    /// <summary>Number of physical cards in a full deck (52 + 2 jokers).</summary>
    public const int DeckSize = 54;

    /// <summary>Sentinel for an empty / inactive slot.</summary>
    public static readonly Card None = new Card(-1);
    public bool IsNone => Id < 0;

    public int Number
    {
        get
        {
            if (Id < 0 || Id >= 52) return 0; // none + jokers
            return (Id % 13) + 1;
        }
    }

    public bool IsRed
    {
        get
        {
            if (Id < 0 || Id >= 52) return false;
            int suit = Id / 13;
            return suit == 1 || suit == 2;
        }
    }

    public int Value
    {
        get
        {
            int n = Number;
            return n switch
            {
                0 => 0,
                13 when IsRed => -1,
                11 or 12 or 13 => 10,
                _ => n
            };
        }
    }

    public CardPower Power
    {
        get
        {
            int n = Number;
            return n switch
            {
                7 or 8 => CardPower.LookOwnCard,
                9 or 10 => CardPower.LookOpponentCard,
                11 or 12 => CardPower.BlindSwap,
                13 when !IsRed => CardPower.LookAndSwap,
                _ => CardPower.None
            };
        }
    }

    public bool Equals(Card other) => Id == other.Id;
    public override bool Equals(object obj) => obj is Card c && Equals(c);
    public override int GetHashCode() => Id;
    public override string ToString() => IsNone ? "None" : $"#{Id}(n{Number}{(IsRed ? "R" : "B")})";
}
