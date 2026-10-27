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
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DeleteOutline
import androidx.compose.material.icons.filled.DownloadDone
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.garfieldgod.mediarack.ui.formatDuration
import io.github.garfieldgod.mediarack.ui.formatSize
import kotlinx.coroutines.launch

/**
 * 「已下载管理」：本地缓存全量列表（手机 B 站缓存 + 从远程源下载落盘）。
 *
 * 口径说明：这里列的是 `items` 表（扫描器扫出来的本地条目），不是 `downloads`
 * 任务表——用户只有一个库，分两个数就出现"本地缓存 3 条、已下载 0 条"的矛盾。
 *
 * 点条目 = 回信息流播这一条（[onOpen]）；右侧垃圾桶 = 删磁盘文件 + 索引，二次确认。
 */
@Composable
fun DownloadsScreen(
    vm: FeedViewModel,
    onBack: () -> Unit,
    onOpen: (FeedItem) -> Unit,
) {
    var tick by remember { mutableStateOf(0) }
    val items by produceState(initialValue = emptyList<FeedItem>(), tick) {
        value = vm.localItemsForManage()
    }
    var pendingDelete by remember { mutableStateOf<FeedItem?>(null) }
    var busyId by remember { mutableStateOf<String?>(null) }
    var rescanning by remember { mutableStateOf(false) }
    var err by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    val totalBytes = remember(items) { items.sumOf { it.sizeBytes } }

    // 缓存队列：正在 / 待缓存 / 失败（已完成的走下面的已下载列表）。
    // 点暂停 = 打断在途任务（断点保留，继续时从 part 长度续传）；✕ = 移出队列。
    val queue by vm.observeDownloadQueue().collectAsState(initial = emptyList())
    var concurrency by remember { mutableStateOf(vm.downloadConcurrency) }

    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFF121419)),
    ) {
        // 顶栏：返回 + 标题 + 条数/占用
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
            Column(Modifier.weight(1f)) {
                Text("已下载管理", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 17.sp)
                Text(
                    "${items.size} 条 · ${formatSize(totalBytes)}",
                    color = Color(0xFF8B919C),
                    fontSize = 11.5.sp,
                )
            }
            // 重扫：外部丢进目录的文件 / 手动整理过的目录，靠这个反映出来
            if (rescanning) {
                CircularProgressIndicator(
                    modifier = Modifier
                        .padding(horizontal = 12.dp)
                        .size(16.dp),
                    strokeWidth = 2.dp,
                    color = Color(0xFFFB7299),
                )
            } else {
                IconButton(
                    onClick = {
                        rescanning = true
                        err = null
                        scope.launch {
                            vm.rescanLocal()
                            tick++
                            rescanning = false
                        }
                    },
                ) {
                    Icon(
                        Icons.Default.Refresh,
                        contentDescription = "重新扫描本地源",
                        tint = Color(0xFF8B919C),
                        modifier = Modifier.size(20.dp),
                    )
                }
            }
        }

        err?.let {
            Text(
                it,
                color = Color(0xFFE05B6B),
                fontSize = 12.sp,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp),
            )
        }

        if (items.isEmpty() && queue.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(
                        Icons.Default.DownloadDone,
                        contentDescription = null,
                        tint = Color(0xFF3A3F4B),
                        modifier = Modifier.size(46.dp),
                    )
                    Spacer(Modifier.height(12.dp))
                    Text("还没有已下载的内容", color = Color(0xFF8B919C), fontSize = 13.sp)
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "手机里缓存的视频、从服务器下载的视频都会列在这里",
                        color = Color(0xFF5A6070),
                        fontSize = 11.5.sp,
                    )
                }
            }
        } else {
            LazyColumn(Modifier.fillMaxSize()) {
                // 缓存队列：播放设置面板「缓存」入队的任务，支持暂停/继续/移除与并发数
                if (queue.isNotEmpty()) {
                    item(key = "queue-header") {
                        QueueHeader(
                            concurrency = concurrency,
                            onConcurrency = {
                                concurrency = it
                                vm.downloadConcurrency = it
                            },
                        )
                    }
                    items(queue, key = { "${it.serverId}|${it.key}" }) { row ->
                        QueueRow(
                            row = row,
                            onPause = { vm.pauseDownload(row.serverId, row.key) },
                            onResume = { vm.resumeDownload(row.serverId, row.key) },
                            onCancel = { vm.cancelDownload(row.serverId, row.key) },
                        )
                    }
                    item(key = "downloaded-label") {
                        Text(
                            "已下载",
                            color = Color(0xFF8B919C),
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Bold,
                            modifier = Modifier.padding(start = 20.dp, top = 14.dp, bottom = 8.dp),
                        )
                    }
                }
                items(items, key = { it.id }) { item ->
                    DownloadRow(
                        item = item,
                        vm = vm,
                        busy = busyId == item.id,
                        onClick = { onOpen(item) },
                        onDelete = { pendingDelete = item },
                    )
                }
                item { Spacer(Modifier.navigationBarsPadding().height(24.dp)) }
            }
        }
    }

    pendingDelete?.let { target ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("删除这一条？") },
            text = {
                Text("《${target.title}》\n\n会同时删掉手机上的文件，删了不能恢复。")
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        pendingDelete = null
                        busyId = target.id
                        err = null
                        scope.launch {
                            val e = vm.deleteLocalItem(target)
                            busyId = null
                            if (e == null) tick++ else err = e
                        }
                    },
                ) { Text("删除", color = Color(0xFFE05B6B)) }
            },
            dismissButton = {
                TextButton(onClick = { pendingDelete = null }) { Text("取消") }
            },
        )
    }
}

@Composable
private fun DownloadRow(
    item: FeedItem,
    vm: FeedViewModel,
    busy: Boolean,
    onClick: () -> Unit,
    onDelete: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .background(Color(0xFF1B1E26), RoundedCornerShape(14.dp))
            .clickable(enabled = !busy) { onClick() }
            .padding(start = 10.dp, end = 2.dp, top = 10.dp, bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(104.dp)
                .aspectRatio(16f / 9f)
                // Cover 铺满时的溢出由容器裁掉（ThumbImage 的 rotatedFrame 会画到框外）
                .clip(RoundedCornerShape(10.dp))
                .background(Color(0xFF2A2E3A)),
        ) {
            ThumbImage(item = item, vm = vm, modifier = Modifier.fillMaxSize())
        }
        Spacer(Modifier.width(12.dp))
        Column(
            Modifier.weight(1f),
            verticalArrangement = Arrangement.spacedBy(3.dp),
        ) {
            Text(
                item.title,
                color = Color.White,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                buildString {
                    item.sourceName?.takeIf { it.isNotBlank() }?.let { append(it).append(" · ") }
                    append(formatDuration(item.durationMs))
                    append(" · ")
                    append(formatSize(item.sizeBytes))
                },
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        if (busy) {
            CircularProgressIndicator(
                modifier = Modifier
                    .padding(horizontal = 12.dp)
                    .size(16.dp),
                strokeWidth = 2.dp,
                color = Color(0xFFFB7299),
            )
        } else {
            IconButton(onClick = onDelete) {
                Icon(
                    Icons.Default.DeleteOutline,
                    contentDescription = "删除",
                    tint = Color(0xFF8B919C),
                    modifier = Modifier.size(20.dp),
                )
            }
        }
    }
}

/** 缓存队列的节标题 + 并发数选择（1-3，立即生效）。 */
@Composable
private fun QueueHeader(concurrency: Int, onConcurrency: (Int) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(start = 20.dp, end = 14.dp, top = 14.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            "缓存队列",
            color = Color(0xFF8B919C),
            fontSize = 11.sp,
            fontWeight = FontWeight.Bold,
        )
        Spacer(Modifier.weight(1f))
        Text("同时下载", color = Color(0xFF8B919C), fontSize = 10.5.sp)
        Spacer(Modifier.width(6.dp))
        for (n in 1..3) {
            val selected = n == concurrency
            Text(
                n.toString(),
                color = if (selected) Color(0xFFFB7299) else Color(0xFF8B919C),
                fontSize = 11.5.sp,
                fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
                modifier = Modifier
                    .padding(horizontal = 2.dp)
                    .size(24.dp)
                    .background(
                        if (selected) Color(0xFFFB7299).copy(alpha = 0.16f) else Color.Transparent,
                        RoundedCornerShape(999.dp),
                    )
                    .clickable { onConcurrency(n) }
                    .wrapContentSize(Alignment.Center),
            )
        }
    }
}

/** 队列里的一行：状态 + 进度 + 暂停/继续/移除。 */
@Composable
private fun QueueRow(
    row: io.github.garfieldgod.mediarack.core.db.DownloadEntity,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onCancel: () -> Unit,
) {
    val percent = if (row.bytesTotal > 0) ((row.bytesDone * 100) / row.bytesTotal).toInt() else 0
    val (statusText, statusColor) = when (row.status) {
        "downloading" -> "下载中 $percent%" to Color(0xFF7CC4FF)
        "queued" -> "等待中" to Color(0xFF8B919C)
        "paused" -> ("已暂停" + if (row.bytesTotal > 0) " · $percent%" else "") to Color(0xFFD8A63A)
        else -> "失败：${row.error ?: "未知原因"}" to Color(0xFFE05B6B)
    }

    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .background(Color(0xFF1B1E26), RoundedCornerShape(14.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(
                row.title,
                color = Color.White,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                if (row.bytesTotal > 0) {
                    "$statusText · ${formatSize(row.bytesDone)} / ${formatSize(row.bytesTotal)}"
                } else {
                    statusText
                },
                color = statusColor,
                fontSize = 11.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        // 等待中 / 下载中 → 可暂停；暂停 / 失败 → 可继续（失败重试走断点续传）
        if (row.status == "downloading" || row.status == "queued") {
            IconButton(onClick = onPause) {
                Icon(
                    Icons.Default.Pause,
                    contentDescription = "暂停",
                    tint = Color(0xFF8B919C),
                    modifier = Modifier.size(20.dp),
                )
            }
        } else {
            IconButton(onClick = onResume) {
                Icon(
                    Icons.Default.PlayArrow,
                    contentDescription = "继续",
                    tint = Color(0xFF8B919C),
                    modifier = Modifier.size(20.dp),
                )
            }
        }
        IconButton(onClick = onCancel) {
            Icon(
                Icons.Default.Close,
                contentDescription = "移出队列",
                tint = Color(0xFF8B919C),
                modifier = Modifier.size(18.dp),
            )
        }
    }
}
