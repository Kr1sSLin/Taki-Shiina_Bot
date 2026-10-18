package com.krisslin.androidaiassistant.core.network.api

import com.krisslin.androidaiassistant.core.database.entity.MemoryPermissionOperationEntity
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import retrofit2.HttpException
import kotlinx.coroutines.CancellationException
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/** Pending changes remain local until acknowledged by the server. Conflict never overwrites it. */
@Singleton
class MemoryPermissionSync @Inject constructor(
    private val api: ChatApi,
    private val facts: UserFactRepository,
    private val auth: AuthRepository
) {
    suspend fun queue(factId: String, userId: String, enabled: Boolean, expectedRevision: Long): Boolean {
        if (facts.latestPermissionOperation(factId)?.state == "pending") return false
        facts.enqueuePermission(MemoryPermissionOperationEntity(
            UUID.randomUUID().toString(), userId, factId, enabled, expectedRevision
        ))
        return true
    }

    suspend fun state(factId: String): String? = facts.latestPermissionOperation(factId)?.state

    suspend fun flush() {
        val currentUserId = auth.getUserId() ?: return
        for (operation in facts.pendingPermissions()) {
            if (operation.userId != currentUserId) continue
            try {
                api.setMemoryPermission(operation.factId, MemoryPermissionHttpRequest(
                    operation.enabled, operation.expectedRevision, operation.operationId
                ))
                facts.markPermissionOperation(operation.operationId, "applied")
            } catch (error: HttpException) {
                if (error.code() == 409 || error.code() == 403) {
                    facts.markPermissionOperation(operation.operationId, "conflict")
                } else {
                    return // Auth or server failure: keep the same operation ID for retry.
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                return
            }
        }
    }
}
