using System;

public enum CardPower { None, LookOwnCard, LookOpponentCard, BlindSwap, LookAndSwap }

/*
    ID layout:
        13..25  suit 1 (red)     ranks 1..13
        26..38  suit 2 (red)     ranks 1..13
        39..51  suit 3 (black)   ranks 1..13
        52,53   jokers (rank 0)
*/

[Serializable]
public readonly struct Card : IEquatable<Card>
{
    public readonly int Id;

    public Card(int id)
    {
        Id = id;
    }

    public const int DeckSize = 54;

    //Sentinel for an empty / inactive slot.
    public static readonly Card None = new Card(-1);
    public bool IsNone => Id < 0;

    public int Number
    {
        get
        {
            if (Id is < 0 or >= 52) return 0; // none + jokers
            
            // formula because there are 13 values max in a normal deck, +1 since the minimum value is 1
            return (Id % 13) + 1;
        }
    }

    // This formula for the id works with out deck layout, red are in the middle, there are 4 suits, 1 and 2 are in the middle
    // if layout is changed, this formula need to be updated as well
    public bool IsRed
    {
        get
        {
            if (Id is < 0 or >= 52) return false;
            
            var suit = Id / 13;
            
            return suit is 1 or 2;
        }
    }

    // This returns the value of the card following cambio rules
    public int Value
    {
        get
        {
            var n = Number;
            return n switch
            {
                0 => 0,
                13 when IsRed => -1,
                11 or 12 or 13 => 10,
                _ => n
            };
        }
    }

    // Same but for powers
    public CardPower Power
    {
        get
        {
            var n = Number;
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
    public override bool Equals(object obj) => obj is Card card && Equals(card);
    public override int GetHashCode() => Id;
}
