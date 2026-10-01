using Microsoft.Extensions.Logging;

namespace OpenVersus.Hooks;

/// <summary>Which patches and hooks took, for the log and the poller; the C++ HookMetadata::sActiveMods.</summary>
public sealed class HookStatus
{
    /// <summary>The pak signature check is patched out (<see cref="SigCheckPatch"/>).</summary>
    public bool AntiSigCheck { get; set; }
    /// <summary>The game-server endpoint is redirected to the configured server (<see cref="EndpointHooks.ApplyGame"/>).</summary>
    public bool GameEndpointSwap { get; set; }
    /// <summary>The prod endpoint is redirected to the configured server (<see cref="EndpointHooks.ApplyProd"/>).</summary>
    public bool ProdEndpointSwap { get; set; }
    /// <summary>The sunset check returns false (<see cref="SunsetPatch"/>).</summary>
    public bool SunsetDate { get; set; }
    /// <summary>
    /// The engine functions are resolved and game instances are recorded (<see cref="UeFunctionHooks"/>).
    /// Dialogs, notifications and the object array all need it.
    /// </summary>
    public bool UeFuncs { get; set; }
    /// <summary>The dialog functions are resolved (<see cref="DialogHooks"/>).</summary>
    public bool Dialog { get; set; }
    /// <summary>The notification functions are resolved (<see cref="NotificationHooks"/>).</summary>
    public bool Notifications { get; set; }
    /// <summary>The post-match freeze is patched out (<see cref="PostMatchFreezePatch"/>).</summary>
    public bool PostMatchFreeze { get; set; }
    /// <summary>Every call to the sunset check is patched out (<see cref="SunsetCallersPatch"/>); false if any site failed.</summary>
    public bool SunsetCallers { get; set; }
    /// <summary>The game's WB telemetry is stopped: its send function never sends (<see cref="TelemetryPatch"/>).</summary>
    public bool TelemetryOff { get; set; }
    /// <summary>RecordEventWithAttributes records nothing: it frees what it is given and returns (<see cref="TelemetryPatch.ApplyRecord"/>).</summary>
    public bool TelemetryRecordOff { get; set; }
    /// <summary>Every Store analytics function returns at once (<see cref="TelemetryPatch.ApplyShop"/>).</summary>
    public bool TelemetryShopOff { get; set; }
    /// <summary>The game's requests to the OpenVersus server carry the client's headers (<see cref="RequestHeadersHook"/>).</summary>
    public bool RequestHeaders { get; set; }
    /// <summary>hiss_amalgamation's zstd sections are decoded (<see cref="HydraZstdHook"/>).</summary>
    public bool HydraZstd { get; set; }
    /// <summary>1v1 and 2v2 queues never switch to bots: PFG.PvPBots is found and set to 0 once the game is up (<see cref="PvPBotsPatch"/>).</summary>
    public bool PvPBotsOff { get; set; }

    /// <summary>Every flag as name=value on one line, for the log.</summary>
    public override string ToString() =>
        $"sigcheck={AntiSigCheck} gameEndpoint={GameEndpointSwap} prodEndpoint={ProdEndpointSwap} sunset={SunsetDate} ue={UeFuncs} dialog={Dialog} notifs={Notifications} postMatchFreeze={PostMatchFreeze} sunsetCallers={SunsetCallers} requestHeaders={RequestHeaders} telemetryOff={TelemetryOff} telemetryRecordOff={TelemetryRecordOff} telemetryShopOff={TelemetryShopOff} pvpBotsOff={PvPBotsOff} hydraZstd={HydraZstd}";
}

/// <summary>What every hook needs: the image, the pattern resolver, the settings and the log.</summary>
/// <param name="Image">The game executable.</param>
/// <param name="Patterns">Finds the settings' patterns in the image.</param>
/// <param name="Settings">OpenVersus.toml.</param>
/// <param name="State">OVSState.toml.</param>
/// <param name="Log">The plugin's log.</param>
/// <param name="Status">Where each hook's result is recorded.</param>
public sealed record HookContext(Game.GameImage Image, Game.PatternResolver Patterns, Config.Settings Settings, Config.State State, ILogger Log, HookStatus Status);
