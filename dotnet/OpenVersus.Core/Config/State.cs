namespace OpenVersus.Config;

/// <summary>OVSState.ini: what the client has already told this player.</summary>
public sealed class State(string path)
{
    public const string FileName = "OVSState.ini";
    private readonly IniFile _ini = IniFile.Load(path);

    public bool PaidModWarned { get; private set; }

    public State Load()
    {
        PaidModWarned = _ini.GetBool("FirstRun", "PaidModWarned", false);
        return this;
    }

    public void MarkPaidModWarned()
    {
        PaidModWarned = true;
        _ini.Set("FirstRun", "PaidModWarned", "true");
        _ini.Save();
    }
}
