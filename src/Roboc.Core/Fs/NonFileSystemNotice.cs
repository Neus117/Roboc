using Roboc.Core.Model;

namespace Roboc.Core.Fs;

/// <summary>
/// The refusal every verb gives when a path is not an item in the file system, and the
/// single place that wording lives.
///
/// <b>One wording, five call sites.</b> A shell namespace item -- a phone over MTP, a
/// library, a search result, the contents of an archive -- exposes a <c>Path</c> property
/// and that property is a string, so it arrives looking exactly like a file. It is not
/// one, and no file API can be asked about it.
///
/// This was written out separately in the copy path, the delete path, the recycle verb and
/// both engines, and by the time it was reviewed there were five near-identical sentences
/// with four different phrasings. Changing one line meant finding all of them. It lives
/// here now so the next change is one edit.
///
/// <b>The wording has to fit both directions.</b> A device path can arrive as the source
/// (pasting *from* a phone) or as the destination (pasting *to* one). Advice phrased for
/// the first -- "copy it to a local disk first" -- is nonsense for the second, where the
/// destination already is a local disk. So the message names the limitation, and its one
/// piece of advice covers both directions: copy out, or copy in, with Explorer; then use
/// this tool on the copy.
///
/// <b>Two wordings, on purpose.</b> <see cref="Text"/> is for a dialog, where there is room
/// to explain. <see cref="Reason"/> is for a line inside a failure list -- "已回收 0 项，
/// 1 项失败：" followed by ten of these -- where the full explanation would bury the list it
/// belongs to. Both live here so a list and a dialog cannot end up saying different things.
///
/// <b>Nothing here prints the path.</b> That path is a device identifier: it means nothing
/// to a user, and it is long enough that the dialog had to cut it off. The device's own
/// name is what identifies the place.
///
/// <b>"File system", not "local file system".</b> The test accepts a UNC path, so a network
/// share works. Saying "local" would tell everyone with a mapped drive or a NAS that their
/// setup is unsupported, which is false. For the same reason the limitation is stated as
/// "MTP and anything else", not "MTP and local storage" -- MTP to a share is unsupported
/// too.
/// </summary>
internal static class NonFileSystemNotice
{
    /// <summary>True when this is a path the Win32 file APIs can be asked about.</summary>
    public static bool IsUsable(string path) => PathUtil.LooksLikeFileSystemPath(path);

    /// <summary>
    /// The explanation, in full. Callers decide how to show it -- a dialog from the menu,
    /// stderr from a terminal -- because that difference is theirs, not this text's.
    /// </summary>
    public static string Text => Lang.Pick(
        "⚠️这个位置不在文件系统内。\r\n\r\n" +
        "便携设备（手机、相机等以 MTP 连接）、库、搜索结果、压缩包里的内容由资源管理器用 " +
        "shell 命名空间表示，没有文件系统路径。\r\n\r\n" +
        "本工具无法在 MTP 设备与其它存储之间传送文件。请先用资源管理器把文件复制出来或" +
        "复制进去，再用本工具处理那一份。",

        "⚠️This location is not in the file system.\r\n\r\n" +
        "Content inside a portable device (a phone or camera connected over MTP), a library, " +
        "a search result or an archive is represented by Explorer through the shell namespace " +
        "and has no file system path.\r\n\r\n" +
        "This tool cannot move files between an MTP device and any other storage. Copy the " +
        "files out, or in, with Explorer first, then use this tool on that copy.");

    /// <summary>
    /// The same refusal as one short line, for a failure list.
    ///
    /// No path, for the reason above; and no second sentence, because a list of ten of these
    /// is read for which items failed, not for why the whole class of them fails.
    /// </summary>
    public static string Reason => Lang.Pick(
        "读不了：它不在文件系统内（便携设备、库、搜索结果或压缩包）。",
        "cannot read: it is not in the file system (a portable device, library, search result or archive).");
}
