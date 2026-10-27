package io.github.garfieldgod.mediarack.core.db

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * 源。本地源 = App 私有目录下的子目录；网络源复用本表并用 `kind` 区分，
 * 字段命名与服务端 library.db 对齐（snake_case）。
 */
@Entity(tableName = "sources")
data class SourceEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val name: String,
    @ColumnInfo(name = "root_path") val rootPath: String,
    @ColumnInfo(name = "created_at") val createdAt: Long,
)

/**
 * 条目。复合主键 `(source_id, key)` 与服务端一致——源独立，去重只在聚合视图做。
 *
 * key 由 [io.github.garfieldgod.mediarack.core.ContentKeys] 生成，跨端必须一致。
 */
@Entity(
    tableName = "items",
    primaryKeys = ["source_id", "key"],
    indices = [
        Index("source_id"),
        Index(value = ["source_id", "collection_dir"]),
    ],
)
data class ItemEntity(
    @ColumnInfo(name = "source_id") val sourceId: Long,
    val key: String,
    val title: String,
    @ColumnInfo(name = "rel_path") val relPath: String,
    @ColumnInfo(name = "abs_path") val absPath: String,
    val size: Long,
    /** 0 字节等坏文件为 0。 */
    @ColumnInfo(name = "duration_ms") val durationMs: Long,
    val width: Int,
    val height: Int,
    /** 所在子目录的相对路径；null = 源根目录散落文件（对应服务端"根合集"）。 */
    @ColumnInfo(name = "collection_dir") val collectionDir: String?,
    @ColumnInfo(name = "has_poster") val hasPoster: Boolean,
    /** ok / corrupt。single 源无完整性契约，不判中间态（对齐 PC 端）。 */
    val status: String,
    /**
     * 用户覆盖的旋转角（0/90/180/270）。本地条目没有服务端可存，就存在这儿——
     * 语义仍是"跟视频信息走"：下次打开同一条自动保持，不是播放器的临时状态。
     */
    val rotation: Int = 0,
) {
    companion object {
        const val STATUS_OK = "ok"
        const val STATUS_CORRUPT = "corrupt"
    }
}

/**
 * 下载任务。主键 `(server_id, key)` 与条目对齐：同一服务器上同一内容只挂一个下载。
 * 落盘布局见 [io.github.garfieldgod.mediarack.core.download.DownloadLayouts]。
 */
@Entity(
    tableName = "downloads",
    primaryKeys = ["server_id", "key"],
    indices = [Index("status")],
)
data class DownloadEntity(
    @ColumnInfo(name = "server_id") val serverId: Long,
    val key: String,
    /** 远端源 id（拉详情 / 流地址用） */
    @ColumnInfo(name = "source_id") val sourceId: String,
    val title: String,
    /** dual / single */
    val layout: String,
    /** single：服务器上的原始相对路径，决定 `s_` key 与落盘路径；dual 为 null */
    @ColumnInfo(name = "rel_path") val relPath: String?,
    /** queued / downloading / paused / done / failed */
    val status: String,
    @ColumnInfo(name = "bytes_done") val bytesDone: Long,
    @ColumnInfo(name = "bytes_total") val bytesTotal: Long,
    val error: String? = null,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
) {
    companion object {
        const val STATUS_QUEUED = "queued"
        const val STATUS_DOWNLOADING = "downloading"
        const val STATUS_PAUSED = "paused"
        const val STATUS_DONE = "done"
        const val STATUS_FAILED = "failed"
    }
}

/**
 * 条目标记（UI 信息流版）：收藏、看过、播放进度，主键 `(scope, key)` 与条目对齐。
 *
 * scope = `local:<sourceId>` 或 `srv:<serverId>`——源独立原则同样适用于标记：
 * 同一内容在本地源与服务器各有一份标记，去重只留到聚合视图（对齐 PC 决策）。
 */
@Entity(tableName = "marks", primaryKeys = ["scope", "key"])
data class MarkEntity(
    val scope: String,
    val key: String,
    val title: String,
    /** 非空 = 已收藏（时间戳）。清除收藏置 null，行保留。 */
    @ColumnInfo(name = "liked_at") val likedAt: Long?,
    /** 非空 = 标记看过。 */
    @ColumnInfo(name = "watched_at") val watchedAt: Long?,
    /** 播放进度（毫秒）；0 = 未播过。 */
    @ColumnInfo(name = "position_ms") val positionMs: Long,
    /** 条目时长（毫秒），进度归一化与「快看完」判断用。 */
    @ColumnInfo(name = "duration_ms") val durationMs: Long,
    @ColumnInfo(name = "updated_at") val updatedAt: Long,
    /**
     * **上一次播放**的时间戳（0 = 从没在这台设备上播过）。
     *
     * 与 [updatedAt] 分开：updated_at 是"这一行的任何字段被改过"，收藏/看过都会刷新它。
     * 「继续观看」要的是"最后播的那条视频"，用 updated_at 会被一次收藏误伤——
     * 刚点了收藏的旧视频会顶掉真正在看的那个。
     */
    @ColumnInfo(name = "played_at", defaultValue = "0") val playedAt: Long = 0,
)
