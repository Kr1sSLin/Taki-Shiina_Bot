import { randomUUID } from 'node:crypto'
import { TksApiError } from '@shared/errors'
import type { MemoryDetail } from '@shared/protocol'
import type { RestClient } from '../network/rest-client'
import {
  finishPermission, listQueuedPermissions, queuePermission,
  type QueuedPermission
} from '../database/repositories/notification-repository'

export class MemoryManagementService {
  private inFlight: Promise<void> | null = null

  constructor(
    private readonly rest: RestClient,
    private readonly owner: () => string | null,
    private readonly refresh: () => Promise<void>
  ) {}

  pending(): QueuedPermission[] {
    const owner = this.owner()
    return owner ? listQueuedPermissions(owner) : []
  }

  detail(factId: string): Promise<MemoryDetail> {
    return this.rest.request<MemoryDetail>(`memory/facts/${encodeURIComponent(factId)}`)
  }

  async correctFact(factId: string, content: string, expectedRevision: number): Promise<void> {
    if (!this.owner() || !factId || !content.trim() || content.trim().length > 2000 ||
        !Number.isInteger(expectedRevision) || expectedRevision < 1) {
      throw new Error('Invalid memory correction request')
    }
    await this.rest.request(`memory/facts/${encodeURIComponent(factId)}/correction`, {
      method: 'PATCH',
      body: { content: content.trim(), expectedRevision, operationId: randomUUID() }
    })
    await this.refresh()
  }

  async setPermission(factId: string, enabled: boolean, expectedRevision: number): Promise<'applied' | 'pending' | 'conflict'> {
    const owner = this.owner()
    if (!owner || !factId || typeof enabled !== 'boolean' || !Number.isInteger(expectedRevision) || expectedRevision < 1) {
      throw new Error('Invalid memory permission request')
    }
    if (this.pending().some((item) => item.factId === factId && item.state === 'pending')) return 'pending'
    const operationId = randomUUID()
    queuePermission({ operationId, owner, factId, enabled, expectedRevision })
    await this.flush()
    return this.pending().find((item) => item.operationId === operationId)?.state ?? 'applied'
  }

  flush(): Promise<void> {
    if (this.inFlight) return this.inFlight
    this.inFlight = this.doFlush().finally(() => { this.inFlight = null })
    return this.inFlight
  }

  private async doFlush(): Promise<void> {
    const owner = this.owner()
    if (!owner) return
    for (const item of listQueuedPermissions(owner)) {
      if (item.state !== 'pending') continue
      try {
        await this.rest.request(`memory/facts/${encodeURIComponent(item.factId)}/permission`, {
          method: 'PATCH',
          body: { enabled: item.enabled, expectedRevision: item.expectedRevision, operationId: item.operationId }
        })
        finishPermission(item.operationId, 'done')
        await this.refresh()
      } catch (err) {
        if (err instanceof TksApiError && (err.httpStatus === 409 || err.httpStatus === 403)) {
          finishPermission(item.operationId, 'conflict')
          await this.refresh().catch(() => undefined)
          continue
        }
        // Keep the same idempotency ID and expected revision for a later retry.
        break
      }
    }
  }
}
