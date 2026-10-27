package io.github.garfieldgod.mediarack.ui.feed

import android.util.Log
import java.io.File
import java.util.UUID

/**
 * 二级页（合集 / 收藏）的离线兜底缓存：服务器不可达时显示上次加载的内容，
 * 页面顶部标注「离线」，未缓存到本地的视频打标且不可播放。
 *
 * 为什么不用 Room：这是纯展示层的只读快照，按 (serverId, pageKey) 一文件一份 JSON 最省事，
 * 删服务器时整个目录删掉，不需要 migration。文件名用 key 的确定性 UUID——
 * pageKey 里带 `:` `/` 不能直接当文件名，且可读性对缓存没有价值。
 */
class PageCache(private val root: File) {

    fun put(serverId: Long, key: String, json: String) {
        runCatching {
            dir(serverId).apply { mkdirs() }.let { d ->
                File(d, name(key)).writeText(json)
            }
        }.onFailure { Log.w(TAG, "page cache put failed: ${it.message}") }
    }

    fun get(serverId: Long, key: String): String? =
        runCatching { File(dir(serverId), name(key)).takeIf { it.isFile }?.readText() }
            .getOrNull()

    /** 删服务器配置时调用：缓存里的源内容跟着失效。 */
    fun clear(serverId: Long) {
        runCatching { dir(serverId).deleteRecursively() }
    }

    private fun dir(serverId: Long): File = File(root, serverId.toString())

    private fun name(key: String): String = UUID.nameUUIDFromBytes(key.toByteArray()).toString() + ".json"

    companion object {
        private const val TAG = "MediaRack"

        /** 合集列表快照。 */
        const val KEY_COLLECTIONS = "collections"

        /** 收藏夹列表快照。 */
        const val KEY_FAVORITES = "favorites"

        /** 合集 / 收藏夹的条目页快照：`col:<collectionId>` / `fav:<favoriteId>`。 */
        fun keyItems(collectionId: String? = null, favoriteId: String? = null): String =
            if (favoriteId != null) "fav:$favoriteId" else "col:$collectionId"
    }
}
