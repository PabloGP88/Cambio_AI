
public static class MctsDebug
{
    public static bool Enabled = true;
    public static int Verbosity = 1;
    private const string Tag = "[ISMCTS]";

    public static bool At(int level) => Enabled && Verbosity >= level;
    public static void Log(int level, string msg) { if (At(level)) UnityEngine.Debug.Log($"{Tag} {msg}"); }
    public static void LogWarning(string msg) => UnityEngine.Debug.LogWarning($"{Tag} {msg}");
}
