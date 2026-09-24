namespace OpenVersus.Hooks;

/// <summary>Which patches and hooks took, for the log and the poller; the C++ HookMetadata::sActiveMods.</summary>
public sealed class HookStatus
{
    public bool AntiSigCheck { get; set; }
    public bool GameEndpointSwap { get; set; }
    public bool ProdEndpointSwap { get; set; }
    public bool SunsetDate { get; set; }
    public bool UeFuncs { get; set; }
    public bool Dialog { get; set; }
    public bool Notifications { get; set; }
    public bool PostMatchFreeze { get; set; }
    public bool SunsetCallers { get; set; }

    public override string ToString() =>
        $"sigcheck={AntiSigCheck} gameEndpoint={GameEndpointSwap} prodEndpoint={ProdEndpointSwap} sunset={SunsetDate} ue={UeFuncs} dialog={Dialog} notifs={Notifications} postMatchFreeze={PostMatchFreeze} sunsetCallers={SunsetCallers}";
}

/// <summary>What every hook needs: the image, the pattern resolver, the settings and the log.</summary>
public sealed record HookContext(Game.GameImage Image, Game.PatternResolver Patterns, Config.Settings Settings, Config.State State, Log Log, HookStatus Status);
