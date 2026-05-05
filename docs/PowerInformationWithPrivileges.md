# PowerInformationWithPrivileges — API 仕様書

> [!CAUTION]
> **注意:** 本 API は `powrprof.dll` の非公開エクスポートです。  
> 公式ドキュメントは存在せず、本仕様は Windows 11 Pro x64 25H2 上での実測調査に基づきます。

---

## 1. 関数シグネチャ

```c
// powrprof.dll (非公開エクスポート)
NTSTATUS PowerInformationWithPrivileges(
    ULONG   InformationLevel,
    PVOID   InputBuffer,
    ULONG   InputBufferLength,
    PVOID   OutputBuffer,
    ULONG   OutputBufferLength
);
```

### C# P/Invoke 定義

```csharp
[DllImport("powrprof.dll", ExactSpelling = true)]
static extern int PowerInformationWithPrivileges(
    int    informationLevel,
    IntPtr inputBuffer,
    uint   inputBufferLength,
    IntPtr outputBuffer,
    uint   outputBufferLength);
```

---

## 2. パラメータ

| パラメータ | 型 | 説明 |
|---|---|---|
| `InformationLevel` | `ULONG` | 取得する情報の種別を示すレベル番号（下表参照） |
| `InputBuffer` | `PVOID` | 入力バッファ。電力要求リスト取得では `NULL` |
| `InputBufferLength` | `ULONG` | 入力バッファサイズ。`NULL` のとき `0` |
| `OutputBuffer` | `PVOID` | 出力バッファへのポインタ |
| `OutputBufferLength` | `ULONG` | 出力バッファのバイト数 |

### InformationLevel 値

| 値 | 用途 | 必要権限 |
|----|------|---------|
| **45** | 電力要求リスト取得（本仕様の対象） | `SeShutdownPrivilege` + 管理者 |
| 49 | `GetPowerRequestList`（公式レベル） | `SeTcbPrivilege`（SYSTEM のみ） |

**→ 管理者権限で使用できるのはレベル 45 のみ。**

---

## 3. 戻り値（NTSTATUS）

| 値 | 定数 | 意味 |
|----|------|------|
| `0x00000000` | `STATUS_SUCCESS` | 成功。OutputBuffer にデータが格納された |
| `0xC0000023` | `STATUS_BUFFER_TOO_SMALL` | バッファ不足。OutputBufferLength を拡大して再呼び出しする |
| `0xC0000022` | `STATUS_ACCESS_DENIED` | 権限不足。`SeShutdownPrivilege` が有効でない |

---

## 4. 必要権限

`AdjustTokenPrivileges` で以下を有効化してから呼び出す。

| 権限 | 用途 |
|------|------|
| `SeShutdownPrivilege` | レベル 45 の呼び出しに必須 |
| `SeDebugPrivilege` | 推奨（一部構成で必要になる場合がある） |

```csharp
// 権限有効化の例
EnablePrivilege("SeShutdownPrivilege");
EnablePrivilege("SeDebugPrivilege");
```

---

## 5. 呼び出し手順

```csharp
uint size = 16384;
while (true)
{
    IntPtr buf = Marshal.AllocHGlobal((int)size);
    try
    {
        int status = PowerInformationWithPrivileges(45, IntPtr.Zero, 0, buf, size);

        if (status == STATUS_BUFFER_TOO_SMALL) { size *= 2; continue; }
        if (status != STATUS_SUCCESS) return; // エラー処理

        Parse(buf, size);
        return;
    }
    finally { Marshal.FreeHGlobal(buf); }
}
```

初期サイズは 16384 バイトを推奨。`STATUS_BUFFER_TOO_SMALL` が返った場合は2倍に拡張して再試行する。

---

## 6. 出力バッファのレイアウト

### 6.1 ヘッダー

```
Offset  Type    Field    説明
+0x00   uint64  Count    要素数
+0x08   uint64  Offsets  Offsets[Count]: 各要素のバッファ先頭からのバイトオフセット
```

- ヘッダーサイズ: `8 + Count × 8` バイト
- `Offsets[i]` の値がそのまま `buf + Offsets[i]` の位置を指す

```csharp
ulong count = (ulong)Marshal.ReadInt64(buf, 0);

for (ulong i = 0; i < count; i++)
{
    ulong elemOff = (ulong)Marshal.ReadInt64(buf, 8 + (int)i * 8);
    IntPtr elem   = IntPtr.Add(buf, (int)elemOff);
    // elem を解析する
}
```

### 6.2 要素レイアウト（Element）

各要素は以下のフィールドを持つ（すべて 8 バイト幅）。

```
Offset   Type    Field  説明
+0x00    uint64  type   TypeMarker（要素の種別）
+0x08    uint64  f1     【0x12 型】SYSTEM アクティブフラグ
+0x10    uint64  f2     【0x3F 型】DISPLAY アクティブ要求数
+0x18    uint64  f3     未解析
+0x20    uint64  f4     コンテンツサイズ（文字列を含む構造体の合計バイト数）
+0x28    uint64  f5     【0x1E / 0x1000003F 型】アクティブフラグ
+0x30    uint64  f6     サブ構造オフセット（通常 0x28）
+0x38    uint64  f7     PID（0x3F / 0x1000003F 型で経験的に確認済み・未公式）
+0x40    uint64  f8     文字列レイアウト情報
+0x48    WCHAR[] name   1本目の文字列（NT パス or デバイス名、null 終端 UTF-16LE）
         WCHAR[] reason 2本目の文字列（Reason、name の直後、null 終端 UTF-16LE）
```

> **特例:** TypeMarker `0x12` の一部要素（"Power Manager", "Sleep Idle State Disabled"）は
> 文字列開始位置が +0x48 ではなく +0x68 になる。DISPLAY 解析には無関係。

### 6.3 TypeMarker 値と種別

| TypeMarker | 種別 | 対応 powercfg カテゴリ |
|---|---|---|
| `0x12` | カーネル / ドライバー（主に音声デバイス） | SYSTEM |
| `0x1E` | レガシーカーネル呼び出し / サービス | SYSTEM |
| `0x3F` | ユーザーモードプロセス | **DISPLAY** / SYSTEM / AWAYMODE |
| `0x1000003F` | ユーザーモードプロセス（Execution 変種） | EXECUTION |

---

## 7. DISPLAY ブロッカーの抽出（詳細）

DISPLAY 要求を持つ要素の条件:

```
TypeMarker == 0x3F  かつ  f2 (+0x10) != 0
```

### 7.1 アクティブ判定

```csharp
ulong typeMarker = (ulong)Marshal.ReadInt64(elem, 0x00);
if (typeMarker != 0x3F) continue;         // DISPLAY 対象外

ulong f2 = (ulong)Marshal.ReadInt64(elem, 0x10);
if (f2 == 0) continue;                    // アクティブな DISPLAY 要求なし（登録済みだが非アクティブ）
```

`f2` の値は現在アクティブな要求数を示す（0 = 非アクティブ、1 以上 = アクティブ）。

### 7.2 NT パスの読み取り

1本目の文字列（要素先頭 +0x48 から、null 終端 UTF-16LE）がプロセスの NT パスになる。

```csharp
// null 終端 WCHAR 文字列を読む
static string ReadNullTermWChar(IntPtr elem, ref int byteOffset, int limit)
{
    var sb = new StringBuilder();
    while (byteOffset + 1 < limit)
    {
        short w = Marshal.ReadInt16(elem, byteOffset);
        byteOffset += 2;
        if (w == 0) break;
        sb.Append((char)w);
    }
    return sb.ToString();
}

int offset = 0x48;
int limit   = (int)(bufSize - elemOff);       // バッファ終端を超えないよう制限
string ntPath = ReadNullTermWChar(elem, ref offset, limit);
// 例: \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe
```

### 7.3 Reason 文字列の読み取り

2本目の文字列は 1 本目の null 終端直後から始まる。

```csharp
// ntPath 読み取り後、offset はすでに次の文字列先頭を指している
string reason = ReadNullTermWChar(elem, ref offset, limit);
// 例: "Video Wake Lock"
```

### 7.4 PID の取得

`f7` (+0x38) に PID が格納されていることを実測で確認済み（未公式）。  
値の信頼性を検証してから使用し、不正な場合はプロセス名マッチングにフォールバックする。

```csharp
ulong f7 = (ulong)Marshal.ReadInt64(elem, 0x38);

if (f7 > 0 && f7 <= uint.MaxValue)
{
    uint pid = (uint)f7;
    try
    {
        using var proc = Process.GetProcessById((int)pid);
        // パスが一致すれば採用
        if (string.Equals(proc.MainModule?.FileName, win32Path,
                StringComparison.OrdinalIgnoreCase))
            return pid;
    }
    catch { }
}
// フォールバック: プロセス名マッチング
```

### 7.5 NT パス → Win32 パス変換

`\Device\HarddiskVolumeX\...` 形式の NT パスは `QueryDosDevice` で Win32 パスに変換する。

```csharp
// QueryDosDevice でドライブレターとデバイスパスの対応を列挙し変換する
// 例: \Device\HarddiskVolume3\Windows\... → C:\Windows\...
string? win32Path = NtPathConverter.ToWin32Path(ntPath);
```

---

## 8. 実測サンプル（Windows 11 x64、Chrome で YouTube 再生中）

### powercfg /requests の出力（比較用）

```
DISPLAY:
[PROCESS] \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe
Video Wake Lock
```

### レベル 45 バッファから抽出した対応要素

```
idx  TypeMarker  f2  f7(PID)  name
[23] 0x3F        1   <PID>    \Device\HarddiskVolume3\...\chrome.exe
                              "Video Wake Lock"  ← reason
```

### 非アクティブ要素の例（f2=0 のため除外される）

```
idx  TypeMarker  f2  name
[17] 0x3F        0   \...\PowerToys.Awake.exe   ← 登録済みだが要求なし → スキップ
```

---

## 9. 未解決事項

| 項目 | 状態 |
|------|------|
| `f7` が常に正確な PID を保持するか | 経験的に確認済みだが未公式 |
| AWAYMODE の TypeMarker 値 | 未観測 |
| TypeMarker `0x1E` の f7 以降フィールド | 一部未解析 |
| 他の Windows バージョンでの互換性 | Windows 11 x64 のみ確認 |

---

## 10. 参考

- 本 API は `powercfg.exe` が内部で使用していることをリバースエンジニアリングにより確認
- 公式の等価 API: `NtPowerInformation(GetPowerRequestList=49)` — ただし `SeTcbPrivilege`（SYSTEM）が必要
- バッファ構造は `PowerInformationWithPrivileges(45)` と `powercfg /requests` の同時キャプチャにより相関付けて確認
