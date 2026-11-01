<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import * as api from './api'
import { messages } from './host-api'
import { zh, en, type Key } from './i18n'
const props=defineProps<{language:string}>()
const events=ref<api.Event[]>([]),source=ref('all'),severity=ref('all'),error=ref('')
const t=(key:Key)=>(props.language==='en'?en:zh)[key]
const label=(value:string)=>messages[value]?.[props.language==='en'?1:0]??(value in zh?t(value as Key):value)
const sources:Record<string,[string,string]>={all:['全部','All'],mapping:['网络盘','Network drive'],host:['主机检测','Host checks'],remote:['远程连接','Remote connection']}
const sourceLabel=(value:string)=>sources[value]?.[props.language==='en'?1:0]??value
const filtered=computed(()=>events.value.filter(e=>(source.value==='all'||(e.source||'mapping')===source.value)&&(severity.value==='all'||e.severity===severity.value)))
let alive=true,timer:ReturnType<typeof setTimeout>|undefined
async function refresh(){try{const data=await api.events();if(alive){events.value=data;error.value=''}}catch(e){if(alive)error.value=String(e)}finally{if(alive)timer=setTimeout(refresh,1500)}}
async function diagnostics(){try{await api.diagnostics()}catch(e){error.value=String(e)}}
onMounted(refresh)
onUnmounted(()=>{alive=false;clearTimeout(timer)})
</script>
<template>
<section class="events-section" :aria-label="t('events')">
  <div class="events-heading"><h2>{{t('events')}}</h2><span class="muted">{{filtered.length}} / {{events.length}}</span><button :disabled="!api.desktop" @click="diagnostics">{{t('diagnostics')}}</button></div>
  <div v-if="error" class="banner error" role="alert">{{label(error)}}</div>
  <div class="event-filters"><label>{{language==='en'?'Source':'来源'}}<select v-model="source" :aria-label="language==='en'?'Source':'来源'"><option v-for="(_,key) in sources" :key="key" :value="key">{{sourceLabel(key)}}</option></select></label><label>{{t('severity')}}<select v-model="severity" :aria-label="t('severity')"><option v-for="level in ['all','info','warning','error']" :key="level" :value="level">{{label(level)}}</option></select></label></div>
  <div class="event-scroll" tabindex="0" :aria-label="t('events')"><table><thead><tr><th>{{t('eventTime')}}</th><th>{{t('severity')}}</th><th>{{language==='en'?'Source':'来源'}}</th><th>{{t('eventMessage')}}</th></tr></thead><tbody><tr v-for="(event,index) in filtered" :key="`${event.source}-${event.id}-${event.atMs}-${event.code}-${index}`"><td>{{new Date(event.atMs).toLocaleString(language==='en'?'en-US':'zh-CN')}}</td><td :class="'severity-'+event.severity">{{label(event.severity)}}</td><td>{{sourceLabel(event.source||'mapping')}}</td><td>{{label(event.code)}}<template v-if="event.nativeCode!=null"> · {{event.nativeCode}}</template></td></tr><tr v-if="!filtered.length"><td colspan="4" class="empty">{{t('emptyEvents')}}</td></tr></tbody></table></div>
</section>
</template>
