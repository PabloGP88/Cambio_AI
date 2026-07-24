using System.Collections.Generic;

public struct BeliefSlotRow
{
    public SlotRef Slot;
    public bool IsOpponent;
    public bool Known;         // agent is certain of this card
    public bool OppKnows;      // agent believes the OPPONENT knows this slot
    public double TiltRaw;     // CardBeliefs.TiltFor  — the signal the layer computed
    public double TiltEff;     // EffTilt              — the signal the search consumed (0 when baseline)
    public int TrueValue;      // ground truth, analysis only
    public int TrueNumber;     // ground truth, analysis only
}

public class BeliefReport
{
    public int Side;
    public GamePhase Phase;
    public PowerStep Step;

    public GameCommand Chosen;
    public bool BayesianOn;

    public double BelievedOwnScore;   // what the agent thinks it is holding
    public int ActualOwnScore;        // what it is actually holding
    public int ActualOppScore;

    public double OppGlobalTilt;      // accumulated "opponent is running low" shift
    public int OppTurnCount;

    public int HiddenCount;
    public int KnownOwnCount;
    public int KnownOppCount;

    public List<BeliefSlotRow> Slots; // every active slot, both sides
}