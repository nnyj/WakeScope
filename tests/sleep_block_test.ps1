function Read-Default {
  param(
    [string]$Prompt,
    [string]$Default
  )

  $value = Read-Host "$Prompt [$Default]"
  if ([string]::IsNullOrWhiteSpace($value)) {
    return $Default
  }

  return $value.Trim()
}

do {
  $Mode = Read-Default "Mode, display/system/both" "display"
} until ($Mode -in @("display", "system", "both"))

$Reason = "WakeScope test blocker"

$type_name = "WakeScopeTest.PowerRequest"

if (-not ($type_name -as [type])) {
  Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace WakeScopeTest {
  public static class PowerRequest {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct REASON_CONTEXT {
      public uint Version;
      public uint Flags;
      public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
  }
}
"@
}

$reason_ptr = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($Reason)
$context = [WakeScopeTest.PowerRequest+REASON_CONTEXT]@{
  Version = 0
  Flags = 1
  SimpleReasonString = $reason_ptr
}

$handle = [WakeScopeTest.PowerRequest]::PowerCreateRequest([ref]$context)
[Runtime.InteropServices.Marshal]::FreeHGlobal($reason_ptr)

if ($handle -eq [IntPtr]::Zero -or $handle -eq [IntPtr]::new(-1)) {
  throw "PowerCreateRequest failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}

$request_types = @()
if ($Mode -in @("display", "both")) { $request_types += 0 }
if ($Mode -in @("system", "both")) { $request_types += 1 }

try {
  foreach ($request_type in $request_types) {
    if (-not [WakeScopeTest.PowerRequest]::PowerSetRequest($handle, $request_type)) {
      throw "PowerSetRequest failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
  }

  Write-Host "Blocking $Mode until this window closes."
  Write-Host "Reason: $Reason"
  Write-Host "Close this window or press Ctrl+C to stop."
  while ($true) {
    Start-Sleep -Seconds 3600
  }
}
finally {
  foreach ($request_type in $request_types) {
    [WakeScopeTest.PowerRequest]::PowerClearRequest($handle, $request_type) | Out-Null
  }
  [WakeScopeTest.PowerRequest]::CloseHandle($handle) | Out-Null
}
