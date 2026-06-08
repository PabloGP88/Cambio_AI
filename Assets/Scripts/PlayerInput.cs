/// <summary>
/// Turns raw human input (button presses, slot taps) into GameCommands and submits them.
/// This is where UI-only affordances live — most importantly the "arm a slot, tap again
/// to confirm a match" interaction, which used to leak into the rules engine. The AI never
/// arms anything; it just emits AttemptMatch directly. Keeping this here means GameState
/// stays pure and both sides share one command vocabulary.
///
/// Plain C# (not a MonoBehaviour) — owned by GameManager, holds no Unity refs except via
/// the GameManager it talks back to.
/// </summary>
public class PlayerInput
{
    private readonly GameManager _gm;
    private SlotRef _armed = SlotRef.None;
    private bool _selectingSwap;

    public PlayerInput(GameManager gm) { _gm = gm; }

    // --- Buttons ---
    public void PressDrawDeck()    { _selectingSwap = false; if (CanAct) _gm.SubmitPlayer(GameCommand.DrawFromDeck()); }
    public void PressDrawDiscard() { _selectingSwap = false; if (CanAct) _gm.SubmitPlayer(GameCommand.DrawFromDiscard()); }
    public void PressDiscardDrawn(){ _selectingSwap = false; if (CanAct) _gm.SubmitPlayer(GameCommand.DiscardDrawn()); }
    public void PressCambio()      { if (CanAct) _gm.SubmitPlayer(GameCommand.CallCambio()); }
    public void PressConfirmTrade(){ if (CanAct) _gm.SubmitPlayer(GameCommand.ConfirmTrade()); }
    public void PressFinishPeek()  { if (CanAct) _gm.SubmitPlayer(GameCommand.FinishPeeking()); }

    /// <summary>"Swap" button: arm swap-target selection and tell the view to show arrows.
    /// GameState stays in CardDrawn (SwapDrawnIntoSlot is legal there); this is UI-only.</summary>
    public void PressBeginSwap()
    {
        if (CanAct && _gm.State.Phase == GamePhase.CardDrawn)
        {
            _selectingSwap = true;
            _gm.EnterSwapSelection();
        }
    }

    // --- Slot taps ---
    public void ClickSlot(int side, Zone zone, int index)
    {
        if (!CanAct) return;
        var slot = new SlotRef(side, zone, index);
        var st = _gm.State;

        switch (st.Phase)
        {
            case GamePhase.DrawingCard:
                if (st.AwaitingGiveCard) _gm.SubmitPlayer(GameCommand.Give(slot));
                else HandleMatchTap(slot);
                break;

            case GamePhase.CardDrawn:
                if (_selectingSwap)
                {
                    _selectingSwap = false;
                    _gm.SubmitPlayer(GameCommand.SwapDrawnInto(slot));
                }
                break;

            case GamePhase.UsingPower:
                _gm.SubmitPlayer(GameCommand.UsePowerOn(slot));
                break;
        }
    }

    private void HandleMatchTap(SlotRef slot)
    {
        if (!_gm.State.IsActive(slot)) return;
        if (_gm.State.TopDiscard.IsNone) return;

        if (_armed.IsNone)
        {
            _armed = slot;
            _gm.SetSlotArmed(slot, true);
        }
        else if (_armed.Equals(slot))
        {
            _gm.SetSlotArmed(slot, false);
            _armed = SlotRef.None;
            _gm.SubmitPlayer(GameCommand.Match(slot));
        }
        else
        {
            _gm.SetSlotArmed(_armed, false);
            _armed = SlotRef.None;
        }
    }

    public void ClearArmed()
    {
        if (_armed.IsNone) return;
        _gm.SetSlotArmed(_armed, false);
        _armed = SlotRef.None;
    }

    private bool CanAct => _gm.State != null && _gm.State.IsPlayerTurn && !_gm.State.IsTerminal;
}
