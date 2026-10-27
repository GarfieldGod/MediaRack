package io.github.garfieldgod.mediarack.ui.feed

import androidx.media3.common.MediaItem
import androidx.media3.exoplayer.source.MediaSource
import androidx.media3.exoplayer.source.preload.DefaultPreloadManager
import androidx.media3.exoplayer.source.preload.TargetPreloadStatusControl
import kotlin.math.abs

/**
 * Feed 预载器（方案 B：DefaultPreloadManager + PreloadMediaSource）。
 *
 * 相邻 ±[RANGE] 页的远程条目提前 prepare + 缓冲前 [PRELOAD_MS] 毫秒进**内存**
 * （SampleQueue）；翻到它时播放端通过同一个 canonical MediaItem 取回
 * PreloadMediaSource 包装实例，预载好的 MediaPeriod 原样移交给播放器——
 * 不重新连网、不重新缓冲，切换即出画面。
 *
 * 两块职责：
 * - [statusControl]：管理器预载任何候选前都会问"这条要载到什么程度"。
 *   判据只有"离当前页多远"（[currentIdx] 每次翻页更新，管理器据此重排优先级）。
 * - [registered]：页号 → 已登记的条目。播放端接管、滑出窗口或条目被换时
 *   才注销（remove 会真正释放预载数据，不能乱来）；条目在窗口里反复横跳
 *   （滑过去又滑回来）期间保持登记，双向切换都是秒开。
 */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
internal class FeedPreloader {

    /** 一条登记：canonical MediaItem（管理器查 holder 的键）+ 交给管理器的源。 */
    internal class Entry(
        val itemId: String,
        val mediaItem: MediaItem,
        val source: MediaSource,
    )

    /** 管理器眼里的"当前播放页号"（SimpleRanking 按与它的距离排序）。 */
    @Volatile
    var currentIdx: Int = -1

    /** 已登记的预载条目（页号 → 条目）。主线程专用。 */
    val registered = HashMap<Int, Entry>()

    val statusControl: TargetPreloadStatusControl<Int, DefaultPreloadManager.PreloadStatus> =
        TargetPreloadStatusControl { idx ->
            if (abs(idx - currentIdx) in 1..RANGE) {
                // 只 prepare + 轨道选择 + 缓冲前 5 秒；样本留在内存里等着移交
                DefaultPreloadManager.PreloadStatus.specifiedRangeLoaded(PRELOAD_MS)
            } else {
                DefaultPreloadManager.PreloadStatus.PRELOAD_STATUS_NOT_PRELOADED
            }
        }

    companion object {
        /** 只预载紧邻的上/下一条（用户要求"上下两个视频的前一小段"）。 */
        const val RANGE = 1

        /** 每条邻居预载前 5 秒：首帧 + 起播缓冲绰绰有余，双流 4K 约 3~8MB。 */
        const val PRELOAD_MS = 5_000L
    }
}
