using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/* telemetry for agent comparison. emits three thin CSVs, all joinable on
   session_id and match_index:

     cambio_matches_*  one row per game, objective performance plus AI process metrics
     cambio_calls_*    one row per game in which a Cambio was called, call justification
     cambio_beliefs_*  one row per hidden slot per AI decision, belief-vs-truth calibration

   the matches table answers "who plays better". the calls table justifies each agent's
   Cambio decision on its own terms; baseline uses a flat believed_own_score cap, Bayesian
   uses guard_p_ahead from the score-distribution test. the beliefs table validates the
   per-card value estimates the Bayesian layer feeds the search, tilt vs true_value; with
   the layer off tilt is identically zero, so those rows act as a null control */
public class MatchTracker : MonoBehaviour
{
    public static MatchTracker Instance { get; private set; }

    [Header("Tagging")]
    [Tooltip("Written into every row so you can compare agent versions later, e.g. 'baseline' vs 'bayesian'.")]
    public string agentLabel = "baseline";

    [Header("Output")]
    public bool useDownloadsFolder = true;
    public string outputSubfolder = "CambioTelemetry";
    [Tooltip("Append every finished match/call/belief batch to *_live.csv immediately, so nothing is lost if you quit Play mode without exporting.")]
    public bool writeLiveFiles = true;
    [Tooltip("Log the per-decision belief calibration rows. Turn off for a pure behavioural run.")]
    public bool logBeliefRows = true;

    [Header("Session")]
    [Tooltip("How many games make up one data-collection run for this agent. The player plays this many, then the CSV is exported and nextSceneName is loaded.")]
    public int gamesToPlay = 10;
    [Tooltip("Scene loaded once every game in the session has been played and the CSV has been exported. Leave empty to just export and stay in this scene.")]
    public string nextSceneName;

    // public session accessors for the on-screen HUD
    public int  GamesToPlay     => gamesToPlay;
    public int  GamesCompleted  => _gamesCompleted;
    public int  PlayerWins      => _playerWins;
    public int  AiWins          => _aiWins;
    public int  Draws           => _draws;
    public bool SessionComplete => _gamesCompleted >= gamesToPlay;

    // fires whenever a game finishes and the session totals change, so any HUD can refresh
    public event Action OnSessionUpdated;

    private const int P = GameState.PlayerSide;   // 0
    private const int A = GameState.AISide;       // 1

    private static readonly CardPower[] PowerKinds =
    {
        CardPower.LookOwnCard, CardPower.LookOpponentCard, CardPower.BlindSwap, CardPower.LookAndSwap
    };

    // accumulated across the whole play session; survives scene reloads
    private readonly List<string> _matchLines  = new();
    private readonly List<string> _cambioLines = new();
    private readonly List<string> _beliefLines = new();
    private int _matchIndex;

    // session totals; survive same-scene reloads, a new scene taking over starts fresh
    private int _gamesCompleted;
    private int _playerWins;
    private int _aiWins;
    private int _draws;
    private string _ownerScene;

    private MatchData _m;
    private bool _matchInProgress;
    private BeliefReport _lastAiBelief;      // AI's decision-time snapshot; drives unknown-swap and cambio justification

    private int _lastTurnOwner = -1;
    private GamePhase _prevPhase;

    private GameManager _gm;
    private string _sessionId;

    private class MatchData
    {
        public int Index;
        public bool completed;
        public int winnerSide = -2;              // -2 unset, -1 draw, 0 player, 1 ai
        public bool bayesianOn;

        public int[] score = new int[2];
        public int[] turns = new int[2];
        public int plies;                        // used only to stamp belief rows

        // drawn-card decisions, AI only
        public int[] swaps = new int[2];
        public int[] discards = new int[2];
        public int[] unknownSwaps = new int[2];              // swapped into a slot it could not identify
        public readonly List<double>[] swapDelta = { new(), new() };  // placed - displaced; negative = improvement

        // steady-state uncertainty: mean number of own hidden slots across AI decisions
        public readonly List<double> hiddenOwn = new();

        // powers, AI only
        public int[] powerDrawn = new int[2];
        public int[,] powerPlayed = new int[2, 5];           // activated, by CardPower

        // matching, AI only
        public int[] matchAttempts = new int[2];
        public int[] matchSuccess = new int[2];
        public int[] matchFail = new int[2];
        public int[] matchOnOwn = new int[2];
        public int[] matchOnOpp = new int[2];
        public int[] penalties = new int[2];

        // decision latency, AI search wall-clock only
        public readonly List<double>[] decisionMs = { new(), new() };

        // cambio call, populated only if a call happened
        public int    cambioCaller = -2;
        public int    cambioCallerTurn = -1;
        public int[]  cambioScore = { -1, -1 };              // [P], [A] at the instant of the call
        public double cambioBelievedScore = double.NaN;      // flat-prior BelievedOwnScore, baseline's guard var
        public bool   guardEvaluated;                        // Bayesian guard ran on the calling decision
        public double guardOwnMean, guardOppMean, guardPAhead;
    }

    // lifecycle

    private void Awake()
    {
        _ownerScene = gameObject.scene.name;

        if (Instance != null && Instance != this)
        {
            if (Instance._ownerScene == _ownerScene)
            {
                Destroy(gameObject);
                return;
            }
            SceneManager.sceneLoaded -= Instance.OnSceneLoaded;
            Instance.StopAllCoroutines();
            Destroy(Instance.gameObject);
        }

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
        gm.OnCommandApplied   += HandleCommand;
        gm.OnEffectApplied    += HandleEffect;
        gm.OnAiBeliefSnapshot += HandleBelief;
        gm.OnAiSearchDecision += HandleAiDecision;
        gm.OnGameOver         += HandleGameOver;
    }

    // match setup

    private void BeginNewMatch()
    {
        if (_matchInProgress && _m != null && !_m.completed)
            FinalizeMatch(false, -2);

        _matchIndex++;
        _m = new MatchData
        {
            Index = _matchIndex,
            bayesianOn = _gm.AiUsesBayesian
        };
        _matchInProgress = true;
        _lastAiBelief = null;

        _lastTurnOwner = -1;
        _prevPhase = _gm.State.Phase;
    }

    // event hooks

    private void HandlePhase(GamePhase phase, bool isPlayerTurn)
    {
        if (_m == null) return;

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
            }
        }

        _prevPhase = phase;
    }

    private void HandleCommand(CommandType type, bool wasPlayerTurn, int actorSide)
    {
        if (_m == null) return;
        _m.plies++;

        int side = actorSide == A ? A : P;

        if (type == CommandType.DiscardDrawn) _m.discards[side]++;

        if (type == CommandType.CallCambio)
        {
            _m.cambioCaller     = side;
            _m.cambioCallerTurn = _m.turns[side];
            _m.cambioScore[P]   = _gm.GetScore(true);
            _m.cambioScore[A]   = _gm.GetScore(false);

            // justification: what did the AI base the call on?
            //   baseline uses flat BelievedOwnScore vs an absolute cap
            //   bayesian uses guard means plus P(ahead) from the distribution test
            if (side == A && _lastAiBelief != null)
            {
                _m.cambioBelievedScore = _lastAiBelief.BelievedOwnScore;
                if (_lastAiBelief.GuardEvaluated)
                {
                    _m.guardEvaluated = true;
                    _m.guardOwnMean   = _lastAiBelief.GuardMeanOwn;
                    _m.guardOppMean   = _lastAiBelief.GuardMeanOpp;
                    _m.guardPAhead    = _lastAiBelief.GuardPAhead;
                }
            }
        }
    }

    private void HandleEffect(GameEffect fx, int actorSide)
    {
        if (_m == null) return;
        int side = actorSide == A ? A : P;

        switch (fx.Kind)
        {
            case EffectKind.CardDrawn:
                if (fx.Card.Power != CardPower.None) _m.powerDrawn[side]++;
                break;

            case EffectKind.SlotsSwapped:
                // only the single-slot, drawn-into-slot, case carries swap quality; ignore
                // cross-side power swaps here since that's diagnostic style, not performance
                if (fx.Slot2.IsNone)
                {
                    _m.swaps[side]++;

                    double delta = fx.Card.Value - fx.Card2.Value;   // negative = they improved
                    _m.swapDelta[side].Add(delta);

                    // "blind" swaps only meaningful for the AI, whose beliefs we export
                    if (side == A && _lastAiBelief != null && _lastAiBelief.Slots != null &&
                        !_lastAiBelief.Slots.Exists(sl => sl.Known && sl.Slot.Equals(fx.Slot)))
                        _m.unknownSwaps[side]++;
                }
                break;

            case EffectKind.MatchResolved:
                if (!fx.Slot.IsNone)
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
    }

    /* records the AI's decision-time snapshot, accumulates own-hidden count, and optionally
       emits one calibration row per hidden slot */
    private void HandleBelief(BeliefReport r)
    {
        if (_m == null || r == null) return;
        _lastAiBelief = r;
        if (r.Slots == null) return;

        // steady-state uncertainty metric, counted every AI decision regardless of logging
        int hiddenOwn = 0;
        foreach (var s in r.Slots)
            if (!s.IsOpponent && !s.Known) hiddenOwn++;
        _m.hiddenOwn.Add(hiddenOwn);

        if (!logBeliefRows) return;

        foreach (var s in r.Slots)
        {
            if (s.Known) continue;   // known slots carry no belief signal

            var f = new List<string>
            {
                Csv(_sessionId),
                I(_m.Index),
                Csv(agentLabel),
                _m.bayesianOn ? "1" : "0",
                I(_m.plies),
                I(_m.turns[A]),
                s.IsOpponent ? "1" : "0",
                s.OppKnows   ? "1" : "0",
                F(s.TiltRaw),      // believed-value shift from beliefs alone
                F(s.TiltEff),      // believed-value shift the search actually consumed
                I(s.TrueValue),
            };
            string line = string.Join(",", f);
            _beliefLines.Add(line);
            if (writeLiveFiles) AppendLive(LiveBeliefPath, BeliefHeader, line);
        }
    }

    private void HandleAiDecision(IsmctsReport r)
    {
        if (_m == null || r == null) return;
        _m.decisionMs[A].Add(r.ElapsedMs);
    }

    private void HandleGameOver(int winnerSide) => FinalizeMatch(true, winnerSide);

    // finalisation

    private void FinalizeMatch(bool completed, int winnerSide)
    {
        if (_m == null) return;

        _m.completed = completed;
        _m.winnerSide = winnerSide;
        if (_gm != null && _gm.State != null)
        {
            _m.score[P] = _gm.GetScore(true);
            _m.score[A] = _gm.GetScore(false);
        }

        string mline = BuildMatchLine(_m);
        _matchLines.Add(mline);
        if (writeLiveFiles) AppendLive(LiveMatchPath, MatchHeader, mline);

        if (_m.cambioCaller >= 0)
        {
            string cline = BuildCambioLine(_m);
            _cambioLines.Add(cline);
            if (writeLiveFiles) AppendLive(LiveCambioPath, CambioHeader, cline);
        }

        _matchInProgress = false;
        _m = null;

        if (completed)
        {
            _gamesCompleted++;
            if (winnerSide == P) _playerWins++;
            else if (winnerSide == A) _aiWins++;
            else _draws++;

            OnSessionUpdated?.Invoke();
        }
    }

    public void AdvanceToNextGame()
    {
        if (SessionComplete)
        {
            FinishSessionAndAdvance();
            return;
        }

        string scene = string.IsNullOrEmpty(_ownerScene)
            ? SceneManager.GetActiveScene().name
            : _ownerScene;
        SceneManager.LoadScene(scene);
    }

    public void FinishSessionAndAdvance()
    {
        ExportCsv();

        if (!string.IsNullOrEmpty(nextSceneName))
            SceneManager.LoadScene(nextSceneName);
        else
            Debug.Log("[MatchTracker] Session complete and exported; no nextSceneName set.");
    }

    // match row

    private string BuildMatchLine(MatchData m)
    {
        int played = 0;
        foreach (var pw in PowerKinds) played += m.powerPlayed[A, (int)pw];

        var f = new List<string>
        {
            Csv(_sessionId),
            I(m.Index),
            Csv(agentLabel),
            m.bayesianOn ? "1" : "0",
            m.completed ? "1" : "0",
            SideName(m.winnerSide),
            I(m.score[A]), I(m.score[P]),

            // AI behavioural block; the human opponent is a confound, not a subject
            I(m.turns[A]),
            I(m.swaps[A]),
            I(m.discards[A]),
            F(Avg(m.swapDelta[A])),
            I(m.unknownSwaps[A]),
            F(Avg(m.hiddenOwn)),
            I(m.matchAttempts[A]),
            I(m.matchSuccess[A]),
            I(m.matchFail[A]),
            I(m.matchOnOwn[A]),
            I(m.matchOnOpp[A]),
            I(m.penalties[A]),
            I(m.powerDrawn[A]),
            I(played),
            I(m.powerPlayed[A, (int)CardPower.LookOwnCard]),
            I(m.powerPlayed[A, (int)CardPower.LookOpponentCard]),
            I(m.powerPlayed[A, (int)CardPower.BlindSwap]),
            I(m.powerPlayed[A, (int)CardPower.LookAndSwap]),
            F(Avg(m.decisionMs[A])),
        };
        return string.Join(",", f);
    }

    private static string MatchHeader =>
        "session_id,match_index,agent_label,bayesian_on,completed,winner,ai_score,player_score," +
        "ai_turns,ai_swaps,ai_discards,ai_swap_value_delta_avg,ai_unknown_swaps,ai_hidden_own_avg," +
        "ai_match_attempts,ai_match_success,ai_match_fail,ai_matches_on_own,ai_matches_on_opponent,ai_penalties," +
        "ai_power_cards_drawn,ai_powers_played," +
        "ai_power_look_own,ai_power_look_opp,ai_power_blind_swap,ai_power_look_swap,ai_decision_ms_avg";

    // cambio row

    private string BuildCambioLine(MatchData m)
    {
        bool wasAhead = m.cambioScore[m.cambioCaller] < m.cambioScore[1 - m.cambioCaller];

        var f = new List<string>
        {
            Csv(_sessionId),
            I(m.Index),
            Csv(agentLabel),
            m.bayesianOn ? "1" : "0",
            CallerName(m.cambioCaller),
            m.cambioCallerTurn >= 0 ? I(m.cambioCallerTurn) : "",
            m.cambioScore[A] >= 0 ? I(m.cambioScore[A]) : "",
            m.cambioScore[P] >= 0 ? I(m.cambioScore[P]) : "",
            wasAhead ? "1" : "0",
            double.IsNaN(m.cambioBelievedScore) ? "" : F(m.cambioBelievedScore),
            m.guardEvaluated ? F(m.guardOwnMean) : "",
            m.guardEvaluated ? F(m.guardOppMean) : "",
            m.guardEvaluated ? F(m.guardPAhead)  : "",
        };
        return string.Join(",", f);
    }

    private static string CambioHeader =>
        "session_id,match_index,agent_label,bayesian_on,cambio_caller,cambio_caller_turn," +
        "cambio_ai_score,cambio_player_score,cambio_caller_was_ahead," +
        "cambio_ai_believed_score,guard_believed_own_mean,guard_believed_opp_mean,guard_p_ahead";

    // belief header

    private static string BeliefHeader =>
        "session_id,match_index,agent_label,bayesian_on,ply,ai_turn," +
        "is_opponent_slot,opp_knows,tilt_raw,tilt_eff,true_value";

    // CSV export

    public string ExportCsv()
    {
        try
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string mPath = Path.Combine(Dir, $"cambio_matches_{SafeLabel}_{ts}.csv");
            string cPath = Path.Combine(Dir, $"cambio_calls_{SafeLabel}_{ts}.csv");
            string bPath = Path.Combine(Dir, $"cambio_beliefs_{SafeLabel}_{ts}.csv");

            var matchOut  = new List<string>(_matchLines);
            var cambioOut = new List<string>(_cambioLines);

            if (_matchInProgress && _m != null)
            {
                if (_gm != null && _gm.State != null)
                {
                    _m.score[P] = _gm.GetScore(true);
                    _m.score[A] = _gm.GetScore(false);
                }
                matchOut.Add(BuildMatchLine(_m));
                if (_m.cambioCaller >= 0) cambioOut.Add(BuildCambioLine(_m));
            }

            WriteFile(mPath, MatchHeader, matchOut);
            WriteFile(cPath, CambioHeader, cambioOut);
            if (logBeliefRows) WriteFile(bPath, BeliefHeader, _beliefLines);

            Debug.Log($"[MatchTracker] Exported {matchOut.Count} match / {cambioOut.Count} cambio row(s) -> {Dir}");
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

    private static void WriteFile(string path, string header, List<string> lines)
    {
        using var sw = new StreamWriter(path, false);
        sw.WriteLine(header);
        foreach (var l in lines) sw.WriteLine(l);
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
    private string LiveCambioPath => Path.Combine(Dir, $"cambio_calls_{SafeLabel}_live.csv");
    private string LiveBeliefPath => Path.Combine(Dir, $"cambio_beliefs_{SafeLabel}_live.csv");

    // helpers

    private static double Avg(List<double> xs){ if (xs.Count == 0) return double.NaN; double s = 0; foreach (var x in xs) s += x; return s / xs.Count; }

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