using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

/* headless baseline-vs-Bayesian self-play. plays many games with two AICambioAgents and, for
   each agent, emits the same three CSVs the human game does, matches / calls / beliefs, via a
   standalone AgentTelemetry collector that mirrors MatchTracker's schema exactly.

     baseline agent, label "ben", writes cambio_{matches,calls,beliefs}_ben_*.csv
     Bayesian agent, label "eva", writes cambio_{matches,calls,beliefs}_eva_*.csv

   all six land in Downloads/CambioTelemetry. needs no scene wiring, so no GameManager, UI or
   MatchTracker. drop on any GameObject, enter Play, press B.

   requires the perspective-correct agent, where Ucb and Evaluate use _mySide and Determinize's
   oppCambio is side-aware. MatchTracker and the human game are left completely untouched */
public class SelfPlay : MonoBehaviour
{
    [Header("Batch")]
    [SerializeField] private int totalGames = 1000;
    [SerializeField] private KeyCode runKey = KeyCode.B;
    [SerializeField] private int masterSeed = 12345;
    [SerializeField] private int gamesPerFrame = 1;
    [SerializeField] private int logEvery = 25;

    [Header("Rules (match your GameManager)")]
    [SerializeField] private int handSize = 4;
    [SerializeField] private int penaltyCount = 4;

    [Header("Labels")]
    [SerializeField] private string baselineLabel = "ben";
    [SerializeField] private string bayesianLabel = "eva";

    [Header("Design")]
    [Tooltip("Play each deck twice with the layer swapped between sides. Cancels deck luck AND the player-moves-first edge.")]
    [SerializeField] private bool mirroredPairs = true;
    [Tooltip("Let the non-active side snap its own matching card, mirroring GameManager's AI snap.")]
    [SerializeField] private bool enableReactiveSnaps = true;

    [Header("Performance")]
    [Tooltip("ISMCTS iterations per decision for BOTH agents during the batch. 0 = keep the agent default (4000). Lower = much faster; fair because both agents share the budget.")]
    [SerializeField] private int iterationsOverride = 1200;
    [Tooltip("Skip the per-iteration deck-consistency check. Big speedup once determinization is trusted.")]
    [SerializeField] private bool validateDeterminizations = false;
    [Tooltip("Write the per-slot belief calibration file. Off = skip building belief reports each decision (faster) and write no beliefs CSV.")]
    [SerializeField] private bool logBeliefRows = true;
    [Tooltip("Silence ISMCTS console logging during the batch (strongly recommended).")]
    [SerializeField] private bool muteSearchLogs = true;

    private bool _running;

    private void Update()
    {
        if (!_running && Input.GetKeyDown(runKey)) StartCoroutine(RunBatch());
    }

    // wire to a UI Button if you prefer clicking over the hotkey
    public void RunFromButton() { if (!_running) StartCoroutine(RunBatch()); }

    private IEnumerator RunBatch()
    {
        _running = true;
        bool prevLogs = MctsDebug.Enabled;
        if (muteSearchLogs) MctsDebug.Enabled = false;

        string sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var ben = new AgentTelemetry(sessionId, baselineLabel, logBeliefRows); // baseline
        var eva = new AgentTelemetry(sessionId, bayesianLabel, logBeliefRows); // Bayesian

        var sw = Stopwatch.StartNew();
        int gameIndex = 0, perFrame = Mathf.Max(1, gamesPerFrame);
        int evaWins = 0, benWins = 0, draws = 0;

        Debug.Log($"[SelfPlay] {totalGames} games (mirrored={mirroredPairs}, iters={(iterationsOverride > 0 ? iterationsOverride : 4000)}, beliefs={logBeliefRows}) ...");

        if (mirroredPairs)
        {
            int pairs = (totalGames + 1) / 2;
            var sides = new[] { GameState.AISide, GameState.PlayerSide };
            for (int p = 0; p < pairs && gameIndex < totalGames; p++)
            {
                int deckSeed = masterSeed + p;
                for (int k = 0; k < 2 && gameIndex < totalGames; k++)
                {
                    Tally(PlayOneGame(deckSeed, sides[k], ben, eva), ref evaWins, ref benWins, ref draws);
                    gameIndex++;
                    if (gameIndex % perFrame == 0) { MaybeLog(gameIndex, evaWins, benWins, draws); yield return null; }
                }
            }
        }
        else
        {
            for (int i = 0; i < totalGames; i++)
            {
                int bayesianSide = (i % 2 == 0) ? GameState.AISide : GameState.PlayerSide;
                Tally(PlayOneGame(masterSeed + i, bayesianSide, ben, eva), ref evaWins, ref benWins, ref draws);
                gameIndex++;
                if (gameIndex % perFrame == 0) { MaybeLog(gameIndex, evaWins, benWins, draws); yield return null; }
            }
        }

        sw.Stop();

        string dir = eva.Export();
        ben.Export();

        int n = evaWins + benWins + draws, decided = evaWins + benWins;
        double wr = decided > 0 ? (double)evaWins / decided : 0;
        double se = decided > 0 ? Math.Sqrt(wr * (1 - wr) / decided) : 0;
        double z  = decided > 0 ? (evaWins - decided / 2.0) / (Math.Sqrt(decided) / 2.0) : 0;

        Debug.Log(
            $"[SelfPlay] Done: {n} games in {sw.ElapsedMilliseconds} ms ({(n > 0 ? sw.ElapsedMilliseconds / (double)n : 0):F1} ms/game)\n" +
            $"  {bayesianLabel} (Bayesian) wins : {evaWins}\n" +
            $"  {baselineLabel} (baseline) wins : {benWins}\n" +
            $"  draws                          : {draws}\n" +
            $"  {bayesianLabel} winrate (decided): {wr:P1}  95% CI +/-{1.96 * se:P1}   sign-test z={z:F2}\n" +
            $"  Files -> {dir}");

        if (muteSearchLogs) MctsDebug.Enabled = prevLogs;
        _running = false;
    }

    private static void Tally(int winnerSide, ref int evaWins, ref int benWins, ref int draws)
    {
        // winnerSide is the Bayesian-relative result: 1 = Bayesian won, -1 = baseline, 0 = draw
        if (winnerSide > 0) evaWins++; else if (winnerSide < 0) benWins++; else draws++;
    }

    private void MaybeLog(int done, int eva, int ben, int draw)
    {
        if (logEvery <= 0 || done % logEvery != 0) return;
        Debug.Log($"[SelfPlay] {done} games — {bayesianLabel} {eva} / {baselineLabel} {ben} / draw {draw}");
    }

    // one game

    // returns +1 if the Bayesian agent won, -1 if baseline won, 0 draw
    private int PlayOneGame(int deckSeed, int bayesianSide, AgentTelemetry ben, AgentTelemetry eva)
    {
        int baselineSide = GameState.OpponentOf(bayesianSide);

        int[] deck = BuildDeck(deckSeed);
        var state = new GameState(deck, handSize, penaltyCount, deckSeed);

        var agents = new IAgent[2];
        var bayesAgent = MakeAgent(bayesianSide, true,  deckSeed, state);
        var baseAgent  = MakeAgent(baselineSide, false, deckSeed, state);
        agents[bayesianSide] = bayesAgent;
        agents[baselineSide] = baseAgent;

        // route each agent's own reports to its own collector
        eva.BeginMatch(bayesianSide, bayesianOn: true);
        ben.BeginMatch(baselineSide, bayesianOn: false);
        bayesAgent.OnSearchDecision += eva.OnDecision;
        baseAgent.OnSearchDecision  += ben.OnDecision;
        if (logBeliefRows)
        {
            bayesAgent.OnBeliefSnapshot += eva.OnBelief;
            baseAgent.OnBeliefSnapshot  += ben.OnBelief;
        }

        state.StartPlay();
        eva.OnPhase(state.Phase, state.ActiveSide, state.ActivePower);
        ben.OnPhase(state.Phase, state.ActiveSide, state.ActivePower);

        int steps = 0, lastActive = -1;
        const int maxSteps = 6000;

        while (!state.IsTerminal && steps++ < maxSteps)
        {
            int active = state.ActiveSide;

            if (enableReactiveSnaps && active != lastActive)
            {
                TryReactiveSnap(state, agents, ben, eva);
                if (state.IsTerminal) break;
                active = state.ActiveSide;
            }
            lastActive = active;

            List<GameCommand> legal = state.LegalMoves();
            if (legal.Count == 0) break;

            GameCommand cmd = agents[active].ChooseMove(state);  // fires belief and decision to the active agent's collector
            if (!legal.Contains(cmd)) cmd = legal[0];

            MoveResult r = state.Apply(cmd);
            if (!r.Ok) break;

            int sp = state.Score(GameState.PlayerSide), sa = state.Score(GameState.AISide);
            eva.OnCommand(cmd.Type, active, sp, sa);
            ben.OnCommand(cmd.Type, active, sp, sa);
            Feed(r.Effects, active, ben, eva);

            eva.OnPhase(state.Phase, state.ActiveSide, state.ActivePower);
            ben.OnPhase(state.Phase, state.ActiveSide, state.ActivePower);
        }

        int pScore = state.Score(GameState.PlayerSide);
        int aScore = state.Score(GameState.AISide);
        int winner = state.WinnerSide();

        eva.FinalizeMatch(winner, pScore, aScore);
        ben.FinalizeMatch(winner, pScore, aScore);

        return winner < 0 ? 0 : (winner == bayesianSide ? 1 : -1);
    }

    private void TryReactiveSnap(GameState state, IAgent[] agents, AgentTelemetry ben, AgentTelemetry eva)
    {
        if (state.Phase != GamePhase.DrawingCard || state.AwaitingGiveCard) return;
        int snapper = state.OpponentSide;
        if (agents[snapper] is AICambioAgent agent)
        {
            SlotRef s = agent.SnapOwn(state);
            if (!s.IsNone)
            {
                MoveResult r = state.TrySnap(snapper, s);
                if (r.Ok)
                {
                    Feed(r.Effects, snapper, ben, eva);
                    eva.OnPhase(state.Phase, state.ActiveSide, state.ActivePower);
                    ben.OnPhase(state.Phase, state.ActiveSide, state.ActivePower);
                }
            }
        }
    }

    private void Feed(List<GameEffect> fx, int actorSide, AgentTelemetry ben, AgentTelemetry eva)
    {
        if (fx == null) return;
        foreach (var e in fx)
        {
            eva.OnEffect(e, actorSide);
            ben.OnEffect(e, actorSide);
            // keeps both agents' beliefs in sync with the world
        }
    }

    private AICambioAgent MakeAgent(int side, bool bayesian, int deckSeed, GameState state)
    {
        int seed = 777 + deckSeed * 31 + side * 101;
        var a = new AICambioAgent(seed)
        {
            UseBayesianLayer = bayesian,
            ValidateDeterminizations = validateDeterminizations
        };
        if (iterationsOverride > 0) a.Iterations = iterationsOverride;
        a.OnNewGame(side, state);
        return a;
    }

    private int[] BuildDeck(int seed)
    {
        var rng = new System.Random(seed);
        int n = Card.DeckSize;
        var ids = new int[n];
        for (int i = 0; i < n; i++) ids[i] = i;
        for (int i = n - 1; i > 0; i--) { int j = rng.Next(i + 1); (ids[i], ids[j]) = (ids[j], ids[i]); }
        return ids;
    }
}

/* standalone telemetry collector, one instance per agent. reproduces MatchTracker's three CSV
   schemas verbatim, but from a configurable subject side so it can follow an agent that sits on
   PlayerSide. in each file the subject agent plays the "AI" role, the ai_* columns, and its
   opponent the "Player" role, exactly like a human-game export */
public class AgentTelemetry
{
    private const int P = GameState.PlayerSide;   // 0
    private const int A = GameState.AISide;       // 1

    private static readonly CardPower[] PowerKinds =
    {
        CardPower.LookOwnCard, CardPower.LookOpponentCard, CardPower.BlindSwap, CardPower.LookAndSwap
    };

    private readonly string _sessionId;
    private readonly string _label;
    private readonly bool _logBeliefRows;

    private readonly List<string> _matchLines  = new();
    private readonly List<string> _cambioLines = new();
    private readonly List<string> _beliefLines = new();
    private int _matchIndex;

    private Match _m;
    private BeliefReport _lastBelief;
    private int _lastTurnOwner = -1;
    private GamePhase _prevPhase;

    public AgentTelemetry(string sessionId, string label, bool logBeliefRows)
    {
        _sessionId = sessionId;
        _label = label;
        _logBeliefRows = logBeliefRows;
    }

    private class Match
    {
        public int Index;
        public int subjectSide;                  // the agent this file is about, the "AI" role
        public int oppSide;                      // its opponent, the "Player" role
        public bool bayesianOn;
        public bool completed;
        public int winnerSide = -2;

        public int[] score = new int[2];
        public int[] turns = new int[2];
        public int plies;

        public int[] swaps = new int[2];
        public int[] discards = new int[2];
        public int[] unknownSwaps = new int[2];
        public readonly List<double>[] swapDelta = { new(), new() };
        public readonly List<double> hiddenOwn = new();

        public int[] powerDrawn = new int[2];
        public int[,] powerPlayed = new int[2, 5];

        public int[] matchAttempts = new int[2];
        public int[] matchSuccess = new int[2];
        public int[] matchFail = new int[2];
        public int[] matchOnOwn = new int[2];
        public int[] matchOnOpp = new int[2];
        public int[] penalties = new int[2];

        public readonly List<double>[] decisionMs = { new(), new() };

        public int cambioCaller = -2;
        public int cambioCallerTurn = -1;
        public int[] cambioScore = { -1, -1 };
        public double cambioBelievedScore = double.NaN;
        public bool guardEvaluated;
        public double guardOwnMean, guardOppMean, guardPAhead;
    }

    // match lifecycle

    public void BeginMatch(int subjectSide, bool bayesianOn)
    {
        _matchIndex++;
        _m = new Match
        {
            Index = _matchIndex,
            subjectSide = subjectSide,
            oppSide = GameState.OpponentOf(subjectSide),
            bayesianOn = bayesianOn
        };
        _lastBelief = null;
        _lastTurnOwner = -1;
        _prevPhase = GamePhase.Dealing;   // any non-DrawingCard sentinel
    }

    public void FinalizeMatch(int winnerSide, int scoreP, int scoreA)
    {
        if (_m == null) return;
        _m.completed = winnerSide != -2;
        _m.winnerSide = winnerSide;
        _m.score[P] = scoreP;
        _m.score[A] = scoreA;

        _matchLines.Add(BuildMatchLine(_m));
        if (_m.cambioCaller >= 0) _cambioLines.Add(BuildCambioLine(_m));
        _m = null;
    }

    // event hooks

    public void OnPhase(GamePhase phase, int activeSide, CardPower activePower)
    {
        if (_m == null) return;

        if (phase == GamePhase.UsingPower && _prevPhase != GamePhase.UsingPower && activePower != CardPower.None)
            _m.powerPlayed[activeSide, (int)activePower]++;

        if (phase == GamePhase.DrawingCard && activeSide != _lastTurnOwner)
        {
            _lastTurnOwner = activeSide;
            _m.turns[activeSide]++;
        }
        _prevPhase = phase;
    }

    public void OnCommand(CommandType type, int actorSide, int scoreP, int scoreA)
    {
        if (_m == null) return;
        _m.plies++;

        if (type == CommandType.DiscardDrawn) _m.discards[actorSide]++;

        if (type == CommandType.CallCambio)
        {
            _m.cambioCaller = actorSide;
            _m.cambioCallerTurn = _m.turns[actorSide];
            _m.cambioScore[P] = scoreP;
            _m.cambioScore[A] = scoreA;

            if (actorSide == _m.subjectSide && _lastBelief != null)
            {
                _m.cambioBelievedScore = _lastBelief.BelievedOwnScore;
                if (_lastBelief.GuardEvaluated)
                {
                    _m.guardEvaluated = true;
                    _m.guardOwnMean = _lastBelief.GuardMeanOwn;
                    _m.guardOppMean = _lastBelief.GuardMeanOpp;
                    _m.guardPAhead  = _lastBelief.GuardPAhead;
                }
            }
        }
    }

    public void OnEffect(GameEffect fx, int actorSide)
    {
        if (_m == null) return;

        switch (fx.Kind)
        {
            case EffectKind.CardDrawn:
                if (fx.Card.Power != CardPower.None) _m.powerDrawn[actorSide]++;
                break;

            case EffectKind.SlotsSwapped:
                if (fx.Slot2.IsNone)
                {
                    _m.swaps[actorSide]++;
                    _m.swapDelta[actorSide].Add(fx.Card.Value - fx.Card2.Value);
                    if (actorSide == _m.subjectSide && _lastBelief?.Slots != null &&
                        !_lastBelief.Slots.Exists(sl => sl.Known && sl.Slot.Equals(fx.Slot)))
                        _m.unknownSwaps[actorSide]++;
                }
                break;

            case EffectKind.MatchResolved:
                if (!fx.Slot.IsNone)
                {
                    _m.matchAttempts[actorSide]++;
                    if (fx.Success)
                    {
                        _m.matchSuccess[actorSide]++;
                        if (fx.Slot.Side == actorSide) _m.matchOnOwn[actorSide]++;
                        else _m.matchOnOpp[actorSide]++;
                    }
                    else _m.matchFail[actorSide]++;
                }
                break;

            case EffectKind.PenaltyAdded:
                _m.penalties[fx.Success ? P : A]++;   // Success == forPlayer
                break;
        }
    }

    public void OnBelief(BeliefReport r)
    {
        if (_m == null || r == null) return;
        _lastBelief = r;
        if (r.Slots == null) return;

        int hiddenOwn = 0;
        foreach (var s in r.Slots) if (!s.IsOpponent && !s.Known) hiddenOwn++;
        _m.hiddenOwn.Add(hiddenOwn);

        if (!_logBeliefRows) return;

        foreach (var s in r.Slots)
        {
            if (s.Known) continue;
            _beliefLines.Add(string.Join(",", new[]
            {
                Csv(_sessionId), I(_m.Index), Csv(_label), _m.bayesianOn ? "1" : "0",
                I(_m.plies), I(_m.turns[_m.subjectSide]),
                s.IsOpponent ? "1" : "0", s.OppKnows ? "1" : "0",
                F(s.TiltRaw), F(s.TiltEff), I(s.TrueValue),
            }));
        }
    }

    public void OnDecision(IsmctsReport r)
    {
        if (_m == null || r == null) return;
        _m.decisionMs[_m.subjectSide].Add(r.ElapsedMs);
    }

    // rows; subject = "AI"

    private string BuildMatchLine(Match m)
    {
        int subj = m.subjectSide, opp = m.oppSide;
        int played = 0;
        foreach (var pw in PowerKinds) played += m.powerPlayed[subj, (int)pw];

        return string.Join(",", new List<string>
        {
            Csv(_sessionId), I(m.Index), Csv(_label), m.bayesianOn ? "1" : "0",
            m.completed ? "1" : "0", WinnerName(m.winnerSide, subj, opp),
            I(m.score[subj]), I(m.score[opp]),
            I(m.turns[subj]), I(m.swaps[subj]), I(m.discards[subj]),
            F(Avg(m.swapDelta[subj])), I(m.unknownSwaps[subj]), F(Avg(m.hiddenOwn)),
            I(m.matchAttempts[subj]), I(m.matchSuccess[subj]), I(m.matchFail[subj]),
            I(m.matchOnOwn[subj]), I(m.matchOnOpp[subj]), I(m.penalties[subj]),
            I(m.powerDrawn[subj]), I(played),
            I(m.powerPlayed[subj, (int)CardPower.LookOwnCard]),
            I(m.powerPlayed[subj, (int)CardPower.LookOpponentCard]),
            I(m.powerPlayed[subj, (int)CardPower.BlindSwap]),
            I(m.powerPlayed[subj, (int)CardPower.LookAndSwap]),
            F(Avg(m.decisionMs[subj])),
        });
    }

    private string BuildCambioLine(Match m)
    {
        int subj = m.subjectSide, opp = m.oppSide, caller = m.cambioCaller;
        bool wasAhead = m.cambioScore[caller] < m.cambioScore[1 - caller];

        return string.Join(",", new List<string>
        {
            Csv(_sessionId), I(m.Index), Csv(_label), m.bayesianOn ? "1" : "0",
            caller == subj ? "AI" : caller == opp ? "Player" : "None",
            m.cambioCallerTurn >= 0 ? I(m.cambioCallerTurn) : "",
            m.cambioScore[subj] >= 0 ? I(m.cambioScore[subj]) : "",
            m.cambioScore[opp]  >= 0 ? I(m.cambioScore[opp])  : "",
            wasAhead ? "1" : "0",
            double.IsNaN(m.cambioBelievedScore) ? "" : F(m.cambioBelievedScore),
            m.guardEvaluated ? F(m.guardOwnMean) : "",
            m.guardEvaluated ? F(m.guardOppMean) : "",
            m.guardEvaluated ? F(m.guardPAhead)  : "",
        });
    }

    private static string MatchHeader =>
        "session_id,match_index,agent_label,bayesian_on,completed,winner,ai_score,player_score," +
        "ai_turns,ai_swaps,ai_discards,ai_swap_value_delta_avg,ai_unknown_swaps,ai_hidden_own_avg," +
        "ai_match_attempts,ai_match_success,ai_match_fail,ai_matches_on_own,ai_matches_on_opponent,ai_penalties," +
        "ai_power_cards_drawn,ai_powers_played," +
        "ai_power_look_own,ai_power_look_opp,ai_power_blind_swap,ai_power_look_swap,ai_decision_ms_avg";

    private static string CambioHeader =>
        "session_id,match_index,agent_label,bayesian_on,cambio_caller,cambio_caller_turn," +
        "cambio_ai_score,cambio_player_score,cambio_caller_was_ahead," +
        "cambio_ai_believed_score,guard_believed_own_mean,guard_believed_opp_mean,guard_p_ahead";

    private static string BeliefHeader =>
        "session_id,match_index,agent_label,bayesian_on,ply,ai_turn," +
        "is_opponent_slot,opp_knows,tilt_raw,tilt_eff,true_value";

    // export

    public string Export()
    {
        string dir = Dir;
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        WriteFile(Path.Combine(dir, $"cambio_matches_{Safe}_{ts}.csv"), MatchHeader, _matchLines);
        WriteFile(Path.Combine(dir, $"cambio_calls_{Safe}_{ts}.csv"),   CambioHeader, _cambioLines);
        if (_logBeliefRows)
            WriteFile(Path.Combine(dir, $"cambio_beliefs_{Safe}_{ts}.csv"), BeliefHeader, _beliefLines);

        Debug.Log($"[Telemetry:{_label}] {_matchLines.Count} matches / {_cambioLines.Count} calls" +
                  (_logBeliefRows ? $" / {_beliefLines.Count} belief rows" : "") + $" -> {dir}");
        return dir;
    }

    private static void WriteFile(string path, string header, List<string> lines)
    {
        using var sw = new StreamWriter(path, false);
        sw.WriteLine(header);
        foreach (var l in lines) sw.WriteLine(l);
    }

    private string Dir
    {
        get { var d = Path.Combine(DownloadsRoot(), "CambioTelemetry"); Directory.CreateDirectory(d); return d; }
    }

    private static string DownloadsRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");
        string downloads = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads");
        return string.IsNullOrEmpty(downloads) || !Directory.Exists(downloads)
            ? Application.persistentDataPath : downloads;
    }

    private string Safe => string.IsNullOrEmpty(_label) ? "unlabeled" : _label.Replace(' ', '_');

    private static string WinnerName(int winner, int subj, int opp) =>
        winner == subj ? "AI" : winner == opp ? "Player" : winner == -1 ? "Draw" : "None";

    private static double Avg(List<double> xs) { if (xs.Count == 0) return double.NaN; double s = 0; foreach (var x in xs) s += x; return s / xs.Count; }
    private static string F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}