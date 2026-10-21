@echo off
rem Builds PowerPaste.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /out:PowerPaste.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Core.dll PowerPaste.cs
