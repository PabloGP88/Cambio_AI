/* leveled console logging for the search. 0 off, 1 per-decision summary, 2 adds expansions
   and determinize stats, 3 adds every selection and rollout. flip via MctsDebug.Verbosity.
   always guard hot-path calls with if (MctsDebug.At(n)) so the interpolated string isn't
   built when the level is disabled */
public static class MctsDebug
{
    public static bool Enabled = true;
    public static int Verbosity = 1;
    private const string Tag = "[ISMCTS]";

    public static bool At(int level) => Enabled && Verbosity >= level;
    public static void Log(int level, string msg) { if (At(level)) UnityEngine.Debug.Log($"{Tag} {msg}"); }
    public static void LogWarning(string msg) => UnityEngine.Debug.LogWarning($"{Tag} {msg}");
}
