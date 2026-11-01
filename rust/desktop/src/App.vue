<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch, type ObjectDirective } from 'vue'
import * as api from './api'
import HostPanel from './HostPanel.vue'
import EventsPanel from './EventsPanel.vue'
const page=ref<'mapping'|'host'|'events'>('mapping')
const preferencesDialog=ref<HTMLDialogElement>(),preferencesOpen=ref(false),preferenceLanguage=ref('zh'),preferenceNotifications=ref(true),preferenceDirectory=ref('')
import { zh,en,type Key } from './i18n'
const state=ref(api.empty()),draft=ref(api.defaults())
const settingsDialog=ref<HTMLDialogElement>(),settingsOpen=ref(false),settingsTab=ref<'basic'|'monitoring'|'advanced'>('basic')
const ready=ref(false),saving=ref(false),error=ref(''),notice=ref(''),directory=ref(''),user=ref(''),password=ref(''),severity=ref('all'),eventState=ref('all')
const states=['not_configured','checking','healthy','server_offline','mapping_missing','repairing','authentication_failed','share_unavailable','drive_conflict','paused','internal_error','mapping_failed']
const times={lastCheck:'lastCheckAt',lastChange:'lastChangeAt',nextCheck:'nextCheckAt',lastShare:'lastShareAt'} as const
let navigateStop:(()=>void)|undefined
let alive=true,unlisten:(()=>void)|undefined
const showChecking=ref(false),nextCheckAt=ref<number|null>(null)
let checkingTimer:ReturnType<typeof setTimeout>|undefined,manualCheck=false
watch(()=>state.value.busy,busy=>{
  clearTimeout(checkingTimer)
  if(!busy){showChecking.value=false;manualCheck=false;return}
  if(manualCheck){showChecking.value=true;return}
  checkingTimer=setTimeout(()=>{showChecking.value=true},800)
})
const t=(key:Key)=>(draft.value.language==='en'?en:zh)[key]
const message=(v:unknown)=>{const s=String(v);return s in zh?t(s as Key):s}
const dirty=computed(()=>JSON.stringify(draft.value)!==JSON.stringify(state.value.settings)||directory.value!==state.value.dataDirectory)
const controlsLocked=computed(()=>!api.desktop||!ready.value||saving.value)
const locked=computed(()=>controlsLocked.value||state.value.busy)
// Keep background polling from restyling or unfocusing settings controls.
// aria-disabled announces the temporary lock; capture blocks mouse and keyboard clicks.
function blockBusyClick(event:MouseEvent){if((event.currentTarget as HTMLElement).getAttribute('aria-disabled')==='true'){event.preventDefault();event.stopImmediatePropagation()}}
const vBusyLock:ObjectDirective<HTMLElement,boolean>={
  beforeMount(el,{value}){el.setAttribute('aria-disabled',String(value));el.addEventListener('click',blockBusyClick,true)},
  updated(el,{value}){el.setAttribute('aria-disabled',String(value))},
  beforeUnmount(el){el.removeEventListener('click',blockBusyClick,true)}
}
const quietBusy=computed(()=>api.desktop&&ready.value&&!saving.value&&state.value.busy&&!showChecking.value)
const custom=computed(()=>draft.value.profile==='custom')
const mode=computed<Key>(()=>state.value.settings.removeOffline?(state.value.settings.reconnect?'autoMode':'removeMode'):(state.value.settings.reconnect?'reconnectMode':'observeMode'))
const filtered=computed(()=>state.value.events.filter(e=>(severity.value==='all'||e.severity===severity.value)&&(eventState.value==='all'||e.state===eventState.value)))
const lastError=computed(()=>state.value.events.find(e=>e.severity==='error'))
const time=(v:number|null)=>v?new Date(v).toLocaleString(draft.value.language==='en'?'en-US':'zh-CN'):t('none')
const apply=(s:api.Snapshot)=>{if(!s.busy||s.nextCheckAt!==null)nextCheckAt.value=s.nextCheckAt;state.value=s}
async function action(work:()=>Promise<unknown>){error.value='';notice.value='';saving.value=true;try{await work()}catch(e){error.value=String(e)}finally{saving.value=false}}
function openPreferences(){preferenceDirectory.value=state.value.dataDirectory;preferenceLanguage.value=state.value.settings.language;preferenceNotifications.value=state.value.settings.notifications;error.value='';preferencesOpen.value=true;preferencesDialog.value?.showModal()}
async function savePreferences(){await action(async()=>{if(preferenceDirectory.value!==state.value.dataDirectory){apply(await api.relocate(preferenceDirectory.value));directory.value=state.value.dataDirectory}apply(await api.preferences(preferenceLanguage.value,preferenceNotifications.value));draft.value.language=state.value.settings.language;draft.value.notifications=state.value.settings.notifications;preferencesDialog.value?.close()})}
function reset(){draft.value={...state.value.settings};directory.value=state.value.dataDirectory;error.value='';notice.value='';password.value='';user.value=''}
async function save(event:Event){const invalid=(event.target as HTMLFormElement).querySelector<HTMLInputElement>('input:invalid,select:invalid');if(invalid){const tab=invalid.closest<HTMLElement>('[data-pane]')?.dataset.pane;if(tab)settingsTab.value=tab as typeof settingsTab.value;await nextTick();invalid.reportValidity();return}await action(async()=>{apply(await api.save({...draft.value},directory.value));reset();settingsDialog.value?.close();settingsOpen.value=false;notice.value='saved'})}
async function check(){manualCheck=true;await action(async()=>apply(await api.check()));if(error.value)manualCheck=false}
async function pause(){await action(async()=>apply(await api.pause(!state.value.paused)))}
async function credentials(remove:boolean){await action(async()=>{try{apply(await api.credential(user.value,password.value,remove));notice.value=remove?'credential_deleted':'credential_saved'}finally{password.value='';user.value=''}})}
async function startup(enabled:boolean){await action(async()=>apply(await api.startup(enabled)))}
async function browsePreferences(){await action(async()=>{const selected=await api.directory();if(selected)preferenceDirectory.value=selected})}
async function browse(){await action(async()=>{const selected=await api.directory();if(selected)directory.value=selected})}
async function openFolder(target:'data'|'mapping'){await action(()=>api.openFolder(target))}
async function diagnostics(){await action(async()=>{if(await api.diagnostics())notice.value='exported'})}
function openSettings(){if(settingsOpen.value)return;reset();settingsTab.value='basic';settingsOpen.value=true;settingsDialog.value?.showModal()}
function closeSettings(){if(saving.value)return;reset();settingsDialog.value?.close();settingsOpen.value=false}
function invalidField(event:Event){const tab=(event.target as HTMLElement).closest<HTMLElement>('[data-pane]')?.dataset.pane;if(tab)settingsTab.value=tab as typeof settingsTab.value}
function changeTab(event:KeyboardEvent){const tabs=['basic','monitoring','advanced'] as const;let index=tabs.indexOf(settingsTab.value);if(event.key==='ArrowRight')index=(index+1)%3;else if(event.key==='ArrowLeft')index=(index+2)%3;else if(event.key==='Home')index=0;else if(event.key==='End')index=2;else return;event.preventDefault();settingsTab.value=tabs[index]!;settingsDialog.value?.querySelector<HTMLButtonElement>(`#tab-${settingsTab.value}`)?.focus()}
function setProfile(){const intervals:Record<string,number>={standard:5,fast:2,energy:30};if(draft.value.profile in intervals)Object.assign(draft.value,{intervalSecs:intervals[draft.value.profile],threshold:3,tcpTimeoutMs:800,overallTimeoutMs:10000})}
watch(()=>draft.value.language,lang=>document.documentElement.lang=lang==='en'?'en':'zh-CN')
onMounted(async()=>{try{const stop=await api.subscribe(apply);if(!alive){stop();return}unlisten=stop;navigateStop=await api.navigation(openSettings);apply(await api.snapshot());reset();if(!state.value.configured&&api.desktop)page.value='host'}catch(e){error.value=String(e)}finally{ready.value=true}})
onUnmounted(()=>{alive=false;clearTimeout(checkingTimer);unlisten?.();navigateStop?.();password.value='';user.value=''})
</script>
<template>
<main class="monitor-window">
  <header class="toolbar"><h1>NetDriveGuard</h1><button :disabled="!ready||saving" @click="page==='mapping'?openSettings():openPreferences()">{{page!=='mapping'?(draft.language==='en'?'Settings':'设置'):t('settings')}}</button></header>
  <nav class="feature-tabs"><button :aria-current="page==='mapping'?'page':undefined" @click="page='mapping'">{{draft.language==='en'?'Network drive':'网络盘'}}</button><button :aria-current="page==='host'?'page':undefined" @click="page='host'">{{draft.language==='en'?'Development host':'开发主机'}}</button><button :aria-current="page==='events'?'page':undefined" @click="page='events'">{{t('events')}}</button></nav>
  <EventsPanel v-if="page==='events'" :language="draft.language"/>
  <div v-show="page==='host'" class="host-container"><HostPanel :language="draft.language"/></div>
  <template v-if="page==='mapping'">
  <div v-if="!api.desktop" class="banner preview" role="status">{{t('preview')}}</div>
  <div v-if="!settingsOpen&&(error||state.error||state.storageError)" class="banner error" role="alert">{{message(error||state.error||state.storageError)}}</div>
  <div v-if="!settingsOpen&&notice" class="banner success" role="status">{{message(notice)}}</div>
  <section class="status-summary" :aria-label="t('overview')">
    <dl>
      <dt>{{t('currentState')}}</dt><dd class="current-state"><strong :class="'state-'+state.state" aria-live="polite">{{message(state.state)}}</strong><span class="muted checking-indicator" :class="{visible:showChecking}" :aria-hidden="!showChecking">{{t('checking')}}…</span></dd>
      <dt>{{t('target')}}</dt><dd class="target"><template v-if="state.configured"><b>{{state.settings.drive}}</b><code>{{state.settings.share}}</code></template><span v-else>{{t('noTarget')}}</span></dd>
      <dt>{{t('mode')}}</dt><dd>{{t(mode)}}</dd>
      <template v-for="(key,label) in times" :key="label"><dt>{{t(label)}}</dt><dd>{{time(key==='nextCheckAt'&&!showChecking?nextCheckAt:state[key])}}</dd></template>
      <dt>{{t('lastError')}}</dt><dd class="last-error">{{lastError?message(lastError.code):t('none')}}<template v-if="lastError?.nativeCode"> · {{t('native')}} {{lastError.nativeCode}}</template></dd>
    </dl>
  </section>
  <section class="events-section" :aria-label="t('events')">
    <div class="events-heading"><h2>{{t('events')}}</h2><span class="muted">{{filtered.length}} / {{state.events.length}}</span><button :disabled="!api.desktop||saving" @click="diagnostics">{{t('diagnostics')}}</button></div>
    <div class="event-filters"><label>{{t('severity')}}<select :aria-label="t('severity')" v-model="severity"><option v-for="level in ['all','info','warning','error']" :key="level" :value="level">{{message(level)}}</option></select></label><label>{{t('stateFilter')}}<select :aria-label="t('stateFilter')" v-model="eventState"><option value="all">{{t('all')}}</option><option v-for="s in states" :key="s" :value="s">{{message(s)}}</option></select></label></div>
    <div class="event-scroll" tabindex="0" :aria-label="t('events')"><table><thead><tr><th>{{t('eventTime')}}</th><th>{{t('severity')}}</th><th>{{t('stateFilter')}}</th><th>{{t('eventMessage')}}</th></tr></thead><tbody><tr v-for="event in filtered" :key="event.id"><td class="event-time">{{time(event.atMs)}}</td><td :class="'severity-'+event.severity">{{message(event.severity)}}</td><td>{{message(event.state)}}</td><td>{{message(event.code)}}<template v-if="event.nativeCode!=null"> · {{event.nativeCode}}</template></td></tr><tr v-if="!filtered.length"><td colspan="4" class="empty">{{t('emptyEvents')}}</td></tr></tbody></table></div>
  </section>
  <footer class="main-actions"><button class="primary" :class="{'quiet-disabled':quietBusy&&state.configured&&!state.paused}" :disabled="locked||!state.configured||state.paused" @click="check">{{t('inspect')}}</button><button :disabled="!api.desktop||saving||!state.configured" @click="pause">{{t(state.paused?'resume':'pause')}}</button><button :class="{'quiet-disabled':quietBusy&&state.state==='healthy'}" :disabled="locked||state.state!=='healthy'" @click="openFolder('mapping')">{{t('openMapping')}}</button><span class="spacer"></span><button :disabled="!api.desktop" @click="action(api.hide)">{{t('hide')}}</button><button :disabled="!api.desktop" @click="action(api.quit)">{{t('quit')}}</button></footer>
</template>
</main>
<dialog ref="preferencesDialog" class="settings-dialog preferences-dialog" @close="preferencesOpen=false"><form v-if="preferencesOpen" @submit.prevent="savePreferences"><header class="dialog-heading"><h2>{{draft.language==='en'?'Settings':'设置'}}</h2><button type="button" :aria-label="t('close')" @click="preferencesDialog?.close()">×</button></header><fieldset :disabled="saving"><label class="field-row">{{t('language')}}<select v-model="preferenceLanguage"><option value="zh">简体中文</option><option value="en">English</option></select></label><div class="options"><label><input type="checkbox" v-model="preferenceNotifications">{{t('notifications')}}</label><label><input type="checkbox" :checked="state.startup.state==='enabled'" :disabled="state.busy" @change="startup(($event.target as HTMLInputElement).checked)">{{t('startup')}}</label></div><label class="stack-field">{{t('dataDirectory')}}<input v-model="preferenceDirectory" autocomplete="off" spellcheck="false"></label><div class="row-actions"><button type="button" @click="browsePreferences">{{t('browse')}}</button><button type="button" @click="openFolder('data')">{{t('openData')}}</button></div><template v-if="state.startup.needsUpdate"><p class="hint">{{t('startupLocationNote')}}</p><button type="button" :disabled="state.busy" @click="startup(true)">{{t('startupUpdate')}}</button></template></fieldset><div v-if="error||state.startup.error" class="banner error">{{message(error||state.startup.error)}}</div><footer class="dialog-actions"><button type="button" @click="preferencesDialog?.close()">{{t('close')}}</button><button class="primary" :disabled="saving">{{t('save')}}</button></footer></form></dialog>
<dialog ref="settingsDialog" class="settings-dialog" aria-labelledby="settings-title" @cancel.prevent="closeSettings" @close="settingsOpen=false">
  <form novalidate @submit.prevent="save" @invalid.capture="invalidField">
    <header class="dialog-heading"><h2 id="settings-title">{{t('settings')}}</h2><button type="button" :aria-label="t('close')" :disabled="saving" @click="closeSettings">×</button></header>
    <div class="tabs" role="tablist" :aria-label="t('settings')" @keydown="changeTab"><button v-for="tab in (['basic','monitoring','advanced'] as const)" :id="'tab-'+tab" :key="tab" type="button" role="tab" :aria-selected="settingsTab===tab" :aria-controls="'pane-'+tab" :tabindex="settingsTab===tab?0:-1" @click="settingsTab=tab">{{t(tab==='basic'?'basicSettings':tab==='monitoring'?'monitoringSettings':'advanced')}}</button></div>
    <div class="settings-content">
      <fieldset :disabled="saving||!ready">
        <section v-show="settingsTab==='basic'" id="pane-basic" data-pane="basic" role="tabpanel" aria-labelledby="tab-basic">
          <label class="field-row">{{t('drive')}}<select :aria-label="t('drive')" v-model="draft.drive" class="drive-select" autofocus><option v-for="letter in 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'" :key="letter" :value="letter+':'">{{letter}}:</option></select></label>
          <label class="field-row">{{t('share')}}<input v-model="draft.share" required placeholder="\\server\share" autocomplete="off" spellcheck="false"></label>
          <div class="options"><label><input v-model="draft.removeOffline" type="checkbox">{{t('remove')}}</label><label><input v-model="draft.reconnect" type="checkbox">{{t('reconnect')}}</label><label><input type="checkbox" :checked="state.startup.state==='enabled'" v-busy-lock="state.busy" :disabled="controlsLocked" @change="startup(($event.target as HTMLInputElement).checked)">{{t('startup')}}</label><label><input v-model="draft.notifications" type="checkbox">{{t('notifications')}}</label></div>

          <div v-if="state.startup.error" class="banner error">{{message(state.startup.error)}}</div><p v-if="state.startup.needsUpdate" class="hint">{{t('startupLocationNote')}}</p><button v-if="state.startup.needsUpdate" type="button" v-busy-lock="state.busy" :disabled="controlsLocked" @click="startup(true)">{{t('startupUpdate')}}</button>
        </section>
        <section v-show="settingsTab==='monitoring'" id="pane-monitoring" data-pane="monitoring" role="tabpanel" aria-labelledby="tab-monitoring">
          <label class="field-row">{{t('profile')}}<select :aria-label="t('profile')" v-model="draft.profile" @change="setProfile"><option v-for="mode in (['standard','fast','energy','custom'] as const)" :key="mode" :value="mode">{{t(mode)}}</option></select></label>
          <label class="field-row">{{t('interval')}}<input v-model.number="draft.intervalSecs" type="number" min="1" max="1800" required :disabled="!custom"></label>
          <label class="field-row">{{t('threshold')}}<input v-model.number="draft.threshold" type="number" min="2" max="10" required :disabled="!custom"></label>
          <label class="field-row">{{t('tcp')}}<input v-model.number="draft.tcpTimeoutMs" type="number" min="100" max="10000" required :disabled="!custom"></label>
          <label class="field-row">{{t('total')}}<input v-model.number="draft.overallTimeoutMs" type="number" :min="draft.tcpTimeoutMs+1" max="120000" required :disabled="!custom"></label>

        </section>
        <section v-show="settingsTab==='advanced'" id="pane-advanced" data-pane="advanced" role="tabpanel" aria-labelledby="tab-advanced">
          <h3>{{t('credentials')}}</h3><p class="hint">{{t(state.credentialSaved?'credentialPresent':'credentialAbsent')}}</p>
          <label class="field-row">{{t('user')}}<input v-model="user" autocomplete="off" :disabled="!state.configured||dirty"></label><label class="field-row">{{t('password')}}<input v-model="password" type="password" autocomplete="new-password" :disabled="!state.configured||dirty"></label>
          <div class="row-actions"><button type="button" v-busy-lock="state.busy" :disabled="controlsLocked||dirty||!state.configured||!user.trim()" @click="credentials(false)">{{t('credentialSave')}}</button><button type="button" v-busy-lock="state.busy" :disabled="controlsLocked||dirty||!state.credentialSaved" @click="credentials(true)">{{t('credentialDelete')}}</button></div><p v-if="dirty" class="hint">{{t('saveFirst')}}</p>
          <hr><label class="stack-field">{{t('dataDirectory')}}<input v-model="directory" autocomplete="off" spellcheck="false"></label><div class="row-actions"><button type="button" v-busy-lock="state.busy" :disabled="controlsLocked" @click="browse">{{t('browse')}}</button><button type="button" v-busy-lock="state.busy" :disabled="controlsLocked" @click="openFolder('data')">{{t('openData')}}</button></div><p class="hint">{{t('storageNote')}}</p>
          <label class="field-row">{{t('language')}}<select :aria-label="t('language')" v-model="draft.language"><option value="zh">简体中文</option><option value="en">English</option></select></label>
        </section>
      </fieldset>
    </div>
    <div v-if="settingsOpen&&(error||state.error||state.storageError)" class="banner error" role="alert">{{message(error||state.error||state.storageError)}}</div><div v-if="settingsOpen&&notice" class="banner success" role="status">{{message(notice)}}</div>
    <footer class="dialog-actions"><span class="muted">{{dirty?t('dirty'):''}}</span><button type="button" :disabled="saving" @click="closeSettings">{{t('close')}}</button><button class="primary" type="submit" v-busy-lock="state.busy" :disabled="controlsLocked||!dirty">{{t(saving?'saving':'save')}}</button></footer>
  </form>
</dialog>
</template>

<style scoped>.preferences-dialog{height:fit-content}.preferences-dialog form{height:auto}.preferences-dialog fieldset{border:0;margin:0;padding:14px;background:white}.preferences-dialog .dialog-actions{justify-content:flex-end}.host-container{display:flex;flex:1;min-height:0;flex-direction:column}.feature-tabs{display:flex;gap:8px}.feature-tabs [aria-current="page"]{border-color:#238b86;font-weight:600}</style>
