<#
.SYNOPSIS
  为开始菜单快捷方式写入 AppUserModelID 属性（PRD §14.3 / FR-W-PKG-9 / V-W-B4）。

.DESCRIPTION
  Windows Toast 通知对**未打包（unpackaged）Win32 应用**的硬前提是：
    ① 进程声明 AUMID（由 TKSDesktop.exe 的 Program.cs 调
       SetCurrentProcessExplicitAppUserModelID("TKSDesktop") 完成）；
    ② 开始菜单存在一个 .lnk，其 **AppUserModelID 属性** 与 ① 逐字一致。

  ⚠️ NSIS 的 `CreateShortCut` **不支持**写该属性；而
     `System::Call 'shell32::SetCurrentProcessExplicitAppUserModelID(...)'`
     设置的是**当前进程**的 AUMID，**不是** .lnk 文件的属性 —— 二者不可互替。
     这正是本脚本存在的原因。

  实现要点（均为实测得出，勿凭直觉「简化」）：
    · 必须用 **ShellLink COM 对象**（CLSID 00021401-…）并 QI 出 IPropertyStore，
      而不是 `SHGetPropertyStoreFromParsingName`；
    · **`IPropertyStore.Commit()` 对 .lnk 不会落盘**，必须再调
      `IPersistFile::Save(path, TRUE)`（实测：少了它文件字节完全不变）；
    · 读回时 `PROPVARIANT` 必须用 **`PropVariantClear`** 释放，
      不能用 `Marshal.FreeCoTaskMem`（实测会出现「写得进、读不出」的假象）；
    · 读回与写入都要用 **`STGM_READWRITE` / `GPS_READWRITE`**。

.PARAMETER ShortcutPath
  目标 .lnk 的完整路径。

.PARAMETER Aumid
  要写入的 AppUserModelID；必须与客户端声明的 AUMID 逐字一致。

.PARAMETER Verify
  写入后读回校验（读不出或不一致则退出码非 0）。

.NOTES
  退出码：0 = 成功（含 -Verify 通过）；2 = 快捷方式不存在；3 = 写入失败；4 = 读回校验失败。
  本脚本以 UTF-8 with BOM 保存（Windows PowerShell 5.1 需要）。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ShortcutPath,
    [Parameter(Mandatory = $true)][string]$Aumid,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if (-not (Test-Path -LiteralPath $ShortcutPath)) {
    Write-Error "快捷方式不存在：$ShortcutPath"
    exit 2
}

# ---- 属性存储互操作 ----
# ⚠️ Add-Type 默认使用随 .NET Framework 附带的旧版 C# 编译器：
#    不支持表达式体成员（=>）等现代语法，故下面一律用最保守写法。
$cs = @'
using System;
using System.Runtime.InteropServices;

public static class ShortcutAumid
{
    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    /// <summary>
    /// PROPVARIANT 必须按完整 24 字节布局声明：
    /// vt(2) + wReserved1/2/3(各2) + 联合体(16)。只声明 vt + IntPtr 会因
    /// 打包差异导致读到的指针位置错误（实测表现为「写得进、读不出」）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort reserved1;
        [FieldOffset(4)] public ushort reserved2;
        [FieldOffset(6)] public ushort reserved3;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(16)] public IntPtr pointerValue2;
    }

    private const ushort VT_LPWSTR = 31;
    private const uint STGM_READ = 0;
    private const uint STGM_READWRITE = 2;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private static PROPERTYKEY AppUserModelIdKey()
    {
        PROPERTYKEY key = new PROPERTYKEY();
        key.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        key.pid = 5;
        return key;
    }

    private static object NewShellLink()
    {
        // ShellLink 的 CLSID。用 Type.GetTypeFromCLSID 而不是 ProgID，
        // 避免依赖过时的 WScript.Shell 封装（它不暴露 IPropertyStore）。
        return Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")), true);
    }

    /// <summary>写入 AppUserModelID。失败抛异常（调用方据此退出码非 0）。</summary>
    public static void Set(string shortcutPath, string aumid)
    {
        object link = NewShellLink();
        try
        {
            IPersistFile persist = (IPersistFile)link;
            persist.Load(shortcutPath, STGM_READWRITE);

            IPropertyStore store = (IPropertyStore)link;

            PROPERTYKEY key = AppUserModelIdKey();
            PROPVARIANT pv = new PROPVARIANT();
            pv.vt = VT_LPWSTR;
            pv.pointerValue = Marshal.StringToCoTaskMemUni(aumid);

            try
            {
                int hrSet = store.SetValue(ref key, ref pv);
                if (hrSet != 0)
                {
                    Marshal.ThrowExceptionForHR(hrSet);
                }

                // 注意：Commit 对 .lnk 不落盘，但仍是必需的（更新内存中的属性存储）。
                int hrCommit = store.Commit();
                if (hrCommit != 0)
                {
                    Marshal.ThrowExceptionForHR(hrCommit);
                }
            }
            finally
            {
                // SetValue 会拷贝值，这里释放我们分配的那份。
                PropVariantClear(ref pv);
            }

            // 关键一步：经 IPersistFile.Save 才会真正写回磁盘。
            persist.Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>读回 AppUserModelID；未设置时返回空串。</summary>
    public static string Get(string shortcutPath)
    {
        object link = NewShellLink();
        try
        {
            IPersistFile persist = (IPersistFile)link;
            persist.Load(shortcutPath, STGM_READ);

            IPropertyStore store = (IPropertyStore)link;

            PROPERTYKEY key = AppUserModelIdKey();
            PROPVARIANT pv;
            int hr = store.GetValue(ref key, out pv);
            if (hr != 0)
            {
                return string.Empty;
            }

            try
            {
                if (pv.vt != VT_LPWSTR || pv.pointerValue == IntPtr.Zero)
                {
                    return string.Empty;
                }

                return Marshal.PtrToStringUni(pv.pointerValue) ?? string.Empty;
            }
            finally
            {
                // ⚠️ 必须用 PropVariantClear（COM 分配），不能用 FreeCoTaskMem。
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
'@

Add-Type -TypeDefinition $cs -Language CSharp

# ---- 写入 ----
try {
    [ShortcutAumid]::Set($ShortcutPath, $Aumid)
}
catch {
    Write-Error "写入 AppUserModelID 失败：$($_.Exception.Message)"
    exit 3
}

Write-Host "已设置 AppUserModelID='$Aumid'  ->  $ShortcutPath"

# ---- 可选：读回校验（§14.3「必须匹配」的机械验证）----
if ($Verify) {
    $readBack = [ShortcutAumid]::Get($ShortcutPath)
    if ($readBack -ne $Aumid) {
        Write-Error "读回校验失败：期望 '$Aumid'，实际 '$readBack'"
        exit 4
    }

    Write-Host "读回校验通过：'$readBack'"
}

exit 0
