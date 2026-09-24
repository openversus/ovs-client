using OpenVersus.Game;

namespace OpenVersus.Tests;

/// <summary>The one gate every engine call goes through: a refusal with a reason, never a crash, before the game is up.
/// Shares the "engine readiness" collection with NetStatsTests because both set Engine.ReadyOverride, a static.</summary>
[Collection("engine readiness")]
public class EngineTests
{
    [Fact]
    public void EngineCallsRefuseUntilTheGameIsUp()
    {
        Engine.ReadyOverride = static () => false;
        try
        {
            Assert.False(Engine.IsUp);
            var e = Assert.Throws<InvalidOperationException>(() => Engine.Require("Probe"));
            Assert.StartsWith("Probe: the engine is not up yet", e.Message);
            // The real entry points refuse before touching any function pointer.
            Assert.Throws<InvalidOperationException>(() => UE.FindName("PfgNetcodeSession"));
            Assert.Throws<InvalidOperationException>(() => UE.NameToString(default));
            Assert.Throws<InvalidOperationException>(() => GameUi.NotificationManager());
            Assert.Throws<InvalidOperationException>(() => GameUi.ShowNotification("x", "y"));
            Assert.False(EngineNames.Instance.Ready);
            using var stopping = new CancellationTokenSource(50);
            Assert.False(Engine.WaitUntilUp(stopping.Token, pollMs: 10));
        }
        finally
        {
            Engine.ReadyOverride = null;
        }

        // Without the override and without a game instance, still not up.
        Assert.False(Engine.IsUp);
    }
}
