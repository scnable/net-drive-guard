import {invoke} from '@tauri-apps/api/core'
import {listen} from '@tauri-apps/api/event'
import {desktop} from './api'
export interface HostSettings {name:string;address:string;sshPort:number;rdpPort:number;smbPort:number;intervalSecs:number;arrangement:string;monitor:string;display:string;repositionOnActivate:boolean;switchHint:boolean;connectOnStart:boolean;connectOnRecovery:boolean}
export interface HostSnapshot {settings:HostSettings;configured:boolean;paused:boolean;busy:boolean;connecting:boolean;services:{port:number;reachable:boolean|null;failures:number}[];lastCheckAt:number|null;connection:string;connectionCode?:number|null;error:string|null;probeError?:string|null;events:{atMs:number;code:string}[]}
export interface Monitor {id:string;name:string;primary:boolean;x:number;y:number;width:number;height:number}
export const defaults=():HostSettings=>({name:'Linux 开发机',address:'',sshPort:22,rdpPort:3389,smbPort:445,intervalSecs:10,arrangement:'current',monitor:'primary',display:'maximized',repositionOnActivate:false,switchHint:true,connectOnStart:false,connectOnRecovery:false})
export const empty=():HostSnapshot=>({settings:defaults(),configured:false,paused:false,busy:false,connecting:false,services:[],lastCheckAt:null,connection:'closed',error:null,events:[]})
export const snapshot=()=>desktop?invoke<HostSnapshot>('host_snapshot'):Promise.resolve(empty())
export const monitors=()=>desktop?invoke<Monitor[]>('host_monitors'):Promise.resolve([])
export const save=(settings:HostSettings)=>invoke<HostSnapshot>('host_save',{settings})
export const check=()=>invoke('host_check')
export const pause=(paused:boolean)=>invoke<HostSnapshot>('host_pause',{paused})
export const connect=()=>invoke('host_connect')
export const subscribe=(callback:(s:HostSnapshot)=>void)=>desktop?listen<HostSnapshot>('host-changed',e=>callback(e.payload)):Promise.resolve(()=>{})
export const messages:Record<string,[string,string]>={
 rdp_connected:['远程会话已连接','Remote session connected'],rdp_disconnected:['远程会话已断开','Remote session disconnected'],rdp_reconnecting:['正在恢复远程连接','Reconnecting remote session'],rdp_connection_failed:['远程连接失败','Remote connection failed'],
 display_changed:['已按显示器变化调整远程窗口','Remote window adjusted to display changes'],
 rdp_user_action_required:['请在远程桌面窗口完成确认或登录','Complete confirmation or sign-in in the Remote Desktop window'],
 host_log_failed:['连接记录保存失败','Could not save connection events'],
 host_address_invalid:['请输入主机名或 IP 地址，端口单独填写','Enter a host name or IP address; enter the port separately'],host_settings_invalid:['请检查主机设置','Check host settings'],host_config_invalid:['主机配置无法读取','Unable to read host settings'],host_operation_busy:['正在安排远程窗口','Arranging the remote window'],host_probe_failed:['主机检查失败','Host check failed'],rdp_unreachable:['远程桌面端口无响应','Remote Desktop port is not responding'],rdp_file_failed:['无法保存连接设置','Cannot save connection settings'],rdp_start_failed:['无法打开远程桌面','Cannot open Remote Desktop'],rdp_window_timeout:['等待远程窗口超时','Timed out waiting for the remote window'],rdp_window_closed:['远程窗口已关闭','Remote window closed'],rdp_window_open:['远程连接窗口已打开','Remote connection window opened'],rdp_move_failed:['窗口移动失败','Could not move the window'],rdp_activate_failed:['请点击远程窗口后重试','Click the remote window and retry'],rdp_keys_busy:['请松开组合键后重试','Release modifier keys and retry'],rdp_keys_failed:['全屏切换失败','Could not switch full screen'],virtual_desktop_unavailable:['虚拟桌面安排失败，已保留远程窗口','Virtual desktop arrangement failed; the remote window is retained'],virtual_desktop_move_failed:['无法将远程窗口移入目标桌面','Could not move the remote window to the target desktop'],virtual_desktop_switch_failed:['无法切换到开发桌面','Could not switch to the development desktop'],monitor_fallback:['目标显示器未连接，已使用主屏','Target display unavailable; using the primary display'],monitor_unavailable:['未找到可用显示器','No display available'],host_unreachable:['已配置的服务均无响应','No configured service is responding'],host_recovered:['开发主机恢复响应','Development host is responding again'],worker_timeout:['检查超时','Check timed out'],worker_failed:['检查失败','Check failed'],config_write_failed:['设置保存失败','Could not save settings']
}
