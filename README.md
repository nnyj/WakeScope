# WakeScope

「なぜスリープしない？」の犯人を1秒ごとに監視する、タスクトレイ常駐ツールです。  
DISPLAY 電力要求をブロックしているプロセスをリアルタイムで検出・表示します。

[English README](README-en.md)

## 概要

Windows はディスプレイのスリープ前に DISPLAY 電力要求の有無を確認します。  
動画再生中のブラウザや一部のアプリはこの要求を発行し続けるため、画面がスリープ状態になりません。  
WakeScope はその犯人を Windows の電力要求 API を直接呼び出して1秒ごとに監視し、トレイアイコンの色で即座に知らせます。

## 機能

- タスクトレイに常駐し、UI を占有しない
- DISPLAY 電力要求を1秒ごとに自動監視
- ブロッカー検出時はトレイアイコンを明るく表示(検出していない場合アイコンを暗く表示)
- 右クリックメニューでプロセス名・PID・アイコンを一覧表示
- 多重起動防止（グローバル Mutex）

## 動作環境

| 項目 | 要件 |
|------|------|
| OS | Windows 11（x64） |
| ランタイム | 不要（自己完結型） |
| 権限 | **管理者権限が必要**（電力要求 API の呼び出しに必須） |

## インストール

1. [Releases](https://github.com/130cmWolf/WakeScope/releases) から最新の zip をダウンロードして展開する、または自分でビルドする
2. `WakeScope.exe` を右クリック →「管理者として実行」

### 自分でビルドする場合

.NET 8 SDK が必要です。

```bash
git clone https://github.com/130cmWolf/WakeScope.git
cd WakeScope
dotnet publish -p:PublishProfile=Release
```

`publish\WakeScope.exe` が生成されます。

## 使い方

1. 管理者として `WakeScope.exe` を実行するとタスクトレイにアイコンが表示される
2. ブロッカーがなければ暗いアイコン、1件以上あれば明るいアイコンに変化する
3. 右クリックでブロッカーの一覧と「Exit」メニューを表示する

## 仕組み

`powrprof.dll` の `PowerInformationWithPrivileges`（レベル 45）を1秒ごとに呼び出し、システム全体の電力要求リストをバッファとして取得します。  
TypeMarker `0x3F` かつアクティブフラグが 0 でないエントリを DISPLAY ブロッカーとして抽出し、NT パスを Win32 パスに変換してプロセスアイコンと PID を特定します。

```mermaid
flowchart TD
    A([起動]) --> B[トレイアイコン・フォールバックアイコンを読み込む]
    B --> C[タスクトレイアイコンを表示]
    C --> D[1秒待機]
    D --> E[PowerInformationWithPrivileges を呼び出す]
    E --> F[TypeMarker 0x3F のアクティブエントリを抽出]
    F --> G{DISPLAY ブロッカーあり?}
    G -- No --> H[アイコン: 明]
    G -- Yes --> I[アイコン: 暗]
    H --> D
    I --> D
    C --> J{{右クリック → 終了}}
    J --> K([終了])
```

## 動作確認

YouTube を Chrome で再生中に以下を実行し、WakeScope の表示と一致すれば正常です。

```
powercfg /requests
DISPLAY:
[PROCESS] \Device\HarddiskVolume3\...\chrome.exe
Video Wake Lock
```

## ライセンス

MIT — [130cmWolf](https://github.com/130cmWolf/WakeScope)
