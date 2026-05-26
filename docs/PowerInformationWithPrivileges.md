# PowerInformationWithPrivileges


Notes for undocumented `powrprof.dll` export used by WakeScope.

No official Microsoft documentation exists for this API.
Facts below are based on observed Windows 11 Pro x64 25H2 behavior and comparison with `powercfg /requests`.

## Function


Native signature:

```c
NTSTATUS PowerInformationWithPrivileges(
  ULONG InformationLevel,
  PVOID InputBuffer,
  ULONG InputBufferLength,
  PVOID OutputBuffer,
  ULONG OutputBufferLength
);
```

C# P/Invoke:

```csharp
[DllImport("powrprof.dll", ExactSpelling = true)]
static extern int PowerInformationWithPrivileges(
  int informationLevel,
  IntPtr inputBuffer,
  uint inputBufferLength,
  IntPtr outputBuffer,
  uint outputBufferLength);
```

## Parameters


| parameter | type | meaning |
|---|---|---|
| `InformationLevel` | `ULONG` | requested information class |
| `InputBuffer` | `PVOID` | input buffer, `NULL` for power request list |
| `InputBufferLength` | `ULONG` | input buffer size, `0` when input is `NULL` |
| `OutputBuffer` | `PVOID` | output buffer |
| `OutputBufferLength` | `ULONG` | output buffer byte count |

Known levels:

| level | use | required privilege |
|---|---|---|
| `45` | power request list, used by WakeScope | `SeShutdownPrivilege`, admin token |
| `49` | official `GetPowerRequestList` level | `SeTcbPrivilege`, SYSTEM only |

Admin apps can use level `45`.
Level `49` is not practical outside SYSTEM.

## Return Codes


| code | name | meaning |
|---|---|---|
| `0x00000000` | `STATUS_SUCCESS` | output buffer contains data |
| `0xC0000023` | `STATUS_BUFFER_TOO_SMALL` | retry with larger output buffer |
| `0xC0000022` | `STATUS_ACCESS_DENIED` | required privilege missing |

## Privileges


Enable before calling:

| privilege | use |
|---|---|
| `SeShutdownPrivilege` | required for level `45` |
| `SeDebugPrivilege` | helps resolve process details on some systems |

WakeScope runs elevated and enables both privileges.

## Call Flow


Use a growable output buffer.
Original testing used `16384` bytes as initial size.

```csharp
uint size = 16384;

while (true)
{
  IntPtr buffer = Marshal.AllocHGlobal((int)size);
  try
  {
    int status = PowerInformationWithPrivileges(45, IntPtr.Zero, 0, buffer, size);

    if (status == STATUS_BUFFER_TOO_SMALL)
    {
      size *= 2;
      continue;
    }

    if (status != STATUS_SUCCESS)
      return;

    Parse(buffer, size);
    return;
  }
  finally
  {
    Marshal.FreeHGlobal(buffer);
  }
}
```

## Buffer Layout


Header:

```text
Offset  Type    Field
+0x00   uint64  count
+0x08   uint64  offsets[count]
```

`offsets[i]` is byte offset from buffer start to element start.
Header size is `8 + count * 8`.

Element, observed on Windows 11 x64:

```text
Offset  Type     Field
+0x00   uint64   type_marker
+0x08   uint64   f1, active flag for type 0x12, SYSTEM count for type 0x3F
+0x10   uint64   f2, DISPLAY active count for type 0x3F
+0x18   uint64   f3, unknown
+0x20   uint64   f4, content size including strings
+0x28   uint64   f5, active flag for type 0x1E and 0x1000003F
+0x30   uint64   f6, substructure offset, often 0x28
+0x38   uint64   f7, PID candidate for type 0x3F and 0x1000003F
+0x40   uint64   f8, string layout data
+0x48   wchar[] native path or device name, null-terminated UTF-16LE
        wchar[] reason, null-terminated UTF-16LE
```

Special case:
some `0x12` kernel entries, such as `Power Manager` or `Sleep Idle State Disabled`, start strings at `+0x68` instead of `+0x48`.

## Type Markers


| marker | source | powercfg category |
|---|---|---|
| `0x12` | kernel or driver, often audio device | `SYSTEM` |
| `0x1E` | legacy kernel caller or service | `SYSTEM` |
| `0x3F` | user-mode process | `DISPLAY`, `SYSTEM`, `AWAYMODE` |
| `0x1000003F` | user-mode execution variant | `EXECUTION` |

## Active Category Mapping


DISPLAY:

```text
type_marker == 0x3F && f2 != 0
```

SYSTEM process:

```text
type_marker == 0x3F && f1 != 0
```

EXECUTION:

```text
type_marker == 0x1000003F && f5 != 0
```

SYSTEM driver or kernel:

```text
(type_marker == 0x12 || type_marker == 0x1E) && (f1 != 0 || f5 != 0)
```

`f2` is active DISPLAY request count.
`0` means registered but inactive.
`1` or higher means active blocker.

## String Parsing


Read first null-terminated UTF-16LE string from element offset `+0x48`.
For process entries this is usually NT path:

```text
\Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe
```

Read second string immediately after first null terminator.
This is reason text:

```text
Video Wake Lock
```

Use bounds checks against element size or remaining output buffer.

## PID Mapping


`f7` at `+0x38` usually stores PID for process entries.
This is observed behavior, not official contract.

WakeScope only trusts PID after validation:

1. Convert NT path to Win32 path.
2. Open process by PID.
3. Compare process executable path to parsed path.
4. If mismatch or access fails, do not trust PID.

Fallback:

1. Search running processes by executable path.
2. If one match exists, use it.
3. If multiple matches exist, list candidates in menu instead of guessing.

## NT Path Conversion


Convert NT device paths with `QueryDosDevice`.

Example:

```text
\Device\HarddiskVolume3\Windows\System32\notepad.exe
C:\Windows\System32\notepad.exe
```

WakeScope uses this for PID validation and display text.

## Observed Sample


`powercfg /requests` during Chrome YouTube playback:

```text
DISPLAY:
[PROCESS] \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe
Video Wake Lock
```

Matching level `45` element:

```text
type_marker  f2  f7      name
0x3F         1   <PID>   \Device\HarddiskVolume3\...\chrome.exe
                       reason = Video Wake Lock
```

Inactive registered request:

```text
type_marker  f2  name
0x3F         0   \...\PowerToys.Awake.exe
```

Skip inactive entries.

## Why WakeScope Also Uses powercfg


Native level `45` gives useful PID candidates and counters.
`powercfg /requests` gives stable user-facing categories, driver text, and reason strings.

WakeScope combines both:

1. Parse `powercfg /requests` as display source of truth.
2. Use level `45` to enrich entries with PID and active counters.
3. Fall back to path matching when native PID is missing or untrusted.

This keeps menu close to `powercfg /requests` while allowing process kill actions when safe.

## Unknowns


| item | state |
|---|---|
| `f7` always accurate PID | observed, unofficial |
| `AWAYMODE` marker mapping | not observed |
| `0x1E` fields after `f7` | partly unknown |
| compatibility outside Windows 11 x64 | not verified |

## Risk


- API is private export from `powrprof.dll`
- layout can change after Windows update
- privilege requirements can change
- parser must fail soft and avoid process crash

## References


- `powercfg.exe` appears to use this API internally
- official equivalent is `NtPowerInformation(GetPowerRequestList = 49)`, but it needs `SeTcbPrivilege`
- structure was correlated by comparing level `45` buffer captures with `powercfg /requests`
