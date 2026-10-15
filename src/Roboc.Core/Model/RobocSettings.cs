using System.Text.Json;
using System.Text.Json.Serialization;

namespace Roboc.Core.Model;

/// <summary>
/// The user's choices, stored beside the executable.
///
/// <b>Beside the executable, not in the registry and not in AppData.</b> That single
/// decision is what makes the portable claim true: this folder can be copied to another
/// machine and behaves identically, and deleting it removes every trace of the program
/// without leaving a settings key behind in a hive nobody remembers to look in.
///
/// The one thing that cannot live here is anything the shell must see before the
/// program runs, which is why the context menu is still registry keys. That is what
/// `roboc uninstall` exists to remove.
/// </summary>
public sealed class RobocSettings
{
    /// <summary>Recreate hard links instead of expanding them into separate copies.</summary>
    public bool RecreateHardLinks { get; set; } = true;

    /// <summary>Record progress so an interrupted copy can continue.</summary>
    public bool ResumeEnabled { get; set; } = true;

    /// <summary>Copy NTFS alternate data streams.</summary>
    public bool CopyAlternateDataStreams { get; set; }

    /// <summary>Read every copied file back and compare it with the source.</summary>
    public bool VerifyChecksum { get; set; }

    /// <summary>Publish through a temporary file and an atomic rename.</summary>
    public bool AtomicPublish { get; set; } = true;

    /// <summary>
    /// "auto", "zh" or "en".
    ///
    /// Stored as a string rather than a bool so that a third language is a new value
    /// rather than a new field, and so that a settings file written by a future version
    /// is readable by this one -- an unknown value falls back to auto.
    /// </summary>
    public string Language { get; set; } = "auto";

    [JsonIgnore]
    public static string FilePath
    {
        get
        {
            string? directory = Path.GetDirectoryName(Environment.ProcessPath);
            return Path.Combine(directory ?? AppContext.BaseDirectory, "Roboc.settings.json");
        }
    }



    /// <summary>
    /// Reads the settings, falling back to defaults for anything missing or unreadable.
    ///
    /// <b>A settings file must never be able to stop a copy.</b> The first version of this
    /// caught IOException, UnauthorizedAccessException and JsonException -- the three
    /// failures that were expected. What actually happened was an
    /// InvalidOperationException from the serializer, because this program is trimmed and
    /// trimmed applications have reflection-based serialization switched off. None of the
    /// three handlers matched it, so it travelled all the way out of Main and every copy
    /// failed with a stack trace about JSON. The settings file is a convenience; the last
    /// catch here is what makes that true.
    /// </summary>
    public static RobocSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new RobocSettings();
            }

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.RobocSettings)
                ?? new RobocSettings();
        }
        catch (IOException) { return new RobocSettings(); }
        catch (UnauthorizedAccessException) { return new RobocSettings(); }
        catch (JsonException) { return new RobocSettings(); }
        catch (InvalidOperationException) { return new RobocSettings(); }
        catch (NotSupportedException) { return new RobocSettings(); }
    }

    public bool Save(out string error)
    {
        error = string.Empty;

        try
        {
            string? directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside the target and moved into place, so a crash mid-write
            // cannot leave a half-file that parses into nonsense on the next run.
            string temporary = FilePath + ".tmp";
            File.WriteAllText(
                temporary, JsonSerializer.Serialize(this, SettingsJsonContext.Default.RobocSettings));

            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (IOException ex) { error = ex.Message; return false; }
        catch (UnauthorizedAccessException ex) { error = ex.Message; return false; }
        catch (InvalidOperationException ex) { error = ex.Message; return false; }
        catch (NotSupportedException ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// Every switch the settings window shows, with what it buys and what it costs.
    ///
    /// Kept as data rather than as prose inside the window so the same text can be
    /// printed by `roboc settings --list`, and so an option cannot be added to the
    /// settings class without someone having to decide what to tell the user about it.
    ///
    /// <b>A method, not a property, and that is not a style choice.</b> A static property
    /// is initialised once, on first access -- and the first access can happen before the
    /// saved language preference has been read, in which case every label and every
    /// explanation would be frozen in whichever language the machine happened to report
    /// and would stay that way for the life of the process. Rebuilding the list on each
    /// call costs five small objects and removes the ordering hazard entirely.
    /// </summary>
    public static IReadOnlyList<SettingInfo> Catalogue() =>
    [
        new(
            Key: nameof(RecreateHardLinks),
            Label: Lang.Pick("保留硬链接", "Keep hard links"),
            Default: true,
            Benefit: Lang.Pick(
                "同一文件的多个名字在目标侧仍是链接，不占额外空间，改一处三处同步变化。",
                "Several names for one file stay linked at the destination. No extra space, "
                + "and editing the content once updates every name."),
            Cost: Lang.Pick(
                "每个多链接文件多一次系统调用。实测开销极小。",
                "One extra system call per linked file. Measured cost is negligible."),
            Advice: Lang.Pick(
                "建议保持开启。关闭后链接会展开成独立副本——内容正确，但占用成倍增加，且改动不再同步。",
                "Leave this on. Turned off, links become independent copies: the content is "
                + "right, but the space multiplies and later edits stop propagating.")),

        new(
            Key: nameof(ResumeEnabled),
            Label: Lang.Pick("断点续传", "Resume interrupted copies"),
            Default: true,
            Benefit: Lang.Pick(
                "中断后重新执行同样的复制即可继续，不必从头再来。",
                "Run the same copy again after an interruption and it continues where it "
                + "stopped instead of starting over."),
            Cost: Lang.Pick(
                "每记录一次进度就要把这部分数据刷到磁盘，所以大文件复制的速度受限于磁盘而非缓存。",
                "Progress is flushed to disk as it is recorded, so large copies run at the "
                + "speed of the disk rather than of the cache."),
            Advice: Lang.Pick(
                "建议保持开启。若只是搬运可丢弃的临时数据、且在意速度，可以关闭。",
                "Leave this on. Turn it off only when moving disposable data and speed "
                + "matters more than being able to resume.")),

        new(
            Key: nameof(CopyAlternateDataStreams),
            Label: Lang.Pick("保留数据流（ADS）", "Keep alternate data streams"),
            Default: false,
            Benefit: Lang.Pick(
                "保留「来自 Internet」标记与 Office 元数据。浏览器下载、邮件附件、压缩包解压的文件几乎都带这个标记。",
                "Preserves the \u201cdownloaded from the internet\u201d mark and Office metadata. "
                + "Files from a browser, a mail attachment or an extracted archive almost "
                + "always carry the mark."),
            Cost: Lang.Pick(
                "吞吐 −3.8% ~ 7.4%（每个文件多一次系统调用，实测 19 µs/文件）。",
                "Throughput −3.8% to 7.4% (one extra system call per file, measured at "
                + "19 µs per file)."),
            Advice: Lang.Pick(
                "备份归档、搬移下载文件时开启；日常整理不必开。目标是 FAT32、U 盘或网络共享时开了也没用——那些文件系统无法承载数据流。",
                "Turn on for backups and for moving downloaded files; not needed for "
                + "ordinary tidying. It has no effect on FAT32, USB drives or network "
                + "shares, which cannot carry streams at all.")),

        new(
            Key: nameof(VerifyChecksum),
            Label: Lang.Pick("复制后校验内容", "Verify contents after copying"),
            Default: false,
            Benefit: Lang.Pick(
                "复制完成后把目标文件读回来逐字节比对。能发现内存位翻转、驱动缺陷、控制器写错扇区——这些都会产生大小正确、时间戳正确、内容却错了的文件。",
                "Reads the destination back and compares it byte for byte. Catches flipped "
                + "bits, driver faults and controllers writing the wrong sector -- all of "
                + "which produce a file of the right size and the right timestamp with the "
                + "wrong contents."),
            Cost: Lang.Pick(
                "耗时 +54%（源和目标各要读一遍）。",
                "Takes 54% longer: both the source and the destination are read."),
            Advice: Lang.Pick(
                "复制到移动硬盘、U 盘、网络位置，或备份不可替代的数据时开启。Windows 原生复制不做这个检查，robocopy 也不做。",
                "Turn on when copying to an external disk, a USB drive or a network "
                + "location, or when backing up anything irreplaceable. Neither Windows' "
                + "own copy nor robocopy checks this.")),

        new(
            Key: nameof(AtomicPublish),
            Label: Lang.Pick("原子发布", "Publish atomically"),
            Default: true,
            Benefit: Lang.Pick(
                "先写临时文件再改名，目标路径永不被观察到半成品。断电或崩溃不会留下损坏的目标文件。",
                "Writes to a temporary file and renames it into place, so the destination "
                + "path is never seen half-written. A power loss or a crash leaves no "
                + "damaged file behind."),
            Cost: Lang.Pick(
                "每个文件多一次改名。对海量小文件有可测量的开销。",
                "One extra rename per file, which is measurable across very many small "
                + "files."),
            Advice: Lang.Pick(
                "建议保持开启。关闭后写入更快，但中途失败会在目标留下不完整的文件。",
                "Leave this on. Turned off, writes are faster, but a failure part way "
                + "through leaves an incomplete file at the destination.")),
    ];

    public bool Get(string key) => key switch
    {
        nameof(RecreateHardLinks) => RecreateHardLinks,
        nameof(ResumeEnabled) => ResumeEnabled,
        nameof(CopyAlternateDataStreams) => CopyAlternateDataStreams,
        nameof(VerifyChecksum) => VerifyChecksum,
        nameof(AtomicPublish) => AtomicPublish,
        _ => false,
    };

    public void Set(string key, bool value)
    {
        switch (key)
        {
            case nameof(RecreateHardLinks): RecreateHardLinks = value; break;
            case nameof(ResumeEnabled): ResumeEnabled = value; break;
            case nameof(CopyAlternateDataStreams): CopyAlternateDataStreams = value; break;
            case nameof(VerifyChecksum): VerifyChecksum = value; break;
            case nameof(AtomicPublish): AtomicPublish = value; break;
        }
    }


}

/// <summary>One switch, and everything the user needs to decide about it.</summary>
public readonly record struct SettingInfo(
    string Key, string Label, bool Default, string Benefit, string Cost, string Advice);

/// <summary>
/// The serializer for the settings file, generated at compile time.
///
/// Required, not preferred: this program is published trimmed, and trimming turns off
/// reflection-based serialization. Calling the reflection overloads compiles cleanly and
/// then throws InvalidOperationException the first time a settings file exists -- which
/// is how it behaved until a stack trace said so.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RobocSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
