// 1.0.147 ([generator], Fire 11-03: "all of our EXCESSIVE logging to be debug level"): the one helper; Unity's Debug.Log records as Info.
internal static class VabLog
{
    private static readonly BepInEx.Logging.ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("VABackpacks");
    internal static void Line(object message) => s_log.LogDebug(message);
    internal static void Info(object message) => s_log.LogInfo(message);
}
