<script setup lang="ts">
import {ref,onMounted,onUnmounted} from 'vue'
import {listen} from '@tauri-apps/api/event'
const text=ref('');let stop:(()=>void)|undefined
onMounted(async()=>{document.documentElement.classList.add('hint-window');stop=await listen<string>('control-hint',e=>text.value=e.payload)})
onUnmounted(()=>{stop?.();document.documentElement.classList.remove('hint-window')})
</script>
<template><div class="control-hint"><span>{{text}}</span></div></template>
<style>.control-hint{height:100vh;display:flex;align-items:center;justify-content:center;background:#172d3b;color:white;font-size:15px;font-family:system-ui,sans-serif;margin:0;padding:0 12px}.control-hint span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.hint-window body{margin:0}.hint-window,.hint-window body,.hint-window #app{height:100%;overflow:hidden}</style>
