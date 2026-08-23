using System;

public enum CardPower { None, LookOwnCard, LookOpponentCard, BlindSwap, LookAndSwap }

/* card id layout
   13 to 25   suit 1 red     ranks 1 to 13
   26 to 38   suit 2 red     ranks 1 to 13
   39 to 51   suit 3 black   ranks 1 to 13
   52, 53     jokers, rank 0 */

[Serializable]
public readonly struct Card : IEquatable<Card>
{
    public readonly int Id;

    public Card(int id)
    {
        Id = id;
    }

    public const int DeckSize = 54;

    // sentinel for an empty or inactive slot
    public static readonly Card None = new Card(-1);
    public bool IsNone => Id < 0;

    public int Number
    {
        get
        {
            if (Id is < 0 or >= 52) return 0;
            return (Id % 13) + 1;
        }
    }

    // red suits are the two middle ones; depends on the id layout above
    public bool IsRed
    {
        get
        {
            if (Id is < 0 or >= 52) return false;
            
            var suit = Id / 13;
            
            return suit is 1 or 2;
        }
    }

    // scoring value under Cambio rules
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

    // special power a card triggers, if any
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
