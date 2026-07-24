using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MatchTracker : MonoBehaviour
{
    public static MatchTracker Instance { get; private set; }

    [Header("Tagging")]
    [Tooltip("Written into every row so you can compare agent versions later, e.g. 'baseline' vs 'bayesian'.")]
    public string agentLabel = "baseline";

    [Header("Output")]
    public bool useDownloadsFolder = true;
    public string outputSubfolder = "CambioTelemetry";
    [Tooltip("Append every finished match to *_live.csv immediately, so nothing is lost if you quit Play mode without exporting.")]
    public bool writeLiveFiles = true;
    [Tooltip("Log the per-decision belief rows. Turn off for a pure behavioural run.")]
    public bool logBeliefRows = true;

    private const int P = GameState.PlayerSide;   // 0
    private const int A = GameState.AISide;       // 1

    // Fixed column order for the move-distribution block.
    private static readonly CommandType[] MoveKinds =
    {
        CommandType.DrawFromDeck, CommandType.DiscardDrawn, CommandType.SwapDrawnIntoSlot,
        CommandType.UsePowerOnSlot, CommandType.AttemptMatch, CommandType.GiveCard,
        CommandType.ConfirmTrade, CommandType.FinishPeeking, CommandType.CallCambio
    };

    private static readonly CardPower[] PowerKinds =
    {
        CardPower.LookOwnCard, CardPower.LookOpponentCard, CardPower.BlindSwap, CardPower.LookAndSwap
    };

    // ---- accumulated across the whole play session (survives scene reloads) ----
    private readonly List<string> _matchLines  = new();
    private readonly List<string> _beliefLines = new();
    private int _matchIndex;

    private MatchData _m;
    private bool _matchInProgress;
    private CardBeliefs _playerBeliefs;      // reconstruction of what the HUMAN knows
    private BeliefReport _lastAiBelief;      // for the Cambio-calibration columns

    private int _lastTurnOwner = -1;
    private GamePhase _prevPhase;
    private float _turnStartRt;
    private bool _turnFirstActionRecorded;

    private GameManager _gm;
    private string _sessionId;

    // ==================================================================== data

    private class MatchData
    {
        public int Index;
        public string StartedUtc;
        public float startRt;
        public bool completed;
        public int winnerSide = -2;              // -2 unset, -1 draw, 0 player, 1 ai
        public bool bayesianOn;

        public int[] score = new int[2];
        public int[] turns = new int[2];
        public int plies;
        public int[] cardsEnd = new int[2];

        // --- drawn-card decisions ---
        public int[] swaps = new int[2];
        public int[] discards = new int[2];
        public int[] unknownSwaps = new int[2];       // swapped into a slot they could not identify
        public int[] wastefulSwaps = new int[2];      // swap that RAISED their own score
        public readonly List<double>[] swapDelta = { new(), new() };  // placed - displaced (negative = improvement)

        // --- powers ---
        public int[] powerDrawn = new int[2];                 // power cards that passed through their hand
        public int[,] powerPlayed = new int[2, 5];            // actually activated, by CardPower
        public int[,] powerBuried = new int[2, 5];            // swapped into hand instead of played
        public int[] powerMatchedAway = new int[2];           // power card spent as a drawn-card match
        public int[] powerTargetsOpp = new int[2];            // power that touched the other side
        public readonly List<double>[] powerSwapGain = { new(), new() }; // gave - got (positive = good for actor)

        // --- matching ---
        public int[] matchAttempts = new int[2];
        public int[] matchSuccess = new int[2];
        public int[] matchFail = new int[2];
        public int[] matchOnOwn = new int[2];
        public int[] matchOnOpp = new int[2];
        public int[] penalties = new int[2];

        // --- move distribution ---
        public readonly Dictionary<CommandType, int>[] moves = { new(), new() };

        // --- cambio ---
        public int cambioCaller = -2;
        public int cambioPly = -1;
        public int cambioCallerTurn = -1;
        public double cambioTimeS = -1;
        public int cambioDrawpile = -1;
        public int[] cambioCards = { -1, -1 };
        public double cambioAiBelievedScore = double.NaN;
        public int[] cambioActualScore = { -1, -1 };

        // --- decision latency ---
        public readonly List<double>[] decisionMs = { new(), new() };
    }

    // ============================================================ lifecycle

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"); 
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
        StartCoroutine(HookRoutine());
    }

    private void OnDestroy()
    {
        if (Instance == this) SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode mode) => StartCoroutine(HookRoutine());

    private IEnumerator HookRoutine()
    {
        int guard = 0;
        while ((GameManager.Instance == null || GameManager.Instance.State == null) && guard++ < 600)
            yield return null;

        var gm = GameManager.Instance;
        if (gm == null || gm.State == null) yield break;
        if (ReferenceEquals(gm, _gm)) yield break;

        Subscribe(gm);
        _gm = gm;
        BeginNewMatch();
    }

    private void Subscribe(GameManager gm)
    {
        gm.OnPhaseChanged     += HandlePhase;
        gm.OnCommandApplied   += HandleCommand;      // signature now carries actorSide
        gm.OnEffectApplied    += HandleEffect;
        gm.OnAiBeliefSnapshot += HandleBelief;       // new event on GameManager
        gm.OnAiSearchDecision += HandleAiDecision;   // search latency only
        gm.OnGameOver         += HandleGameOver;
    }

    // ============================================================ match setup

    private void BeginNewMatch()
    {
        if (_matchInProgress && _m != null && !_m.completed)
            FinalizeMatch(false, -2);

        var st = _gm.State;
        _matchIndex++;
        _m = new MatchData
        {
            Index = _matchIndex,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            startRt = Time.realtimeSinceStartup,
            bayesianOn = _gm.AiUsesBayesian
        };
        _matchInProgress = true;
        _lastAiBelief = null;

        // Reconstruct the human's knowledge exactly like the AI does for itself, so
        // "did they swap into a slot they couldn't identify" is symmetric across sides.
        _playerBeliefs = new CardBeliefs(P, st.HandSize, st.PenaltySize);
        for (int i = 0; i < 2 && i < st.HandSize; i++)
        {
            var slot = new SlotRef(P, Zone.Hand, i);
            _playerBeliefs.SetKnow(slot, st.GetCard(slot));
        }

        _lastTurnOwner = -1;
        _prevPhase = st.Phase;
        _turnFirstActionRecorded = false;
        _turnStartRt = Time.realtimeSinceStartup;
    }

    // ============================================================ event hooks

    private void HandlePhase(GamePhase phase, bool isPlayerTurn)
    {
        if (_m == null) return;

        // A power activates exactly when we transition INTO UsingPower.
        if (phase == GamePhase.UsingPower && _prevPhase != GamePhase.UsingPower)
        {
            int side = isPlayerTurn ? P : A;
            var pw = _gm.State.ActivePower;
            if (pw != CardPower.None) _m.powerPlayed[side, (int)pw]++;
        }

        if (phase == GamePhase.DrawingCard)
        {
            int owner = isPlayerTurn ? P : A;
            if (owner != _lastTurnOwner)
            {
                _lastTurnOwner = owner;
                _m.turns[owner]++;
                _turnStartRt = Time.realtimeSinceStartup;
                _turnFirstActionRecorded = false;

                if (_gm.State != null) { _m.score[P] = _gm.GetScore(true); _m.score[A] = _gm.GetScore(false); }
            }
        }

        _prevPhase = phase;
    }

    private void HandleCommand(CommandType type, bool wasPlayerTurn, int actorSide)
    {
        if (_m == null) return;
        _m.plies++;

        int side = actorSide == A ? A : P;
        _m.moves[side].TryGetValue(type, out var n);
        _m.moves[side][type] = n + 1;

        // Human decision latency: wall clock from turn start to the first turn-consuming action.
        if (side == P && !_turnFirstActionRecorded &&
            (type == CommandType.DrawFromDeck || type == CommandType.CallCambio))
        {
            _m.decisionMs[P].Add((Time.realtimeSinceStartup - _turnStartRt) * 1000.0);
            _turnFirstActionRecorded = true;
        }

        if (type == CommandType.DiscardDrawn) _m.discards[side]++;

        if (type == CommandType.CallCambio)
        {
            _m.cambioCaller     = side;
            _m.cambioPly        = _m.plies;
            _m.cambioCallerTurn = _m.turns[side];
            _m.cambioTimeS      = Time.realtimeSinceStartup - _m.startRt;
            _m.cambioDrawpile   = _gm.State != null ? _gm.State.DrawPileCount : -1;
            _m.cambioCards[P]   = CountCards(P);
            _m.cambioCards[A]   = CountCards(A);
            _m.cambioActualScore[P] = _gm.GetScore(true);
            _m.cambioActualScore[A] = _gm.GetScore(false);

            // Calibration: what did the AI THINK it was holding when it pulled the trigger?
            if (side == A && _lastAiBelief != null)
                _m.cambioAiBelievedScore = _lastAiBelief.BelievedOwnScore;
        }
    }

    private void HandleEffect(GameEffect fx, int actorSide)
    {
        if (_m == null) return;
        int side = actorSide == A ? A : P;
        bool actorIsPlayer = side == P;

        switch (fx.Kind)
        {
            case EffectKind.CardDrawn:
                if (fx.Card.Power != CardPower.None) _m.powerDrawn[side]++;
                break;

            case EffectKind.SlotsSwapped:
                if (fx.Slot2.IsNone)
                {
                    // Drawn card swapped into a slot. Card = placed, Card2 = displaced.
                    bool known = KnownBy(side, fx.Slot);   // checked BEFORE the belief update below
                    _m.swaps[side]++;
                    if (!known) _m.unknownSwaps[side]++;

                    double delta = fx.Card.Value - fx.Card2.Value;   // negative = they improved
                    _m.swapDelta[side].Add(delta);
                    if (delta > 0) _m.wastefulSwaps[side]++;

                    // A power card buried in hand is a power NOT played.
                    if (fx.Card.Power != CardPower.None) _m.powerBuried[side, (int)fx.Card.Power]++;
                }
                else if (fx.Slot.Side != fx.Slot2.Side)
                {
                    // Cross-side swap from a power. The effect carries no cards, but the swap has
                    // already been applied, so read the post-swap truth straight off the state:
                    // the actor's slot now holds what it received, the other slot what it gave.
                    _m.powerTargetsOpp[side]++;

                    SlotRef mine = fx.Slot.Side == side ? fx.Slot : fx.Slot2;
                    SlotRef theirs = fx.Slot.Side == side ? fx.Slot2 : fx.Slot;
                    if (_gm.State != null)
                    {
                        int got  = _gm.State.GetCard(mine).Value;
                        int gave = _gm.State.GetCard(theirs).Value;
                        _m.powerSwapGain[side].Add(gave - got);   // positive = dumped high, took low
                    }
                }
                break;

            case EffectKind.SlotRevealed:
                if (fx.Slot.Side != side) _m.powerTargetsOpp[side]++;
                break;

            case EffectKind.MatchResolved:
                if (fx.Slot.IsNone)
                {
                    // Drawn card auto-matched the discard: turn ends, any power is forgone.
                    if (fx.Card.Power != CardPower.None) _m.powerMatchedAway[side]++;
                }
                else
                {
                    _m.matchAttempts[side]++;
                    if (fx.Success)
                    {
                        _m.matchSuccess[side]++;
                        if (fx.Slot.Side == side) _m.matchOnOwn[side]++;
                        else _m.matchOnOpp[side]++;
                    }
                    else _m.matchFail[side]++;
                }
                break;

            case EffectKind.PenaltyAdded:
                _m.penalties[fx.Success ? P : A]++;   // Success == forPlayer
                break;
        }

        _playerBeliefs.Update(fx, actorIsPlayer);
    }

    /// <summary>One row per active slot, every time the AI commits to a move.</summary>
    private void HandleBelief(BeliefReport r)
    {
        if (_m == null || r == null) return;
        _lastAiBelief = r;
        if (!logBeliefRows || r.Slots == null) return;

        foreach (var s in r.Slots)
        {
            var v = new Dictionary<string, string>
            {
                ["session_id"]           = _sessionId,
                ["match_index"]          = I(_m.Index),
                ["agent_label"]          = agentLabel,
                ["bayesian_on"]          = r.BayesianOn ? "1" : "0",
                ["ply"]                  = I(_m.plies),
                ["ai_turn"]              = I(_m.turns[A]),
                ["phase"]                = r.Phase.ToString(),
                ["power_step"]           = r.Step.ToString(),
                ["chosen_move"]          = r.Chosen.Type.ToString(),
                ["chosen_slot"]          = r.Chosen.Slot.IsNone ? "" : r.Chosen.Slot.ToString(),
                ["believed_own_score"]   = F(r.BelievedOwnScore),
                ["actual_ai_score"]      = I(r.ActualOwnScore),
                ["actual_player_score"]  = I(r.ActualOppScore),
                ["belief_error"]         = F(r.BelievedOwnScore - r.ActualOwnScore),
                ["opp_global_tilt"]      = F(r.OppGlobalTilt),
                ["opp_turn_count"]       = I(r.OppTurnCount),
                ["hidden_count"]         = I(r.HiddenCount),
                ["known_own_count"]      = I(r.KnownOwnCount),
                ["known_opp_count"]      = I(r.KnownOppCount),

                ["slot"]                 = s.Slot.ToString(),
                ["slot_side"]            = s.Slot.Side == A ? "AI" : "Player",
                ["slot_zone"]            = s.Slot.Zone.ToString(),
                ["slot_index"]           = I(s.Slot.Index),
                ["is_opponent_slot"]     = s.IsOpponent ? "1" : "0",
                ["is_chosen_slot"]       = (!r.Chosen.Slot.IsNone && r.Chosen.Slot.Equals(s.Slot)) ? "1" : "0",
                ["known"]                = s.Known ? "1" : "0",
                ["opp_knows"]            = s.OppKnows ? "1" : "0",
                ["tilt_raw"]             = F(s.TiltRaw),
                ["tilt_eff"]             = F(s.TiltEff),
                ["true_value"]           = I(s.TrueValue),
                ["true_number"]          = I(s.TrueNumber),
            };
            EmitBelief(v);
        }
    }

    /// <summary>AI "decision time" is search wall-clock. Decisions with a single legal move
    /// never run a search and so never fire this — that is correct, they are not decisions.</summary>
    private void HandleAiDecision(IsmctsReport r)
    {
        if (_m == null || r == null) return;
        _m.decisionMs[A].Add(r.ElapsedMs);
    }

    private void HandleGameOver(int winnerSide) => FinalizeMatch(true, winnerSide);

    // ============================================================ finalisation

    private void FinalizeMatch(bool completed, int winnerSide)
    {
        if (_m == null) return;

        _m.completed = completed;
        _m.winnerSide = winnerSide;
        if (_gm != null && _gm.State != null)
        {
            _m.score[P] = _gm.GetScore(true);
            _m.score[A] = _gm.GetScore(false);
            _m.cardsEnd[P] = CountCards(P);
            _m.cardsEnd[A] = CountCards(A);
        }

        string line = BuildMatchLine(_m);
        _matchLines.Add(line);
        if (writeLiveFiles) AppendLive(LiveMatchPath, MatchHeader, line);

        _matchInProgress = false;
        _m = null;
    }

    private int CountCards(int side)
    {
        if (_gm == null || _gm.State == null) return -1;
        int n = 0;
        foreach (var _ in _gm.State.GetActiveSlots(side)) n++;
        return n;
    }

    private bool KnownBy(int side, SlotRef slot)
    {
        // The human's knowledge is reconstructed here; the AI reports its own.
        if (side == P) return _playerBeliefs.Known.ContainsKey(slot);
        return _lastAiBelief != null && _lastAiBelief.Slots != null
               && _lastAiBelief.Slots.Exists(s => s.Known && s.Slot.Equals(slot));
    }

    // ============================================================ match row

    private string BuildMatchLine(MatchData m)
    {
        double dur = Time.realtimeSinceStartup - m.startRt;
        var f = new List<string>
        {
            Csv(_sessionId),
            I(m.Index),
            Csv(m.StartedUtc),
            Csv(agentLabel),
            m.bayesianOn ? "1" : "0",
            m.completed ? "1" : "0",
            SideName(m.winnerSide),
            I(m.score[P]), I(m.score[A]), I(m.score[P] - m.score[A]),
            I(m.turns[P]), I(m.turns[A]), I(m.plies), F(dur),
            I(m.cardsEnd[P]), I(m.cardsEnd[A]),
        };

        // Symmetric behavioural block, player first then AI.
        foreach (int s in new[] { P, A })
        {
            int sw = m.swaps[s], di = m.discards[s];
            f.Add(I(sw));
            f.Add(I(di));
            f.Add(F(sw + di > 0 ? (double)sw / (sw + di) : double.NaN));
            f.Add(I(m.unknownSwaps[s]));
            f.Add(F(sw > 0 ? (double)m.unknownSwaps[s] / sw : double.NaN));
            f.Add(I(m.wastefulSwaps[s]));
            f.Add(F(Avg(m.swapDelta[s])));

            int played = 0, buried = 0;
            foreach (var pw in PowerKinds) { played += m.powerPlayed[s, (int)pw]; buried += m.powerBuried[s, (int)pw]; }
            f.Add(I(m.powerDrawn[s]));
            f.Add(I(played));
            f.Add(I(buried));
            f.Add(I(m.powerMatchedAway[s]));
            f.Add(F(m.powerDrawn[s] > 0 ? (double)played / m.powerDrawn[s] : double.NaN));  // play rate
            foreach (var pw in PowerKinds) f.Add(I(m.powerPlayed[s, (int)pw]));
            foreach (var pw in PowerKinds) f.Add(I(m.powerBuried[s, (int)pw]));
            f.Add(I(m.powerTargetsOpp[s]));
            f.Add(F(Avg(m.powerSwapGain[s])));

            f.Add(I(m.matchAttempts[s]));
            f.Add(I(m.matchSuccess[s]));
            f.Add(I(m.matchFail[s]));
            f.Add(F(m.matchAttempts[s] > 0 ? (double)m.matchSuccess[s] / m.matchAttempts[s] : double.NaN));
            f.Add(I(m.matchOnOwn[s]));
            f.Add(I(m.matchOnOpp[s]));
            f.Add(I(m.penalties[s]));

            f.Add(F(Avg(m.decisionMs[s])));
            f.Add(F(Min(m.decisionMs[s])));
            f.Add(F(Max(m.decisionMs[s])));

            foreach (var mk in MoveKinds) { m.moves[s].TryGetValue(mk, out var c); f.Add(I(c)); }
            f.Add(Csv(MostUsed(m.moves[s])));
        }

        // Cambio block.
        f.Add(CallerName(m.cambioCaller));
        f.Add(m.cambioPly >= 0 ? I(m.cambioPly) : "");
        f.Add(m.cambioCallerTurn >= 0 ? I(m.cambioCallerTurn) : "");
        f.Add(m.cambioTimeS >= 0 ? F(m.cambioTimeS) : "");
        f.Add(m.cambioDrawpile >= 0 ? I(m.cambioDrawpile) : "");
        f.Add(m.cambioCards[P] >= 0 ? I(m.cambioCards[P]) : "");
        f.Add(m.cambioCards[A] >= 0 ? I(m.cambioCards[A]) : "");
        f.Add(m.cambioActualScore[P] >= 0 ? I(m.cambioActualScore[P]) : "");
        f.Add(m.cambioActualScore[A] >= 0 ? I(m.cambioActualScore[A]) : "");
        f.Add(F(m.cambioAiBelievedScore));
        f.Add(double.IsNaN(m.cambioAiBelievedScore) ? "" : F(m.cambioAiBelievedScore - m.cambioActualScore[A]));
        // Was the caller genuinely ahead at the moment of the call?
        f.Add(m.cambioCaller < 0 ? "" :
              (m.cambioActualScore[m.cambioCaller] < m.cambioActualScore[1 - m.cambioCaller] ? "1" : "0"));

        return string.Join(",", f);
    }

    private static string SideBlockHeader(string p) =>
        $"{p}_swaps,{p}_discards,{p}_swap_rate,{p}_unknown_swaps,{p}_unknown_swap_rate,{p}_wasteful_swaps,{p}_swap_value_delta_avg," +
        $"{p}_power_cards_drawn,{p}_powers_played,{p}_powers_buried,{p}_powers_matched_away,{p}_power_play_rate," +
        $"{p}_played_look_own,{p}_played_look_opp,{p}_played_blind_swap,{p}_played_look_and_swap," +
        $"{p}_buried_look_own,{p}_buried_look_opp,{p}_buried_blind_swap,{p}_buried_look_and_swap," +
        $"{p}_power_targets_opponent,{p}_power_swap_gain_avg," +
        $"{p}_match_attempts,{p}_match_success,{p}_match_fail,{p}_match_hit_rate,{p}_matches_on_own,{p}_matches_on_opponent,{p}_penalties," +
        $"{p}_decision_ms_avg,{p}_decision_ms_min,{p}_decision_ms_max," +
        $"{p}_mv_draw,{p}_mv_discard,{p}_mv_swap_drawn,{p}_mv_use_power,{p}_mv_match,{p}_mv_give,{p}_mv_confirm_trade,{p}_mv_finish_peek,{p}_mv_cambio," +
        $"{p}_most_used_move";

    private static string MatchHeader =>
        "session_id,match_index,timestamp_utc,agent_label,bayesian_on,completed,winner," +
        "player_score,ai_score,score_margin,player_turns,ai_turns,plies,duration_s,player_cards_end,ai_cards_end," +
        SideBlockHeader("player") + "," + SideBlockHeader("ai") + "," +
        "cambio_caller,cambio_ply,cambio_caller_turn,cambio_time_s,cambio_drawpile_remaining," +
        "cambio_player_cards,cambio_ai_cards,cambio_player_score,cambio_ai_score," +
        "cambio_ai_believed_score,cambio_ai_belief_error,cambio_caller_was_ahead";

    // ============================================================ belief rows

    private static readonly string[] BeliefCols =
    {
        "session_id","match_index","agent_label","bayesian_on","ply","ai_turn","phase","power_step","chosen_move","chosen_slot",
        "believed_own_score","actual_ai_score","actual_player_score","belief_error","opp_global_tilt","opp_turn_count",
        "hidden_count","known_own_count","known_opp_count",
        "slot","slot_side","slot_zone","slot_index","is_opponent_slot","is_chosen_slot","known","opp_knows",
        "tilt_raw","tilt_eff","true_value","true_number"
    };
    private static string BeliefHeader => string.Join(",", BeliefCols);

    private void EmitBelief(Dictionary<string, string> v)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < BeliefCols.Length; i++)
        {
            if (i > 0) sb.Append(',');
            v.TryGetValue(BeliefCols[i], out var val);
            sb.Append(Csv(val ?? ""));
        }
        string line = sb.ToString();
        _beliefLines.Add(line);
        if (writeLiveFiles) AppendLive(LiveBeliefPath, BeliefHeader, line);
    }

    // ============================================================ CSV export

    public string ExportCsv()
    {
        try
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string mPath = Path.Combine(Dir, $"cambio_matches_{SafeLabel}_{ts}.csv");
            string bPath = Path.Combine(Dir, $"cambio_beliefs_{SafeLabel}_{ts}.csv");

            var matchOut = new List<string>(_matchLines);
            if (_matchInProgress && _m != null)
            {
                if (_gm != null && _gm.State != null)
                {
                    _m.score[P] = _gm.GetScore(true); _m.score[A] = _gm.GetScore(false);
                    _m.cardsEnd[P] = CountCards(P); _m.cardsEnd[A] = CountCards(A);
                }
                matchOut.Add(BuildMatchLine(_m));
            }

            using (var sw = new StreamWriter(mPath, false))
            {
                sw.WriteLine(MatchHeader);
                foreach (var l in matchOut) sw.WriteLine(l);
            }

            if (logBeliefRows)
            {
                using var sw = new StreamWriter(bPath, false);
                sw.WriteLine(BeliefHeader);
                foreach (var l in _beliefLines) sw.WriteLine(l);
            }

            Debug.Log($"[MatchTracker] Exported {matchOut.Count} match row(s) -> {mPath}");
            if (logBeliefRows) Debug.Log($"[MatchTracker] Exported {_beliefLines.Count} belief row(s) -> {bPath}");
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(mPath);
#endif
            return mPath;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MatchTracker] export failed: {e}");
            return null;
        }
    }

    private void AppendLive(string path, string header, string line)
    {
        try
        {
            bool exists = File.Exists(path);
            using var sw = new StreamWriter(path, append: true);
            if (!exists) sw.WriteLine(header);
            sw.WriteLine(line);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MatchTracker] live write failed: {e.Message}");
        }
    }

    private string Dir
    {
        get
        {
            string root = useDownloadsFolder ? GetDownloadsFolder() : Application.persistentDataPath;
            var d = Path.Combine(root, outputSubfolder);
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string GetDownloadsFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");

        string downloads = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads");
        if (string.IsNullOrEmpty(downloads) || !Directory.Exists(downloads))
            return Application.persistentDataPath;
        return downloads;
    }

    private string SafeLabel => string.IsNullOrEmpty(agentLabel) ? "unlabeled" : agentLabel.Replace(' ', '_');
    private string LiveMatchPath  => Path.Combine(Dir, $"cambio_matches_{SafeLabel}_live.csv");
    private string LiveBeliefPath => Path.Combine(Dir, $"cambio_beliefs_{SafeLabel}_live.csv");

    // ============================================================ helpers

    private static string MostUsed(Dictionary<CommandType, int> d)
    {
        string best = ""; int bn = -1;
        foreach (var kv in d) if (kv.Value > bn) { bn = kv.Value; best = kv.Key.ToString(); }
        return best;
    }

    private static double Avg(List<double> xs){ if (xs.Count == 0) return double.NaN; double s = 0; foreach (var x in xs) s += x; return s / xs.Count; }
    private static double Max(List<double> xs){ if (xs.Count == 0) return double.NaN; double m = double.NegativeInfinity; foreach (var x in xs) if (x > m) m = x; return m; }
    private static double Min(List<double> xs){ if (xs.Count == 0) return double.NaN; double m = double.PositiveInfinity; foreach (var x in xs) if (x < m) m = x; return m; }

    private static string F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string SideName(int s) =>
        s == P ? "Player" : s == A ? "AI" : s == -1 ? "Draw" : "None";
    private static string CallerName(int s) =>
        s == P ? "Player" : s == A ? "AI" : "None";

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}