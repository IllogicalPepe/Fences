' Silent launcher - no console window (STA for WPF + WinForms tray)
Set sh = CreateObject("WScript.Shell")
dir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
ps = "powershell.exe"
args = "-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & dir & "\FenceDesk.ps1"""
sh.Run ps & " " & args, 0, False
