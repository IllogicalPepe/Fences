Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
exe = root & "\src\FenceDesk.Wpf\bin\Release\net8.0-windows\FenceDesk.exe"
If Not fso.FileExists(exe) Then
  exe = root & "\src\FenceDesk.Wpf\bin\Debug\net8.0-windows\FenceDesk.exe"
End If
If fso.FileExists(exe) Then
  sh.Run """" & exe & """", 0, False
Else
  sh.Run "cmd /c """ & root & "\Start.bat""", 1, False
End If
