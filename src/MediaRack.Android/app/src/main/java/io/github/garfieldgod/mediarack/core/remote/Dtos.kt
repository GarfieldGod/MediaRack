package io.github.garfieldgod.mediarack.core.remote

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * 契约 DTO（contracts/openapi.v1.yaml v1）。字段名与契约一一对应，缺的按 nullable 处理，
 * 服务端将来加字段不炸客户端。
 */
@Serializable
data class MediaPage(
    val total: Int,
    val items: List<MediaSummary> = emptyList(),
    val nextOffset: Int? = null,
)

@Serializable
data class MediaSummary(
    val key: String,
    /** 单源视图为实际源；聚合视图为 preferred 源 */
    val sourceId: String,
    /** dual / single */
    val layout: String,
    val title: String,
    val upName: String? = null,
    val durationMs: Long = 0,
    val width: Int = 0,
    val height: Int = 0,
    val sizeBytes: Long = 0,
    val tagCode: Int? = null,
    val posterUrl: String? = null,
    val hasDanmaku: Boolean = false,
    val hidden: Boolean = false,
    val playPosMs: Long = 0,
    /**
     * 用户覆盖的旋转角（0/90/180/270），跟视频信息走、跨端一致。
     * 注意：容器自带的 rotation 元数据已被解码器自动应用，这个值只是"额外再转"。
     */
    val rotation: Int = 0,
    /** ok / missing / corrupt */
    val status: String,
    /** 仅聚合视图返回 */
    val sources: List<String>? = null,
    val preferred: String? = null,
    /** 所属合集（v4 排他归属）：folder=目录合集 / up=UP 主合集 */
    val collectionId: String = "",
    val collectionName: String = "",
    val collectionKind: String = "",
)

@Serializable
data class MediaDetail(
    val key: String,
    val sourceId: String,
    val layout: String,
    val title: String,
    val upName: String? = null,
    val durationMs: Long = 0,
    val width: Int = 0,
    val height: Int = 0,
    val sizeBytes: Long = 0,
    val tagCode: Int? = null,
    val posterUrl: String? = null,
    val hasDanmaku: Boolean = false,
    val hidden: Boolean = false,
    val playPosMs: Long = 0,
    val status: String,
    val sources: List<String>? = null,
    val preferred: String? = null,
    val relPath: String? = null,
    val relDir: String? = null,
    val codec: String? = null,
    val tracks: List<Track> = emptyList(),
    /** 当前设备实际可用的交付模式：direct / remux / transcode */
    val delivery: List<String> = emptyList(),
    val meta: MediaMeta? = null,
    val collectionId: String = "",
    val collectionName: String = "",
    val collectionKind: String = "",
)

@Serializable
data class Track(
    /** video / audio */
    val role: String,
    val codecid: Int? = null,
    val bandwidth: Long? = null,
    val sizeBytes: Long? = null,
    val md5: String? = null,
    /** 客户端可直接使用的地址（相对路径，single=/file，dual=/video.m4s 或 /audio.m4s） */
    val url: String? = null,
)

@Serializable
data class MediaMeta(
    val titleDisplay: String? = null,
    val tags: List<String> = emptyList(),
    val hidden: Boolean = false,
    val rotation: Int = 0,
    val playPosMs: Long = 0,
    val playCount: Int = 0,
    val lastPlayAt: Long = 0,
    val rev: Int = 0,
)

@Serializable
data class AuthState(
    val authRequired: Boolean,
    val authenticated: Boolean,
)

@Serializable
data class SourceInfo(
    val id: String,
    val name: String,
    val layout: String = "single",
    val virtualWrite: String? = null,
    val enabled: Boolean = true,
    val reachable: Boolean = true,
    val itemCount: Int = 0,
    val lastScanAt: Long? = null,
)

@Serializable
data class Health(
    val status: String = "ok",
    val version: String? = null,
    val schemaVersion: Int? = null,
    val sources: List<HealthSource> = emptyList(),
)

@Serializable
data class HealthSource(
    val id: String,
    val reachable: Boolean = true,
    val itemCount: Int = 0,
)

/**
 * 结构合集（v4）：源级、平铺、只读。kind=folder 是 single 目录，kind=up 是 dual UP 主。
 * [path] 是相对源的目录（folder）或 up_mid（up），[name] 只是展示名。
 */
@Serializable
data class RemoteCollection(
    val id: String,
    val name: String,
    val sourceId: String,
    /** folder / up */
    val kind: String = "folder",
    val path: String = "",
    val itemCount: Int = 0,
)

/** 收藏夹（用户层，跨端）。[itemCount] 收藏的视频数，[collectionCount] 收藏的合集数。 */
@Serializable
data class Favorite(
    val id: String,
    val name: String,
    val itemCount: Int = 0,
    val collectionCount: Int = 0,
)

@Serializable
data class ItemRefBody(val source: String, val key: String)

@Serializable
data class FavoriteItemsPatch(
    val add: List<ItemRefBody>? = null,
    val remove: List<ItemRefBody>? = null,
)

@Serializable
data class FavoriteCollectionsPatch(
    val add: List<String>? = null,
    val remove: List<String>? = null,
)

@Serializable
data class NamePatch(val name: String)

@Serializable
data class ProgressPatch(val posMs: Long, val completed: Boolean? = null)

/**
 * 虚拟元数据补丁（POST /api/v1/meta/{source}/{key}）。
 * 服务端语义：JSON 里为 null = 不改这个字段。
 */
@Serializable
data class MediaPatchBody(
    val rotation: Int? = null,
    /** 整体替换该条目的收藏夹归属（"移动到某夹"用它一步完成） */
    val collections: List<String>? = null,
)

@Serializable
data class ChangeCount(val added: Int = 0, val removed: Int = 0)
