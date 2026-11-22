$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:"$PSScriptRoot\glint.ico" /r:System.Web.Extensions.dll /r:System.Security.dll /out:"$PSScriptRoot\Glint.exe" "$PSScriptRoot\Glint.cs" "$PSScriptRoot\Md.cs" "$PSScriptRoot\ChatForm.cs"
