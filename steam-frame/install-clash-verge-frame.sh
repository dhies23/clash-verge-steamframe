#!/usr/bin/env bash
#
# Clash Verge Rev — Steam Frame (SteamOS / AArch64) 一键安装器
#
# 单文件版本：本文件尾部可以内嵌安装载荷（程序 deb + webkit 包 + 托盘库），
# 内嵌后即为完全离线的自解压安装包，拷进机器 chmod +x 一跑即可。
# 不带载荷时则退化为联网安装（从 GitHub 官方 Release 取包）。
#
# 用法：
#   ./install-clash-verge-frame.run                    # 有内嵌载荷就离线装，否则联网装
#   ./install-clash-verge-frame.sh                     # 同上
#   ./install-clash-verge-frame.sh -f Clash.Verge_2.5.7_arm64.deb
#   ./install-clash-verge-frame.sh --with-tun          # 额外授予 TUN 权限
#   ./install-clash-verge-frame.sh --no-tray           # 不补 libayatana-appindicator
#   ./install-clash-verge-frame.sh --no-steam          # 不改动 Steam 库
#   ./install-clash-verge-frame.sh --offline-only      # 禁止联网，缺什么就报错
#   ./install-clash-verge-frame.sh --uninstall         # 卸载
#
# 设计要点（全部基于 Steam Frame 真机实测）：
#   * SteamOS 根文件系统只读、/usr 随原子更新被整体替换 → 程序必须装进 ~/.local
#   * WebKitGTK 的辅助进程路径 /usr/lib/webkit2gtk-4.1 是编译期硬编码，
#     不支持 WEBKIT_EXEC_PATH，也无法随 AppImage 重定位 → 必须由系统提供
#   * libayatana-appindicator3 是 dlopen 加载的，不是硬依赖 → 放对库路径即可补托盘
#   * Steam Frame 没有"右键安装"，官方入口是 steamos-add-to-steam

set -euo pipefail

# ---------------------------------------------------------------- 常量

PREFIX="${CLASH_VERGE_PREFIX:-$HOME/.local/opt/clash-verge}"
BIN_DIR="$HOME/.local/bin"
APPS_DIR="$HOME/.local/share/applications"
ICONS_DIR="$HOME/.local/share/icons/hicolor"
APPDATA_DIR="$HOME/.local/share/io.github.clash-verge-rev.clash-verge-rev"
DESKTOP_FILE="$APPS_DIR/clash-verge.desktop"
LAUNCHER="$BIN_DIR/clash-verge"
RELEASES_API="https://api.github.com/repos/clash-verge-rev/clash-verge-rev/releases/latest"

# 托盘库：SteamOS 仓库没有、AUR 也没有，只有 Arch Linux ARM 官方仓库有 aarch64 现成包。
# 部分镜像 TLS 证书域名不匹配，按顺序尝试。
ALARM_MIRRORS=(
  "https://ca.us.mirror.archlinuxarm.org/aarch64/extra"
  "http://mirror.archlinuxarm.org/aarch64/extra"
)
ALARM_PKGPAGE="https://archlinuxarm.org/packages/aarch64"

PAYLOAD=""
WITH_TUN=0
DO_TRAY=1
DO_STEAM=1
DO_WEBKIT=1
OFFLINE_ONLY=0
ACTION="install"

# ---------------------------------------------------------------- 输出

c_reset=$'\033[0m'; c_red=$'\033[31m'; c_grn=$'\033[32m'
c_yel=$'\033[33m'; c_cyn=$'\033[36m'; c_bld=$'\033[1m'

info()  { printf '%s==>%s %s\n' "$c_cyn" "$c_reset" "$*"; }
ok()    { printf '%s  ok%s %s\n' "$c_grn" "$c_reset" "$*"; }
warn()  { printf '%s警告%s %s\n' "$c_yel" "$c_reset" "$*" >&2; }
die()   { printf '%s错误%s %s\n' "$c_red" "$c_reset" "$*" >&2; exit 1; }
step()  { printf '\n%s[%s]%s %s\n' "$c_bld" "$1" "$c_reset" "$2"; }

usage() { sed -n '3,26p' "$0" | sed 's/^# \{0,1\}//'; exit 0; }

# ---------------------------------------------------------------- 临时目录与清理

TMPDIRS=()
ROOT_RO=0
SUDO=""

# ---- sudo 密码：非交互提权 ----------------------------------------------------
# 管理器会把 SSH 密码先试一遍，所以这里优先从环境变量或 ~/.cvframe-sudo-pw 取。
# 两个来源都没有时退回交互式 sudo（本地手动运行时用）。
# 密码不对/缺失时输出固定标记并退出，管理器据此弹窗询问，而不是卡在密码提示上。
SUDO_PW_MARKER_NEEDED="CVFRAME_SUDO_PW_NEEDED"
SUDO_PW_MARKER_REJECTED="CVFRAME_SUDO_PW_REJECTED"
SUDO_PW="${CVFRAME_SUDO_PW:-}"
SUDO_PW_FILE="$HOME/.cvframe-sudo-pw"
if [ -z "$SUDO_PW" ] && [ -f "$SUDO_PW_FILE" ]; then
  SUDO_PW="$(cat "$SUDO_PW_FILE" 2>/dev/null || true)"
fi
# 用完立刻删掉，别把密码留在磁盘上
rm -f "$SUDO_PW_FILE" 2>/dev/null || true

# 统一入口：有密码就走 sudo -S（每次重新喂，不依赖时间戳缓存），否则交互式
run_sudo() {
  if [ -n "$SUDO_PW" ]; then
    printf '%s\n' "$SUDO_PW" | sudo -S -p '' "$@"
  else
    sudo "$@"
  fi
}

# 提前验证，避免装到一半才失败
sudo_preflight() {
  command -v sudo >/dev/null 2>&1 || { SUDO=""; return 0; }
  SUDO="sudo"
  if [ -n "$SUDO_PW" ]; then
    if ! printf '%s\n' "$SUDO_PW" | sudo -S -p '' true >/dev/null 2>&1; then
      printf '%s\n' "$SUDO_PW_MARKER_REJECTED"
      exit 4
    fi
    ok "sudo 密码已验证（用的是 SSH 密码）"
  elif ! sudo -n true >/dev/null 2>&1; then
    # 没有可用密码，又不能免密 —— 交给管理器问用户
    printf '%s\n' "$SUDO_PW_MARKER_NEEDED"
    exit 4
  fi
}

cleanup() {
  local d
  for d in ${TMPDIRS[@]+"${TMPDIRS[@]}"}; do
    [ -n "$d" ] && rm -rf "$d" 2>/dev/null || true
  done
  if [ "$ROOT_RO" = "1" ] && [ -n "$SUDO" ]; then
    run_sudo steamos-readonly enable >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

new_tmp() {
  local d
  d="$(mktemp -d)" || die "无法创建临时目录"
  TMPDIRS+=("$d")
  printf '%s' "$d"
}

# ---------------------------------------------------------------- 参数

while [ $# -gt 0 ]; do
  case "$1" in
    -f|--file)     PAYLOAD="${2:-}"; shift 2 ;;
    --with-tun)    WITH_TUN=1; shift ;;
    --no-tray)     DO_TRAY=0; shift ;;
    --no-steam)    DO_STEAM=0; shift ;;
    --no-webkit)   DO_WEBKIT=0; shift ;;
    --offline-only) OFFLINE_ONLY=1; shift ;;
    --uninstall)   ACTION="uninstall"; shift ;;
    -h|--help)     usage ;;
    *)             die "未知参数：$1（用 -h 看用法）" ;;
  esac
done

# ---------------------------------------------------------------- 内嵌载荷检测

OFFLINE_DIR=""
SELF="${BASH_SOURCE[0]:-$0}"
# 安装器所在目录（联网分发时，托盘库可放在同目录的 payload/lib 下）
SCRIPT_DIR_HINT=""
if [ -f "$SELF" ]; then
  SCRIPT_DIR_HINT="$(cd "$(dirname "$SELF")" 2>/dev/null && pwd)" || SCRIPT_DIR_HINT=""
fi

detect_embedded() {
  [ -f "$SELF" ] || return 0

  # 注意：set -o pipefail 下 head|grep -q 会因 SIGPIPE 误判，所以先落盘再匹配
  local marker_probe
  marker_probe="$(new_tmp)"
  head -c 65536 "$SELF" > "$marker_probe/head" 2>/dev/null || true
  grep -aq '^__PAYLOAD_BELOW__$' "$marker_probe/head" || return 0

  info "检测到内嵌安装载荷，切换到离线安装模式"
  OFFLINE_DIR="$(new_tmp)"

  local line
  line="$(awk '/^__PAYLOAD_BELOW__$/{print NR+1; exit 0}' "$SELF")"
  [ -n "$line" ] || { warn "定位不到载荷偏移，回退到联网模式"; OFFLINE_DIR=""; return 0; }

  if ! tail -n +"$line" "$SELF" | bsdtar -xf - -C "$OFFLINE_DIR" 2>/dev/null; then
    warn "内嵌载荷解包失败，回退到联网模式"
    OFFLINE_DIR=""
    return 0
  fi
  ok "载荷已解包到临时目录"
}

# ---------------------------------------------------------------- 卸载

if [ "$ACTION" = "uninstall" ]; then
  step 1 "卸载 Clash Verge Rev"
  rm -rf "$PREFIX" "$LAUNCHER" "$DESKTOP_FILE"
  rm -f  "$ICONS_DIR"/*/apps/clash-verge.png
  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
  fi
  ok "已删除程序与桌面项（用户数据保留在 $APPDATA_DIR）"
  printf '\n如需彻底清除配置与订阅：rm -rf "%s"\n' "$APPDATA_DIR"
  exit 0
fi

detect_embedded

# ---------------------------------------------------------------- 环境自检

step 1 "环境自检"

ARCH="$(uname -m)"
[ "$ARCH" = "aarch64" ] || warn "当前架构为 $ARCH，本安装器针对 Steam Frame 的 aarch64"

[ "$(id -u)" -ne 0 ] || die "请用普通用户（steamos）运行，不要用 root/sudo 运行本脚本"

if [ -r /etc/os-release ]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  if [ "${ID:-}" != "steamos" ]; then
    warn "当前系统不是 SteamOS（ID=${ID:-未知}），继续但可能需要手动处理依赖"
  else
    ok "系统：${PRETTY_NAME:-SteamOS}${VARIANT_ID:+（variant=$VARIANT_ID）}"
  fi
fi

# 只有确实要动系统时才要求提权：装 webkit 需要，设 TUN 能力需要；
# 两者都不需要（webkit 已装、也没加 --with-tun）时不该向用户要密码。
NEED_ROOT=0
pacman -Q webkit2gtk-4.1 >/dev/null 2>&1 || NEED_ROOT=1
[ "$WITH_TUN" -eq 1 ] && NEED_ROOT=1
if [ "$NEED_ROOT" -eq 1 ]; then
  sudo_preflight
else
  info "本次不需要 root 权限（webkit 已装好，且未启用 TUN）"
fi

# ---------------------------------------------------------------- 依赖：webkit2gtk-4.1

webkit_installed() { pacman -Q webkit2gtk-4.1 >/dev/null 2>&1; }

install_webkit_offline() {
  local pkgs=("$OFFLINE_DIR"/webkit/*.pkg.tar.*)
  [ -e "${pkgs[0]:-}" ] || return 1
  info "使用随包自带的 webkit2gtk-4.1（离线，不联网）"
  run_sudo pacman -U --needed --noconfirm "${pkgs[@]}"
}

install_webkit_online() {
  [ "$OFFLINE_ONLY" -eq 1 ] && die "指定了 --offline-only，但包内没有 webkit2gtk-4.1，且系统也没装"
  info "从 Valve 官方 SteamOS 仓库安装 webkit2gtk-4.1"
  run_sudo pacman -Sy --noconfirm
  run_sudo pacman -S --needed --noconfirm webkit2gtk-4.1
}

ensure_webkit() {
  step 2 "运行时依赖 webkit2gtk-4.1（WebKit 无法随应用打包，必须由系统提供）"

  if webkit_installed; then
    ok "已安装"
    return 0
  fi

  warn "未安装 —— 这是 Steam Frame 上唯一缺失的运行时依赖"
  [ "$DO_WEBKIT" -eq 1 ] || die "已指定 --no-webkit，但依赖缺失，无法继续"
  [ -n "$SUDO" ] || die "需要 root 权限安装 webkit2gtk-4.1，但系统里没有 sudo"

  ROOT_RO=1
  run_sudo steamos-readonly disable >/dev/null 2>&1 || true

  local rc=0
  install_webkit_offline || rc=$?
  if [ "$rc" -ne 0 ] || ! webkit_installed; then
    warn "离线安装未成功，改用在线仓库"
    install_webkit_online
  fi

  run_sudo steamos-readonly enable >/dev/null 2>&1 || true
  ROOT_RO=0

  webkit_installed || die "webkit2gtk-4.1 安装失败"
  ok "webkit2gtk-4.1 安装完成，根文件系统只读保护已恢复"
}

ensure_webkit

# ---------------------------------------------------------------- 取得安装包

step 3 "准备程序安装包"

WORK_TMP="$(new_tmp)"

if [ -z "$PAYLOAD" ] && [ -n "$OFFLINE_DIR" ]; then
  # 优先用内嵌载荷
  found=""
  for c in "$OFFLINE_DIR"/app/*.deb "$OFFLINE_DIR"/*.deb; do
    [ -f "$c" ] && { found="$c"; break; }
  done
  [ -n "$found" ] && { PAYLOAD="$found"; ok "使用内嵌安装包：$(basename "$PAYLOAD")"; }
fi

if [ -z "$PAYLOAD" ]; then
  [ "$OFFLINE_ONLY" -eq 1 ] && die "指定了 --offline-only，但包内没有程序安装包"
  command -v curl >/dev/null 2>&1 || die "缺少 curl"

  info "查询官方最新 arm64 包"
  REL_JSON="$(curl -fsSL --max-time 60 "$RELEASES_API")" \
    || die "无法访问 GitHub API，请用 -f 手动指定安装包"

  TAG="$(printf '%s' "$REL_JSON" | grep -oE '"tag_name"[[:space:]]*:[[:space:]]*"[^"]+"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/')"
  URL="$(printf '%s' "$REL_JSON" | grep -oE 'https://[^"]+_arm64\.deb' | head -1)"
  [ -n "$URL" ] || die "在 release ${TAG:-未知} 中找不到 arm64.deb"

  info "版本 $TAG，下载 $(basename "$URL")"
  PAYLOAD="$WORK_TMP/$(basename "$URL")"
  curl -fL --progress-bar --max-time 900 -o "$PAYLOAD" "$URL" || die "下载失败"
fi

[ -f "$PAYLOAD" ] || die "找不到安装包：$PAYLOAD"

KIND=""
case "$PAYLOAD" in
  *.deb)      KIND="deb" ;;
  *.AppImage) KIND="appimage" ;;
  *)
    magic="$(head -c 8 "$PAYLOAD" 2>/dev/null || true)"
    case "$magic" in
      $'\x7fELF'*)   KIND="appimage" ;;
      '!<arch>'*)    KIND="deb" ;;
      *) die "无法识别的包类型：$PAYLOAD（需要 .deb 或 .AppImage）" ;;
    esac
    ;;
esac
ok "包类型：$KIND（$(basename "$PAYLOAD")）"

# ---------------------------------------------------------------- 安装到 ~/.local

step 4 "安装到 $PREFIX"

rm -rf "$PREFIX"
mkdir -p "$PREFIX"

if [ "$KIND" = "deb" ]; then
  command -v bsdtar >/dev/null 2>&1 || die "缺少 bsdtar（包名 libarchive）"
  # 必须保留 deb 内部布局：usr/bin/clash-verge 与 usr/lib/Clash Verge/resources，
  # Tauri 靠这个相对关系定位 resources
  EXTRACT_TMP="$(new_tmp)"
  bsdtar -xf "$PAYLOAD" -C "$EXTRACT_TMP" control.tar.gz data.tar.gz debian-binary 2>/dev/null || true
  if [ -f "$EXTRACT_TMP/data.tar.gz" ]; then
    bsdtar -xf "$EXTRACT_TMP/data.tar.gz" -C "$PREFIX"
  else
    bsdtar -xf "$PAYLOAD" -C "$PREFIX" --exclude 'control.tar.*' --exclude 'debian-binary'
  fi
else
  install -m 0755 "$PAYLOAD" "$PREFIX/Clash.Verge.AppImage"
  info "解包 AppImage"
  ( cd "$PREFIX" && "$PREFIX/Clash.Verge.AppImage" --appimage-extract >/dev/null 2>&1 ) \
    || die "AppImage 解包失败"
fi

APP_BIN=""
for cand in "$PREFIX/usr/bin/clash-verge" "$PREFIX/squashfs-root/usr/bin/clash-verge"; do
  [ -x "$cand" ] && { APP_BIN="$cand"; break; }
done
[ -n "$APP_BIN" ] || die "安装包里找不到 clash-verge 主程序"

chmod +x "$PREFIX/usr/bin/"clash-verge* 2>/dev/null || true
[ -d "$PREFIX/squashfs-root/usr/bin" ] && chmod +x "$PREFIX/squashfs-root/usr/bin/"clash-verge* 2>/dev/null || true
ok "主程序：${APP_BIN#"$PREFIX/"}"

# ---------------------------------------------------------------- 补托盘库

step 5 "补齐系统托盘依赖 libayatana-appindicator3"

if [ "$DO_TRAY" -eq 0 ]; then
  ok "已跳过（--no-tray）"
else
  LIBDIRS=()
  [ -d "$PREFIX/usr/lib" ] && LIBDIRS+=("$PREFIX/usr/lib")
  [ -d "$PREFIX/squashfs-root/usr/lib" ] && LIBDIRS+=("$PREFIX/squashfs-root/usr/lib")
  mkdir -p "$PREFIX/lib"
  LIBDIRS+=("$PREFIX/lib")

  copy_tray_libs() {
    local src="$1" d
    for d in "${LIBDIRS[@]}"; do
      cp -a "$src/." "$d/" 2>/dev/null || true
    done
  }

  if ls "$PREFIX/lib/libayatana-appindicator3.so.1" >/dev/null 2>&1; then
    ok "托盘库已存在"
  elif [ -n "$OFFLINE_DIR" ] && ls "$OFFLINE_DIR"/lib/libayatana-appindicator3.so.1 >/dev/null 2>&1; then
    info "使用内嵌载荷里的托盘库（离线）"
    copy_tray_libs "$OFFLINE_DIR/lib"
  elif [ -n "$SCRIPT_DIR_HINT" ] && [ -d "$SCRIPT_DIR_HINT/payload/lib" ]; then
    info "使用同目录 payload/lib 里的托盘库"
    copy_tray_libs "$SCRIPT_DIR_HINT/payload/lib"
  else
    [ "$OFFLINE_ONLY" -eq 1 ] && die "指定了 --offline-only，但包内没有托盘库"
    info "从 Arch Linux ARM 官方仓库取现成的 aarch64 二进制（不编译、不走 AUR）"
    DL_TMP="$(new_tmp)"
    for name in libayatana-appindicator libayatana-indicator; do
      fname="$(curl -fsSL --max-time 60 "$ALARM_PKGPAGE/$name" 2>/dev/null \
        | grep -oE "${name}-[0-9][^\"'<>[:space:]]*-aarch64\.pkg\.tar\.(zst|xz)" | sort -V | tail -1 || true)"
      if [ -z "$fname" ]; then warn "解析不到 $name 的文件名"; continue; fi
      info "下载 $fname"
      for m in "${ALARM_MIRRORS[@]}"; do
        if curl -fsSL --max-time 300 -o "$DL_TMP/$fname" "$m/$fname"; then
          bsdtar -xf "$DL_TMP/$fname" -C "$DL_TMP" usr/lib 2>/dev/null || true
          break
        fi
      done
    done
    [ -d "$DL_TMP/usr/lib" ] && copy_tray_libs "$DL_TMP/usr/lib"
  fi

  if ls "$PREFIX/lib/libayatana-appindicator3.so.1" >/dev/null 2>&1; then
    ok "托盘库已就位"
  else
    warn "没能获取托盘库。程序照常运行，只是没有系统托盘图标"
    warn "桌面模式下托盘才有意义；游戏模式（gamescope）本来就没有托盘"
  fi
fi

# ---------------------------------------------------------------- 启动器

step 6 "生成启动器"

mkdir -p "$BIN_DIR"
cat > "$LAUNCHER" <<EOF
#!/usr/bin/env bash
# Clash Verge Rev launcher — generated by install-clash-verge-frame.sh
PREFIX="$PREFIX"
export LD_LIBRARY_PATH="\$PREFIX/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}"
if [ -x "\$PREFIX/squashfs-root/AppRun" ]; then
  export APPDIR="\$PREFIX/squashfs-root"
  exec "\$PREFIX/squashfs-root/AppRun" "\$@"
fi
exec "\$PREFIX/usr/bin/clash-verge" "\$@"
EOF
chmod 0755 "$LAUNCHER"
ok "$LAUNCHER"

# ---------------------------------------------------------------- 图标 + 桌面项

step 7 "注册桌面项"

ICON_SRC=""
for d in "$PREFIX/squashfs-root" "$PREFIX"; do
  if [ -e "$d/.DirIcon" ]; then
    ICON_SRC="$(readlink -f "$d/.DirIcon" 2>/dev/null || echo "$d/.DirIcon")"
    break
  fi
done
if [ -z "$ICON_SRC" ] || [ ! -f "$ICON_SRC" ]; then
  ICON_SRC="$(find "$PREFIX" -type f -name '*.png' -path '*icons*' 2>/dev/null | sort -V | tail -1 || true)"
fi

ICON_LINE="$LAUNCHER"
if [ -n "$ICON_SRC" ] && [ -f "$ICON_SRC" ]; then
  mkdir -p "$ICONS_DIR/256x256/apps"
  cp -f "$ICON_SRC" "$ICONS_DIR/256x256/apps/clash-verge.png" 2>/dev/null || true
  ICON_LINE="$ICONS_DIR/256x256/apps/clash-verge.png"
fi

mkdir -p "$APPS_DIR"
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Clash Verge
Name[zh_CN]=Clash Verge
Comment=Clash Verge Rev (Steam Frame / ARM64)
Exec="$LAUNCHER" %u
Icon=$ICON_LINE
Terminal=false
Categories=Network;Utility;
StartupWMClass=clash-verge
MimeType=x-scheme-handler/clash;x-scheme-handler/clash-verge;
EOF
chmod 0644 "$DESKTOP_FILE"
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
fi
ok "$DESKTOP_FILE"

# ---------------------------------------------------------------- Steam 库

step 8 "加入 Steam 库（Steam Frame 非 Steam 应用启动器）"

if [ "$DO_STEAM" -eq 0 ]; then
  ok "已跳过（--no-steam）"
elif command -v steamos-add-to-steam >/dev/null 2>&1; then
  if steamos-add-to-steam "$DESKTOP_FILE" >/dev/null 2>&1; then
    ok "已请求 Steam 添加为「非 Steam 游戏」"
    printf '    若库里没出现，请在桌面模式下手动执行：\n'
    printf '      steamos-add-to-steam "%s"\n' "$DESKTOP_FILE"
  else
    warn "steamos-add-to-steam 调用失败（SSH / 无图形会话时常见）"
    printf '    请在 Steam Frame 桌面模式里手动执行：\n      steamos-add-to-steam "%s"\n' "$DESKTOP_FILE"
  fi
else
  warn "系统里没有 steamos-add-to-steam"
fi

# ---------------------------------------------------------------- TUN

step 9 "TUN（虚拟网卡）权限"

if [ "$WITH_TUN" -eq 1 ]; then
  [ -n "$SUDO" ] || die "TUN 需要 root 权限设置文件能力，但系统里没有 sudo"
  CORE_LIST=()
  while IFS= read -r f; do CORE_LIST+=("$f"); done < <(
    find "$PREFIX" -type f \( -name 'verge-mihomo' -o -name 'verge-mihomo-*' \) 2>/dev/null
  )
  [ "${#CORE_LIST[@]}" -gt 0 ] || die "找不到 mihomo 内核文件"
  for f in "${CORE_LIST[@]}"; do
    if run_sudo setcap 'cap_net_admin,cap_net_raw+ep' "$f" 2>/dev/null; then
      ok "setcap $(basename "$f")"
    else
      warn "setcap 失败：$f"
    fi
  done
  printf '\n    现在可在「设置 → Clash 设置」打开 TUN 模式。\n'
  printf '    每次系统更新或重装后需重新执行一次 --with-tun。\n'
else
  ok "未启用（需要时加 --with-tun 再跑一次）"
fi

# ---------------------------------------------------------------- 收尾

step 10 "完成"

printf '\n'
printf '  启动器     %s\n' "$LAUNCHER"
printf '  桌面项     %s\n' "$DESKTOP_FILE"
printf '  程序目录   %s\n' "$PREFIX"
printf '  配置目录   %s\n' "$APPDATA_DIR"
printf '\n'
printf '%s下一步：%s\n' "$c_bld" "$c_reset"
printf '  1) 启动：     %s\n' "$LAUNCHER"
printf '  2) 或在 Steam Frame 库里选「Clash Verge」（已注册为非 Steam 应用）\n'
printf '  3) 首次使用在「订阅」页导入你的订阅链接\n'
printf '  4) 想用 TUN 模式：重跑并加 --with-tun\n'
printf '\n'

if ! printf '%s' ":$PATH:" | grep -q ":$BIN_DIR:"; then
  warn "$BIN_DIR 不在 PATH 里，直接用完整路径即可"
  printf '    或把这行加进 ~/.bashrc：\n      export PATH="$HOME/.local/bin:$PATH"\n'
fi

exit 0
__PAYLOAD_BELOW__
