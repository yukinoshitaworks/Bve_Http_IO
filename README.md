# Bve_Http_IO

BVE Trainsim (BveEX) の車両プラグインから、車両の状態（速度・パネル値など）や
ハンドル操作（レバーサー／力行／ブレーキ）を **HTTP** でやり取りするためのブリッジです。

もともとはMQTTブローカー経由でNode-REDなどと連携していましたが、MQTTブローカーを
別途用意しなくても動くように、シンプルなHTTPポーリングに置き換えたものです。

## 構成

```
BVEプラグイン(C#, VehiclePlugin)  --Tick毎--  Pythonブリッジサーバー(localhost:5000)  <--HTTP-->  Node-RED / 任意の外部ツール
```

- **BVEプラグイン**: `Tick()`のたびに車両状態を`POST /bve/snapshot`で送り、
  `GET /bve/commands`でハンドル操作コマンドを取得してそのままハンドルへ反映する。
- **Pythonブリッジサーバー**: 車両プラグインと同じフォルダに同梱されており、
  シナリオ起動時にプラグインが自動起動する。外部からのコマンドを一時的に
  保持するだけの単純な仲介役（状態はメモリ上のみ、永続化しない）。
- シナリオ終了時、プラグインが自分で起動したサーバープロセスを終了する。

対応車両（プラグイン部分は共通ロジック、車両ごとに別アセンブリとしてビルド）:

- `Rock_On_115_taka_T1040` — RockOn 115系用
- `Uchibo20_E217r` — Uchibo20 E217系(改)用

## フォルダ構成

```
Scenarios/
  Yokokura/
    Http_IO/
      Rock_On_115_taka_T1040/  115系用の完成品一式（BVEの車両データフォルダにそのまま配置できる）
      Uchibo20_E217r/          E217r用の完成品一式（同上）
Source/                    C#プラグインのソース(Visual Studio 2022 / .NET Framework 4.8)
  BveEX_RockOn_115_Http/
  BveEX_Uchibo20_E217r_Http/
NodeRED/
  bve_http_dashboard_flow.json   ダッシュボードからハンドル操作するためのNode-REDフロー(インポート用)
```

## 導入方法

1. `Scenarios/Yokokura/Http_IO/Rock_On_115_taka_T1040/` または `Scenarios/Yokokura/Http_IO/Uchibo20_E217r/` フォルダを、対象車両のデータフォルダとしてBVEのシナリオから
   参照できる場所に配置する。
   - `Vehicle.txt` / `Vehicle.VehiclePluginUsing.xml` は元になった車両データを前提にした
     サンプルです。`PerformanceCurve` / `Panel` などのパスは環境に合わせて書き換えてください。
2. **Python 3 がインストールされ、`PATH`に`python`コマンドが通っていること**が必要です
   （動作確認は Python 3.14 系）。追加のライブラリは不要（標準ライブラリのみ）。
3. シナリオを開くと、プラグインが自動的に `bve_http_server.py` を起動します
   （新しいコンソールウィンドウが開き、ログが表示されます）。
   既に起動済みなら二重起動はしません。シナリオ終了時に自動で停止します。

## HTTP API（`http://127.0.0.1:5000`）

サーバーは **ループバック(127.0.0.1)のみ** で待ち受けます。同じPC内からのみアクセス可能です。

| メソッド | パス | 用途 |
|---|---|---|
| `POST` | `/bve/snapshot` | BVEプラグイン→サーバー。車両状態をまとめて送信（表示用途、コンソールに出力するのみ）。 |
| `GET` | `/bve/commands` | BVEプラグイン→サーバー。現在のハンドル指令 `{"reverser":0,"power":0,"brake":0}` を取得。 |
| `POST` | `/bve/commands` | 外部ツール→サーバー。送ったキーだけ部分更新（例: `{"power":3}`）。 |

`/bve/commands` の値は次のTickでそのまま `handles.ReverserPosition` / `PowerNotch` / `BrakeNotch` に反映されます。値の範囲チェックはしていないため、送信側で妥当な範囲に収めてください。

## Node-RED ダッシュボード

`NodeRED/bve_http_dashboard_flow.json` は、レバーサー・力行・ブレーキを操作するボタン付き
ダッシュボードのフローです。Node-RED エディタのメニュー → Import から読み込んでください
（`node-red-dashboard` が別途必要です）。読み込むと「BVE (HTTP)」という新規タブが追加されます。

初期値は Power 0〜5、Brake 0〜8 + EB(9) 想定です。実車のノッチ数に合わせて、フロー内の
`power step` / `brake step` ファンクションノードの上限値を調整してください。

## ソースからビルドする場合

- Visual Studio 2022、.NET Framework 4.8 SDK が必要です。
- `Source/BveEX_RockOn_115_Http.csproj` は `Mackoy.IInputDevice.DLL` を
  `C:\Program Files (x86)\mackoy\BveTs5\` から参照します（BVE Trainsim 5 のインストールが前提）。
  パスが異なる場合は `.csproj` の `HintPath` を書き換えてください。
- `packages/` (BveEx.CoreExtensions / BveEx.Diagnostics / BveEx.PluginHost の NuGet パッケージ)は
  このリポジトリには含めていません。Visual Studio でNuGetの復元を行ってください。

## 既知の制限・注意点

- サーバーはBVEプラグインが自動起動する前提のため、**1台のPCで同時に動かせるのは基本1車両**です
  （どちらも既定でポート5000を使うため、複数車両を同時にHTTP接続する構成にはしていません）。
- `/bve/commands` に認証はありません。ループバックのみの待ち受けにしているため、外部ネットワークからは
  到達できませんが、同じPC上の他プロセスからは誰でも操作コマンドを送れます。
- リクエストボディのサイズ上限、`reverser`/`power`/`brake`の値の範囲チェックは行っていません。
  信頼できるツール（自分で用意したNode-REDフローなど）からの利用を想定しています。
