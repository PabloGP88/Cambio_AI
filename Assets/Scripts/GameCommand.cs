using System;

/// <summary>Tells what card is bring targeted, in normal "hand" or one of the penalty ones.</summary>
public enum Zone
{
    Hand, 
    Penalty
}

public enum Side
{
    Player = 0, 
    AI = 1
}

/// <summary>
/// Address of a card slot: (side, zone, index)
/// </summary>
[Serializable]
public readonly struct SlotRef : IEquatable<SlotRef>
{
    public readonly int Side;   
    public readonly Zone Zone;
    public readonly int Index;

    public SlotRef(int side, Zone zone, int index) { Side = side; Zone = zone; Index = index; }

    public static readonly SlotRef None = new SlotRef(-1, Zone.Hand, -1);
    public bool IsNone => Side < 0;

    public bool Equals(SlotRef o) => Side == o.Side && Zone == o.Zone && Index == o.Index;
    public override bool Equals(object obj) => obj is SlotRef s && Equals(s);
    public override int GetHashCode() => (Side * 397 ^ (int)Zone) * 397 ^ Index;
    public override string ToString() => IsNone ? "Slot(None)" : $"Slot({(Side == 0 ? "P" : "AI")},{Zone},{Index})";
}

public enum CommandType
{
    DrawFromDeck,
    DrawFromDiscard,
    DiscardDrawn,
    SwapDrawnIntoSlot,
    UsePowerOnSlot,
    AttemptMatch,
    GiveCard,
    ConfirmTrade,
    FinishPeeking,
    CallCambio
}

//  Command is what PLayer and AI will use to send the game state to activate the actions that can be done in Cambio

[Serializable]
public readonly struct GameCommand : IEquatable<GameCommand>
{
    public readonly CommandType Type;
    public readonly SlotRef Slot;   // SlotRef.None when the command needs no slot

    public GameCommand(CommandType type, SlotRef slot)
    {
        Type = type;
        Slot = slot;
    }

    // Default ones to iterate faster
    public static GameCommand DrawFromDeck()        => new(CommandType.DrawFromDeck, SlotRef.None);
    public static GameCommand DrawFromDiscard()     => new(CommandType.DrawFromDiscard, SlotRef.None);
    public static GameCommand DiscardDrawn()        => new(CommandType.DiscardDrawn, SlotRef.None);
    public static GameCommand ConfirmTrade()        => new(CommandType.ConfirmTrade, SlotRef.None);
    public static GameCommand FinishPeeking()       => new(CommandType.FinishPeeking, SlotRef.None);
    public static GameCommand CallCambio()          => new(CommandType.CallCambio, SlotRef.None);
    public static GameCommand SwapDrawnInto(SlotRef s) => new(CommandType.SwapDrawnIntoSlot, s);
    
    // This is to use a power on a specific slot
    public static GameCommand UsePowerOn(SlotRef s)    => new(CommandType.UsePowerOnSlot, s);
    public static GameCommand Match(SlotRef s)         => new(CommandType.AttemptMatch, s);
    
    // When AI or Player matches their opponent card, they give one of their cards to them
    public static GameCommand Give(SlotRef s)          => new(CommandType.GiveCard, s);

    public bool Equals(GameCommand o) => Type == o.Type && Slot.Equals(o.Slot);
    public override bool Equals(object obj) => obj is GameCommand c && Equals(c);
    public override int GetHashCode() => (int)Type * 397 ^ Slot.GetHashCode();
    public override string ToString() => Slot.IsNone ? Type.ToString() : $"{Type} {Slot}";
}
