#:project ../OpenVersus.Core/OpenVersus.Core.csproj
// dotnet run --file tools/package-config.cs -- DIR TESTING_URL
//
// Writes DIR/OpenVersus.toml for the release zip (build.sh package): the default file exactly as the mod writes it on a
// first launch (Settings.Load into an empty folder), then three changes for players who switch between the servers:
// in [Server.Game] and [Server.Prod] the prod ServerUrl line is commented out and a ServerUrl line for TESTING_URL is
// added below it, and [Settings] LogLevel is "trace". The file is then loaded again as the game will load it, and
// refused (exit 1) unless the testing URL and trace are what the mod reads and the commented prod lines are still there.
using OpenVersus;
using OpenVersus.Config;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run --file tools/package-config.cs -- DIR TESTING_URL");
    return 2;
}

string dir = args[0];
string testing = args[1];
string path = Path.Combine(dir, Settings.FileName);
Directory.CreateDirectory(dir);
if (File.Exists(path))
{
    Console.Error.WriteLine($"{path} exists; this writes a fresh one into an empty folder");
    return 1;
}

Settings.Load(dir);
string text = File.ReadAllText(path);
string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
string prodLine = $"ServerUrl = \"{OvsVersion.DefaultServerUrl}\"";
string testingLine = $"ServerUrl = \"{testing}\"";
const string TraceLine = "LogLevel = \"trace\"";

var lines = text.Split(newLine).ToList();
string section = "";
int serverUrls = 0;
int logLevels = 0;
for (int i = 0; i < lines.Count; i++)
{
    string line = lines[i];
    if (line.StartsWith('[') && line.TrimEnd().EndsWith(']'))
    {
        section = line.Trim()[1..^1];
    }
    else if (section is "Server.Game" or "Server.Prod" && line == prodLine)
    {
        lines[i] = "#" + prodLine;
        lines.Insert(i + 1, testingLine);
        i++;
        serverUrls++;
    }
    else if (section == "Settings" && line.StartsWith("LogLevel = ", StringComparison.Ordinal))
    {
        lines[i] = TraceLine;
        logLevels++;
    }
}

if (serverUrls != 2 || logLevels != 1)
{
    Console.Error.WriteLine($"the default file did not have what was expected: {serverUrls} of 2 prod ServerUrl lines ({prodLine}), {logLevels} of 1 LogLevel line");
    return 1;
}

string written = string.Join(newLine, lines);
File.WriteAllText(path, written);

// As the game will see it on its first launch with this file.
var settings = Settings.Load(dir);
string after = File.ReadAllText(path);
var problems = new List<string>();
if (settings.Problem is { } problem)
{
    problems.Add($"the mod reports a problem: {problem.Title}: {problem.Detail}");
}
if (settings.ServerUrl != testing || settings.ProdServerUrl != testing)
{
    problems.Add($"the mod reads ServerUrl {settings.ServerUrl} and [Server.Prod] ServerUrl {settings.ProdServerUrl}, not {testing}");
}
if (settings.LogLevel != "trace")
{
    problems.Add($"the mod reads LogLevel {settings.LogLevel}, not trace");
}
if (after.Split(newLine).Count(l => l == "#" + prodLine) != 2 || after.Split(newLine).Count(l => l == testingLine) != 2)
{
    problems.Add("loading the file again lost the commented prod ServerUrl lines or the testing ones");
}
if (problems.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, problems));
    return 1;
}

Console.WriteLine($"{path}: default settings, ServerUrl {testing} (prod commented out above it) in [Server.Game] and [Server.Prod], LogLevel trace{(after == written ? "" : "; the mod rewrote the file on loading it, and it still holds all of that")}");
return 0;
