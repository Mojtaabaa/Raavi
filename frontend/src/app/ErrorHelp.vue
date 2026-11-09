<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import Message from 'primevue/message'
import { errorMessage, errorSteps } from '@/core/api/errors'
import HelpLink from '@/app/HelpLink.vue'

/**
 * An error with guidance: what went wrong, what the user can try, and the link to the guide. Shown inline where the problem happened,
 * for the errors that have steps (errorHelp.<code>.n in the messages); other errors show just the message.
 */
const props = defineProps<{ error: unknown }>()
const { t, te } = useI18n()
const steps = computed(() => errorSteps(props.error, t, te))
const message = computed(() => (props.error ? errorMessage(props.error, t, te) : ''))
</script>

<template>
  <Message v-if="error" severity="error" :closable="false" class="error-help" data-testid="error-help">
    <div class="body">
      <strong>{{ message }}</strong>
      <template v-if="steps.length">
        <p class="try">{{ t('errorHelp.whatToDo') }}</p>
        <ol>
          <li v-for="(s, i) in steps" :key="i">{{ s }}</li>
        </ol>
        <HelpLink />
      </template>
    </div>
  </Message>
</template>

<style scoped>
.body { display: grid; gap: var(--raavi-space-1); }
.try { margin: var(--raavi-space-1) 0 0; font-weight: 600; }
ol { margin: 0; padding-inline-start: 1.4rem; display: grid; gap: var(--raavi-space-1); }
</style>
