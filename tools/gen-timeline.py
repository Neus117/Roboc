"""从 _raw-requests.txt 重建 03-项目时间线.md。

为什么要有这个脚本，而不是手改那 44 条截断：

  · 源文件是 205 条真实请求的**全文**，目标文件里每一条都要出现，
    顺序、时间戳、归属日期都不能错。手抄 205 条错误率不低，
    而且错了不会报错 —— 只会静默地少一条或串一行。
  · 重建必须可复现。将来源文件更新了，重跑一次就行，不需要再手工对一遍。

策略（依据是量出来的分布，不是感觉）：

  真实需求（187 条，合计 20,620 字，中位 76 字）
      全文保留。短是这个语料的常态，"截断"砍掉的是本来就不长的话。

  控制台整段粘贴（18 条，合计 33,570 字，占全文 62%）
      截断。这些是把 PowerShell 的版权头、提示符、进度条刷屏一起粘进来的，
      全文保留会让文档六成是终端噪音。但**只砍粘贴部分**：粘贴之后用户
      自己写的话全部保留，因为那才是需求。

  识别方式：正文以版权头 / 提示符开头，或含两个以上 `PS X:\\` 提示符。
  这是启发式，所以脚本会把判定结果打印出来供人工核对。

格式：

  每条请求占一行（`- **HH:MM**  正文`）。原始记录里换行不表示新请求，
  保留原始换行会让"一条请求"看起来像好几条 —— 那正是这份文档最该避免的
  误读。正文里的换行折成空格。

用法：

  python tools\\gen-timeline.py --check     # 只报告，不写文件
  python tools\\gen-timeline.py --write     # 写入 03-项目时间线.md
"""

import argparse
import re
import sys
from pathlib import Path

RAW = Path(r"D:\Coding\DeepSeek 开发经验\_raw-requests.txt")
OUT = Path(r"D:\Coding\DeepSeek 开发经验\03-项目时间线.md")

# 控制台粘贴的标记
PS_HEAD = re.compile(r"^\s*(Windows PowerShell|版权所有|Copyright \(C\))")
PS_PROMPT = re.compile(r"PS [A-Z]:\\")
# 进度条 / 速率行：这类行在粘贴里成片出现，单独看毫无信息
PS_NOISE = re.compile(r"^\s*[\d.,]+\s*[fF]/s|^\s*\d+%|ETA \d|items/s")

CONSOLE_HEAD = 120      # 粘贴部分最多贡献多少字
CONSOLE_FLOOR = 300     # 整条短于这个字数就不截断：为省几十字丢掉上下文不划算

# 人工归纳表：这部分不是从原始记录生成的，重建时必须原样保留。
# 脚本从现有文件里把它摘出来，附在新内容之后。
SUMMARY_HEADING = "## 这一天做了什么（人工归纳）"


def parse_entries(text):
    """解析成 [(日期, 时间, 正文)]，顺序即原始顺序。"""
    blocks = re.split(r"^### (\d\d-\d\d) (\d\d:\d\d)\s*$", text, flags=re.M)

    entries = []
    for i in range(1, len(blocks), 3):
        day = blocks[i].strip()
        clock = blocks[i + 1].strip()
        body = blocks[i + 2].strip() if i + 2 < len(blocks) else ""
        entries.append((day, clock, body))

    return entries


def looks_like_console(body):
    """是否按"控制台整段粘贴"处理（即截断）。

    短条目整条保留。整条只有两三百字时，粘贴部分也就一两百字，
    截断省不下什么，却会把"他当时看到的是什么"这个上下文丢掉。
    截断的目的是不让文档被终端刷屏淹没，不是为了短而短。
    """
    if len(body) < CONSOLE_FLOOR:
        return False

    return bool(PS_HEAD.match(body)) or len(PS_PROMPT.findall(body)) >= 2


def collapse(body):
    """一条请求占一行：正文里的换行折成空格，连续空白合并。"""
    return re.sub(r"\s+", " ", body).strip()


def condense_console(body):
    """截断控制台粘贴，但保留用户自己写的那部分。

    关键点：**判据是粘贴从哪开始，不是整条像不像粘贴。**

    2026-10-14 实测到的反例：10-12 11:48 那条开头是人写的一整段测试结论
    （"警告成功消除，多选文件…功能初步测试正常"），末尾才附了一段控制台。
    按"整条像粘贴"判定会把那段真需求一起砍掉 —— 那正是这次重建要消除的
    问题，不能自己再制造一次。
    """
    lines = body.splitlines()

    # 粘贴的起点：第一处强控制台标记；没有则是第一个提示符行。
    start = None
    for index, line in enumerate(lines):
        stripped = line.strip()
        if PS_HEAD.match(stripped) or "Copyright (C)" in stripped:
            start = index
            break
        if start is None and PS_PROMPT.search(stripped):
            start = index

    if start is None:
        return collapse(body)

    prose = " ".join(part.strip() for part in lines[:start] if part.strip())
    prose = collapse(prose)

    marker = "（此处为控制台整段粘贴，已截断；全文见 `_raw-requests.txt`）"

    if prose:
        return f"{prose} {marker}"

    # 整条就是粘贴，没有自己的话：留一小段开头，说明粘的是什么。
    head = collapse(body)[:CONSOLE_HEAD]
    return f"{head}… {marker}"


def build(entries):
    days = []
    for day, clock, body in entries:
        if not days or days[-1][0] != day:
            days.append((day, []))
        days[-1][1].append((clock, body))

    out = []
    out.append("# Roboc 项目时间线")
    out.append("")
    out.append(f"**{entries[0][0]} {entries[0][1]} → {entries[-1][0]} {entries[-1][1]}，"
               f"{len(entries)} 条真实请求。**")
    out.append("")
    out.append("**从会话原始记录提取，不是回忆。** 每一条都可回溯到 `sessions/` 里的原文，"
               "全文见 `_raw-requests.txt`。")
    out.append("")
    out.append("**每一条占一行。** 原始记录里的换行不代表新的请求，"
               "所以正文中的换行已折成空格 —— 否则一条请求会被误读成好几条。")
    out.append("")
    out.append("---")
    out.append("")

    for day, items in days:
        out.append(f"## {day}   （{len(items)} 条）")
        out.append("")
        for clock, body in items:
            text = condense_console(body) if looks_like_console(body) else collapse(body)
            out.append(f"- **{clock}**  {text}")
        out.append("")

    return out


def extract_summary(existing):
    """把人工归纳那一节从现有文件里摘出来，原样保留。"""
    if not existing.exists():
        return []

    text = existing.read_text(encoding="utf-8")
    at = text.find(SUMMARY_HEADING)
    if at < 0:
        return []

    return text[at:].rstrip().splitlines()


def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="只报告")
    mode.add_argument("--write", action="store_true", help="写入目标文件")
    args = parser.parse_args()

    entries = parse_entries(RAW.read_text(encoding="utf-8"))

    if not entries:
        sys.exit("没有解析出任何条目，检查 _raw-requests.txt 的格式")

    console = [(d, c, b) for d, c, b in entries if looks_like_console(b)]

    print(f"条目总数      : {len(entries)}")
    print(f"其中控制台粘贴: {len(console)}")
    for d, c, b in console:
        print(f"    {d} {c}  {len(b):>5} 字")

    # 同一天内时间必须递增，否则说明解析串位了
    prev = None
    for day, clock, _ in entries:
        if prev is not None and day == prev[0] and clock < prev[1]:
            print(f"  ⚠ 时间倒序: {prev[0]} {prev[1]} -> {day} {clock}")
        prev = (day, clock)

    lines = build(entries)
    summary = extract_summary(OUT)

    if summary:
        lines.append("---")
        lines.append("")
        lines.extend(summary)
        lines.append("")

    body = "\r\n".join(lines)

    real_chars = sum(len(b) for d, c, b in entries if not looks_like_console(b))
    print(f"\n真实需求字数  : {real_chars:,}")
    print(f"生成后行数    : {len(lines)}")
    print(f"生成后字符数  : {len(body):,}")
    print(f"人工归纳      : {'已保留' if summary else '★ 没找到，会被丢掉'}")

    if args.check:
        print("\n--check：未写入。")
        return

    OUT.write_text(body, encoding="utf-8", newline="")
    print(f"\n已写入 {OUT}")


if __name__ == "__main__":
    main()
