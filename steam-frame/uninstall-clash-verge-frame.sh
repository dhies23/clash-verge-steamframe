#!/usr/bin/env bash
#
# Clash Verge Rev × Steam Frame 一键卸载
#
# 清理范围：
#   1. 运行中的 Clash Verge 与 mihomo 内核
#   2. Steam「非 Steam 游戏库」里的快捷方式（shortcuts.vdf，二进制 VDF）
#   3. 桌面菜单项（.desktop）与图标
#   4. 官方系统服务 clash-verge-service（需要 sudo）
#   5. 程序本体与启动器
#   6. 配置、订阅与日志（默认保留；要删需显式加 --purge-config）
#   7. 系统依赖 webkit2gtk-4.1（--remove-deps 才动，默认保留）
#   8. 安装/辅助脚本自己留在 ~ 下的临时文件
#
# 用法：
#   ./uninstall-clash-verge-frame.sh [选项]
#     --purge-config        删除配置、订阅与日志（默认保留）
#     --remove-deps         同时卸载系统依赖 webkit2gtk-4.1
#     --no-steam-shutdown   不主动退出 Steam（此时快捷方式可能删不掉）
#     --dry-run             只显示将要做什么，不实际改动
#     --yes                 不交互确认
#
# 安全取向：破坏性动作一律"默认关闭、必须显式开启"。
# 这样即使参数在传递中丢了，也不会误删用户的订阅。
#
set -u

KEEP_CONFIG=1
REMOVE_DEPS=0
NO_STEAM_SHUTDOWN=0
DRY_RUN=0
ASSUME_YES=0

while [ $# -gt 0 ]; do
  case "$1" in
    --purge-config)       KEEP_CONFIG=0 ;;
    --keep-config)        KEEP_CONFIG=1 ;;   # 兼容旧写法，本就是默认
    --remove-deps)        REMOVE_DEPS=1 ;;
    --no-steam-shutdown)  NO_STEAM_SHUTDOWN=1 ;;
    --dry-run)            DRY_RUN=1 ;;
    --yes)                ASSUME_YES=1 ;;
    -h|--help)            sed -n '2,28p' "$0"; exit 0 ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
  shift
done

PREFIX="${CLASH_VERGE_PREFIX:-$HOME/.local/opt/clash-verge}"
BIN_DIR="$HOME/.local/bin"
LAUNCHER="$BIN_DIR/clash-verge"
APPS_DIR="$HOME/.local/share/applications"
ICONS_DIR="$HOME/.local/share/icons"
APPDATA_DIR="$HOME/.local/share/io.github.clash-verge-rev.clash-verge-rev"
CONFIG_DIR="$HOME/.config/io.github.clash-verge-rev.clash-verge-rev"
SERVICE_UNIT="/etc/systemd/system/clash-verge-service.service"
SERVICE_LIB="/var/lib/clash-verge-service"
SERVICE_RUN="/run/clash-verge-service"

step() { printf '\n\033[1;36m[%s/8]\033[0m %s\n' "$1" "$2"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
info() { printf '  \033[36m·\033[0m %s\n' "$1"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$1"; }
skip() { printf '  \033[90m-\033[0m %s\n' "$1"; }

# 统一处理"要删的东西"，dry-run 时只打印
remove_path() {
  local p="$1" label="${2:-$1}"
  if [ ! -e "$p" ] && [ ! -L "$p" ]; then
    skip "$label（不存在）"
    return 1
  fi
  if [ "$DRY_RUN" -eq 1 ]; then
    info "将删除 $label"
    return 0
  fi
  if rm -rf "$p" 2>/dev/null; then
    ok "已删除 $label"
    return 0
  fi
  warn "删除失败（权限？）：$label"
  return 1
}

# ---- sudo 密码：非交互提权 ----------------------------------------------------
# 管理器会先把 SSH 密码试一遍；不行才弹窗问。这里优先从环境变量或
# ~/.cvframe-sudo-pw 取，两个都没有时退回交互式 sudo（本地手动运行用）。
# 密码缺失/不对时输出固定标记并退出，让管理器去问，而不是卡在密码提示上。
SUDO_PW_MARKER_NEEDED="CVFRAME_SUDO_PW_NEEDED"
SUDO_PW_MARKER_REJECTED="CVFRAME_SUDO_PW_REJECTED"
SUDO_PW="${CVFRAME_SUDO_PW:-}"
SUDO_PW_FILE="$HOME/.cvframe-sudo-pw"
if [ -z "$SUDO_PW" ] && [ -f "$SUDO_PW_FILE" ]; then
  SUDO_PW="$(cat "$SUDO_PW_FILE" 2>/dev/null || true)"
fi
rm -f "$SUDO_PW_FILE" 2>/dev/null || true   # 用完立刻删，别留在磁盘上

run_sudo() {
  if [ "$DRY_RUN" -eq 1 ]; then
    info "将执行: sudo $*"
    return 0
  fi
  if [ -n "$SUDO_PW" ]; then
    printf '%s\n' "$SUDO_PW" | sudo -S -p '' "$@"
  else
    sudo "$@"
  fi
}

SUDO=""
if command -v sudo >/dev/null 2>&1; then SUDO="sudo"; fi

# 只有在真要动系统服务/依赖时才需要提权，所以放到"确实需要"之前验证。
sudo_preflight() {
  [ -n "$SUDO" ] || return 0
  [ "$DRY_RUN" -eq 1 ] && return 0
  if [ -n "$SUDO_PW" ]; then
    if ! printf '%s\n' "$SUDO_PW" | sudo -S -p '' true >/dev/null 2>&1; then
      printf '%s\n' "$SUDO_PW_MARKER_REJECTED"
      exit 4
    fi
    ok "sudo 密码已验证（用的是 SSH 密码）"
  elif ! sudo -n true >/dev/null 2>&1; then
    printf '%s\n' "$SUDO_PW_MARKER_NEEDED"
    exit 4
  fi
}

echo "============================================================"
echo "  Clash Verge Rev × Steam Frame 卸载"
echo "============================================================"
echo "  程序目录   : $PREFIX"
echo "  配置与订阅 : $([ "$KEEP_CONFIG" -eq 1 ] && echo '保留' || echo '删除')"
echo "  系统依赖   : $([ "$REMOVE_DEPS" -eq 1 ] && echo '一并卸载 webkit2gtk-4.1' || echo '保留')"
echo "  执行方式   : $([ "$DRY_RUN" -eq 1 ] && echo '仅预览（dry-run）' || echo '实际执行')"
echo "============================================================"

if [ "$DRY_RUN" -eq 0 ] && [ "$ASSUME_YES" -eq 0 ]; then
  printf '\n确认执行卸载？输入 yes 继续：'
  read -r ans
  [ "$ans" = "yes" ] || { echo "已取消。"; exit 0; }
fi

# ---------------------------------------------------------------- 提权预检
# 放在所有破坏性动作之前：密码不对就立刻退出，不会留下"进程已被杀、
# Steam 条目已删，但服务还装着"的半成品状态。
# 只有确实需要 root 时才要求密码（没装服务、也不删依赖的话根本用不到 sudo）。
NEED_ROOT=0
[ -f "$SERVICE_UNIT" ] && NEED_ROOT=1
[ -d "$SERVICE_LIB" ] && NEED_ROOT=1
[ -x "$PREFIX/usr/bin/clash-verge-service-uninstall" ] && NEED_ROOT=1
[ "$REMOVE_DEPS" -eq 1 ] && NEED_ROOT=1
if [ "$NEED_ROOT" -eq 1 ]; then
  sudo_preflight
else
  info "本次不需要 root 权限（没有要卸载的系统服务或依赖）"
fi

# ---------------------------------------------------------------- 1. 停进程
step 1 "停止 Clash Verge 与内核"
# 注意用 -x 精确匹配：用 -f 会匹配到本脚本自己的命令行
for name in clash-verge verge-mihomo verge-mihomo-alpha; do
  if pgrep -x "$name" >/dev/null 2>&1; then
    if [ "$DRY_RUN" -eq 1 ]; then
      info "将结束进程 $name"
    else
      pkill -x "$name" 2>/dev/null || true
    fi
  fi
done
[ "$DRY_RUN" -eq 0 ] && sleep 2
if pgrep -x clash-verge >/dev/null 2>&1; then
  warn "clash-verge 仍在运行，稍后可能覆盖部分文件"
else
  ok "进程已停止"
fi

# ---------------------------------------------------------------- 2. Steam 库
step 2 "从 Steam「非 Steam 游戏库」移除"
SHORTCUT_HELPER=""
for cand in "$HOME/steam-shortcuts.py" "$(dirname "$0")/steam-shortcuts.py"; do
  [ -f "$cand" ] && SHORTCUT_HELPER="$cand" && break
done

if [ -z "$SHORTCUT_HELPER" ]; then
  warn "找不到 steam-shortcuts.py，跳过（Steam 库里的条目需要手动删）"
elif ! command -v python3 >/dev/null 2>&1; then
  warn "没有 python3，跳过 Steam 快捷方式清理"
else
  # Steam 只在退出时写回 shortcuts.vdf，改的时候它必须不在跑，
  # 否则它会把我们的改动覆盖掉。
  if pgrep -x steam >/dev/null 2>&1; then
    if [ "$NO_STEAM_SHUTDOWN" -eq 1 ]; then
      warn "Steam 正在运行且指定了 --no-steam-shutdown，本次改动很可能被 Steam 覆盖"
    elif [ "$DRY_RUN" -eq 1 ]; then
      info "Steam 正在运行，实际执行时会先退出 Steam"
    else
      info "Steam 正在运行，先退出以便写回快捷方式…"
      steam -shutdown >/dev/null 2>&1 || true
      for _ in $(seq 1 30); do
        pgrep -x steam >/dev/null 2>&1 || break
        sleep 1
      done
      if pgrep -x steam >/dev/null 2>&1; then
        warn "Steam 仍在运行，改动可能被覆盖；建议手动完全退出 Steam 后重跑本脚本"
      else
        ok "Steam 已退出"
      fi
    fi
  else
    ok "Steam 未运行，可以安全写入"
  fi

  if [ "$DRY_RUN" -eq 1 ]; then
    python3 "$SHORTCUT_HELPER" --list
    info "将删除上面列的 Clash Verge 条目"
  else
    python3 "$SHORTCUT_HELPER" --remove-clash || warn "清理 Steam 快捷方式失败"
  fi
fi

# ---------------------------------------------------------------- 3. 系统服务
step 3 "卸载官方系统服务 clash-verge-service"
UNINSTALLER="$PREFIX/usr/bin/clash-verge-service-uninstall"
if [ -x "$UNINSTALLER" ]; then
  if [ "$DRY_RUN" -eq 1 ]; then
    info "将执行: sudo $UNINSTALLER"
  else
    info "调用官方卸载器…"
    if run_sudo "$UNINSTALLER" 2>&1 | sed 's/^/    /'; then
      ok "官方卸载器执行完毕"
    else
      warn "官方卸载器返回非零，继续做兜底清理"
    fi
  fi
else
  skip "找不到官方卸载器（服务可能本来就没装）"
fi

# 兜底：确保 unit 与数据目录都不留
if [ -f "$SERVICE_UNIT" ] || [ -d "$SERVICE_LIB" ] || [ -d "$SERVICE_RUN" ]; then
  if [ -n "$SUDO" ]; then
    run_sudo systemctl disable --now clash-verge-service >/dev/null 2>&1 || true
    run_sudo rm -f "$SERVICE_UNIT"
    run_sudo rm -rf "$SERVICE_LIB" "$SERVICE_RUN"
    run_sudo systemctl daemon-reload >/dev/null 2>&1 || true
    run_sudo systemctl reset-failed clash-verge-service >/dev/null 2>&1 || true
    [ "$DRY_RUN" -eq 0 ] && ok "已移除 service 单元与数据目录"
  else
    warn "没有 sudo，无法移除系统服务（残留：$SERVICE_UNIT）"
  fi
else
  ok "没有残留的服务单元"
fi

# ---------------------------------------------------------------- 4. 桌面项
step 4 "移除桌面菜单项与图标"
DESKTOP_REMOVED=0
# 用通配符一次覆盖，别再单独列一遍名字 —— 否则 dry-run 会把同一个文件报两次
for f in "$APPS_DIR"/*clash*.desktop; do
  [ -e "$f" ] || continue
  remove_path "$f" "$(basename "$f")" && DESKTOP_REMOVED=1
done
[ "$DESKTOP_REMOVED" -eq 0 ] && skip "没有找到桌菜单项"

ICON_REMOVED=0
for f in "$ICONS_DIR"/hicolor/*/apps/clash-verge.png; do
  [ -e "$f" ] || continue
  remove_path "$f" "${f#$HOME/.local/share/icons/}" && ICON_REMOVED=1
done

if [ "$DESKTOP_REMOVED" -eq 1 ] && [ "$DRY_RUN" -eq 0 ]; then
  command -v update-desktop-database >/dev/null 2>&1 && \
    update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
  ok "已刷新桌面数据库"
fi
[ "$ICON_REMOVED" -eq 0 ] && skip "没有找到图标文件"

# ---------------------------------------------------------------- 5. 程序本体
step 5 "删除程序本体与启动器"
remove_path "$PREFIX" "程序目录 $PREFIX"
remove_path "$LAUNCHER" "启动器 $LAUNCHER"

# ---------------------------------------------------------------- 6. 配置数据
step 6 "处理配置、订阅与日志"
if [ "$KEEP_CONFIG" -eq 1 ]; then
  info "按要求保留：$APPDATA_DIR"
  info "           $CONFIG_DIR"
else
  remove_path "$APPDATA_DIR" "数据目录（含订阅）"
  remove_path "$CONFIG_DIR" "配置目录"
fi

# ---------------------------------------------------------------- 7. 系统依赖
step 7 "系统依赖"
if [ "$REMOVE_DEPS" -eq 1 ]; then
  if command -v pacman >/dev/null 2>&1; then
    if pacman -Q webkit2gtk-4.1 >/dev/null 2>&1; then
      info "卸载 webkit2gtk-4.1（pacman -Rns 会一并清掉不再被需要的依赖）"
      if [ "$DRY_RUN" -eq 1 ]; then
        run_sudo pacman -Rns --noconfirm --print webkit2gtk-4.1 2>&1 | sed 's/^/    /'
      else
        run_sudo pacman -Rns --noconfirm webkit2gtk-4.1 2>&1 | sed 's/^/    /' || \
          warn "pacman 卸载失败，可手动执行：sudo pacman -Rns webkit2gtk-4.1"
      fi
    else
      skip "系统里没有 webkit2gtk-4.1"
    fi
  else
    warn "没有 pacman，跳过"
  fi
else
  info "按要求保留 webkit2gtk-4.1"
fi

# ---------------------------------------------------------------- 8. 临时文件
step 8 "清理安装脚本留下的临时文件"
for f in "$HOME/steam-shortcuts.py" \
         "$HOME/push-subscription.py" \
         "$HOME/cvframe-helper.py" \
         "$HOME/install-clash-verge-frame.run" \
         "$HOME/install-clash-verge-frame.sh" \
         "$HOME/roundtrip-test.py" \
         "$HOME/svc-recon.sh" \
         "$HOME/uninstall-recon.sh" \
         "$HOME/setup-session-proxy.sh" \
         "$HOME/check-dropin.sh" \
         "$HOME/remove-dropin.sh"; do
  [ -e "$f" ] && remove_path "$f" "$(basename "$f")"
done

# ---------------------------------------------------------------- 汇总
echo
echo "============================================================"
if [ "$DRY_RUN" -eq 1 ]; then
  echo "  预览结束（未做任何改动）"
else
  echo "  卸载完成"
fi
echo "============================================================"
echo
echo "剩余检查："
for p in "$PREFIX" "$LAUNCHER" "$APPS_DIR/clash-verge.desktop" "$SERVICE_UNIT"; do
  if [ -e "$p" ]; then echo "  [残留] $p"; else echo "  [已清] $p"; fi
done
if [ "$KEEP_CONFIG" -eq 1 ]; then
  [ -e "$APPDATA_DIR" ] && echo "  [保留] $APPDATA_DIR"
else
  [ -e "$APPDATA_DIR" ] && echo "  [残留] $APPDATA_DIR" || echo "  [已清] $APPDATA_DIR"
fi
echo
if [ "$DRY_RUN" -eq 0 ]; then
  echo "提示：Steam 库里的条目要重启 Steam 后才完全看不到。"
  echo "      如果想重装，直接跑管理器里的「一键部署安装」即可。"
fi
echo
echo "DONE-UNINSTALL"
