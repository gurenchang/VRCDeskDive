# VRCDeskDive

VRChat を落とさずに、VR 操作とデスクトップ操作を切り替えるツールです。

VRChat は VR モードのまま、HMD を外したらキーボードとマウスで操作できるようにします。
移動やジャンプなどは VRChat の OSC 入力で、視点（上下左右）は SteamVR ドライバーで HMD の姿勢を書き換えて動かします。

## 動作環境

- Windows 10 / 11（Windows 10 + Quest 3 + Steam Link で動作確認）
- [.NET 10 デスクトップランタイム](https://dotnet.microsoft.com/download/dotnet/10.0)
- SteamVR
- VRChat の OSC を有効にしておくこと（アクションメニュー → オプション → OSC）

HMD を外すとスリープしてしまう場合は、近接センサーをテープなどで塞いでください。

## ビルドと起動

SteamVR ドライバー（C）を先にビルドしてから、アプリをビルドします。
ドライバーのビルドには Visual Studio Build Tools か [zig](https://ziglang.org/)（`tools\zig\` に置けば自動で使われます）が必要です。

```
powershell -ExecutionPolicy Bypass -File driver\build.ps1
dotnet build -c Release
bin\Release\net10.0-windows\VRCDeskDive.exe
```

初回は画面の「SteamVRに登録」を押してから SteamVR を再起動してください。

- 登録されるのはビルド先のフォルダ（`bin\Release\net10.0-windows\driver\vrcdeskdive`）です。フォルダを移動したら登録し直してください。
- SteamVR の起動中はドライバーの DLL が使用中になるので、ドライバーを作り直すときは SteamVR を終了してください。

## 使い方

メインウィンドウのボタンで「VR 操作」と「デスクトップ操作」を切り替えます。
HMD を着けたままデスクトップ操作に切り替えてから外すと、その時の視点の位置と向きのまま操作を続けられます。

- タイトルバーのボタンで、最前面に固定・縮小表示を切り替えられます（次回起動時も引き継がれます）。
- ウィンドウを閉じるとアプリが終了します。

### デスクトップ操作中の操作

VRChat かこのツールのウィンドウが前面にあるときだけ効きます。

| 操作 | 動作 |
| --- | --- |
| W A S D | 移動 |
| 左クリック（VRChat の画面内） | マウスルック開始（マウスの動きがそのまま視点になる） |
| Alt（設定で Tab / Ctrl に変更可） | マウスルック解除（VRChat から離れても解除） |
| 右クリック＋ドラッグ（ツールのパッド上） | スティックのように視点を回す |
| ホイールクリック | 視点を水平に戻す |
| Shift | 走る |
| Space | ジャンプ |
| V | マイク |
| Enter | チャットボックスに入力（Enter で送信 / Esc でキャンセル） |

Esc はそのまま VRChat に届くので、VRChat のメニューを開けます。
視点の感度は設定の「マウス感度」で調整できます。

## 仕組み

```
VRCDeskDive ── OSC (127.0.0.1:9000) ──────────────► VRChat        移動・ジャンプ・走る・マイク・チャット
     │
     └── 共有メモリ ──► SteamVR ドライバー（vrserver 内）  HMD の姿勢を書き換えて視点を動かす
```

- ドライバーは HMD の姿勢だけを書き換えます。プレイスペースには触れません。
- デスクトップ操作中は、切り替えた時の頭の位置と左右の向きを保ったまま視点を水平にします。HMD を机に置いても視点の高さは変わりません。
- VR 操作に戻すと書き換えをやめるので、本来の HMD の姿勢に戻ります。
- アプリが落ちた場合も、ドライバーは 1.5 秒で姿勢の書き換えをやめます。
- VRChat 本体の改造は行っていません。

## ファイルの場所

| ファイル | 内容 |
| --- | --- |
| `%APPDATA%\VRCDeskDive\settings.json` | 設定 |
| `%APPDATA%\VRCDeskDive\debug.log` | 不具合調査用のログ（起動ごとに作り直す） |

## ドライバーの登録を外す

SteamVR を終了してから、次のコマンドを実行します（パスは環境に合わせてください）。

```
"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\vrpathreg.exe" removedriver "<ビルド先>\driver\vrcdeskdive"
```

## ライセンス

[MIT License](LICENSE)

## サードパーティ

- [OpenVR SDK](https://github.com/ValveSoftware/openvr)（BSD-3-Clause）: `ThirdParty/OpenVR/`
- [Rajdhani](https://fonts.google.com/specimen/Rajdhani)（SIL Open Font License 1.1）: `Fonts/`
