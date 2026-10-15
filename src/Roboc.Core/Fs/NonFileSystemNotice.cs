using Roboc.Core.Model;

namespace Roboc.Core.Fs;

/// <summary>
/// The refusal every verb gives when a path is not an item in the file system, and the
/// single place that wording lives.
///
/// <b>One wording, four call sites.</b> A shell namespace item -- a phone over MTP, a
/// library, a search result, the contents of an archive -- exposes a <c>Path</c> property
/// and that property is a string, so it arrives looking exactly like a file. It is not
/// one, and no file API can be asked about it.
///
/// This was written out separately in the copy path, the delete path and the recycle verb,
/// and by the time it was reviewed there were four near-identical sentences with different
/// phrasings. Changing the last line meant finding all of them. It lives here now so the
/// next change is one edit.
///
/// <b>The wording has to fit both directions.</b> A device path can arrive as the source
/// (pasting *from* a phone) or as the destination (pasting *to* one). Advice phrased for
/// the first -- "copy it to a local disk first" -- is nonsense for the second, where the
/// destination already is a local disk. So the message names the limitation instead of
/// prescribing one direction's workaround.
///
/// <b>And it deliberately does not print the path.</b> That path is a device identifier:
/// it means nothing to a user, and it is long enough that the dialog had to cut it off.
/// The device's own name is what identifies the place.
/// </summary>
internal static class NonFileSystemNotice
{
    /// <summary>True when this is a path the Win32 file APIs can be asked about.</summary>
    public static bool IsUsable(string path) => PathUtil.LooksLikeFileSystemPath(path);

    /// <summary>
    /// The explanation, on its own. Callers decide how to show it -- a dialog from the
    /// menu, stderr from a terminal -- because that difference is theirs, not this text's.
    /// </summary>
    public static string Text => Lang.Pick(
        "⚠️这个位置不是文件系统里的内容，本工具只能处理文件系统路径。\r\n\r\n" +
        "便携设备（手机、相机等以 MTP 连接）、库、搜索结果、压缩包里的内容由资源管理器用 " +
        "shell 命名空间表示，没有文件系统路径。\r\n\r\n" +
        "本工具因此无法处理 MTP 设备与本地存储之间的数据交换。请先用资源管理器完成这一步，" +
        "再对本地那一份使用本工具。",

        "⚠️This location is not an item in the file system; this tool works only with file " +
        "system paths.\r\n\r\n" +
        "Content inside a portable device (a phone or camera connected over MTP), a library, " +
        "a search result or an archive is represented by Explorer through the shell namespace " +
        "and has no file system path.\r\n\r\n" +
        "This tool therefore cannot move data between an MTP device and local storage. Use " +
        "Explorer for that step, then work on the local copy with this tool.");
}
