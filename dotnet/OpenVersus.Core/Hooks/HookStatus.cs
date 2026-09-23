namespace OpenVersus.Hooks;

/// <summary>Which patches and hooks took, for the log and the poller; the C++ HookMetadata::sActiveMods.</summary>
public sealed class HookStatus
{
	public bool AntiSigCheck;
	public bool GameEndpointSwap;
	public bool ProdEndpointSwap;
	public bool SunsetDate;
	public bool UeFuncs;
	public bool Dialog;
	public bool Notifications;
	public bool PostMatchFreeze;

	public override string ToString() =>
		$"sigcheck={AntiSigCheck} gameEndpoint={GameEndpointSwap} prodEndpoint={ProdEndpointSwap} sunset={SunsetDate} ue={UeFuncs} dialog={Dialog} notifs={Notifications} postMatchFreeze={PostMatchFreeze}";
}

/// <summary>What every hook needs: the image, the pattern resolver, the settings and the log.</summary>
public sealed record HookContext(Game.GameImage Image, Game.PatternResolver Patterns, Config.Settings Settings, Config.State State, Log Log, HookStatus Status);
