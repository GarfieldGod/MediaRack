package io.github.garfieldgod.mediarack.ui.feed

/**
 * 信息流条目：本地与远程的统一投影。
 *
 * - 本地条目 videoUri 直接可用；远程条目为 null，翻到该页时才解析
 *   （先查已下载本地文件，再拉服务器详情取直链——本地优先、远程兜底）
 * - scope 规则与 marks 表一致：`local:<sourceId>` / `srv:<serverId>`
 */
data class FeedItem(
    val scope: String,
    val key: String,
    val title: String,
    /** 合集名：本地 = 目录名；远程 = 服务端合集名（v4 契约已带）。 */
    val collectionName: String?,
    /** 本地合集目录（旧本地合集页数据源）；远程为 null。 */
    val collectionDir: String?,
    /** 远程所属合集 id（`dir:<源>:/…` 或 `up:<源>:<mid>`）；本地为 null。 */
    val collectionId: String? = null,
    /** 远程合集类型（folder / up）；本地为 null。 */
    val collectionKind: String? = null,
    /** 本地源 id（scope 为 local 时有效）。 */
    val sourceId: Long,
    /** 服务器 id（scope 为 srv 时有效）。 */
    val serverId: Long?,
    /** 服务器上的源 id。 */
    val remoteSourceId: String?,
    /** dual / single（远程）；本地 dual 合成条目同样标 dual。 */
    val layout: String?,
    val durationMs: Long,
    val sizeBytes: Long,
    val width: Int,
    val height: Int,
    val videoUri: String?,
    val audioUri: String?,
    /** 源显示名：顶栏「源名：合集名」、连接提示「正在连接 xxx」。 */
    val sourceName: String? = null,
    /** 本地海报文件绝对路径（hasPoster 时）。 */
    val posterFile: String? = null,
    /** 远程海报绝对 URL（已拼 base+token）。 */
    val posterUrl: String? = null,
    /**
     * 用户覆盖的旋转角（0/90/180/270）。跟条目走、不跟播放器走——
     * 同一个视频下次打开自动保持，跨端也一致（服务端 media_meta.rotation）。
     */
    val rotation: Int = 0,
) {
    val id: String get() = "$scope|$key"

    val isRemote: Boolean get() = serverId != null
}

/** 信息流过滤：全部 / 本地缓存（所有本地源合一）/ 某服务器 / 收藏夹连播。 */
sealed interface FeedFilter {
    data object All : FeedFilter

    /** 本地缓存：抽屉里就一项，不分本地源——源列表已经合并成单一列表了。 */
    data object Local : FeedFilter
    data class Server(val serverId: Long, val name: String) : FeedFilter
    data object Liked : FeedFilter
}

/**
 * 跨源翻页时「跳过不可达源」：从 [from] 往后找第一个不属于 [serverId] 的页。
 * 返回 null = 该源已经是最后一个源（调用方退回上一个可播条目）。
 *
 * 抽成纯函数是为了能单测：判错要么跳掉整个正常源，要么在坏源里反复重连。
 */
internal fun nextPageOutsideSource(items: List<FeedItem>, from: Int, serverId: Long): Int? =
    (from + 1 until items.size).firstOrNull { items[it].serverId != serverId }

/**
 * 续播游标择优（纯函数，可单测）。本地与服务端各存一份进度：
 * - 标过「看过」不续；进度 ≤30s、剩余 ≤20s、时长为 0 都不续
 * - 服务端游标比本地**新 1s 以上**才以它为准——本机刚上报过的不算，
 *   否则同一位置两边时间戳互相压，续播点会抖
 */
internal fun pickResumePosMs(
    localPosMs: Long,
    localUpdatedAt: Long,
    localWatchedAt: Long?,
    serverPosMs: Long,
    serverUpdatedAt: Long,
    durationMs: Long,
): Long {
    if (localWatchedAt != null) return 0
    val pos = if (serverUpdatedAt > localUpdatedAt + 1_000) serverPosMs else localPosMs
    if (pos <= 30_000 || durationMs <= 0) return 0
    return if (pos < durationMs - 20_000) pos else 0
}

/**
 * 播放模式（点一次换下一个，循环切换）：
 *
 * - [PauseAtEnd] 播完暂停：停在最后一帧，不自动往下走（默认，最不容易"手一滑就丢了进度"）
 * - [LoopOne] 循环播放：本条播完从头再来（ExoPlayer 的 REPEAT_MODE_ONE，不走 ENDED）
 * - [AutoNext] 自动切集：播完自动翻到下一条，**最后一条则停**（不回头、不循环整个列表）
 *
 * 抽成枚举 + [next] 而不是散落的布尔量：三个状态互斥，用两个 bool 表达会出现
 * "既是循环又是自动切集"这种组合，到时候没人知道该信谁。
 */
enum class PlayMode(val label: String) {
    PauseAtEnd("播完暂停"),
    LoopOne("循环播放"),
    AutoNext("自动切集");

    /** 下一次点击该切到哪个模式（PauseAtEnd → LoopOne → AutoNext → PauseAtEnd）。 */
    fun next(): PlayMode = entries[(ordinal + 1) % entries.size]

    companion object {
        /** 由持久化的序号还原；越界（改过枚举顺序的老数据）一律退回默认值。 */
        fun of(ordinal: Int): PlayMode = entries.getOrElse(ordinal) { PauseAtEnd }
    }
}

/** 播完后的目标页；null = 停在当前条。自动切集到最后一条也不回绕。 */
internal fun pageAfterPlaybackEnded(mode: PlayMode, currentPage: Int, lastIndex: Int): Int? =
    if (mode == PlayMode.AutoNext && currentPage < lastIndex) currentPage + 1 else null

// ── 二级页（合集 / 收藏）的加载结果 ──────────────────────

/** 从播放页「合集」按钮直接打开的服务端合集。 */
data class CollectionRef(val serverId: Long, val collectionId: String, val name: String, val kind: String)

/**
 * 连播队列：从某个收藏夹 / 合集点开一条时，把它所在的**整份列表**一起带进播放页。
 *
 * 这是「在收藏夹里连续播放」的唯一实现方式：播放页永远是"按当前 items 的 index 翻页"的整体结构，
 * 只把被点的那一条 pin 到信息流最前（旧做法）等于把上下文丢了——上滑下一条是信息流里的无关视频，
 * 而且抖完那一条就再也翻不到夹子里的第二条。
 *
 * [label] 只用于顶栏「连播：收藏夹·稍后再看」的提示与退出按钮。
 * [items] 的顺序 = 列表页显示的顺序（收藏夹按加入时间倒序，合集按标题），所以"往下滑=往下看"。
 */
data class PlayQueue(val label: String, val items: List<FeedItem>)

/**
 * 连播的**来源页**：退出连播要原路返回。
 *
 * 以前退出连播就是"把 items 换回信息流"，人直接被扔回主页面——从收藏夹点进去看了一条，
 * 想接着挑第二条时发现自己站在信息流里，得重新「我的 → 收藏的视频 → 那个夹子」走一遍。
 * 记住来源之后，退出 = 关掉播放回到刚才那一页（收藏夹的 tab、进到哪个夹子都在 nav 状态里）。
 */
sealed interface QueueHome {
    /** 收藏页（tab 与"打开的是哪个收藏夹/合集"记在 [FavoritesNav]）。 */
    data object Favorites : QueueHome

    /** 合集页（记在 [CollectionsNav]）。 */
    data object Collections : QueueHome

    /**
     * 搜索页。带着关键词回去——搜索页的输入框是受控的（状态在 FeedScreen 层），
     * 还原关键词后重开页面会自动重跑同一份搜索，用户接着挑下一条。
     */
    data class Search(val query: String) : QueueHome

    /** 本地条目的目录合集页（旧入口）：它的页面本身就是这一条，直接带着回去。 */
    data class LocalCollection(val origin: FeedItem) : QueueHome
}

/**
 * 进连播前的落脚点。
 *
 * [keepId] = 进连播前主信息流正在看的那一条：退出时播放器要回到它，
 * 而不是"停在刚看完的连播条目上"（后者会让主信息流被这一次连播改掉）。
 */
data class QueueOrigin(val keepId: String?, val home: QueueHome?)

/**
 * 二级页加载结果。[offline] = 服务端不可达、当前显示的是上次的缓存快照——
 * 页面据此顶部挂提示条，并给未缓存到本地的条目打「离线」标、禁止播放。
 */
data class CollectionsLoad(val rows: List<Pair<Long, io.github.garfieldgod.mediarack.core.remote.RemoteCollection>>, val offline: Boolean)

data class FavoritesLoad(val rows: List<Pair<Long, io.github.garfieldgod.mediarack.core.remote.Favorite>>, val offline: Boolean)

data class CollectionPageLoad(val items: List<FeedItem>, val total: Int, val offline: Boolean)
