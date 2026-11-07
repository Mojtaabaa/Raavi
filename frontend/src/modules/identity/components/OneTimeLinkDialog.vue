<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Button from 'primevue/button'
import Message from 'primevue/message'

/** Shows an invitation/setup link exactly once (there is no e-mail delivery yet). */
const props = defineProps<{ link: string | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()
const copied = ref(false)

async function copy() {
  if (!props.link) return
  try {
    await navigator.clipboard.writeText(props.link)
    copied.value = true
  } catch {
    copied.value = false
  }
}
</script>

<template>
  <Dialog :visible="!!link" modal :header="t('members.inviteLinkTitle')" :style="{ inlineSize: 'min(36rem, 95vw)' }" @update:visible="(v) => !v && emit('close')">
    <Message severity="warn" :closable="false">{{ t('members.inviteLinkHint') }}</Message>
    <div class="row">
      <InputText :model-value="link ?? ''" readonly dir="ltr" class="grow" data-testid="one-time-link" @focus="($event.target as HTMLInputElement).select()" />
      <Button :label="copied ? t('common.copied') : t('common.copy')" icon="pi pi-copy" severity="secondary" @click="copy" />
    </div>
    <template #footer>
      <Button :label="t('common.close')" @click="emit('close')" />
    </template>
  </Dialog>
</template>

<style scoped>
.row { display: flex; gap: var(--raavi-space-2); margin-block-start: var(--raavi-space-3); }
.grow { flex: 1; min-inline-size: 0; }
</style>
