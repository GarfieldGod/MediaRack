package io.github.garfieldgod.mediarack.core

import java.security.MessageDigest

/**
 * 内容标识键。
 *
 * 与 PC 端 `MediaRack.Core/Models/MediaKey.cs` **严格对齐**（契约冻结，见 docs/design.md §11）：
 * - dual：`<avid>_c<cid>`
 * - single：`s_<sha1(relpath)[:16]>`
 *
 * 只含 `[0-9a-z_]`，可直接进 URL path 段。键一旦写入预取目录不可更改。
 */
object ContentKeys {
    const val SINGLE_PREFIX = "s_"

    /**
     * single 源的键：相对路径规范化（`\`→`/`、去 `./` 与开头 `/`、整串小写）后
     * UTF-8 SHA-1 的十六进制前 16 位（小写）。
     *
     * 用相对路径，换盘符/换机器不失效；代价是同内容不同路径视为两条。
     */
    fun forSingle(relativePath: String): String {
        require(relativePath.isNotBlank()) { "relativePath 不能为空" }
        val hash = MessageDigest.getInstance("SHA-1")
            .digest(normalize(relativePath).toByteArray(Charsets.UTF_8))
        val hex = hash.joinToString("") { "%02x".format(it) }
        return SINGLE_PREFIX + hex.take(16)
    }

    /** 统一分隔符与大小写，保证同一文件在不同写法下得到同一个键。 */
    fun normalize(relativePath: String): String {
        var s = relativePath.replace('\\', '/').trim()
        while (s.startsWith("./")) s = s.substring(2)
        s = s.trimStart('/')
        return s.lowercase()
    }
}
