/// <summary>
/// Leveled console logging for the search. 0 off, 1 per-decision summary, 2 + expansions
/// and determinize stats, 3 + every selection / rollout. Flip via MctsDebug.Verbosity.
/// NOTE: always guard hot-path calls with `if (MctsDebug.At(n))` so the interpolated
/// string isn't built when the level is disabled.
/// </summary>
public static class MctsDebug
{
    public static bool Enabled = true;
    public static int Verbosity = 1;
    private const string Tag = "[ISMCTS]";

    public static bool At(int level) => Enabled && Verbosity >= level;
    public static void Log(int level, string msg) { if (At(level)) UnityEngine.Debug.Log($"{Tag} {msg}"); }
    public static void LogWarning(string msg) => UnityEngine.Debug.LogWarning($"{Tag} {msg}");
}
