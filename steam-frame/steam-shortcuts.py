#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Steam 非 Steam 游戏库快捷方式（shortcuts.vdf）的操作助手。

shortcuts.vdf 是 Valve 的二进制 VDF 格式，不是文本 VDF，所以必须按字节解析；
直接 sed/替换字符串会把文件写坏，Steam 会当成空库（等于把用户所有非 Steam 快捷方式清空）。

格式（type byte 后跟 NUL 结尾的 key）：
    0x00 object    0x01 string    0x02 int32     0x03 float32
    0x04 pointer   0x05 wstring   0x06 color     0x07 uint64   0x08 end

顶层结构：
    \\x00 "Shortcuts" \\x00
        \\x00 "0" \\x00 <各字段> \\x08
        \\x00 "1" \\x00 <各字段> \\x08
        ...
    \\x08
    \\x08

用法：
    steam-shortcuts.py --list                    # 列出所有快捷方式
    steam-shortcuts.py --remove-clash            # 删除所有 Clash Verge 条目
    steam-shortcuts.py --dedupe                  # 去掉重复条目（保留第一个）
    steam-shortcuts.py --list --file <路径>      # 指定文件
    --dry-run                                    # 只显示将要做什么，不写文件
"""

import os
import shutil
import struct
import sys
import glob

TYPE_OBJECT  = 0x00
TYPE_STRING  = 0x01
TYPE_INT32   = 0x02
TYPE_FLOAT   = 0x03
TYPE_POINTER = 0x04
TYPE_WSTRING = 0x05
TYPE_COLOR   = 0x06
TYPE_UINT64  = 0x07
TYPE_END     = 0x08


def read_cstr(b, i):
    j = b.find(b"\x00", i)
    if j < 0:
        raise ValueError("字符串没有终止符，文件可能已损坏（偏移 %d）" % i)
    return b[i:j], j + 1


def parse(b, i=0):
    """解析一个 object，返回 (有序字典, 下一个偏移)。字段值为 (类型标记, 值) 二元组。"""
    d = {}
    n = len(b)
    while i < n:
        t = b[i]
        i += 1
        if t == TYPE_END:
            return d, i
        raw_key, i = read_cstr(b, i)
        key = raw_key.decode("utf-8", "replace")

        if t == TYPE_OBJECT:
            val, i = parse(b, i)
            d[key] = ("obj", val)
        elif t == TYPE_STRING:
            raw, i = read_cstr(b, i)
            d[key] = ("str", raw.decode("utf-8", "replace"))
        elif t in (TYPE_INT32, TYPE_POINTER):
            d[key] = ("int", struct.unpack("<i", b[i:i + 4])[0])
            i += 4
        elif t == TYPE_UINT64:
            d[key] = ("u64", struct.unpack("<Q", b[i:i + 8])[0])
            i += 8
        elif t == TYPE_FLOAT:
            d[key] = ("flt", struct.unpack("<f", b[i:i + 4])[0])
            i += 4
        elif t == TYPE_COLOR:
            d[key] = ("col", b[i:i + 4])
            i += 4
        elif t == TYPE_WSTRING:
            j = i
            while j + 1 < n and not (b[j] == 0 and b[j + 1] == 0):
                j += 2
            d[key] = ("wstr", b[i:j].decode("utf-16-le", "replace"))
            i = j + 2
        else:
            raise ValueError("未知的类型字节 0x%02x（偏移 %d）" % (t, i - 1))
    return d, i


def put_cstr(out, s):
    out += (s.encode("utf-8") if isinstance(s, str) else s) + b"\x00"


def serialize(d, out=None):
    """把 (类型标记, 值) 的有序字典序列化成二进制 VDF。
    注意：本函数只为传入的 object 写"内容"，嵌套对象由调用处补结束标记；
    顶层调用方必须自己再补一个 0x08 表示根对象结束（见 serialize_file）。"""
    if out is None:
        out = bytearray()
    for k, (t, v) in d.items():
        if t == "obj":
            out.append(TYPE_OBJECT)
            put_cstr(out, k)
            serialize(v, out)
            out.append(TYPE_END)
        elif t == "str":
            out.append(TYPE_STRING)
            put_cstr(out, k)
            put_cstr(out, v)
        elif t == "int":
            out.append(TYPE_INT32)
            put_cstr(out, k)
            out += struct.pack("<i", v)
        elif t == "u64":
            out.append(TYPE_UINT64)
            put_cstr(out, k)
            out += struct.pack("<Q", v)
        elif t == "flt":
            out.append(TYPE_FLOAT)
            put_cstr(out, k)
            out += struct.pack("<f", v)
        elif t == "col":
            out.append(TYPE_COLOR)
            put_cstr(out, k)
            out += v
        elif t == "wstr":
            out.append(TYPE_WSTRING)
            put_cstr(out, k)
            out += v.encode("utf-16-le") + b"\x00\x00"
    return out


def serialize_file(root):
    """序列化整个文件。根对象结束后还需要一个 0x08 —— 漏掉会让文件短 1 字节，
    Steam 可能直接判定为空库（等于清掉用户所有非 Steam 快捷方式）。"""
    return bytes(serialize(root)) + bytes([TYPE_END])


def get_entries(root):
    """返回 Shortcuts 下的条目列表 [(key, entry_dict), ...]（保持原顺序）。"""
    sc = root.get("Shortcuts")
    if not sc or sc[0] != "obj":
        return []
    return [(k, v[1]) for k, v in sc[1].items() if v[0] == "obj"]


def set_entries(root, entries):
    """重新编号为 0..N-1 后写回。Steam 依赖连续的数字键。"""
    sc = {str(i): ("obj", e) for i, (_oldkey, e) in enumerate(entries)}
    root["Shortcuts"] = ("obj", sc)


def field(entry, name):
    v = entry.get(name)
    return v[1] if v else ""


def is_clash(entry):
    app = str(field(entry, "AppName"))
    exe = str(field(entry, "Exe"))
    return "clash" in app.lower() or "clash" in exe.lower()


def find_files():
    pats = [
        os.path.expanduser("~/.local/share/Steam/userdata/*/config/shortcuts.vdf"),
        os.path.expanduser("~/.steam/steam/userdata/*/config/shortcuts.vdf"),
        os.path.expanduser("~/.steam/root/userdata/*/config/shortcuts.vdf"),
    ]
    found = []
    seen = set()
    for p in pats:
        for f in glob.glob(p):
            # ~/.local/share/Steam、~/.steam/steam、~/.steam/root 通常互为符号链接，
            # 不去重的话同一个文件会被处理多次（备份互相覆盖、重复写入）
            real = os.path.realpath(f)
            if real in seen:
                continue
            seen.add(real)
            found.append(real)
    return found


def show(entries, title):
    print("=== %s（共 %d 条）===" % (title, len(entries)))
    if not entries:
        print("  (空)")
        return
    for i, (_k, e) in enumerate(entries):
        print("  [%d] %-34s  appid=%-12s" % (
            i, str(field(e, "AppName"))[:34], field(e, "appid")))
        print("        Exe: %s" % field(e, "Exe"))


def main():
    args = sys.argv[1:]
    do_list = "--list" in args
    do_rm = "--remove-clash" in args
    do_dedupe = "--dedupe" in args
    dry = "--dry-run" in args

    path = None
    if "--file" in args:
        path = args[args.index("--file") + 1]

    files = [path] if path else find_files()
    if not files:
        print("找不到 shortcuts.vdf")
        return 1

    if not (do_list or do_rm or do_dedupe):
        print("请指定动作：--list | --remove-clash | --dedupe")
        return 2

    total_changed = 0
    for f in files:
        print("配置文件: %s" % f)
        with open(f, "rb") as fp:
            raw = fp.read()
        try:
            root, _ = parse(raw)
        except Exception as e:
            print("  解析失败：%s" % e)
            print("  -> 不做任何修改，避免写坏用户的游戏库")
            return 1

        entries = get_entries(root)
        show(entries, "当前快捷方式")

        new_entries = list(entries)
        removed = 0

        if do_rm:
            kept = [(k, e) for (k, e) in new_entries if not is_clash(e)]
            removed = len(new_entries) - len(kept)
            new_entries = kept
            print("  将删除 Clash 条目：%d 条" % removed)

        if do_dedupe:
            seen = set()
            kept = []
            for (k, e) in new_entries:
                sig = (str(field(e, "AppName")), str(field(e, "Exe")))
                if sig in seen:
                    removed += 1
                    continue
                seen.add(sig)
                kept.append((k, e))
            new_entries = kept
            print("  去重后剩余：%d 条" % len(new_entries))

        if do_list and not (do_rm or do_dedupe):
            print()
            continue

        if len(new_entries) == len(entries):
            print("  无需改动")
            print()
            continue

        print("--- 改动后的列表 ---")
        show(new_entries, "改动后")

        if dry:
            print("  (--dry-run，未写入)")
            total_changed += 1
            print()
            continue

        bak = f + ".bak"
        shutil.copy2(f, bak)
        set_entries(root, new_entries)
        data = serialize_file(root)
        with open(f, "wb") as fp:
            fp.write(data)
        total_changed += 1
        print("  已写入（备份：%s，%d -> %d 字节）" % (bak, len(raw), len(data)))
        print()

    print("处理完成，改动了 %d 个文件" % total_changed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
