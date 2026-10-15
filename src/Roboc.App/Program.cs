using Roboc.App.Cli;
using Roboc.Core.Diagnostics;
using Roboc.Core.Model;
using Roboc.Core.Shell;

namespace Roboc.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Attach for every command, not just the -- diagnostics. Without this a
        // WinExe writes its output into nothing when run from a terminal, which
        // is exactly what `install` did: no message, no error, no clue.
        bool diagnostic = args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal);

        // First, before anything else touches stdout.
        //
        // ConsoleHost builds its StreamWriter from the encoding that is current at
        // the moment it runs, so choosing the encoding afterwards has no effect on
        // output that has already been bound: the UTF-8 default stays baked into
        // the writer and every Chinese label arrives as mojibake. Setting it here
        // means the writer ConsoleHost creates is already correct.
        ConsoleOutput.Install();

        if (diagnostic)
        {
            // Only diagnostics may create a console. Doing so from a context-menu
            // invocation would pop a window.
            ConsoleHost.AllocateIfMissing();
        }
        else
        {
            ConsoleHost.AttachIfPossible();
        }

        try
        {
            // Before anything can print, and before anything can build a menu label.
            //
            // This has to be first. Lang decides every user-visible string in the program,
            // and a string resolved before this point is fixed for the life of the
            // process -- so the saved preference is read here rather than wherever it
            // happens to be convenient later. Loading it twice is deliberate: the cost is
            // one small file read, and the alternative is a startup order that has to be
            // reasoned about every time a new caller appears.
            Lang.Apply(RobocSettings.Load().Language);

            // Cheap, and the alternative is a menu entry pointing at a path this
            // executable no longer occupies.
            //
            // Told which command is running, because one part of it is not cheap.
            // Explorer starts one process per selected item, and the clipboard probe
            // inside SelfHeal opens the clipboard -- so a hundred-file copy paid for a
            // hundred of them before any of them had recorded its path. That delay is
            // what made 粘贴 take seconds to appear.
            ShellCommand.SelfHeal(args.Length > 0 ? args[0] : string.Empty);

            return Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"roboc: fatal: {ex}");
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        // No arguments means Explorer, or a double-click. That is how a portable
        // program is expected to be started, and it used to answer with
        // "no command given (see --help)" in a console window that closed before it
        // could be read -- leaving the user to discover, unaided, that the menu they
        // were promised needs a command they were never told about.
        //
        // It now does the thing they were trying to do.
        if (args.Length == 0)
        {
            return FirstRun();
        }

        var parsed = new Args(args);

        switch (parsed.Command)
        {
            case "--help" or "-h" or "/?":
                PrintHelp();
                return 0;

            case "--version" or "-v":
                Console.WriteLine("Roboc " + ThisAssembly.Version);
                return 0;

            case "--selftest":
                return SelfTest.Run(Console.Out, parsed.Option("--scratch"));

            case "copy":
                return CopyCommand.Run(parsed, move: false);

            case "move":
                return CopyCommand.Run(parsed, move: true);

            case "delete" or "del" or "rm":
                return DeleteCommand.Run(parsed);

            case "undo":
                return UndoCommand.Run(parsed);

            case "clipboard" or "copy-clipboard":
                return ShellCommand.Clipboard(parsed);

            case "paste":
                return ShellCommand.Paste(parsed, move: false);

            case "install":
                return ShellCommand.Install(parsed);

            case "uninstall":
                return UninstallCommand.Run(parsed);

            case "settings" or "set":
                return UninstallCommand.RunSettings(parsed);

            case "status":
                return ShellCommand.Status(parsed);

            case "recycle":
                return MaintenanceCommands.Recycle(parsed);

            case "restore":
                return MaintenanceCommands.Restore(parsed);

            case "reclaim":
                return MaintenanceCommands.Reclaim(parsed);

            case "purge":
                return MaintenanceCommands.Purge(parsed);

            case "diagnose" or "--diagnose":
                return MaintenanceCommands.Diagnose(parsed);

            default:
                Console.Error.WriteLine($"roboc: unknown command '{parsed.Command}' (see --help)");
                return 2;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        Roboc - high-throughput copy / delete for Windows 11

        USAGE
          Roboc copy --to <dir> [--workers N] [--timing] <path>...
          Roboc move --to <dir> <path>...
          Roboc delete --yes [--direct] [--undo-window <minutes>] <path>...
          Roboc undo
          Roboc --selftest

        DELETE
          Removes the given items. Without --yes the command refuses; from an
          interactive terminal it will ask you to type DELETE.

          By default items are detached to a same-volume staging directory and
          kept for --undo-window minutes (default 10), so 'roboc undo' can
          bring them back. --direct unlinks in place instead, with no undo.

          Refused outright: volume roots, drive-relative paths such as "D:", and
          any target that contains the running program or the current directory.

        DIAGNOSTICS
          --selftest             Verify native interop against a scratch directory
          --version              Print version
          --help                 Print this text
        """);
    /// <summary>
    /// What a double-click does.
    ///
    /// The program is distributed as a folder that is supposed to work on being opened.
    /// Everything needed is inside it -- the executable, the icons, and now the settings
    /// -- except the registry keys the shell needs before it can offer the menu at all.
    /// Those cannot live in the folder, so they are written on first run instead of
    /// being left as an instruction.
    ///
    /// Running again when already installed does not re-register blindly: it reports what
    /// is there. Re-registering is what repair is for, and doing it silently on every
    /// double-click would hide a genuine problem behind a success message.
    /// </summary>
    private static int FirstRun()
    {
        bool installed = ContextMenuInstaller.IsInstalled;

        if (!installed)
        {
            int registered = ContextMenuInstaller.Install(ShellCommand.ExecutablePath, out string failure, null);

            if (registered <= 0)
            {
                MessageDialog.Show(
                    Lang.Pick(
                        $"⚠️无法注册右键菜单：\r\n\r\n{(failure.Length > 0 ? failure : "未注册任何项")}\r\n\r\n"
                        + "可以改用命令行使用：\r\n"
                        + "　roboc copy --to <目标> <来源>\r\n"
                        + "　roboc --help　查看全部命令",
                        $"⚠️The context menu could not be registered:\r\n\r\n{(failure.Length > 0 ? failure : "nothing was registered")}\r\n\r\n"
                        + "You can still use it from the command line:\r\n"
                        + "  roboc copy --to <destination> <source>\r\n"
                        + "  roboc --help   lists every command"),
                    Lang.Pick("注册失败", "Registration Failed"),
                    DialogButtons.Acknowledge,
                    warning: true);

                return 3;
            }
        }

        string folder = Path.GetDirectoryName(ShellCommand.ExecutablePath) ?? AppContext.BaseDirectory;

        // Written as one paragraph per language rather than sentence by sentence.
        //
        // Concatenating translated fragments is how a translation ends up reading like a
        // translation: word order differs, the punctuation differs, and a full stop that
        // belongs to the end of a Chinese clause lands in the middle of an English one.
        // One string per language keeps each readable on its own terms.
        string body = installed
            ? Lang.Pick(
                "✅Roboc 已经可以使用。\r\n\r\n"
                + "在资源管理器中选中文件或文件夹，右键即可看到 Roboc 菜单。",
                "✅Roboc is ready to use.\r\n\r\n"
                + "Select a file or folder in Explorer and right-click to find the Roboc menu.")
            : Lang.Pick(
                "✅已为你注册右键菜单：\r\n\r\n"
                + "　复制　粘贴　删除　回收　回收站　设置\r\n\r\n"
                + "在资源管理器中选中文件或文件夹，右键即可看到它们。\r\n\r\n"
                + "「设置」里可以开关各项功能——每一项都写明了它的收益与代价，\r\n"
                + "「卸载」也在那里。\r\n\r\n"
                + "这个程序不需要安装。把整个文件夹放在哪里都行，\r\n"
                + "不想用了执行一次「卸载」，再删掉文件夹即可。",
                "✅The context menu has been registered:\r\n\r\n"
                + "  Copy   Paste   Delete   Recycle   Recycle bin   Settings\r\n\r\n"
                + "Select a file or folder in Explorer and right-click to find them.\r\n\r\n"
                + "Settings turns individual features on and off, and states what each one\r\n"
                + "costs and what it buys. Remove lives there too.\r\n\r\n"
                + "There is nothing to install. Put the folder wherever you like;\r\n"
                + "when you are done, run Remove once and delete the folder.");

        MessageDialog.Show(
            body + "\r\n\r\n" +
            Lang.Pick($"程序位置：\r\n　{folder}", $"Location:\r\n  {folder}"),
            // Both branches name the outcome rather than the program. 就绪 said nothing
            // about what had just happened, and a caption reading only "Roboc" said
            // nothing at all -- the same as the progress windows, which name what they
            // are doing and leave the program name to the icon.
            installed
                ? Lang.Pick("安装完成", "Installed")
                : Lang.Pick("已经就绪", "Already Ready"),
            DialogButtons.Acknowledge);

        return 0;
    }
}

internal static class ThisAssembly
{
    /// <summary>
    /// The version, with the commit when one is known.
    ///
    /// The hash arrives as assembly metadata rather than as part of the informational
    /// version, because the SDK builds the file's version resource out of that string and
    /// the properties dialog ended up showing "1.0.0+51fc3bb8422e..." -- a useful fact in
    /// a place nobody wants it. Keeping the two separate lets the dialog stay clean and
    /// the version command stay diagnostic.
    /// </summary>
    public static string Version
    {
        get
        {
            string version =
                typeof(ThisAssembly).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is [System.Reflection.AssemblyInformationalVersionAttribute attr, ..]
                    ? attr.InformationalVersion
                    : "0.0.0-dev";

            string? commit = typeof(ThisAssembly).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "Commit")
                ?.Value;

            return string.IsNullOrWhiteSpace(commit) ? version : $"{version}+{commit}";
        }
    }
}
