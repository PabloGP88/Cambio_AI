using System;

// which area a targeted card sits in: the normal hand or the penalty pile
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

// address of a card slot by side, zone and index
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
    DiscardDrawn,
    SwapDrawnIntoSlot,
    UsePowerOnSlot,
    AttemptMatch,
    GiveCard,
    ConfirmTrade,
    FinishPeeking,
    CallCambio
}

// the single action type both the player and the AI submit to drive the game
[Serializable]
public readonly struct GameCommand : IEquatable<GameCommand>
{
    public readonly CommandType Type;
    public readonly SlotRef Slot;   // SlotRef.None when no slot is needed

    public GameCommand(CommandType type, SlotRef slot)
    {
        Type = type;
        Slot = slot;
    }

    // slot-less command factories
    public static GameCommand DrawFromDeck()        => new(CommandType.DrawFromDeck, SlotRef.None);
    public static GameCommand DiscardDrawn()        => new(CommandType.DiscardDrawn, SlotRef.None);
    public static GameCommand ConfirmTrade()        => new(CommandType.ConfirmTrade, SlotRef.None);
    public static GameCommand FinishPeeking()       => new(CommandType.FinishPeeking, SlotRef.None);
    public static GameCommand CallCambio()          => new(CommandType.CallCambio, SlotRef.None);
    public static GameCommand SwapDrawnInto(SlotRef s) => new(CommandType.SwapDrawnIntoSlot, s);
    public static GameCommand UsePowerOn(SlotRef s)    => new(CommandType.UsePowerOnSlot, s);
    public static GameCommand Match(SlotRef s)         => new(CommandType.AttemptMatch, s);

    // matching an opponent card obliges the matcher to give one of their own into the gap
    public static GameCommand Give(SlotRef s)          => new(CommandType.GiveCard, s);

    public bool Equals(GameCommand o) => Type == o.Type && Slot.Equals(o.Slot);
    public override bool Equals(object obj) => obj is GameCommand c && Equals(c);
    public override int GetHashCode() => (int)Type * 397 ^ Slot.GetHashCode();
    public override string ToString() => Slot.IsNone ? Type.ToString() : $"{Type} {Slot}";
}
