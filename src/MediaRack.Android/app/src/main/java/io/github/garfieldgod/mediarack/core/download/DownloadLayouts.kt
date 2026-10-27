package io.github.garfieldgod.mediarack.core.download

import android.content.Context
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File

/**
 * 下载落盘布局。与 PC dual 缓存布局**逐字段对齐**：
 *
 * - dual：`<root>/<avid>/c_<cid>/<type_tag>/video.m4s + audio.m4s`，entry.json 在 `c_<cid>` 层，
 *   字段镜像 B 站缓存 JSON——手机下载目录拷到 PC 可直接当 dual 源扫描（PC DualScanner 兼容）
 * - single：`<root>/<原始相对路径>`，保证 `s_<sha1(relpath)[:16]>` key 跨端一致
 * - 断点续传的半成品放 `files/downloads_staging/<key>/` 下的 .part 文件（downloads 根之外），
 *   扫描器永远看不到半成品；完成后整体搬入正式布局
 *
 * key 解析复刻 PC `MediaKey.TryParseDual`（最后一个 `_c` 切开，两段纯数字）。
 */
object DownloadLayouts {
    val JSON = Json { ignoreUnknownKeys = true; encodeDefaults = true }

    fun downloadsRoot(context: Context): File = File(context.filesDir, "downloads")

    /** 半成品暂存区，刻意放在 downloads 之外。 */
    fun stagingRoot(context: Context): File = File(context.filesDir, "downloads_staging")

    fun stagingDir(context: Context, key: String): File = File(stagingRoot(context), key)

    /** PC MediaKey.TryParseDual 的镜像。解析失败返回 null。 */
    fun parseDualKey(key: String): Pair<String, String>? {
        val sep = key.lastIndexOf("_c")
        if (sep <= 0) return null
        val avid = key.substring(0, sep)
        val cid = key.substring(sep + 2)
        if (avid.isEmpty() || cid.isEmpty()) return null
        return if (avid.all { it.isDigit() } && cid.all { it.isDigit() }) avid to cid else null
    }

    fun dualItemDir(root: File, avid: String, cid: String): File = File(File(root, avid), "c_$cid")

    fun singleFile(root: File, relPath: String): File = File(root, relPath)

    /** PC DualScanner.ResolveTagDir 的镜像：优先 entry.type_tag，退化到第一个含 m4s 的子目录。 */
    fun resolveTagDir(itemDir: File, typeTag: String?): File? {
        if (!typeTag.isNullOrEmpty()) {
            val candidate = File(itemDir, typeTag)
            if (candidate.isDirectory) return candidate
        }
        itemDir.listFiles()
            ?.filter { it.isDirectory }
            ?.forEach { sub ->
                if (File(sub, "video.m4s").isFile || File(sub, "audio.m4s").isFile) return sub
            }
        return null
    }

    /**
     * 合成 entry.json。字段名与 B 站客户端缓存一致（PC DualScanner 的硬性要求：
     * 顶层 avid + page_data.cid，m4s 目录按 type_tag 命名）。
     */
    fun writeEntryJson(itemDir: File, spec: EntrySpec) {
        val entry = EntryJson(
            title = spec.title,
            avid = spec.avid.toLongOrNull(),
            type_tag = spec.typeTag,
            total_time_milli = spec.durationMs.toInt(),
            total_bytes = spec.totalBytes,
            has_dash_audio = spec.hasAudio,
            is_completed = true,
            owner_name = spec.upName,
            page_data = PageDataJson(cid = spec.cid.toLongOrNull(), width = spec.width, height = spec.height),
        )
        File(itemDir, "entry.json").writeText(JSON.encodeToString(entry))
    }

    fun readEntryJson(itemDir: File): EntryJson? = runCatching {
        val f = File(itemDir, "entry.json")
        if (!f.isFile) return null
        JSON.decodeFromString<EntryJson>(f.readText())
    }.getOrNull()

    // ── entry.json 镜像（字段名与 PC DualScanner / B 站缓存一致）──────────────

    @Serializable
    data class EntryJson(
        val title: String? = null,
        val avid: Long? = null,
        val bvid: String? = null,
        @SerialName("type_tag") val type_tag: String? = null,
        @SerialName("total_time_milli") val total_time_milli: Int? = null,
        @SerialName("total_bytes") val total_bytes: Long? = null,
        @SerialName("has_dash_audio") val has_dash_audio: Boolean? = null,
        @SerialName("is_completed") val is_completed: Boolean? = null,
        @SerialName("owner_name") val owner_name: String? = null,
        @SerialName("owner_id") val owner_id: Long? = null,
        @SerialName("page_data") val page_data: PageDataJson? = null,
    )

    @Serializable
    data class PageDataJson(
        val cid: Long? = null,
        val width: Int? = null,
        val height: Int? = null,
    )

    /** 写 entry.json 所需的完整规格（下载完成时由 detail 构造）。 */
    data class EntrySpec(
        val title: String,
        val avid: String,
        val cid: String,
        val typeTag: String,
        val durationMs: Long,
        val totalBytes: Long,
        val hasAudio: Boolean,
        val upName: String?,
        val width: Int,
        val height: Int,
    )
}
