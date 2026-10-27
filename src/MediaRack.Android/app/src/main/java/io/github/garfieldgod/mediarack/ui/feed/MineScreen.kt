package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.garfieldgod.mediarack.core.db.MarkEntity
import io.github.garfieldgod.mediarack.ui.formatDuration
import io.github.garfieldgod.mediarack.ui.formatSize

/**
 * 「我的」（左滑进入的二级页）：统计、继续观看、收藏的两个入口、管理入口。
 * 返回键 / 左上角箭头回信息流。
 */
@Composable
fun MineScreen(
    vm: FeedViewModel,
    onBack: () -> Unit,
    onResume: (MarkEntity) -> Unit,
    /** 收藏的两个入口：0 = 收藏的视频，1 = 收藏的合集。 */
    onOpenFavorites: (Int) -> Unit,
    onOpenDownloads: () -> Unit,
) {
    val stats by vm.stats.collectAsState(
        initial = FeedViewModel.Stats(liked = 0, watched = 0, downloads = 0, bytes = 0)
    )
    val liked by vm.likedList.collectAsState(initial = emptyList())
    val continueList by vm.continueList.collectAsState(initial = emptyList())
    val servers by vm.servers.collectAsState(initial = emptyList())
    val localCount by vm.localCount.collectAsState()

    // 「我的」跟随信息流当前选中的源：标记的 scope 是 local:<sourceId> / srv:<serverId>，
    // 前缀匹配即可把继续观看、收藏视频数切到该源的份上；「全部」/ 连播不过滤。
    val filter by vm.filter.collectAsState()
    val scopedServer = filter as? FeedFilter.Server
    val markPrefix = when (filter) {
        is FeedFilter.Server -> "srv:${(filter as FeedFilter.Server).serverId}"
        FeedFilter.Local -> "local:"
        else -> null
    }
    val continueShown =
        if (markPrefix == null) continueList else continueList.filter { it.scope.startsWith(markPrefix) }
    val likedShown = if (markPrefix == null) liked else liked.filter { it.scope.startsWith(markPrefix) }

    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFF121419))
            .verticalScroll(rememberScrollState())
            .padding(bottom = 32.dp),
    ) {
        // 头部（带返回）
        Row(
            Modifier
                .fillMaxWidth()
                .background(Color(0xFF241B2B))
                .statusBarsPadding()
                .padding(horizontal = 10.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.AutoMirrored.Filled.ArrowBack,
                contentDescription = "返回",
                tint = Color.White,
                modifier = Modifier
                    .size(30.dp)
                    .clickable { onBack() }
                    .padding(5.dp),
            )
            Spacer(Modifier.width(8.dp))
            Box(
                Modifier
                    .size(54.dp)
                    .background(Color(0xFFFB7299).copy(alpha = 0.25f), RoundedCornerShape(999.dp)),
                contentAlignment = Alignment.Center,
            ) { Text("🦊", fontSize = 26.sp) }
            Spacer(Modifier.width(14.dp))
            Column {
                Text("我的媒体库", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 17.sp)
                Text(
                    "$localCount 条本地缓存 · ${servers.size} 个远程源",
                    color = Color(0xFF8B919C),
                    fontSize = 11.5.sp,
                )
            }
        }

        // 统计行
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            StatCard(stats.liked.toString(), "收藏", Modifier.weight(1f))
            StatCard(stats.downloads.toString(), "已下载", Modifier.weight(1f))
            StatCard(stats.watched.toString(), "看过", Modifier.weight(1f))
            StatCard(formatSize(stats.bytes).replace(" ", ""), "占用", Modifier.weight(1f))
        }

        // 继续观看（跟随当前源；选中某源时只显示该源看过的）
        if (continueShown.isNotEmpty()) {
            SectionLabel(if (scopedServer != null) "继续观看 · ${scopedServer.name}" else "继续观看")
            continueShown.forEach { mark ->
                // 按条目 id 缓存查找结果：按 updatedAt 做 key 的话，每 4s 落一次进度就要重查一遍
                val feedItem = remember(mark.scope, mark.key) {
                    vm.findItem("${mark.scope}|${mark.key}")
                }
                ContinueCard(mark, feedItem, vm) { onResume(mark) }
            }
        }

        // 收藏：两个入口。
        // 收藏是**用户层**（跨源、跨端同步），不属于任何源——所以入口在「我的」，
        // 不挂在抽屉的源列表里（以前那个「收藏」行会让人以为收藏是某个源的属性）。
        // 但收藏夹本身是源级的（每个源有自己的「默认收藏夹」）：选中某源时，
        // 里面只看得到该源的收藏夹；「全部」才合并展示（夹子名标注来源，见收藏页）。
        SectionLabel("收藏")
        EntryRow(
            emoji = "❤️",
            title = "收藏的视频",
            sub = if (scopedServer != null) {
                "${likedShown.size} 个视频 · ${scopedServer.name}"
            } else {
                "${liked.size} 个视频 · 服务端为准，跨端同步"
            },
        ) { onOpenFavorites(0) }
        Spacer(Modifier.height(8.dp))
        EntryRow(
            emoji = "📚",
            title = "收藏的合集",
            sub = if (scopedServer != null) {
                "${scopedServer.name} · 关注的 UP 主 / 文件夹"
            } else {
                "关注的 UP 主 / 文件夹"
            },
        ) { onOpenFavorites(1) }

        // 管理：只挂真正能用的入口。
        // 「观看历史 / 合集管理 / 设置」原来也在这儿，但 onClick 全是空 lambda——
        // 点了没反应比没有更糟，等做出来再放回去。
        SectionLabel("管理")
        ManageRow(
            Icons.Default.Download,
            "已下载管理",
            "${stats.downloads} 条 · ${formatSize(stats.bytes)}",
            onOpenDownloads,
        )
    }
}

/** 「我的」页的大入口行（图标 + 标题 + 副标题 + 右箭头）。收藏的两个入口共用。 */
@Composable
private fun EntryRow(emoji: String, title: String, sub: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp)
            .background(Color(0xFF1B1E26), RoundedCornerShape(14.dp))
            .clickable { onClick() }
            .padding(horizontal = 12.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier.size(40.dp).background(Color(0xFFFB7299).copy(alpha = 0.16f), RoundedCornerShape(12.dp)),
            contentAlignment = Alignment.Center,
        ) { Text(emoji, fontSize = 18.sp) }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, color = Color.White, fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold)
            Text(
                sub,
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Icon(
            Icons.AutoMirrored.Filled.KeyboardArrowRight,
            contentDescription = null,
            tint = Color(0xFF8B919C),
            modifier = Modifier.size(18.dp),
        )
    }
}

@Composable
private fun StatCard(value: String, label: String, modifier: Modifier = Modifier) {
    Column(
        modifier
            .background(Color(0xFF1B1E26), RoundedCornerShape(13.dp))
            .padding(vertical = 11.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(value, color = Color.White, fontWeight = FontWeight.Bold, fontSize = 15.sp)
        Text(label, color = Color(0xFF8B919C), fontSize = 10.5.sp)
    }
}

@Composable
private fun SectionLabel(text: String) {
    Text(
        text,
        color = Color(0xFF8B919C),
        fontSize = 11.sp,
        fontWeight = FontWeight.Bold,
        modifier = Modifier.padding(start = 20.dp, top = 14.dp, bottom = 8.dp),
    )
}

@Composable
private fun ContinueCard(mark: MarkEntity, feedItem: FeedItem?, vm: FeedViewModel, onResume: () -> Unit) {
    // 播到结尾的也算"上次播放"（这才是继续观看该有的语义），只是按钮换成"重播"。
    // 续播点由 pickResumePosMs 决定：剩余不足 20s 一律从头，这里只负责把话说清楚。
    val finished = mark.durationMs > 0 && mark.positionMs >= mark.durationMs - 3_000
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp)
            .background(Color(0xFF1B1E26), RoundedCornerShape(14.dp))
            .clickable { onResume() }
            .padding(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(110.dp)
                .aspectRatio(16f / 10f)
                // 同 SearchRow：Cover 会画到容器外，裁剪交给容器（顺带正确渲染圆角）
                .clip(RoundedCornerShape(10.dp))
                .background(Color(0xFF2A2E3A)),
            contentAlignment = Alignment.Center,
        ) {
            if (feedItem != null) {
                ThumbImage(item = feedItem, vm = vm, modifier = Modifier.fillMaxSize())
            } else {
                Icon(Icons.Default.PlayArrow, contentDescription = null, tint = Color(0xFF8B919C))
            }
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                mark.title,
                color = Color.White,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                if (finished) "已看完 · ${formatDuration(mark.durationMs)}"
                else "看到 ${formatDuration(mark.positionMs)} / ${formatDuration(mark.durationMs)}",
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
            )
        }
        PlayPill(if (finished) "▶ 重播" else "▶ 续播") { onResume() }
    }
}

@Composable
private fun PlayPill(label: String, onClick: () -> Unit) {
    Text(
        label,
        color = Color(0xFFFB7299),
        fontSize = 11.5.sp,
        fontWeight = FontWeight.Bold,
        modifier = Modifier
            .background(Color(0xFFFB7299).copy(alpha = 0.16f), RoundedCornerShape(999.dp))
            .clickable { onClick() }
            .padding(horizontal = 13.dp, vertical = 7.dp),
    )
}

@Composable
private fun ManageRow(icon: ImageVector, label: String, sub: String?, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable { onClick() }
            .padding(horizontal = 20.dp, vertical = 13.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = Color(0xFF8B919C), modifier = Modifier.size(19.dp))
        Spacer(Modifier.width(13.dp))
        Text(label, color = Color.White, fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
        sub?.let { Text(it, color = Color(0xFF8B919C), fontSize = 11.sp) }
        Icon(
            Icons.AutoMirrored.Filled.KeyboardArrowRight,
            contentDescription = null,
            tint = Color(0xFF8B919C),
            modifier = Modifier.size(18.dp),
        )
    }
}
