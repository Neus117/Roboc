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
        SettingsOutcome outcome = SettingsWindow.Show();

        if (outcome != SettingsOutcome.Uninstall)
        {
            if (outcome == SettingsOutcome.Saved && !args.Flag("--quiet"))
            {
                Console.WriteLine("设置已保存。");
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
    /// The second question, stating what goes and what stays before either happens.
    ///
    /// The recycle bins are computed here rather than described vaguely, because the
    /// number is what makes someone stop and read: "3 项，共 1.2 GB" is a fact about
    /// their files, and "回收站不会被删除" is a sentence they can skim past.
    /// </summary>
    private static bool Confirm()
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine("⚠️即将移除 Roboc：");
        text.AppendLine();
        text.AppendLine("　✅ 右键菜单与发送到菜单");
        text.AppendLine("　✅ 程序数据与设置");
        text.AppendLine("　✅ 临时痕迹");

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
            text.AppendLine("⚠️不会动你的回收站——里面是文件，不是程序数据：");
            text.AppendLine();

            foreach ((string root, long bytes) in bins)
            {
                text.AppendLine(bytes > 0
                    ? $"　{PathUtil.ToDisplay(root)}　{Describe(bytes)}"
                    : $"　{PathUtil.ToDisplay(root)}");
            }

            text.AppendLine();
            text.AppendLine("　要取回就打开它，不要了就自行删除。");
        }

        string? directory = Path.GetDirectoryName(Environment.ProcessPath);

        text.AppendLine();
        text.AppendLine("移除后，再删除程序文件夹即可彻底清除：");
        text.AppendLine();
        text.AppendLine($"　{directory ?? AppContext.BaseDirectory}");

        return MessageDialog.Show(
            text.ToString().TrimEnd(),
            "Roboc — 确认卸载",
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
                removed.Add("右键菜单（文件、文件夹、文件夹背景）");
            }

            SendToMenu.Uninstall(null);
            removed.Add("发送到菜单");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add($"移除 Shell 集成：{ex.Message}");
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
                removed.Add($"临时数据  {local}");
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

                removed.Add($"程序数据  {name}");
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
            text.AppendLine("⚠️已清除以下内容：");
            text.AppendLine();

            foreach (string line in removed)
            {
                text.AppendLine($"　✅ {line}");
            }

            text.AppendLine();
        }

        if (failed.Count > 0)
        {
            text.AppendLine("⚠️以下内容未能清除：");
            text.AppendLine();

            foreach (string line in failed)
            {
                text.AppendLine($"　❌ {line}");
            }

            text.AppendLine();
        }

        if (bins.Count > 0)
        {
            text.AppendLine("⚠️回收站未动——里面是你的文件：");
            text.AppendLine();

            foreach ((string root, long bytes) in bins)
            {
                text.AppendLine($"　{PathUtil.ToDisplay(root)}　{Describe(bytes)}");
            }

            text.AppendLine();
            text.AppendLine("　要取回就打开它，不要了就自行删除。");
            text.AppendLine();
        }

        text.AppendLine("现在删除这个文件夹即可彻底清除：");
        text.AppendLine();
        text.AppendLine($"　{folder}");

        MessageDialog.Show(
            text.ToString().TrimEnd(),
            "Roboc — 卸载",
            DialogButtons.Acknowledge,
            warning: true,
            centre: false);
    }

    private static void Report(string folder, List<(string Root, long Bytes)> bins)
    {
        Console.WriteLine("Roboc 已从系统中移除。");
        Console.WriteLine();

        if (bins.Count > 0)
        {
            Console.WriteLine("回收站未动——里面是你要么取回、要么自行删除的文件：");

            foreach ((string root, long bytes) in bins)
            {
                Console.WriteLine($"  {PathUtil.ToDisplay(root)}   {Describe(bytes)}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("现在删除这个文件夹即可彻底清除：");
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
