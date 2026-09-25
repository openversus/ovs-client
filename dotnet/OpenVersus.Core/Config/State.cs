namespace OpenVersus.Config;

/// <summary>OVSState.ini: what the client has already told this player.</summary>
public sealed class State(string path)
{
    /// <summary>The state file's name, beside the plugin.</summary>
    public const string FileName = "OVSState.ini";
    private readonly IniFile _ini = IniFile.Load(path);

    /// <summary>Whether the player has agreed to the free-mod notice, which is then not shown again.</summary>
    public bool PaidModWarned { get; private set; }

    /// <summary>Reads the file; returns this instance.</summary>
    public State Load()
    {
        PaidModWarned = _ini.GetBool("FirstRun", "PaidModWarned", false);
        return this;
    }

    /// <summary>Records the agreement and writes the file.</summary>
    public void MarkPaidModWarned()
    {
        PaidModWarned = true;
        _ini.Set("FirstRun", "PaidModWarned", "true");
        _ini.Save();
    }
}
