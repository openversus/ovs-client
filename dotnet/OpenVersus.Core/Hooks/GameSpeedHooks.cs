using OpenVersus.Game;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Game speed for offline testing ([Settings.Debug] LabGameSpeedPercent): a Lab or Local Play match runs at that
/// percentage of normal speed. The fixed simulation still steps 1/60 of sim time each frame; the game state's step
/// accumulator is fed the world's time-dilated delta, so at 120 the match takes 72 steps a real second instead of 60
/// (docs: GAME_SPEED_MUTATOR). It is the engine's own UGameplayStatics::SetGlobalTimeDilation on the match world, what
/// WB's debug menu (Comp_GameState_Debug.ApplyGameSpeed) called on non-live builds, through reflection: no code bytes
/// change. An online match is never touched. Tested in the Lab at 120% and 50% (2026-10-09): the world kept
/// the value for the whole match.
/// </summary>
public static class GameSpeedHooks
{
    private static ILogger? s_log;
    private static int s_percent = 100;
    private static GameFunction? s_set;
    private static GameFunction? s_get;
    private static nint s_statics;
    private static nint s_gameMode;

    /// <summary>Reads the setting. 100 (the default) is off; 10 to 300 is accepted.</summary>
    public static void Attach(ILogger log, int percent)
    {
        s_log = log;
        s_percent = percent is >= 10 and <= 300 ? percent : 100;
        if (s_percent != 100)
        {
            log.Warn($"[GameSpeed] Lab and Local Play matches run at {s_percent}% speed ([Settings.Debug] LabGameSpeedPercent); online matches are unchanged");
        }
    }

    /// <summary>
    /// A new match (<see cref="StockRulesHooks"/>, on the game thread): an offline Lab or Local Play match gets the
    /// configured speed; any other match is left at the world's own (1.0).
    /// </summary>
    public static void StartMatch(ObjectFinder finder, nint gameMode, MatchSettings? settings)
    {
        ClearMatch();
        if (s_percent == 100 || settings is null || settings.Online
            || settings.MatchType is not (MatchSettings.LabMatchType or MatchSettings.LocalPlayMatchType))
        {
            return;
        }

        s_set ??= Reflection.Find(finder, finder.Image, "GameplayStatics", "SetGlobalTimeDilation");
        s_get ??= Reflection.Find(finder, finder.Image, "GameplayStatics", "GetGlobalTimeDilation");
        if (s_statics == 0)
        {
            s_statics = finder.FindDefaultObject(finder.FindClass("GameplayStatics"));
        }

        if (s_set?.Signature.Reflected is not { } sig || s_statics == 0
            || sig.Parameters.FirstOrDefault(p => !p.IsReturn && p.Type == "ObjectProperty") is not { } context
            || sig.Parameters.FirstOrDefault(p => !p.IsReturn && p.Type == "FloatProperty") is not { } dilation)
        {
            s_log?.Error($"[GameSpeed] SetGlobalTimeDilation not found (function {(s_set != null ? "found" : "missing")}, GameplayStatics default object 0x{s_statics:X}); the speed is not changed");
            return;
        }

        Span<byte> parameters = stackalloc byte[sig.ParmsSize];
        parameters.Clear();
        BitConverter.TryWriteBytes(parameters[context.Offset..], (long)gameMode);
        BitConverter.TryWriteBytes(parameters[dilation.Offset..], s_percent / 100f);
        Reflection.Invoke(s_set, s_statics, parameters);

        s_gameMode = gameMode;
        s_log?.Info($"[GameSpeed] {(settings.MatchType == MatchSettings.LabMatchType ? "Lab" : "Local Play")} match at {s_percent}%: time dilation set to {s_percent / 100f:0.00}, the world reports {ReadDilation():0.00}");
    }

    // The world's time dilation through GetGlobalTimeDilation, or -1 when it cannot be asked. Game thread.
    private static float ReadDilation()
    {
        if (s_get?.Signature.Reflected is not { ReturnValueOffset: >= 0 } sig || s_statics == 0
            || sig.Parameters.FirstOrDefault(p => !p.IsReturn && p.Type == "ObjectProperty") is not { } context)
        {
            return -1;
        }

        Span<byte> parameters = stackalloc byte[sig.ParmsSize];
        parameters.Clear();
        BitConverter.TryWriteBytes(parameters[context.Offset..], (long)s_gameMode);
        Reflection.Invoke(s_get, s_statics, parameters);
        return BitConverter.ToSingle(parameters[sig.ReturnValueOffset..]);
    }

    private static void ClearMatch() => s_gameMode = 0;
}
