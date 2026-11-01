import { createApp } from 'vue'
import App from './App.vue'
import Hint from './Hint.vue'
import './style.css'
createApp(new URLSearchParams(location.search).has('hint')?Hint:App).mount('#app')
