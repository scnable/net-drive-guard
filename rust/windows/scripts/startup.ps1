$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::InputEncoding=[Text.Encoding]::UTF8
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
try {
 $request=[Console]::In.ReadToEnd() | ConvertFrom-Json
 $name='NetDriveGuard'
 # Test names are enabled only by the Debug worker before this script is called.
 if($request.testName){$name=$request.testName}
 $marker='NetDriveGuard.ManagedStartup.v1'
 $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $service=New-Object -ComObject 'Schedule.Service';$service.Connect();$folder=$service.GetFolder('\')
 $task=$null
 try { $task=$folder.GetTask($name) } catch { if(($_.Exception.HResult -band 0xFFFF) -ne 2){throw} }
 $state='disabled';$needsUpdate=$false
 if($task){
  [xml]$xml=$task.Xml
  $action=@($xml.Task.Actions.ChildNodes)
  if($xml.Task.RegistrationInfo.Description -ne $marker -or $action.Count -ne 1 -or $action[0].Name -ne 'Exec' -or $xml.Task.Principals.Principal.UserId -ne $sid){throw 'startup_conflict'}
  $command=[string]$action[0].Command
  if([IO.Path]::GetFileName($command) -notin @('NetDriveGuard.exe','NetDriveGuard.App.exe','net-drive-guard-desktop.exe') -or [string]$action[0].Arguments -notin @('','--background')){throw 'startup_conflict'}
  if($task.Enabled){$state='enabled'}
  $needsUpdate=$command -ine $request.exe
 }
 if($request.action -eq 'enable'){
  $path=[Security.SecurityElement]::Escape($request.exe)
  $body=@"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<RegistrationInfo><Description>$marker</Description></RegistrationInfo>
<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$sid</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
<Principals><Principal id="Author"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
<Actions Context="Author"><Exec><Command>$path</Command><Arguments>--background</Arguments></Exec></Actions>
</Task>
"@
  $folder.RegisterTask($name,$body,6,$null,$null,3,$null) | Out-Null
  $state='enabled';$needsUpdate=$false
 } elseif($request.action -eq 'disable') {if($task){$folder.DeleteTask($name,0)};$state='disabled';$needsUpdate=$false}
 @{state=$state;needsUpdate=$needsUpdate} | ConvertTo-Json -Compress
} catch {
 $code=if($_.Exception.Message -like '*startup_conflict*'){'startup_conflict'}else{'startup_failed'}
 @{state='error';error=$code;needsUpdate=$false} | ConvertTo-Json -Compress
 exit 1
}
