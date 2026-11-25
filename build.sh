#!/usr/bin/env bash
# 构建 NativeAOT 单文件二进制,装到 niri 配置目录的 sh/ 下。
#
# niri 配置里 spawn-at-startup 指向 ~/.config/niri/sh/niri-auto-stack,
# 改完代码跑一次这个脚本,然后重启 niri(或手动 kill 掉旧进程再起)。
#
# 也可以指定安装位置:
#   ./build.sh /usr/local/bin/niri-auto-stack
set -euo pipefail

cd "$(dirname "$(readlink -f "$0")")"

# NativeAOT 需要 C 链接器。NixOS 上 gcc 不在默认 PATH 里,这里补上。
for p in /nix/store/*gcc-wrapper*/bin /nix/store/*binutils-wrapper*/bin; do
    [ -d "$p" ] && PATH="$PATH:$p"
done
export PATH

TARGET="${1:-$HOME/.config/niri/sh/niri-auto-stack}"

# 发布到 obj/ 下(已被 .gitignore 忽略),只把二进制挑出来装走。
# 不用 mktemp + trap rm:某些环境的 rm 是包装脚本,退出钩子里容易出岔子。
STAGE="$PWD/obj/publish"

dotnet publish -c Release -r linux-x64 -o "$STAGE"
install -m 755 "$STAGE/niri-auto-stack" "$TARGET"

echo
echo "完成: $TARGET"
ls -la "$TARGET"
