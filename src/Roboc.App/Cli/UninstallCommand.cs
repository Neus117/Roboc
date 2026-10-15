using Roboc.Core.Fs;
using Roboc.Core.Interop;
using Roboc.Core.Model;
using Roboc.Core.Shell;

namespace Roboc.App.Cli;

/// <summary>
/// Removes everything this program has put on the machine.
///
/// <b>What it is for.</b> The program presents itself as portable: a folder you can
/// move, copy or delete. Everything it owns inside that folder already honours that --
/// the settings, the resume journals, the icons. Two things do not: the registry keys
/// the shell needs in order to offer the menu at all, and one directory under
/// %LOCALAPPDATA% that was used as a fallback when the program's own folder was not
/// writable. This removes both, so that deleting the folder afterwards really is the
/// whole of it.
///
/// <b>What it deliberately does not touch.</b> The recycle bins. They are named after
/// this program but they hold the user's files -- things that were set aside rather
/// than destroyed, and that may be the only copy left. Dropping them because the user
/// asked to remove a program would be the program deciding what its own files are
/// worth. They are counted and named instead, and the decision stays with the person
/// who made them.
/// </summary>
internal static class UninstallCommand
{
    /// <summary>
    /// Opens the settings window, and removes the program if the user asked for that
    /// from inside it.
    ///
    /// The uninstall button lives in that window rather than in the context menu, so
    /// this is how it is reached. Keeping the two together puts the one action that
    /// makes the menu disappear one step behind a page that explains what it will and
    /// will not touch.
    /// </summary>
    public static int RunSettings(Args args)
    {
        // Single instance, and only from the menu.
        //
        // The 设置 entry takes no selection, and Explorer still invokes a path-less
        // verb once per selected item -- so selecting four files and choosing 设置
        // opened four identical settings windows. Reported by the user on 2026-10-15,
        // the same day and the same cause as the identical defect in 回收站.
        //
        // The claim is deliberately confined to the menu path: `roboc settings` typed
        // in a terminal is one deliberate invocation, and making the second one exit
        // silently would be worse than the bug being fixed.
        if (args.Flag("--gui-errors") && !PendingSelection.TryClaimImmediate("settings"))
        {
            return 0;
        }

        // Read before the window opens: the window applies a language change to the
        // running process as soon as it is clicked, so afterwards there is no way to tell
        // whether it changed.
        string before = RobocSettings.Load().Language;

        SettingsOutcome outcome = SettingsWindow.Show();

        if (outcome != SettingsOutcome.Uninstall)
        {
            if (outcome == SettingsOutcome.Saved)
            {
                ReinstallMenuIfLanguageChanged(before);

                if (!args.Flag("--quiet"))
                {
                    Console.WriteLine(Lang.Pick("设置已保存。", "Settings saved."));
                }
            }

            return 0;
        }

        // Asked again before anything is removed.
        //
        // The settings window is one click from a menu, and 卸载 sits at the opposite
        // corner from 确定 but is the same size and the same shape. Removing the shell
        // integration is the one action that cannot be undone from the menu, because it
        // is the menu. A second question costs a second and is the only thing standing
        // between a mis-click and having to reinstall.
        if (!Confirm())
        {
            return 0;
        }

        return Run(new Args(["uninstall", "--gui-errors"]));
    }

    /// <summary>
    /// Re-registers the context menu when the language was changed.
    ///
    /// The menu labels are written into the registry at install time, so they are fixed
    /// in whichever language was in force at that moment. Nothing about a running
    /// program can change them afterwards -- Explorer reads the registry, not the
    /// settings file. Re-registering is the only way the menu follows the setting, and it
    /// costs one pass over fifteen keys.
    ///
    /// Not re-registered when the menu was never installed: someone who only ever uses
    /// the command line should not acquire a context menu by changing a preference.
    /// </summary>
    private static void ReinstallMenuIfLanguageChanged(string before)
    {
        string after = RobocSettings.Load().Language;

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        if (!ContextMenuInstaller.IsInstalled)
        {
            return;
        }

        try
        {
            ContextMenuInstaller.Install(ShellCommand.ExecutablePath, out _, null);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    /// <summary>
    /// The second question, stating what goes and what stays before either happens.
    ///
    /// The recycle bins are computed here rather than described vaguely, because the
    /// number is what makes someone stop and read: "3 项，共 1.2 GB" is a fact about
    /// their files, and "回收站不会被删除" is a sentence they can skim past.
    /// </summary>
    private static bool Confirm()
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine(Lang.Pick("⚠️即将移除 Roboc：", "⚠️About to remove Roboc:"));
        text.AppendLine();
        text.AppendLine(Lang.Pick("　✅ 右键菜单与发送到菜单", "  ✅ Context menu and Send To entry"));
        text.AppendLine(Lang.Pick("　✅ 程序数据与设置", "  ✅ Program data and settings"));
        text.AppendLine(Lang.Pick("　✅ 临时痕迹", "  ✅ Temporary traces"));

        var bins = new List<(string Root, long Bytes)>();

        foreach (string root in RecycleBin.ExistingRoots())
        {
            if (!RecycleBin.HasItems(root))
            {
                continue;
            }

            long bytes = 0;

            try { bytes = RecycleBin.MeasureTree(PathUtil.ToExtended(root)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            bins.Add((root, bytes));
        }

        if (bins.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Lang.Pick("⚠️不会动你的回收站——里面是文件，不是程序数据：", "⚠️Your recycle bins will not be touched -- they hold files, not program data:"));
            text.AppendLine();

            foreach ((string root, long bytes) in bins)
            {
                text.AppendLine(bytes > 0
                    ? $"　{PathUtil.ToDisplay(root)}　{Describe(bytes)}"
                    : $"　{PathUtil.ToDisplay(root)}");
            }

            text.AppendLine();
            text.AppendLine(Lang.Pick("　要取回就打开它，不要了就自行删除。", "  Open them to get things back, or delete them yourself if you do not want them."));
        }

        string? directory = Path.GetDirectoryName(Environment.ProcessPath);

        text.AppendLine();
        text.AppendLine(Lang.Pick("移除后，再删除程序文件夹即可彻底清除：", "After that, deleting the program folder removes everything:"));
        text.AppendLine();
        text.AppendLine($"　{directory ?? AppContext.BaseDirectory}");

        return MessageDialog.Show(
            text.ToString().TrimEnd(),
            Lang.Pick("Roboc — 确认卸载", "Roboc — Confirm removal"),
            DialogButtons.YesNo,
            warning: true) == 0;
    }

    public static int Run(Args args)
    {
        bool gui = args.Flag("--gui-errors");
        bool quiet = args.Flag("--quiet");

        var removed = new List<string>();
        var failed = new List<string>();

        // ---------------------------------------------------------- shell integration

        try
        {
            if (!args.Flag("--keep-menu"))
            {
                ContextMenuInstaller.Uninstall(null);
                removed.Add(Lang.Pick("右键菜单（文件、文件夹、文件夹背景）", "Context menu (files, folders, folder background)"));
            }

            SendToMenu.Uninstall(null);
            removed.Add(Lang.Pick("发送到菜单", "Send To entry"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(Lang.Pick($"移除 Shell 集成：{ex.Message}", $"removing shell integration: {ex.Message}"));
        }

        // ---------------------------------------------------------- files outside here

        // The fallback directory. Present only on machines where the program's own
        // folder was read-only at some point -- installing under Program Files, for
        // instance -- which is exactly the case a portable program should still clean
        // up after.
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roboc");

        if (Directory.Exists(local))
        {
            try
            {
                Directory.Delete(local, recursive: true);
                removed.Add(Lang.Pick($"临时数据  {local}", $"temporary data  {local}"));
            }
            catch (IOException ex) { failed.Add($"{local}：{ex.Message}"); }
            catch (UnauthorizedAccessException ex) { failed.Add($"{local}：{ex.Message}"); }
        }

        // ---------------------------------------------------------- inside this folder

        string? directory = Path.GetDirectoryName(Environment.ProcessPath);
        string folder = directory ?? AppContext.BaseDirectory;

        foreach (string name in new[] { ".roboc", "Roboc.settings.json", "Roboc.settings.json.tmp" })
        {
            string path = Path.Combine(folder, name);

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                removed.Add(Lang.Pick($"程序数据  {name}", $"program data    {name}"));
            }
            catch (IOException ex) { failed.Add($"{name}：{ex.Message}"); }
            catch (UnauthorizedAccessException ex) { failed.Add($"{name}：{ex.Message}"); }
        }

        // ---------------------------------------------------------- the recycle bins

        // Counted, never deleted. These hold files the user put aside; removing the
        // program is not the same as discarding them.
        var bins = new List<(string Root, long Bytes)>();

        foreach (string root in RecycleBin.ExistingRoots())
        {
            if (!RecycleBin.HasItems(root))
            {
                continue;
            }

            long bytes = 0;

            try
            {
                bytes = RecycleBin.MeasureTree(PathUtil.ToExtended(root));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            bins.Add((root, bytes));
        }

        if (gui)
        {
            ShowDialog(folder, removed, failed, bins);
            return failed.Count == 0 ? 0 : 3;
        }

        foreach (string line in removed)
        {
            Console.WriteLine($"  removed  {line}");
        }

        foreach (string line in failed)
        {
            Console.Error.WriteLine($"  failed   {line}");
        }

        Console.WriteLine();
        Report(folder, bins);
        return failed.Count == 0 ? 0 : 3;
    }

    private static void ShowDialog(
        string folder, List<string> removed, List<string> failed,
        List<(string Root, long Bytes)> bins)
    {
        var text = new System.Text.StringBuilder();

        if (removed.Count > 0)
        {
            text.AppendLine(Lang.Pick("⚠️已清除以下内容：", "⚠️Removed:"));
            text.AppendLine();

            foreach (string line in removed)
            {
                text.AppendLine($"　✅ {line}");
            }

            text.AppendLine();
        }

        if (failed.Count > 0)
        {
            text.AppendLine(Lang.Pick("⚠️以下内容未能清除：", "⚠️Could not be removed:"));
            text.AppendLine();

            foreach (string line in failed)
            {
                text.AppendLine($"　❌ {line}");
            }

            text.AppendLine();
        }

        if (bins.Count > 0)
        {
            text.AppendLine(Lang.Pick("⚠️回收站未动——里面是你的文件：", "⚠️Recycle bins untouched -- they hold your files:"));
            text.AppendLine();

            foreach ((string root, long bytes) in bins)
            {
                text.AppendLine($"　{PathUtil.ToDisplay(root)}　{Describe(bytes)}");
            }

            text.AppendLine();
            text.AppendLine(Lang.Pick("　要取回就打开它，不要了就自行删除。", "  Open them to get things back, or delete them yourself if you do not want them."));
            text.AppendLine();
        }

        text.AppendLine(Lang.Pick("现在删除这个文件夹即可彻底清除：", "Delete this folder now to remove everything:"));
        text.AppendLine();
        text.AppendLine($"　{folder}");

        MessageDialog.Show(
            text.ToString().TrimEnd(),
            Lang.Pick("Roboc — 卸载", "Roboc — Removed"),
            DialogButtons.Acknowledge,
            warning: true,
            centre: false);
    }

    private static void Report(string folder, List<(string Root, long Bytes)> bins)
    {
        Console.WriteLine(Lang.Pick("Roboc 已从系统中移除。", "Roboc has been removed from the system."));
        Console.WriteLine();

        if (bins.Count > 0)
        {
            Console.WriteLine(Lang.Pick("回收站未动——里面是你要么取回、要么自行删除的文件：", "Recycle bins untouched -- they hold files for you to recover or delete yourself:"));

            foreach ((string root, long bytes) in bins)
            {
                Console.WriteLine($"  {PathUtil.ToDisplay(root)}   {Describe(bytes)}");
            }

            Console.WriteLine();
        }

        Console.WriteLine(Lang.Pick("现在删除这个文件夹即可彻底清除：", "Delete this folder now to remove everything:"));
        Console.WriteLine($"  {folder}");
    }

    private static string Describe(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
