package io.github.garfieldgod.mediarack.core.db

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 远程服务器（网络源）。一个服务器 = 一个 base URL + 可选令牌。
 * 库内容不落地——只缓存最近一次 summary 的 JSON（断网降级：条目变灰不消失）。
 */
@Entity(tableName = "servers")
data class ServerEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val name: String,
    /** 规范化后的 base URL，无尾斜杠，如 http://192.0.2.10:8080 */
    @ColumnInfo(name = "base_url") val baseUrl: String,
    /** 未配置令牌的服务器为 null */
    val token: String?,
    @ColumnInfo(name = "created_at") val createdAt: Long,
    /** 最近一次成功拉取的聚合列表 JSON（原始契约形状，posterUrl 保持相对路径）。断网时用它渲染灰条目。 */
    @ColumnInfo(name = "last_summary_json") val lastSummaryJson: String? = null,
    /** lastSummaryJson 的写入时刻（epoch ms）。null = 从未成功拉取。 */
    @ColumnInfo(name = "fetched_at") val fetchedAt: Long? = null,
)
