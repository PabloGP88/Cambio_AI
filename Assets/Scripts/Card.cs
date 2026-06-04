using UnityEngine;

public enum CardPower { None, LookOwnCard, LookOpponentCard, BlindSwap, LookAndSwap }

[System.Serializable]
public class Card
{
    public Sprite sprite;
    public int displayNumber;
    public bool isRed;

    public int Value => displayNumber switch
    {
        0 => 0,
        13 when isRed => -1,
        11 or 12 or 13 => 10,
        _ => displayNumber
    };

    public CardPower Power => displayNumber switch
    {
        7 or 8 => CardPower.LookOwnCard,
        9 or 10 => CardPower.LookOpponentCard,
        11 or 12 => CardPower.BlindSwap,
        13 when !isRed => CardPower.LookAndSwap,
        _ => CardPower.None
    };
}