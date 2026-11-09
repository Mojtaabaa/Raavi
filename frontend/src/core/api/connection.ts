import { ref } from 'vue'

/**
 * Whether the browser can reach the API right now, as seen by real requests: a network failure or a gateway error (502/503/504) marks it down,
 * any answer from the API marks it up again. Plain module state (no store), so the HTTP layer can use it without an active Pinia.
 */
export const connection = {
  down: ref(false),
  /** Set when the connection came back, until the banner has said so. */
  restored: ref(false),
  since: ref<number | null>(null),
}

export function markDown(): void {
  if (!connection.down.value) {
    connection.down.value = true
    connection.restored.value = false
    connection.since.value = Date.now()
  }
}

export function markUp(): void {
  if (connection.down.value) {
    connection.down.value = false
    connection.restored.value = true
    connection.since.value = null
    setTimeout(() => (connection.restored.value = false), 6000)
  }
}

export const isGatewayStatus = (status: number): boolean => status === 502 || status === 503 || status === 504
