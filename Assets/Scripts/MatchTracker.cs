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
    [Tooltip("Write CSVs to the OS Downloads folder instead of Application.persistentDataPath.")]
    public bool useDownloadsFolder = true;
    [Tooltip("Subfolder created inside the chosen output root.")]
    public string outputSubfolder = "CambioTelemetry";
    [Tooltip("Append every finished match to *_live.csv immediately, so nothing is lost if you quit Play mode without exporting.")]
    public bool writeLiveFiles = true;
    [Tooltip("Also log per-turn / per-decision detail rows (larger files).")]
    public bool logDetailRows = true;

    // ---- accumulated across the whole play session (survives scene reloads) ----
    private readonly List<string> _summaryLines = new();
    private readonly List<string> _detailLines  = new();
    private int _matchIndex;

    // ---- current match ----
    private MatchData _m;
    private bool _matchInProgress;
    private CardBeliefs _playerBeliefs;

    // ---- per-turn tracking ----
    private int _lastTurnOwner = -1;   // 0 player, 1 ai
    private GamePhase _prevPhase;
    private float _turnStartRt;
    private bool  _turnFirstActionRecorded;
    private double _turnDecisionMs = -1;
    private string _turnAction = "";
    private bool  _turnDidSwap, _turnSwapUnknown, _turnDidDiscard, _turnPowerUsed, _turnMatched, _turnMatchSuccess;
    private string _turnPowerTargetSide = "";

    // ---- discard reaction clock ----
    private int   _lastDiscardId = -1;
    private float _lastDiscardChangeRt;

    private GameManager _gm;

    // ==================================================================== data

    private class MatchData
    {
        public int index;
        public string startedUtc;
        public float startRt;
        public bool completed;
        public int winnerSide = -2;              // -2 unset, -1 draw, 0 player, 1 ai
        public int playerScore, aiScore;
        public int playerTurns, aiTurns, plies;

        // player behavioural
        public int playerSwaps, playerDiscards, playerUnknownSwaps;
        public int pLookOwn, pLookOpp, pBlindSwap, pLookAndSwap, playerPowerTargetsAi;
        public int playerMatchAttempts, playerMatchSuccess, playerMatchFail, playerMatchesOnAi;
        public int playerPenalties, aiPenalties;
        public readonly List<double> matchReactionMs = new();
        public readonly List<double> playerDecisionMs = new();

        public bool playerCalledCambio;
        public int cambioCallerSide = -2;
        public int cambioCallPlayerTurn = -1;
        public double cambioCallTimeS = -1;
        public int cambioCallDrawpile = -1;

        // ai behavioural (so you can see if the AI itself shifts vs the adaptive version)
        public int aiSwaps, aiDiscards;
        public int aiLookOwn, aiLookOpp, aiBlindSwap, aiLookAndSwap;
        public int aiMatchAttempts, aiMatchSuccess;

        // ai performance
        public readonly List<double> aiDecisionMs = new();
        public readonly List<double> aiIterations = new();
        public readonly List<double> aiNodes = new();
        public readonly List<double> aiRootVisits = new();
        public double memPeakMB, memSumMB;
        public int memSamples;
    }

    // ============================================================ lifecycle

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
        StartCoroutine(HookRoutine());
    }

    private void OnDestroy()
    {
        if (Instance == this) SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode mode) => StartCoroutine(HookRoutine());

    // Wait until the freshly-loaded scene has a live GameManager+State, then hook it.
    // Idempotent: if we've already hooked this exact GameManager, do nothing.
    private IEnumerator HookRoutine()
    {
        int guard = 0;
        while ((GameManager.Instance == null || GameManager.Instance.State == null) && guard++ < 600)
            yield return null;

        var gm = GameManager.Instance;
        if (gm == null || gm.State == null) yield break;
        if (ReferenceEquals(gm, _gm)) yield break;   // same GM already hooked

        Subscribe(gm);
        _gm = gm;
        BeginNewMatch();
    }

    private void Subscribe(GameManager gm)
    {
        gm.OnPhaseChanged     += HandlePhase;
        gm.OnCommandApplied   += HandleCommand;
        gm.OnEffectApplied    += HandleEffect;      // requires the one-line GameManager addition
        gm.OnAiSearchDecision += HandleAiDecision;
        gm.OnGameOver         += HandleGameOver;
        // No need to unsubscribe the previous GM: it was destroyed on scene reload.
    }

    private void Update()
    {
        if (_gm == null || _gm.State == null) return;
        var top = _gm.State.TopDiscard;
        int id = top.IsNone ? -1 : top.Id;
        if (id != _lastDiscardId)
        {
            _lastDiscardId = id;
            _lastDiscardChangeRt = Time.realtimeSinceStartup;   // a new match opportunity opened
        }
    }

    // ============================================================ match setup

    private void BeginNewMatch()
    {
        // If a previous match never reached GameOver (you pressed R mid-game), close it as abandoned.
        if (_matchInProgress && _m != null && !_m.completed)
        {
            FlushPendingPlayerTurn();
            FinalizeMatch(false, -2);
        }

        var st = _gm.State;
        _matchIndex++;
        _m = new MatchData
        {
            index = _matchIndex,
            startedUtc = DateTime.UtcNow.ToString("o"),
            startRt = Time.realtimeSinceStartup
        };
        _matchInProgress = true;

        // Reconstruct the player's knowledge exactly like the AI does for itself.
        _playerBeliefs = new CardBeliefs(GameState.PlayerSide, st.HandSize, st.PenaltySize);
        for (int i = 0; i < 2 && i < st.HandSize; i++)
        {
            var slot = new SlotRef(GameState.PlayerSide, Zone.Hand, i);
            _playerBeliefs.SetKnow(slot, st.GetCard(slot));   // the opening two-card peek
        }

        _lastTurnOwner = -1;
        _prevPhase = st.Phase;
        ResetTurnFlags();
        _turnStartRt = Time.realtimeSinceStartup;
        _lastDiscardId = st.TopDiscard.IsNone ? -1 : st.TopDiscard.Id;
        _lastDiscardChangeRt = Time.realtimeSinceStartup;
    }

    private void ResetTurnFlags()
    {
        _turnFirstActionRecorded = false;
        _turnDecisionMs = -1;
        _turnAction = "";
        _turnDidSwap = _turnSwapUnknown = _turnDidDiscard = _turnPowerUsed = _turnMatched = _turnMatchSuccess = false;
        _turnPowerTargetSide = "";
    }

    // ============================================================ event hooks

    private void HandlePhase(GamePhase phase, bool isPlayerTurn)
    {
        if (_m == null) return;

        // A power activates exactly when we transition INTO UsingPower.
        if (phase == GamePhase.UsingPower && _prevPhase != GamePhase.UsingPower)
            RecordPowerBegin(isPlayerTurn);

        // A turn always (re)starts at DrawingCard, and only counts when the owner flips.
        if (phase == GamePhase.DrawingCard)
        {
            int owner = isPlayerTurn ? 0 : 1;
            if (owner != _lastTurnOwner)
            {
                if (_lastTurnOwner == 0) FlushPendingPlayerTurn();  // close the previous player turn

                _lastTurnOwner = owner;
                if (owner == 0) _m.playerTurns++; else _m.aiTurns++;
                _turnStartRt = Time.realtimeSinceStartup;
                ResetTurnFlags();

                if (_gm.State != null) { _m.playerScore = _gm.GetScore(true); _m.aiScore = _gm.GetScore(false); }
            }
        }

        _prevPhase = phase;
    }

    private void RecordPowerBegin(bool isPlayerTurn)
    {
        _turnPowerUsed = true;
        switch (_gm.State.ActivePower)
        {
            case CardPower.LookOwnCard:      if (isPlayerTurn) _m.pLookOwn++;     else _m.aiLookOwn++;     break;
            case CardPower.LookOpponentCard: if (isPlayerTurn) _m.pLookOpp++;     else _m.aiLookOpp++;     break;
            case CardPower.BlindSwap:        if (isPlayerTurn) _m.pBlindSwap++;   else _m.aiBlindSwap++;   break;
            case CardPower.LookAndSwap:      if (isPlayerTurn) _m.pLookAndSwap++; else _m.aiLookAndSwap++; break;
        }
    }

    private void HandleCommand(CommandType type, bool wasPlayerTurn)
    {
        if (_m == null) return;
        _m.plies++;

        // Decision time = wall-clock from turn start to the first turn-consuming action.
        if (wasPlayerTurn && !_turnFirstActionRecorded &&
            (type == CommandType.DrawFromDeck || type == CommandType.CallCambio))
        {
            _turnDecisionMs = (Time.realtimeSinceStartup - _turnStartRt) * 1000.0;
            _turnAction = type.ToString();
            _turnFirstActionRecorded = true;
            _m.playerDecisionMs.Add(_turnDecisionMs);
        }

        if (type == CommandType.DiscardDrawn)
        {
            if (wasPlayerTurn) { _m.playerDiscards++; _turnDidDiscard = true; }
            else _m.aiDiscards++;
        }

        if (type == CommandType.CallCambio)
        {
            _m.cambioCallerSide  = wasPlayerTurn ? GameState.PlayerSide : GameState.AISide;
            _m.cambioCallTimeS   = Time.realtimeSinceStartup - _m.startRt;
            _m.cambioCallDrawpile = _gm.State != null ? _gm.State.DrawPileCount : -1;
            if (wasPlayerTurn)
            {
                _m.playerCalledCambio = true;
                _m.cambioCallPlayerTurn = _m.playerTurns;
            }
        }
    }

    private void HandleEffect(GameEffect fx, int actorSide)
    {
        if (_m == null) return;
        bool actorIsPlayer = actorSide == GameState.PlayerSide;

        switch (fx.Kind)
        {
            case EffectKind.SlotsSwapped:
                if (fx.Slot2.IsNone)
                {
                    // Swapping the drawn card into a slot (Slot2 == None distinguishes this).
                    bool known = _playerBeliefs.Known.ContainsKey(fx.Slot);   // checked BEFORE the belief update below
                    if (actorIsPlayer)
                    {
                        _m.playerSwaps++; _turnDidSwap = true;
                        if (!known) { _m.playerUnknownSwaps++; _turnSwapUnknown = true; }
                    }
                    else _m.aiSwaps++;
                }
                else if (actorIsPlayer && (fx.Slot.Side == GameState.AISide || fx.Slot2.Side == GameState.AISide))
                {
                    // Blind swap / informed trade that touched one of the opponent's cards.
                    _m.playerPowerTargetsAi++; _turnPowerTargetSide = "AI";
                }
                break;

            case EffectKind.SlotRevealed:
                if (actorIsPlayer && fx.Slot.Side == GameState.AISide)
                {
                    _m.playerPowerTargetsAi++; _turnPowerTargetSide = "AI";   // peeked an opponent card
                }
                break;

            case EffectKind.MatchResolved:
                if (!fx.Slot.IsNone)   // Slot == None is a drawn-card auto-match, not a snap
                {
                    if (fx.ByPlayer)
                    {
                        _m.playerMatchAttempts++;
                        if (fx.Success)
                        {
                            _m.playerMatchSuccess++;
                            if (fx.Slot.Side == GameState.AISide) _m.playerMatchesOnAi++;
                        }
                        else _m.playerMatchFail++;

                        double rms = (Time.realtimeSinceStartup - _lastDiscardChangeRt) * 1000.0;
                        _m.matchReactionMs.Add(rms);
                        _turnMatched = true; _turnMatchSuccess = fx.Success;
                        EmitSnapRow(fx, rms);
                    }
                    else
                    {
                        _m.aiMatchAttempts++;
                        if (fx.Success) _m.aiMatchSuccess++;
                    }
                }
                break;

            case EffectKind.PenaltyAdded:
                if (fx.Success) _m.playerPenalties++; else _m.aiPenalties++;   // Success == forPlayer
                break;
        }

        // Update the reconstructed player knowledge with the same rules the AI uses.
        _playerBeliefs.Update(fx, actorIsPlayer);
    }

    private void HandleAiDecision(IsmctsReport report)
    {
        if (_m == null || report == null) return;

        double memMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        if (memMB > _m.memPeakMB) _m.memPeakMB = memMB;
        _m.memSumMB += memMB; _m.memSamples++;

        _m.aiDecisionMs.Add(report.ElapsedMs);
        _m.aiIterations.Add(report.IterationsDone);
        _m.aiNodes.Add(report.NodesExpanded);
        _m.aiRootVisits.Add(report.RootVisits);

        if (logDetailRows) EmitAiRow(report, memMB);
    }

    private void HandleGameOver(int winnerSide)
    {
        if (_m == null) return;
        FlushPendingPlayerTurn();
        FinalizeMatch(true, winnerSide);
    }

    // ============================================================ finalisation

    private void FlushPendingPlayerTurn()
    {
        if (!logDetailRows) return;
        if (_lastTurnOwner != 0) return;
        if (!_turnFirstActionRecorded && !_turnDidSwap && !_turnDidDiscard && !_turnPowerUsed && !_turnMatched) return;

        EmitPlayerTurnRow();

        // guard against a second emit for the same turn (e.g. boundary then game-over)
        _turnFirstActionRecorded = false;
        _turnDidSwap = _turnDidDiscard = _turnPowerUsed = _turnMatched = false;
    }

    private void FinalizeMatch(bool completed, int winnerSide)
    {
        if (_m == null) return;

        _m.completed = completed;
        _m.winnerSide = winnerSide;
        if (completed && _gm != null && _gm.State != null)
        {
            _m.playerScore = _gm.GetScore(true);
            _m.aiScore = _gm.GetScore(false);
        }

        string line = BuildSummaryLine(_m);
        _summaryLines.Add(line);
        if (writeLiveFiles) AppendLive(LiveSummaryPath, SummaryHeader, line);

        _matchInProgress = false;
        _m = null;
    }

    // ============================================================ detail rows

    private void EmitPlayerTurnRow()
    {
        var v = new Dictionary<string, string>();
        AddCommonContext(v);
        v["record_type"]  = "PLAYER_TURN";
        v["turn_owner"]   = "Player";
        v["action"]       = string.IsNullOrEmpty(_turnAction) ? "(no draw)" : _turnAction;
        v["decision_ms"]  = _turnDecisionMs >= 0 ? F(_turnDecisionMs) : "";
        v["swap_unknown"] = _turnDidSwap ? (_turnSwapUnknown ? "1" : "0") : "";
        v["power_used"]   = _turnPowerUsed ? "1" : "0";
        v["power_target_side"] = _turnPowerTargetSide;
        v["matched"]      = _turnMatched ? "1" : "0";
        v["match_success"] = _turnMatched ? (_turnMatchSuccess ? "1" : "0") : "";
        EmitDetail(v);
    }

    private void EmitSnapRow(GameEffect fx, double rms)
    {
        if (!logDetailRows) return;
        var v = new Dictionary<string, string>();
        AddCommonContext(v);
        v["record_type"]  = "PLAYER_SNAP";
        v["turn_owner"]   = (_gm != null && _gm.State != null && _gm.State.IsPlayerTurn) ? "Player" : "AI";
        v["action"]       = "AttemptMatch";
        v["matched"]      = "1";
        v["match_success"] = fx.Success ? "1" : "0";
        v["match_reaction_ms"] = F(rms);
        v["power_target_side"] = fx.Slot.Side == GameState.AISide ? "AI" : "Player";
        EmitDetail(v);
    }

    private void EmitAiRow(IsmctsReport report, double memMB)
    {
        MoveStat chosen = default, runnerUp = default;
        bool hasChosen = false, hasRun = false;
        foreach (var ms in report.Moves)
        {
            if (ms.IsChosen && !hasChosen) { chosen = ms; hasChosen = true; continue; }
            if (!hasRun) { runnerUp = ms; hasRun = true; }
        }

        var v = new Dictionary<string, string>();
        AddCommonContext(v);
        v["record_type"]       = "AI_DECISION";
        v["turn_owner"]        = "AI";
        v["action"]            = hasChosen ? chosen.Move.ToString() : "";
        v["ai_iterations"]     = I(report.IterationsDone);
        v["ai_elapsed_ms"]     = I((int)report.ElapsedMs);
        v["ai_root_visits"]    = I(report.RootVisits);
        v["ai_nodes_expanded"] = I(report.NodesExpanded);
        if (hasChosen)
        {
            v["ai_chosen_move"]       = chosen.Move.ToString();
            v["ai_chosen_visits"]     = I(chosen.Visits);
            v["ai_chosen_avg_reward"] = F(chosen.AvgReward);
        }
        if (hasRun)
        {
            v["ai_runnerup_move"]   = runnerUp.Move.ToString();
            v["ai_runnerup_visits"] = I(runnerUp.Visits);
        }
        v["mem_managed_mb"] = F(memMB);
        EmitDetail(v);
    }

    private void AddCommonContext(Dictionary<string, string> v)
    {
        v["match_index"]  = I(_m.index);
        v["agent_label"]  = agentLabel;
        v["ply"]          = I(_m.plies);
        v["player_score"] = (_gm != null && _gm.State != null) ? I(_gm.GetScore(true))  : "";
        v["ai_score"]     = (_gm != null && _gm.State != null) ? I(_gm.GetScore(false)) : "";
        v["drawpile_remaining"] = (_gm != null && _gm.State != null) ? I(_gm.State.DrawPileCount) : "";
        v["cambio_caller"] = CallerName(_m.cambioCallerSide);
        v["cum_player_swaps"]    = I(_m.playerSwaps);
        v["cum_player_discards"] = I(_m.playerDiscards);
        v["cum_player_powers"]   = I(_m.pLookOwn + _m.pLookOpp + _m.pBlindSwap + _m.pLookAndSwap);
        v["cum_player_matches_on_ai"] = I(_m.playerMatchesOnAi);
        v["t_ms"] = F((Time.realtimeSinceStartup - _m.startRt) * 1000.0);
    }

    private void EmitDetail(Dictionary<string, string> v)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < DetailCols.Length; i++)
        {
            if (i > 0) sb.Append(',');
            v.TryGetValue(DetailCols[i], out var val);
            sb.Append(Csv(val ?? ""));
        }
        string line = sb.ToString();
        _detailLines.Add(line);
        if (writeLiveFiles) AppendLive(LiveDetailPath, DetailHeader, line);
    }

    // ============================================================ CSV export

    /// <summary>Writes a clean timestamped snapshot of everything collected this session.
    /// Wire a UI Button's OnClick to TrackerExportButton.Export (which calls this).</summary>
    public string ExportCsv()
    {
        try
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string sPath = Path.Combine(Dir, $"cambio_matches_{SafeLabel}_{ts}.csv");
            string dPath = Path.Combine(Dir, $"cambio_turns_{SafeLabel}_{ts}.csv");

            var summaryOut = new List<string>(_summaryLines);
            if (_matchInProgress && _m != null)   // include the in-progress match as a provisional row
            {
                if (_gm != null && _gm.State != null) { _m.playerScore = _gm.GetScore(true); _m.aiScore = _gm.GetScore(false); }
                summaryOut.Add(BuildSummaryLine(_m));
            }

            using (var sw = new StreamWriter(sPath, false))
            {
                sw.WriteLine(SummaryHeader);
                foreach (var l in summaryOut) sw.WriteLine(l);
            }

            if (logDetailRows)
            {
                using var sw = new StreamWriter(dPath, false);
                sw.WriteLine(DetailHeader);
                foreach (var l in _detailLines) sw.WriteLine(l);
            }

            Debug.Log($"[MatchTracker] Exported {summaryOut.Count} match row(s) -> {sPath}");
            if (logDetailRows) Debug.Log($"[MatchTracker] Exported {_detailLines.Count} detail row(s) -> {dPath}");
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(sPath);
#endif
            return sPath;
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
        // Works in the Editor and desktop standalone builds on Windows / macOS / Linux.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");

        string downloads = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads");

        // If we can't resolve a real Downloads folder, fall back so exporting never silently fails.
        if (string.IsNullOrEmpty(downloads) || !Directory.Exists(downloads))
            return Application.persistentDataPath;

        return downloads;
    }

    private string SafeLabel => string.IsNullOrEmpty(agentLabel) ? "unlabeled" : agentLabel.Replace(' ', '_');
    private string LiveSummaryPath => Path.Combine(Dir, $"cambio_matches_{SafeLabel}_live.csv");
    private string LiveDetailPath  => Path.Combine(Dir, $"cambio_turns_{SafeLabel}_live.csv");

    // ============================================================ summary row

    private string BuildSummaryLine(MatchData m)
    {
        double dur = Time.realtimeSinceStartup - m.startRt;
        int swaps = m.playerSwaps, disc = m.playerDiscards;
        double swapRate = (swaps + disc) > 0 ? (double)swaps / (swaps + disc) : double.NaN;
        double unkRate  = swaps > 0 ? (double)m.playerUnknownSwaps / swaps : double.NaN;
        int powerTotal  = m.pLookOwn + m.pLookOpp + m.pBlindSwap + m.pLookAndSwap;
        int aiSwapDisc  = m.aiSwaps + m.aiDiscards;
        double aiSwapRate = aiSwapDisc > 0 ? (double)m.aiSwaps / aiSwapDisc : double.NaN;

        var f = new List<string>
        {
            I(m.index),
            Csv(m.startedUtc),
            Csv(agentLabel),
            m.completed ? "1" : "0",
            SideName(m.winnerSide),
            I(m.playerScore),
            I(m.aiScore),
            I(m.playerTurns),
            I(m.aiTurns),
            I(m.plies),
            F(dur),
            I(swaps),
            I(disc),
            F(swapRate),
            I(m.playerUnknownSwaps),
            F(unkRate),
            I(m.pLookOwn),
            I(m.pLookOpp),
            I(m.pBlindSwap),
            I(m.pLookAndSwap),
            I(powerTotal),
            I(m.playerPowerTargetsAi),
            I(m.playerMatchAttempts),
            I(m.playerMatchSuccess),
            I(m.playerMatchFail),
            I(m.playerMatchesOnAi),
            F(Avg(m.matchReactionMs)),
            F(Min(m.matchReactionMs)),
            F(Max(m.matchReactionMs)),
            F(Avg(m.playerDecisionMs)),
            F(Median(m.playerDecisionMs)),
            F(Min(m.playerDecisionMs)),
            F(Max(m.playerDecisionMs)),
            CallerName(m.cambioCallerSide),
            m.playerCalledCambio ? "1" : "0",
            m.cambioCallPlayerTurn >= 0 ? I(m.cambioCallPlayerTurn) : "",
            m.cambioCallTimeS >= 0 ? F(m.cambioCallTimeS) : "",
            m.cambioCallDrawpile >= 0 ? I(m.cambioCallDrawpile) : "",
            I(m.playerPenalties),
            I(m.aiPenalties),
            I(m.aiSwaps),
            I(m.aiDiscards),
            F(aiSwapRate),
            I(m.aiLookOwn),
            I(m.aiLookOpp),
            I(m.aiBlindSwap),
            I(m.aiLookAndSwap),
            I(m.aiMatchAttempts),
            I(m.aiMatchSuccess),
            I(m.aiDecisionMs.Count),
            F(Avg(m.aiDecisionMs)),
            F(Max(m.aiDecisionMs)),
            F(Sum(m.aiDecisionMs)),
            F(Avg(m.aiIterations)),
            F(Avg(m.aiNodes)),
            F(Avg(m.aiRootVisits)),
            F(m.memPeakMB),
            F(m.memSamples > 0 ? m.memSumMB / m.memSamples : double.NaN),
        };
        return string.Join(",", f);
    }

    private const string SummaryHeader =
        "match_index,timestamp_utc,agent_label,completed,winner,player_score,ai_score,player_turns,ai_turns,plies,duration_s," +
        "player_swaps,player_discards,swap_rate,player_unknown_swaps,unknown_swap_rate," +
        "p_look_own,p_look_opp,p_blind_swap,p_look_and_swap,player_power_total,player_power_targets_ai," +
        "player_match_attempts,player_match_success,player_match_fail,player_matches_on_ai," +
        "match_reaction_ms_avg,match_reaction_ms_min,match_reaction_ms_max," +
        "player_decision_ms_avg,player_decision_ms_median,player_decision_ms_min,player_decision_ms_max," +
        "cambio_caller,player_called_cambio,cambio_call_player_turn,cambio_call_time_s,cambio_call_drawpile_remaining," +
        "player_penalties,ai_penalties," +
        "ai_swaps,ai_discards,ai_swap_rate,ai_look_own,ai_look_opp,ai_blind_swap,ai_look_and_swap,ai_match_attempts,ai_match_success," +
        "ai_decisions,ai_decision_ms_avg,ai_decision_ms_max,ai_decision_ms_total,ai_iterations_avg,ai_nodes_avg,ai_root_visits_avg," +
        "mem_managed_mb_peak,mem_managed_mb_avg";

    private static readonly string[] DetailCols =
    {
        "record_type","match_index","agent_label","ply","turn_owner","action","decision_ms","swap_unknown","power_used",
        "power_target_side","matched","match_success","match_reaction_ms","player_score","ai_score","drawpile_remaining",
        "cambio_caller","cum_player_swaps","cum_player_discards","cum_player_powers","cum_player_matches_on_ai",
        "ai_iterations","ai_elapsed_ms","ai_root_visits","ai_nodes_expanded","ai_chosen_move","ai_chosen_visits",
        "ai_chosen_avg_reward","ai_runnerup_move","ai_runnerup_visits","mem_managed_mb","t_ms"
    };
    private static string DetailHeader => string.Join(",", DetailCols);

    // ============================================================ helpers

    private static double Avg(List<double> xs){ if (xs.Count == 0) return double.NaN; double s = 0; foreach (var x in xs) s += x; return s / xs.Count; }
    private static double Sum(List<double> xs){ double s = 0; foreach (var x in xs) s += x; return s; }
    private static double Max(List<double> xs){ if (xs.Count == 0) return double.NaN; double m = double.NegativeInfinity; foreach (var x in xs) if (x > m) m = x; return m; }
    private static double Min(List<double> xs){ if (xs.Count == 0) return double.NaN; double m = double.PositiveInfinity; foreach (var x in xs) if (x < m) m = x; return m; }
    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return double.NaN;
        var c = new List<double>(xs); c.Sort();
        int n = c.Count;
        return n % 2 == 1 ? c[n / 2] : (c[n / 2 - 1] + c[n / 2]) / 2.0;
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string SideName(int s) =>
        s == GameState.PlayerSide ? "Player" : s == GameState.AISide ? "AI" : s == -1 ? "Draw" : "None";
    private static string CallerName(int s) =>
        s == GameState.PlayerSide ? "Player" : s == GameState.AISide ? "AI" : "None";

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}