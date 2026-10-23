#!/bin/bash
# AI Usage Bar — Mac 用チーム参加ツール（ダブルクリックで起動）
# 追加インストール不要。Mac 標準の Python 3 で動きます。
cd "$(dirname "$0")" || exit 1
if command -v python3 >/dev/null 2>&1; then
  exec python3 -m aiusagebar gui
else
  osascript -e 'display dialog "Python 3 が見つかりません。ターミナルで「xcode-select --install」を実行してから、もう一度お試しください。" buttons {"OK"} default button 1'
fi
