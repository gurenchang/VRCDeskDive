# VRCDeskDive

VRChat を落とさずに、VR 操作とデスクトップ操作を切り替えるツールです。

VRChat は VR モードのまま、HMD を外したらキーボードとマウスで操作できるようにします。
移動や左右の旋回は VRChat の OSC 入力、上下の視点は SteamVR のプレイスペースの回転で実現しています。

## 動作環境

- Windows 10 / 11
- [.NET 10 デスクトップランタイム](https://dotnet.microsoft.com/download/dotnet/10.0)
- SteamVR（Quest 3 + Steam Link で動作想定）
- VRChat の OSC を有効にしておくこと（アクションメニュー → オプション → OSC）

HMD を外すとスリープしてしまう場合は、近接センサーをテープなどで塞いでください。

## ビルドと起動

```
dotnet build -c Release
bin\Release\net10.0-windows\VRCDeskDive.exe
```

## 使い方

`Ctrl+F12`（変更可）、メインウィンドウのボタン、トレイアイコンのメニューから
「VR 操作」と「デスクトップ操作」を切り替えます。

### デスクトップ操作中の操作

| 操作 | 動作 |
| --- | --- |
| W A S D | 移動 |
| 右ドラッグ（VRChat 上） | 視点を上下左右に動かす |
| 右クリック＋ドラッグ（ツールのパッド上） | スティックのように視点を回す |
| ホイールクリック | 上下の視点を正面に戻す |
| Q / E | 左右に旋回 |
| Shift | 走る |
| Space | ジャンプ |
| V | マイク |
| Enter | チャットボックスに入力 |

## 注意

- 上下の視点は SteamVR のプレイスペース（Live 設定）を傾けて動かします。
  VR 操作に戻す・SteamVR が終了する・次回起動時のいずれかで元に戻します
  （退避ファイル: `%APPDATA%\VRCDeskDive\playspace_backup.json`）。
- OVR Advanced Settings などプレイスペースを操作する他のツールと同時に使うと競合することがあります。
- VRChat 本体の改造は行っていません。

## サードパーティ

- [OpenVR SDK](https://github.com/ValveSoftware/openvr)（BSD-3-Clause）: `ThirdParty/OpenVR/`
