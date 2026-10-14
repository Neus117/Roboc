using System.Text;
using Microsoft.Win32;
using Roboc.Core.Interop;
using Roboc.Core.Model;

namespace Roboc.Core.Shell;

/// <summary>
/// Registers and removes the Explorer context menu: one <c>Roboc</c> cascade
/// containing 复制, 粘贴, 回收, 回收空间, 彻底删除.
///
/// <b>Why the submenu is nested rather than referenced.</b> The submenu was
/// originally defined once, in a key of its own, and referenced from each parent
/// with <c>ExtendedSubCommandsKey</c>. That looked tidier and it did not work: the
/// sub-verbs received no context at all. 复制, 回收 and 彻底删除 were invoked with
/// no file paths, and 粘贴 received a literal <c>%V</c> instead of the folder,
/// because a verb defined in a neutral key is not invoked *on* anything. The
/// selection and the substitution belong to the parent context, and a referenced
/// submenu does not inherit them.
///
/// The verbs are therefore written under each parent verb's own <c>shell</c>
/// subkey, where the context is inherited. It costs a few more keys, and they are
/// generated rather than maintained by hand, so the duplication is free.
///
/// <c>MultiSelectModel</c> is set on the sub-verbs as well as the parents: it is
/// the invoked verb's own selection model that decides whether one invocation
/// receives every selected file or one invocation per file.
///
/// Everything lives under HKEY_CURRENT_USER, so installation needs no
/// administrator rights and never touches another user's settings.
/// </summary>
public static class ContextMenuInstaller
{
    private const string ClassesRoot = @"Software\Classes";

    /// <summary>
    /// Registers under HKEY_LOCAL_MACHINE instead of HKEY_CURRENT_USER.
    ///
    /// Worth having because every third-party entry that actually appears in this
    /// machine's context menu is registered in HKLM -- and none of the static
    /// verbs in HKCU appear at all, including other applications' ones. Whether
    /// that is a scope rule or a coincidence is what this switch exists to settle.
    ///
    /// Requires elevation, because HKLM\SOFTWARE\Classes does.
    /// </summary>
    public static bool MachineScope { get; set; }

    private static RegistryKey Root => MachineScope ? Registry.LocalMachine : Registry.CurrentUser;

    /// <summary>One submenu entry.</summary>
    /// <param name="Key">Registry key name. The numeric prefix fixes the order,
    /// because a static cascade is enumerated by key name.</param>
    /// <param name="Label">What the user reads.</param>
    /// <param name="Args">Command line, with %V substituted by Explorer.</param>
    /// <param name="NeedsSelection">False for verbs that make sense with nothing
    /// selected; those are the only ones offered on a folder's background.</param>
    private readonly record struct Verb(
        string Key, string Label, string Args, bool NeedsSelection, string IconFile,
        bool BackgroundTarget = false, string? AppliesTo = null);

    /// <summary>
    /// The menu entries, rebuilt on every access.
    ///
    /// <b>Not a static readonly field, and that is the whole point.</b> It was one, which
    /// meant the six labels were resolved once, on the first touch of this type, and
    /// frozen in whatever language was in force at that moment. Main calls SelfHeal
    /// almost immediately, so that moment is always startup -- and re-registering the
    /// menu after a language change therefore rewrote the same labels it had written
    /// before. The setting appeared to do nothing at all.
    ///
    /// This is the second time this exact mistake has been made here; the first was
    /// RobocSettings.Catalogue. Anything that calls Lang.Pick must be evaluated after the
    /// language is known, and a static initialiser runs whenever the runtime chooses.
    ///
    /// Rebuilding costs six small structs per call, and this type is touched a handful of
    /// times per process.
    /// </summary>
    private static Verb[] Verbs =>
    [
        // %1 rather than nothing: Explorer substitutes it and never appends the
        // selection, which is what every selection verb here got wrong. With
        // MultiSelectModel Single it is invoked once per selected file, and the
        // program reassembles the batch.
        new("01copy",    Lang.Pick("复制", "Copy"),     @"clipboard --gui-errors ""%1""",          true,  "copy.ico"),
        new("02paste",   Lang.Pick("粘贴", "Paste"),     @"paste --to ""%V"" --gui-errors",         false, "paste.ico", true),
        new("03purge",   Lang.Pick("删除", "Delete"), @"purge --gui-errors ""%1""",              true,  "delete.ico"),
        new("04recycle", Lang.Pick("回收", "Recycle"),     @"recycle --gui-errors ""%1""",            true,  "recycle.ico"),
        new("05reclaim", Lang.Pick("回收站", "Recycle bin"), "reclaim --gui-errors",                    false, "recycle_bin.ico"),

        // Last, and it takes no selection. A context-menu entry cannot show whether a
        // switch is on, so four of them would be four entries that always look the same
        // and always have to be opened to find out what they currently do. One page that
        // shows every switch, what each costs and what each defaults to is fewer things
        // to learn -- and it is also where 卸载 lives, one step behind an explanation of
        // what it will and will not touch.
        new("06settings", Lang.Pick("设置", "Settings"),   "settings --gui-errors",                  false, "settings.ico"),

        // 取回 is not its own entry.
        //
        // A static verb cannot change its label with the selection, and AppliesTo
        // cannot express a path condition here -- four expressions were tried
        // against this machine and every one of them hid the entry outright. So
        // 回收 carries both meanings and the command announces which one it is
        // about to perform, which also gives the user a way to back out.
    ];

    /// <summary>The three places the cascade is offered.</summary>
    private static readonly (string Path, bool Background)[] Parents =
    [
        (@"*\shell\Roboc", false),
        (@"Directory\shell\Roboc", false),
        (@"Directory\Background\shell\Roboc", true),
    ];

    /// <summary>
    /// The executable the menu currently points at, read back from the menu's own
    /// copy command.
    ///
    /// This deliberately does not live in a bookkeeping key of its own. Such a key
    /// would be redundant, since the path is already in the command string, and it
    /// would be a single point of failure: a registry that refused that one key
    /// would fail the whole install while the menu itself would have worked. A
    /// redundant key did exactly that once.
    /// </summary>
    public static string? InstalledPath
    {
        get
        {
            // Both layouts are checked, not just the cascade. Reading only the
            // cascade path made IsInstalled report false whenever the flat layout
            // was installed, which silently disabled the start-up repair.
            string[] candidates =
            [
                ClassesRoot + @"\*\shell\Roboc.01copy\command",
                ClassesRoot + @"\*\shell\Roboc\shell\01copy\command",
            ];

            RegistryKey? key = null;
            foreach (string candidate in candidates)
            {
                key = Root.OpenSubKey(candidate);
                if (key is not null)
                {
                    break;
                }
            }

            using (key)
            {
                if (key?.GetValue(string.Empty) is not string command)
                {
                    return null;
                }

                if (command.Length < 2 || command[0] != '"')
                {
                    return command;
                }

                int closing = command.IndexOf('"', 1);
                return closing > 1 ? command[1..closing] : null;
            }
        }
    }

    public static bool IsInstalled => InstalledPath is not null;

    /// <summary>True when the registered path differs from where this exe now lives.</summary>
    public static bool IsStale(string currentExecutablePath)
    {
        string? installed = InstalledPath;
        return installed is not null &&
               !string.Equals(installed, currentExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the five verbs directly under the context instead of inside a
    /// Roboc cascade.
    ///
    /// A fallback, not the default. Explorer passes the selection to a verb that
    /// is registered in a context it understands; a cascade is one layer of
    /// indirection more, and it is that layer which has been unreliable here. This
    /// layout gives up the submenu and keeps the behaviour.
    /// </summary>
    public static bool FlatLayout { get; set; }

    /// <summary>
    /// Writes the whole menu. Returns the number of command keys written, or -1
    /// with a reason when the registry refused it.
    /// </summary>
    public static int Install(string executablePath, out string failure, Action<string>? log = null)
    {
        string quoted = '"' + executablePath + '"';
        failure = string.Empty;
        int written = 0;

        try
        {
            foreach ((string path, bool background) in Parents)
            {
                if (FlatLayout)
                {
                    RemoveKey(Root, ClassesRoot + @"\" + path);
                    written += WriteFlat(ClassesRoot + @"\" + path, quoted, background);
                }
                else
                {
                    foreach (string flat in FlatKeysOf(path))
                    {
                        RemoveKey(Root, flat);
                    }

                    written += WriteCascade(ClassesRoot + @"\" + path, quoted, background);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // A locked-down or virtualised registry is a real configuration, not a
            // crash. Say what could not be done and leave the rest alone.
            failure = Lang.Pick($"注册表写入被拒绝：{ex.Message}", $"registry write denied: {ex.Message}");
            log?.Invoke(failure);
            return -1;
        }
        catch (System.Security.SecurityException ex)
        {
            failure = Lang.Pick($"注册表写入被拒绝：{ex.Message}", $"registry write denied: {ex.Message}");
            log?.Invoke(failure);
            return -1;
        }

        // The clipboard decides whether 粘贴 starts enabled.
        SyncPaste(ClipboardFiles.Get().Count > 0, out _);

        NotifyShell();

        log?.Invoke(Lang.Pick($"已注册 {written} 个右键菜单项（HKCU，无需管理员）", $"Registered {written} context-menu entries (HKCU, no administrator needed)"));
        return written;
    }

    /// <summary>The flat verbs belonging to one parent context.</summary>
    private static IEnumerable<string> FlatKeysOf(string parentPath)
    {
        string context = ContextOf(parentPath);

        foreach (Verb verb in Verbs)
        {
            yield return $@"{ClassesRoot}\{context}\Roboc.{verb.Key}";
        }
    }

    /// <summary>
    /// Writes the five verbs at the same level as everything else, one registry key
    /// each, with no parent between them and the context.
    /// </summary>
    private static int WriteFlat(string parentPath, string quotedExe, bool background)
    {
        string context = ContextOf(parentPath);

        // A bare path, quotes stripped: the verbs on this machine that actually
        // appear use an unquoted icon path even though it contains spaces.
        string executablePathOfVerb = quotedExe.Trim('"');
        int written = 0;

        foreach (Verb verb in Verbs)
        {
            // The numeric prefix is kept so the entries sort as intended: a flat
            // verb list is ordered by key name, and dropping the prefix produced
            // copy, purge, reclaim, recycle.
            string path = $@"{context}\Roboc.{verb.Key}";
            using RegistryKey key = Root.CreateSubKey(path);

            string label = "Roboc " + verb.Label;

            // Both label forms. The third-party verbs that do appear on this machine
            // set the default value and MUIVerb to the same string.
            key.SetValue(string.Empty, label);
            key.SetValue("MUIVerb", label);
            key.SetValue("Icon", IconFor(executablePathOfVerb, verb.IconFile));
            // Player for every verb, including the two that need no selection.
            //
            // Single was measured to do the opposite of what it was assumed to do:
            // a verb marked Single was absent with one file selected and present
            // with several. Whatever the exact rule is, Player has been observed to
            // keep an entry visible in both cases, and visibility should not depend
            // on how many files happen to be selected.
            //
            // %1 stays in the command because that is what a selection substitutes,
            // and PendingSelection covers the case of one invocation per file.
            key.SetValue("MultiSelectModel", "Player");

            // Explorer evaluates this against the selection and omits the entry when
            // it does not match, which is what lets 回收 and 取回 look like one command
            // that changes its name.
            if (verb.AppliesTo is { Length: > 0 } condition)
            {
                key.SetValue("AppliesTo", condition);
            }

            if (background && verb.NeedsSelection)
            {
                key.SetValue("LegacyDisable", string.Empty);
            }

            using RegistryKey command = key.CreateSubKey("command");

            // --here tells the program that %V is the folder to paste into, which is
            // only true for the background verb. On a selection %V is the selected
            // item, and with several selected it is merely the first of them -- so a
            // multi-selection used to paste into whichever item happened to be first.
            string arguments = background && verb.BackgroundTarget
                ? verb.Args + " --here"
                : verb.Args;

            command.SetValue(string.Empty, quotedExe + " " + arguments);
            written++;
        }

        return written;
    }

    /// <summary>Writes one parent verb and the five verbs nested beneath it.</summary>
    private static int WriteCascade(string parentPath, string quotedExe, bool background)
    {
        // The bare path, for IconFor. The verbs below each have their own icon file and
        // IconFor is what resolves one -- this writer used to skip it entirely and point
        // every entry at the executable, which is why the menu showed the program's icon
        // six times over while five purpose-drawn ones sat unused beside it. The flat
        // writer had always done this correctly; only the layout that ships by default
        // did not.
        string executablePath = quotedExe.Trim('"');

        using RegistryKey parent = Root.CreateSubKey(parentPath);
        parent.SetValue("MUIVerb", "Roboc");

        // The parent's icon follows the Windows app theme.
        //
        // It used to be the executable's own icon resource -- quotedExe + ",0" -- which
        // cannot be switched: a resource index is fixed, and the ICO format has no theme
        // axis to switch on either. Naming a file is what makes the choice possible at all,
        // and it is the same artwork: Roboc.ico is what the executable embeds.
        parent.SetValue("Icon", ThemeIcon(executablePath));

        // Player: one invocation carrying every selected file, rather than one
        // invocation per file.
        parent.SetValue("MultiSelectModel", "Player");

        // An empty SubCommands is what tells Explorer the nested shell subkey is a
        // cascade. Without it the nested verbs are ignored entirely, the parent is
        // treated as a verb with no command, and clicking it offers to open the
        // file with some application instead.
        parent.SetValue("SubCommands", string.Empty);

        int written = 0;

        foreach (Verb verb in Verbs)
        {
            string path = $@"{parentPath}\shell\{verb.Key}";
            using RegistryKey key = Root.CreateSubKey(path);

            key.SetValue("MUIVerb", verb.Label);
            key.SetValue("Icon", IconFor(executablePath, verb.IconFile));
            // Player for every verb, including the two that need no selection.
            //
            // Single was measured to do the opposite of what it was assumed to do:
            // a verb marked Single was absent with one file selected and present
            // with several. Whatever the exact rule is, Player has been observed to
            // keep an entry visible in both cases, and visibility should not depend
            // on how many files happen to be selected.
            //
            // %1 stays in the command because that is what a selection substitutes,
            // and PendingSelection covers the case of one invocation per file.
            key.SetValue("MultiSelectModel", "Player");

            // Explorer evaluates this against the selection and omits the entry when
            // it does not match, which is what lets 回收 and 取回 look like one command
            // that changes its name.
            if (verb.AppliesTo is { Length: > 0 } condition)
            {
                key.SetValue("AppliesTo", condition);
            }

            // On the background there is no selection, so a verb that needs one is
            // disabled rather than offered and then refused.
            if (background && verb.NeedsSelection)
            {
                key.SetValue("LegacyDisable", string.Empty);
            }

            using RegistryKey command = key.CreateSubKey("command");

            // --here tells the program that %V is the folder to paste into, which is
            // only true for the background verb. On a selection %V is the selected
            // item, and with several selected it is merely the first of them -- so a
            // multi-selection used to paste into whichever item happened to be first.
            string arguments = background && verb.BackgroundTarget
                ? verb.Args + " --here"
                : verb.Args;

            command.SetValue(string.Empty, quotedExe + " " + arguments);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Enables or disables 粘贴 to match the clipboard.
    ///
    /// A static registry verb cannot be dimmed at the moment Explorer builds the
    /// menu; the only lever is the LegacyDisable value, which Explorer reads while
    /// building it. So the state is pushed into the registry instead of being
    /// queried, and this runs on every start.
    ///
    /// Writing only when the value would actually change keeps the common case to
    /// a few reads and no writes, which matters because this runs before every
    /// context-menu command.
    /// </summary>
    public static bool SyncPaste(bool hasClipboardFiles, out string failure)
    {
        failure = string.Empty;

        try
        {
            bool touchedAnything = false;

            // Both hives are examined, and only those where the menu is actually
            // installed are updated. Using the in-process Root/MachineScope flags
            // meant a machine-scope install was silently maintained in HKCU, so the
            // real 粘贴 verb never had its LegacyDisable cleared.
            foreach ((RegistryKey hive, bool machine) in new[]
                     {
                         (Registry.CurrentUser, false),
                         (Registry.LocalMachine, true),
                     })
            {
                List<string> keyPaths = InstalledPasteKeyPaths(hive);

                if (keyPaths.Count == 0)
                {
                    continue;
                }

                foreach (string keyPath in keyPaths)
                {
                    try
                    {
                        using RegistryKey key = hive.CreateSubKey(keyPath, writable: true);
                        bool disabled = key.GetValue("LegacyDisable") is not null;

                        if (hasClipboardFiles && disabled)
                        {
                            key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
                        }
                        else if (!hasClipboardFiles && !disabled)
                        {
                            key.SetValue("LegacyDisable", string.Empty);
                        }

                        touchedAnything = true;
                    }
                    catch (UnauthorizedAccessException)
                {
                    // A machine-scope menu maintained without elevation cannot be
                    // updated; the entries stay as they are rather than the command
                    // failing outright.
                        _ = machine;
                    }
                }
            }

            if (!touchedAnything && !hasClipboardFiles)
            {
                failure = Lang.Pick("未找到已安装的菜单项", "no installed menu entries were found");
                return false;
            }

            NotifyShell();
            RemoveStaleHusks();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return SyncPasteWithRegExe(hasClipboardFiles, out failure);
        }
        catch (System.Security.SecurityException)
        {
            return SyncPasteWithRegExe(hasClipboardFiles, out failure);
        }
    }

    /// <summary>
    /// Asks Explorer to rebuild its context menus.
    ///
    /// Registering a verb is not enough on its own: the shell caches the menu, and
    /// without this notification the new entries appear only if something else
    /// makes Explorer re-enumerate. The symptom is a menu that shows up after a
    /// manual Explorer restart and then stops showing, while the registry is
    /// demonstrably correct.
    /// </summary>
    public static void NotifyShell()
    {
        try
        {
            Shell32.SHChangeNotify(Shell32.SHCNE_ASSOCCHANGED, Shell32.SHCNF_IDLIST, 0, 0);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>
    /// Every 粘贴 verb, one per parent context, in whichever layout is installed.
    ///
    /// The layout is read from the registry rather than from FlatLayout, because
    /// that flag does not survive the process: every command other than the install
    /// that set it saw false, so SyncPaste wrote the cascade path. That recreated
    /// the empty Roboc parent key on every invocation -- the dead menu entry
    /// that kept coming back -- and left the real 粘贴 verb's LegacyDisable
    /// untouched, so 粘贴 never lit up no matter how many times files were copied.
    /// </summary>
    private static IEnumerable<string> PasteKeys()
    {
        bool flat = IsFlatLayoutInstalled();

        foreach ((string path, bool _) in Parents)
        {
            yield return flat
                ? $@"{ClassesRoot}\{ContextOf(path)}\Roboc.paste"
                : $@"{ClassesRoot}\{path}\shell\02paste";
        }
    }

    /// <summary>Whether the flat verbs are present, checked against the registry.</summary>
    private static bool IsFlatLayoutInstalled()
    {
        using RegistryKey? probe = Root.OpenSubKey(ClassesRoot + @"\*\shell\Roboc.01copy");
        return probe is not null;
    }

    /// <summary>
    /// The 粘贴 key of every context where the menu is installed in this hive.
    ///
    /// All of them, not just the files one. Updating only "*\shell" left the folders
    /// entry permanently enabled while the files entry followed the clipboard, so
    /// 粘贴 appeared for a folder selection and disappeared the moment a file was
    /// included -- two entries for one feature, each showing a different state.
    ///
    /// A flat verb list is ordered by key name, so the numeric prefix is what puts
    /// 回收 above 回收空间 rather than wherever the alphabet lands.
    /// </summary>
    private static List<string> InstalledPasteKeyPaths(RegistryKey hive)
    {
        var paths = new List<string>(3);

        foreach ((string path, bool _) in Parents)
        {
            foreach (string candidate in new[]
                     {
                         $@"{ClassesRoot}\{ContextOf(path)}\Roboc.{PasteVerbKey}",
                         $@"{ClassesRoot}\{path}\shell\02paste",
                     })
            {
                using RegistryKey? probe = hive.OpenSubKey(candidate);

                if (probe is not null)
                {
                    paths.Add(candidate);
                    break;
                }
            }
        }

        return paths;
    }

    /// <summary>The paste verb's key name, including its ordering prefix.</summary>
    private const string PasteVerbKey = "02paste";

    /// <summary>
    /// Deletes the cascade parent keys when the flat layout is the one installed.
    ///
    /// They are invisible husks: no label, no command, and Explorer renders them as
    /// a menu entry that does nothing when clicked.
    /// </summary>
    private static void RemoveStaleHusks()
    {
        if (!IsFlatLayoutInstalled())
        {
            return;
        }

        foreach ((string path, bool _) in Parents)
        {
            RemoveKey(Root, ClassesRoot + @"\" + path);
        }
    }

    /// <summary>
    /// The icon file to show for a verb.
    ///
    /// The icons ship in an assets folder beside the executable, because a static
    /// registry verb can only name a file: unlike an icon resource compiled into the
    /// binary, there is no way to address one of several icons inside a single exe.
    /// Falling back to the executable keeps the menu usable if the folder is moved
    /// away rather than leaving the entries iconless.
    /// </summary>
    private static string IconFor(string executablePath, string iconFile)
    {
        string? directory = Path.GetDirectoryName(executablePath);

        if (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "assets", "icons", iconFile);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return executablePath;
    }

    /// <summary>The program's own entry icon, by theme. Only this one varies.</summary>
    private const string LightThemeIcon = "Roboc.ico";
    private const string DarkThemeIcon = "Roboc-dark.ico";

    /// <summary>
    /// True when Windows is set to dark app mode.
    ///
    /// AppsUseLightTheme rather than SystemUsesLightTheme: the context menu follows the app
    /// theme, which is the setting the user changes under Personalisation &gt; Colours &gt;
    /// "Choose your default app mode".
    ///
    /// Absent means light. A profile that has never touched the setting has no value at all,
    /// and light is also the safer guess -- the light icon is the one that is legible on
    /// both backgrounds, which is why it is the default everywhere else too.
    /// </summary>
    private static bool IsDarkAppsTheme()
    {
        try
        {
            using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return personalize?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Whether Windows is set to dark app mode, for callers outside this class.
    ///
    /// The windows need it too, and for a different reason: their title-bar icon is drawn by
    /// the program rather than by the shell, so it can follow the theme with no lag at all --
    /// unlike the menu entry, which is a registry string Explorer reads when it builds the
    /// menu.
    /// </summary>
    public static bool DarkAppsTheme => IsDarkAppsTheme();

    /// <summary>The icon file for the program's own entry, in the current theme.</summary>
    private static string ThemeIcon(string executablePath)
        => IconFor(executablePath, IsDarkAppsTheme() ? DarkThemeIcon : LightThemeIcon);

    /// <summary>
    /// Points the program's own menu entry at the icon for the current theme.
    ///
    /// <b>Why this is done here and not by the shell.</b> A static verb's Icon value is a
    /// fixed path. The ICO format has no theme axis -- Explorer picks a frame by size, for
    /// DPI, and nothing else -- and there is no IconDark value to set beside Icon. The only
    /// way to vary it at runtime is to run inside Explorer, which means a native
    /// IExplorerCommand shell extension and an MSVC toolchain this project does not build
    /// with. Writing the registry is the whole of the alternative.
    ///
    /// <b>What it costs.</b> One use of lag. Explorer reads the value when it builds the menu,
    /// so the menu that is already open when the theme changes keeps the icon it was built
    /// with, and the next right-click has the new one. Nothing else: this is a registry read,
    /// a string comparison, and a write only when the two differ -- and SelfHeal, which calls
    /// it, already runs on every start.
    ///
    /// <b>Why comparing rather than remembering.</b> No bookkeeping key records what was last
    /// applied. The registry already holds the answer, so reading it back is both simpler and
    /// self-correcting: a value someone else changed, or one left behind by an older version,
    /// is put right by the same comparison.
    /// </summary>
    public static int SyncThemeIcon(string executablePath, Action<string>? log = null)
    {
        bool dark = IsDarkAppsTheme();
        string wanted = IconFor(executablePath, dark ? DarkThemeIcon : LightThemeIcon);
        int changed = 0;

        foreach ((string path, _) in Parents)
        {
            try
            {
                using RegistryKey? key = Root.OpenSubKey(ClassesRoot + @"\" + path, writable: true);

                // Not installed, or installed for another user: nothing to keep in step.
                if (key is null)
                {
                    continue;
                }

                if (key.GetValue("Icon") as string == wanted)
                {
                    continue;
                }

                key.SetValue("Icon", wanted);
                changed++;
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (IOException) { }
        }

        if (changed > 0)
        {
            log?.Invoke($"menu icon set to the {(dark ? "dark" : "light")} variant, {changed} key(s)");
        }

        return changed;
    }

    /// <summary>"@*\shell\Roboc" becomes "@*\shell".</summary>
    private static string ContextOf(string parentPath)
        => parentPath[..parentPath.LastIndexOf('\\')];

    /// <summary>
    /// Changes the 粘贴 state by running reg.exe.
    ///
    /// Kept as a fallback because direct registry writes can be refused for reasons
    /// invisible from inside the process. The known cause here was a Low mandatory
    /// integrity label on the executable, which is fixed in the artifact rather
    /// than worked around in code, but a fallback costs nothing when it is not
    /// needed and rescues the feature when it is.
    /// </summary>
    private static bool SyncPasteWithRegExe(bool hasClipboardFiles, out string failure)
    {
        failure = string.Empty;

        foreach (string keyPath in PasteKeys())
        {
            string key = "HKCU\\" + keyPath;

            string arguments = hasClipboardFiles
                ? $@"delete ""{key}"" /v LegacyDisable /f"
                : $@"add ""{key}"" /v LegacyDisable /t REG_SZ /d """" /f";

            if (!RunRegExe(arguments, out string error))
            {
                failure = error;
                return false;
            }
        }

        return true;
    }

    private static bool RunRegExe(string arguments, out string error)
    {
        error = string.Empty;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);

            if (process is null)
            {
                error = Lang.Pick("无法启动 reg.exe", "could not start reg.exe");
                return false;
            }

            process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 && !stderr.Contains("unable to find", StringComparison.OrdinalIgnoreCase))
            {
                error = stderr.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the same menu as a .reg file for the user to import.
    ///
    /// Emitting a file is also more honest than writing silently: the user can read
    /// exactly what will be added before deciding to add it.
    /// </summary>
    public static string BuildRegistryFile(string executablePath)
    {
        string quoted = '"' + executablePath + '"';
        bool hasClipboardFiles = ClipboardFiles.Get().Count > 0;
        var text = new StringBuilder();

        text.AppendLine("Windows Registry Editor Version 5.00");
        text.AppendLine();

        foreach ((string path, bool background) in Parents)
        {
            string parentKey = $@"HKEY_CURRENT_USER\{ClassesRoot}\{path}";
            text.AppendLine($"[{parentKey}]");
            text.AppendLine("\"MUIVerb\"=\"Roboc\"");
            text.AppendLine($"\"Icon\"=\"{Escape(IconFor(executablePath, "copy.ico"))}\"");
            text.AppendLine("\"MultiSelectModel\"=\"Player\"");
            text.AppendLine("\"SubCommands\"=\"\"");
            text.AppendLine();

            foreach (Verb verb in Verbs)
            {
                string key = $@"{parentKey}\shell\{verb.Key}";
                text.AppendLine($"[{key}]");
                text.AppendLine($"\"MUIVerb\"=\"{Escape(verb.Label)}\"");

            if (verb.AppliesTo is { Length: > 0 } verbCondition)
            {
                text.AppendLine($"\"AppliesTo\"=\"{Escape(verbCondition)}\"");
            }
                text.AppendLine($"\"Icon\"=\"{Escape(IconFor(executablePath, verb.IconFile))}\"");
                text.AppendLine($"\"MultiSelectModel\"=\"{(verb.NeedsSelection ? "Player" : "Single")}\"");

                if ((background && verb.NeedsSelection) || (verb.Key == "02paste" && !hasClipboardFiles))
                {
                    text.AppendLine("\"LegacyDisable\"=\"\"");
                }

                text.AppendLine();
                text.AppendLine($"[{key}\\command]");
                text.AppendLine($"@=\"{Escape(quoted + " " + verb.Args)}\"");
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>Removals, as a .reg file: the leading minus deletes a key.</summary>
    public static string BuildRegistryRemovalFile()
    {
        var text = new StringBuilder();
        text.AppendLine("Windows Registry Editor Version 5.00");
        text.AppendLine();

        foreach ((string path, bool _) in Parents)
        {
            text.AppendLine($@"[-HKEY_CURRENT_USER\{ClassesRoot}\{path}]");
        }

        return text.ToString();
    }

    /// <summary>A .reg value is quoted, so backslashes and quotes must be escaped.</summary>
    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Removes everything this installs. Safe to call when not installed.</summary>
    public static int Uninstall(Action<string>? log = null)
    {
        // Both hives, always. Uninstall used to follow the same MachineScope flag as
        // install, but that flag was never set on this path, so "uninstall --machine"
        // silently removed from HKCU instead -- reporting success while leaving the
        // machine-wide menu in place. Removing from both cannot be got wrong.
        int removed = Remove(Registry.CurrentUser, log) + Remove(Registry.LocalMachine, log);

        NotifyShell();
        return removed;
    }

    private static int Remove(RegistryKey hive, Action<string>? log)
    {
        int removed = 0;
        bool machine = ReferenceEquals(hive, Registry.LocalMachine);
        Action<string>? previous = null;
        _ = previous;

        foreach ((string path, bool _) in Parents)
        {
            if (RemoveKey(hive, ClassesRoot + @"\" + path))
            {
                removed++;
            }
        }

        // Keys left behind by earlier layouts, so switching between them does not
        // accumulate duplicates: the referenced submenu, and the flat verbs.
        foreach (string legacy in new[] { @"Roboc.Menu", @"Roboc.Menu.Background" })
        {
            RemoveKey(hive, ClassesRoot + @"\" + legacy);
        }

        foreach ((string path, bool _) in Parents)
        {
            foreach (string flat in FlatKeysOf(path))
            {
                RemoveKey(hive, flat);
            }
        }

        log?.Invoke(Lang.Pick(
            $"[{hive}] 已移除 {removed} 个右键菜单项",
            $"[{hive}] Removed {removed} context-menu entries"));
        return removed;
    }

    /// <summary>
    /// Deletes a key only if it exists. DeleteSubKeyTree with
    /// throwOnMissingSubKey:false succeeds on a key that was never created, which
    /// once made this report "removed 7" when nothing had been installed.
    /// </summary>
    private static bool RemoveKey(RegistryKey hive, string path)
    {
        try
        {
            using (RegistryKey? existing = hive.OpenSubKey(path))
            {
                if (existing is null)
                {
                    return false;
                }
            }

            Root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
