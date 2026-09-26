using System.Text;

namespace OpenVersus.Config;

/// <summary>
/// The OpenVersus.toml written when there is none (a fresh install, or an old ini that would not
/// convert): every row at its default, in <see cref="Settings.Table"/> order, with a comment
/// explaining each one. A converted or merged file keeps what the player had and gets none of
/// this. <c>dotnet/sample.toml</c> is a copy of it.
/// </summary>
internal static class DefaultConfig
{
    private const string Rule = "# ---------------------------------------------------------------------------";

    private const string Header = """
        # OpenVersus settings.
        #
        # This file is read each time the game starts, so edit it with the game closed.
        #
        # Text goes in double quotes ("like this"); true, false and numbers don't. A line starting
        # with # is a comment, and so is anything after a # at the end of a line.
        #
        # If this file has a mistake in it, the mod runs on default settings, leaves the file as it
        # is, and tells you in the game (and in logs/OpenVersus.log) which line and column to fix.
        # Delete this file and the mod writes a fresh one like it on the next launch.
        """;

    /// <summary>What each table is, shown in a banner above it.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["Settings"] = "General settings.",
        ["Settings.Keybinds"] = "Keys. Nothing reads these yet.",
        ["Patches"] = "Changes the mod makes to the game itself.",
        ["Features"] = "Parts of the game the mod hooks into. The defaults are what you want.",
        ["Patterns"] = """
            Byte patterns the mod uses to find things in the game, made for the game's final patch.
            Don't change these unless you know exactly why; a wrong one can break the feature it belongs to.
            """,
        ["Patterns.UE"] = "More byte patterns: Unreal Engine functions.",
        ["Patterns.MVS"] = "More byte patterns: MultiVersus functions.",
        ["Settings.Debug"] = "For troubleshooting and development. The defaults are what you want.",
        ["Server.Game"] = "The OpenVersus server. Leave this section alone unless you run your own server.",
        ["Server.Prod"] = "The server the game's WB network connection goes to. Same advice as above.",
    };

    /// <summary>The comment above each row, without the leading "# ".</summary>
    internal static readonly IReadOnlyDictionary<SettingDef, string> Comments = new Dictionary<SettingDef, string>
    {
        [Settings.Rows.LogLevel] = """
            TL;DR: How much goes into logs/OpenVersus.log. "info" is right for almost everyone.

            A name in quotes: "trace", "debug", "info", "warn", "error", "critical" or "none"; or a
            number from 1 (debug) to 6 (none). 0 means: decide from DebugLogging under [Settings.Debug].
            """,
        [Settings.Rows.EnableKeyboardHotkeys] = "Installs the keyboard hook. For now it only notes F1 presses in the log.",
        [Settings.Rows.AutoUpdate] = """
            TL;DR: Keeps the mod up to date by itself.

            At launch, asks the OpenVersus server whether there is a newer version of the mod. If
            there is, it is downloaded and installed, and the game closes so the next launch runs it.
            """,
        [Settings.Rows.ToggleMenu] = "Kept so older settings files keep their meaning; nothing uses it.",
        [Settings.Rows.SunsetDate] = """
            TL;DR: Needed for online play. Leave it on.

            The game switches its online features off after its official end-of-service date. This
            makes that check always answer "not yet".
            """,
        [Settings.Rows.PakLoader] = """
            TL;DR: Lets custom content load, like skins that replace an existing skin or emote.

            Skips the game's signature check, so .pak and .utoc/.ucas files with invalid signatures load.
            """,
        [Settings.Rows.PostMatchFreeze] = """
            TL;DR: Everyone sees what the winner does after the final stock again.

            A match lingers for a few seconds after it is won. An official update made the losers
            and spectators stop seeing anything the winner does in that time: to them the winner just
            stands still. This puts it back the way it was at launch.
            """,
        [Settings.Rows.SunsetCallers] = """
            For profiling; leave it off. Instead of patching the end-of-service check, patches every
            place in the game that calls it (about 146), so it is never called at all. Needs SunsetDate.
            """,
        [Settings.Rows.HookUe] = "Finds the engine functions the mod calls into. The dialogs, notifications and NetStats need it.",
        [Settings.Rows.Dialog] = "Finds the game's dialog functions, which the mod's in-game dialogs use.",
        [Settings.Rows.Notifications] = """
            Finds the game's notification functions, which show the mod's toasts ("OpenVersus Loaded",
            settings problems) and announcements from the OpenVersus server.
            """,
        [Settings.Rows.NetStats] = """
            TL;DR: Logs network stats for every match, for troubleshooting connections.

            Once a second during a match, writes the rollback session's statistics to a log in logs/,
            one file per match, named for the match and your opponents.
            """,
        [Settings.Rows.SigCheckPattern] = "The pak signature check (PakLoader).",
        [Settings.Rows.EndpointLoaderPattern] = "Where the game stores its game-server address ([Server.Game]).",
        [Settings.Rows.ProdEndpointLoaderPattern] = "Where the game stores its WB network address ([Server.Prod]).",
        [Settings.Rows.SunsetDatePattern] = "The end-of-service check (SunsetDate).",
        [Settings.Rows.FTextPattern] = "The engine's text and name functions.",
        [Settings.Rows.CFNamePattern] = "The engine's name constructor.",
        [Settings.Rows.WCFNamePattern] = "The engine's wide-character name constructor.",
        [Settings.Rows.DialogPattern] = "The game's function that opens a dialog.",
        [Settings.Rows.DialogParamsPattern] = "The game's dialog settings.",
        [Settings.Rows.DialogCallbackPattern] = "What connects a dialog button to what it does.",
        [Settings.Rows.QuitGameCallbackPattern] = "The game's quit function, for dialogs that offer to quit.",
        [Settings.Rows.FighterInstancePattern] = "Where the game creates its game instance.",
        [Settings.Rows.NotificationsPattern] = "The game's function that shows a toast notification.",
        [Settings.Rows.PostMatchFreezePattern] = "The post-match freeze (PostMatchFreeze).",
        [Settings.Rows.ShowConsole] = "Opens a console window beside the game, showing the log as it is written.",
        [Settings.Rows.DebugPause] = "Holds startup at a message box until you click OK (handy for attaching a debugger).",
        [Settings.Rows.DebugLogging] = "With LogLevel = 0, logs at debug level. Otherwise it does nothing.",
        [Settings.Rows.NonMvsPatching] = "Silences the warning shown when the mod is loaded by something that isn't MultiVersus.",
        [Settings.Rows.CountSunsetCalls] = "For profiling; leave it off. Counts calls to the end-of-service check and logs them once a minute.",
        [Settings.Rows.ServerUrl] = """
            TL;DR: The OpenVersus server.

            The game's own server connection goes here, and so do the mod's update check, server
            announcements and registration.
            """,
        [Settings.Rows.EnableServerProxy] = """
            Points the game's server connection at ServerUrl above, and checks for announcements.
            Off means the game tries its original servers, which no longer exist.
            """,
        [Settings.Rows.ProdServerUrl] = "Where the game's WB network connection goes.",
        [Settings.Rows.EnableProdServerProxy] = "Points the game's WB network connection at ServerUrl above.",
    };

    /// <summary>The file's text, with <paramref name="newLine"/> line endings.</summary>
    public static string Text(string newLine = "\r\n")
    {
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append(newLine);
        void CommentLines(string comment)
        {
            foreach (string line in comment.ReplaceLineEndings("\n").Split('\n'))
            {
                Line(line.Length == 0 ? "#" : "# " + line);
            }
        }

        foreach (string line in Header.ReplaceLineEndings("\n").Split('\n'))
        {
            Line(line);
        }

        // A table's rows are not always next to each other in the table order (the two [Server.*]
        // tables alternate), and TOML allows a table only once, so group them.
        foreach (IGrouping<string, SettingDef> section in Settings.Table.GroupBy(d => d.Section))
        {
            Line("");
            Line(Rule);
            CommentLines(Sections[section.Key]);
            Line(Rule);
            Line($"[{section.Key}]");
            foreach (SettingDef def in section)
            {
                Line("");
                CommentLines(Comments[def]);
                string value = def.Kind == SettingKind.Bool ? def.Default : SettingsMigration.Quote(def.Default);
                Line($"{def.Key} = {value}");
            }
        }

        return text.ToString();
    }
}
