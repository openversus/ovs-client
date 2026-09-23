namespace OpenVersus.Config;

/// <summary>OVSState.ini: what the client has already told this player.</summary>
public sealed class State(string path)
{
	public const string FileName = "OVSState.ini";
	private readonly IniFile _ini = new(path);

	public bool PaidModWarned { get; private set; }

	public State Load()
	{
		PaidModWarned = _ini.ReadBool("FirstRun", "PaidModWarned", false);
		return this;
	}

	public void MarkPaidModWarned()
	{
		PaidModWarned = true;
		_ini.WriteBool("FirstRun", "PaidModWarned", true);
	}
}
