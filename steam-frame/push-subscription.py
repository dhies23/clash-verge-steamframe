#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Clash Verge Rev 订阅导入助手（在 Steam Frame 上运行）

用法：
    python3 push-subscription.py --name "我的机场" --url "https://..." [--activate] < 配置文件内容
    python3 push-subscription.py --list
    python3 push-subscription.py --remove <uid>

设计说明：
  * 直接写 Clash Verge 自己的持久化文件，不依赖 GUI、不依赖 clash:// 深链
    （实测深链在 SSH / gamescope 会话下不会触发 on_open_url）
  * profiles.yaml 的 item 结构来自上游 src-tauri/src/config/prfitem.rs 的 PrfItem
  * 用纯文本方式增量修改，保留原有注释与顺序；改动前自动备份
"""

import argparse
import os
import re
import secrets
import shutil
import string
import sys
import time

APPDATA = os.path.expanduser(
    "~/.local/share/io.github.clash-verge-rev.clash-verge-rev"
)
PROFILES_YAML = os.path.join(APPDATA, "profiles.yaml")
PROFILES_DIR = os.path.join(APPDATA, "profiles")
BACKUP_DIR = os.path.join(APPDATA, "profiles.yaml.bak")

ALPHABET = string.ascii_letters + string.digits

# 一个 item 块 = 从 "- uid:" 开始，到下一个 "- uid:" 或文件结尾
# 注意：这里只能用 re.M，不能用 re.S —— 否则 [^\n] 之外的通配会跨行吞掉整个文件
ITEM_BLOCK_RE = re.compile(r"(?m)^- uid:[^\n]*\n(?:^(?!- uid:)[^\n]*\n?)*")


def new_uid(prefix="R"):
    return prefix + "".join(secrets.choice(ALPHABET) for _ in range(11))


def yaml_scalar(value):
    """安全地把任意字符串写成单引号 YAML 标量（内部单引号加倍）。"""
    return "'" + str(value).replace("'", "''") + "'"


def read_text(path, default=""):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read()
    except FileNotFoundError:
        return default


def parse_items(text):
    """返回 [(uid, type, url, block_text), ...]"""
    items = []
    for m in ITEM_BLOCK_RE.finditer(text):
        block = m.group(0)
        uid = re.search(r"^- uid:\s*(.+)$", block, re.M)
        typ = re.search(r"^\s+type:\s*(.+)$", block, re.M)
        url = re.search(r"^\s+url:\s*(.+)$", block, re.M)

        def clean(x):
            if not x:
                return None
            v = x.group(1).strip()
            if len(v) >= 2 and v[0] == v[-1] and v[0] in "'\"":
                v = v[1:-1]
                v = v.replace("''", "'")
            if v in ("null", "~", ""):
                return None
            return v

        items.append((clean(uid), clean(typ), clean(url), block))
    return items


def template_uid(items, itype, fallback):
    for uid, typ, _url, _blk in items:
        if typ == itype and uid:
            return uid
    return fallback


def build_item_block(uid, name, url, updated, merge_uid, script_uid):
    lines = [
        f"- uid: {uid}",
        "  type: remote",
        f"  name: {yaml_scalar(name)}",
        f"  file: {uid}.yaml",
    ]
    if url:
        lines.append(f"  url: {yaml_scalar(url)}")
    lines += [
        "  selected: []",
        "  extra:",
        "    upload: 0",
        "    download: 0",
        "    total: 0",
        "    expire: 0",
        f"  updated: {updated}",
        "  option:",
        "    update_interval: 1440",
        f"    merge: {merge_uid}",
        f"    script: {script_uid}",
        "    allow_auto_update: true",
    ]
    return "\n".join(lines) + "\n"


def set_current(text, uid):
    if re.search(r"^current:.*$", text, re.M):
        return re.sub(r"^current:.*$", f"current: {uid}", text, count=1, flags=re.M)
    return f"current: {uid}\n" + text


def ensure_items_key(text):
    if re.search(r"^items:\s*$", text, re.M):
        return text
    if re.search(r"^items:\s*null\s*$", text, re.M):
        return re.sub(r"^items:\s*null\s*$", "items:", text, count=1, flags=re.M)
    return text.rstrip("\n") + "\nitems:\n"


def backup(path):
    if not os.path.exists(path):
        return
    os.makedirs(BACKUP_DIR, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    shutil.copy2(path, os.path.join(BACKUP_DIR, f"profiles.yaml.{stamp}"))


def cmd_list():
    text = read_text(PROFILES_YAML)
    items = parse_items(text)
    cur = re.search(r"^current:\s*(.+)$", text, re.M)
    cur = cur.group(1).strip() if cur else "null"
    print(f"配置文件: {PROFILES_YAML}")
    print(f"current : {cur}")
    print("items   :")
    for uid, typ, url, _blk in items:
        mark = " *" if uid == cur else "  "
        print(f" {mark} [{typ}] uid={uid} url={url or '-'}")
    return 0


def cmd_remove(uid):
    text = read_text(PROFILES_YAML)
    items = parse_items(text)
    target = next((it for it in items if it[0] == uid), None)
    if not target:
        print(f"找不到 uid={uid}", file=sys.stderr)
        return 2
    backup(PROFILES_YAML)
    text = text.replace(target[3], "")
    text = re.sub(r"\n{3,}", "\n\n", text)
    if re.search(rf"^current:\s*{re.escape(uid)}\s*$", text, re.M):
        text = set_current(text, "null")
    with open(PROFILES_YAML, "w", encoding="utf-8") as fh:
        fh.write(text)
    prof_file = os.path.join(PROFILES_DIR, f"{uid}.yaml")
    if os.path.exists(prof_file):
        os.remove(prof_file)
    print(f"已移除 uid={uid}")
    return 0


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--name", help="订阅名称（显示用）")
    ap.add_argument("--url", help="订阅链接（记录进配置，便于日后自动更新）")
    ap.add_argument("--uid", help="指定 uid（默认自动生成 R+11位）")
    ap.add_argument("--activate", action="store_true", help="设为当前使用中的配置")
    ap.add_argument("--fetch", action="store_true", help="由本机（设备）自行下载 --url，而不是从标准输入读取")
    ap.add_argument("--user-agent", default="clash-verge/v2.5.7", help="下载订阅时使用的 User-Agent")
    ap.add_argument("--list", action="store_true", help="列出已有订阅")
    ap.add_argument("--remove", metavar="UID", help="删除指定订阅")
    args = ap.parse_args()

    if args.list:
        return cmd_list()
    if args.remove:
        return cmd_remove(args.remove)

    if not os.path.isdir(APPDATA):
        print(f"找不到 Clash Verge 数据目录: {APPDATA}", file=sys.stderr)
        print("请确认 Clash Verge 已至少启动过一次。", file=sys.stderr)
        return 3

    if args.fetch:
        if not args.url:
            print("--fetch 需要同时给出 --url", file=sys.stderr)
            return 4
        try:
            import urllib.request

            req = urllib.request.Request(args.url, headers={"User-Agent": args.user_agent})
            with urllib.request.urlopen(req, timeout=30) as resp:
                content = resp.read().decode("utf-8", "replace")
            print(f"设备侧下载成功: {len(content)} 字节")
        except Exception as exc:  # noqa: BLE001
            print(f"设备侧下载失败: {exc}", file=sys.stderr)
            return 6
    else:
        content = sys.stdin.read()

    if not content.strip():
        print("没有读到配置内容", file=sys.stderr)
        return 4

    # 上游 from_url 的校验：必须含 proxies 或 proxy-providers
    if not re.search(r"^\s*(proxies|proxy-providers)\s*:", content, re.M):
        print("配置内容里找不到 proxies / proxy-providers，Clash Verge 会拒绝导入", file=sys.stderr)
        return 5

    os.makedirs(PROFILES_DIR, exist_ok=True)
    backup(PROFILES_YAML)

    text = read_text(PROFILES_YAML)
    text = ensure_items_key(text)
    items = parse_items(text)

    merge_uid = template_uid(items, "merge", "Merge")
    script_uid = template_uid(items, "script", "Script")

    # 同一个 URL 已经导入过 → 复用 uid，覆盖内容，避免重复条目
    uid = args.uid
    if not uid and args.url:
        for i_uid, i_type, i_url, _blk in items:
            if i_type == "remote" and i_url == args.url:
                uid = i_uid
                break
    if not uid:
        uid = new_uid("R")

    name = args.name or "导入的订阅"
    profile_path = os.path.join(PROFILES_DIR, f"{uid}.yaml")
    with open(profile_path, "w", encoding="utf-8") as fh:
        fh.write(content)

    updated = int(time.time())
    new_block = build_item_block(uid, name, args.url, updated, merge_uid, script_uid)

    existing = next((it for it in items if it[0] == uid), None)
    if existing:
        text = text.replace(existing[3], new_block)
    else:
        if not text.endswith("\n"):
            text += "\n"
        text += new_block

    if args.activate:
        text = set_current(text, uid)

    with open(PROFILES_YAML, "w", encoding="utf-8") as fh:
        fh.write(text)

    print(f"uid      : {uid}")
    print(f"name     : {name}")
    print(f"url      : {args.url or '-'}")
    print(f"profile  : {profile_path}")
    print(f"activate : {bool(args.activate)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
