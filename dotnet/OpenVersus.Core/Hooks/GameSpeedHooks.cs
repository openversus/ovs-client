using OpenVersus.Game;
using OpenVersus.Memory;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>
/// Game speed. A match with the Beta Speed mutator (<see cref="BetaSpeedSlug"/>, custom games, online too) runs at
/// 110%: every player's client does the same, and the rollback server paces the match at 66 frames a second (the HTTP
/// server's registry gives it tick_rate 66). For offline testing ([Settings.Debug] LabGameSpeedPercent) a Lab or Local
/// Play match without it runs at that percentage of normal speed. The fixed simulation still steps 1/60 of sim time each frame; the game state's step
/// accumulator is fed the world's time-dilated delta, so at 110 the match takes 66 steps a real second instead of 60
/// (docs: GAME_SPEED_MUTATOR). It is the engine's own UGameplayStatics::SetGlobalTimeDilation on the match world, what
/// WB's debug menu (Comp_GameState_Debug.ApplyGameSpeed) called on non-live builds, through reflection: no code bytes
/// change. The speed never enters the simulation: every step is still 1/60 s of game time, so it cannot desync. Tested
/// in the Lab at 120% and 50% (2026-10-09): the world kept the value for the whole match.
/// </summary>
public static class GameSpeedHooks
{
    /// <summary>The Beta Speed mutator's slug (the HTTP server's GameplayConfigs.BetaSpeedMutator).</summary>
    public const string BetaSpeedSlug = "ovs_beta_speed";
    private const int BetaSpeedPercent = 110;

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
    /// A new match (<see cref="StockRulesHooks"/>, on the game thread): a Beta Speed match runs at 110%, an offline Lab
    /// or Local Play match at the configured speed; any other match is left at the world's own (1.0).
    /// </summary>
    public static void StartMatch(ObjectFinder finder, nint gameMode, MatchSettings? settings)
    {
        ClearMatch();
        if (settings is null)
        {
            return;
        }

        bool betaSpeed = settings.HasWorldBuff(BetaSpeedSlug);
        int percent = PercentFor(settings, s_percent);
        Countdown.SetSpeed(finder, percent / 100f, s_log);
        if (percent == 100)
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
        BitConverter.TryWriteBytes(parameters[dilation.Offset..], percent / 100f);
        Reflection.Invoke(s_set, s_statics, parameters);

        s_gameMode = gameMode;
        string what = betaSpeed ? $"Beta Speed {(settings.Online ? "online" : "offline")} match" : settings.MatchType == MatchSettings.LabMatchType ? "Lab match" : "Local Play match";
        s_log?.Info($"[GameSpeed] {what} at {percent}%: time dilation set to {percent / 100f:0.00}, the world reports {ReadDilation():0.00}");
    }

    /// <summary>
    /// The speed a match runs at, in percent: 110 with the Beta Speed mutator (any match, online too), else
    /// <paramref name="offlinePercent"/> for an offline Lab or Local Play match, else 100.
    /// </summary>
    public static int PercentFor(MatchSettings settings, int offlinePercent) =>
        settings.HasWorldBuff(BetaSpeedSlug) ? BetaSpeedPercent
        : !settings.Online && settings.MatchType is MatchSettings.LabMatchType or MatchSettings.LocalPlayMatchType ? offlinePercent
        : 100;

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

    /// <summary>
    /// The intro countdown (UI_Countdown_C, its "3", "2", "1", GO) is a widget animation, which plays in real time, while
    /// the fighters are let go by a fixed timer in game time (6 s: the intro handler's EndIntro). At 1.2x they could move
    /// while "1" still showed. Its PlayAnimation call's PlaybackSpeed literal (1.0, in the class's loaded ubergraph
    /// bytecode) becomes the match's speed, so the numbers keep pace; 1.0 again for a match at normal speed. UI only:
    /// nothing in the simulation changes. Every byte around the literal is checked first (docs: BETA_SPEED_COUNTDOWN).
    /// </summary>
    private static class Countdown
    {
        private const int ScriptLength = 7644;
        private const int CallAt = 4716;           // EX_LetObj (0x5F), the return value, then EX_FinalFunction (0x1C) PlayAnimation
        private const int SpeedAt = 4756;          // EX_FloatConst (0x1E) PlaybackSpeed
        private const int UStructScript = 0x70;   // UStruct::Script (TArray<uint8>): data, then Num at +8
        private static float s_written = 1f;

        public static void SetSpeed(ObjectFinder finder, float speed, ILogger? log)
        {
            if (speed == s_written)
            {
                return;
            }

            nint ubergraph = finder.FindFunction("UI_Countdown_C", "ExecuteUbergraph_UI_Countdown", out _);
            nint playAnimation = finder.FindFunction("UserWidget", "PlayAnimation", out _);
            byte[] code = new byte[ScriptLength];
            if (ubergraph == 0 || playAnimation == 0
                || !finder.Memory.TryRead(ubergraph + UStructScript, out nint script) || script == 0
                || !finder.Memory.TryRead(ubergraph + UStructScript + 8, out int length) || length != ScriptLength
                || !finder.Memory.TryRead(script, code)
                || code[CallAt] != 0x5F || code[CallAt + 10] != 0x1C || BitConverter.ToInt64(code, CallAt + 11) != playAnimation
                || code[SpeedAt] != 0x1E)
            {
                log?.Warn($"[GameSpeed] the intro countdown's speed is not changed: its script is not as expected (ubergraph 0x{ubergraph:X}, PlayAnimation 0x{playAnimation:X})");
                return;
            }

            if (CodeWriter.TryWrite(script + SpeedAt + 1, speed))
            {
                s_written = speed;
                log?.Info($"[GameSpeed] the intro countdown plays at {speed:0.00}x");
            }
        }
    }
}
