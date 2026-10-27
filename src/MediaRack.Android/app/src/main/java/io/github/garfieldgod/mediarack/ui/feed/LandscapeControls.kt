package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.pager.PagerState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.CollectionsBookmark
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.media3.exoplayer.ExoPlayer
import io.github.garfieldgod.mediarack.ui.formatDuration
import kotlinx.coroutines.launch

/**
 * 横屏（全屏）控制层：壳不动，只换这一层。
 *
 * 顶部：菜单 + 标题 …… 我的
 * 底部：细进度条
 *       [上一个 暂停 下一个] [倍速]  ……  [合集 收藏 看过 设置 全屏]
 *
 * 「适应 / 旋转 / 播放模式」收进「设置」小面板（点面板外任意处或再点设置收起）——
 * 一行 8 个按钮在窄一点的手机上已经贴到一起了，这三项又是低频设置。
 *
 * 手势（点按切 UI 显隐、双击暂停/继续、双指缩放、横滑拖进度、左/右半屏竖滑亮度/音量）
 * 见 FeedGestureLayer，横竖屏共用同一层。
 * 顶栏与底部控制栏都挂 [blockTouchPassThrough]：点在栏内空位不再穿透成"隐藏 UI"。
 */

private val Accent = Color(0xFFFB7299)

@Composable
fun LandscapeControls(
    items: List<FeedItem>,
    pagerState: PagerState,
    player: ExoPlayer,
    title: String,
    playing: Boolean,
    playbackSpeed: Float,
    onSpeedChange: (Float) -> Unit,
    liked: Boolean,
    watched: Boolean,
    /** 画面适配：false = Fit（整幅可见）/ true = Cover（铺满裁边）。 */
    cover: Boolean,
    onToggleCover: () -> Unit,
    /** 用户覆盖旋转角（0/90/180/270），点一次 +90。 */
    rotation: Int,
    onRotate: () -> Unit,
    /** 播放模式（播完暂停 / 循环播放 / 自动切集），点一次换下一个。 */
    playMode: PlayMode,
    onCycleMode: () -> Unit,
    fullscreen: Boolean,
    onOpenDrawer: () -> Unit,
    onOpenMine: () -> Unit,
    onOpenSearch: () -> Unit,
    onOpenCollection: () -> Unit,
    /** 只有远程条目给「缓存」入口（设置面板 → 视频行）。 */
    cacheable: Boolean = false,
    onCache: () -> Unit = {},
    onToggleLiked: () -> Unit,
    onToggleWatched: () -> Unit,
    onToggleFullscreen: () -> Unit,
    sliderPos: Float,
    sliderDur: Float,
    onSeekPreview: (Float) -> Unit,
    onSeekFinished: (Float) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var speedMenu by remember { mutableStateOf(false) }
    /** 播放设置面板（适应 / 旋转 / 模式）。点面板外任意处或再点「设置」收起。 */
    var showSettings by remember { mutableStateOf(false) }

    Box(Modifier.fillMaxSize()) {
        // 展开设置面板时铺一层透明遮罩：点面板以外任何地方都收起。
        // 垫在顶栏与底部栏**之前**画，命中顺序是"后画的先"，所以那两块照常可点。
        if (showSettings) {
            Box(
                Modifier
                    .fillMaxSize()
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null,
                    ) { showSettings = false }
            )
        }

        // 顶栏：菜单 + 标题 …… 我的
        Row(
            Modifier
                .fillMaxWidth()
                .background(Color.Black.copy(alpha = 0.35f))
                .blockTouchPassThrough()
                .statusBarsPadding()
                .padding(horizontal = 10.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.Default.Menu,
                contentDescription = "菜单",
                tint = Color.White,
                modifier = Modifier
                    .size(36.dp)
                    .clickable { onOpenDrawer() }
                    .padding(7.dp),
            )
            Spacer(Modifier.width(4.dp))
            Text(
                title,
                color = Color.White,
                fontWeight = FontWeight.SemiBold,
                fontSize = 13.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            LandIconButton(Icons.Default.Search, "搜索") { onOpenSearch() }
            Spacer(Modifier.width(4.dp))
            LandIconButton(Icons.Default.Person, "我的") { onOpenMine() }
        }

        // 底部：进度条 + 单行控制条。
        // 图标行上方（距进度条）与下方（距屏幕边缘）各 10dp，整行比原来抬高一点、上下等距。
        Column(
            Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .background(Color.Black.copy(alpha = 0.45f))
                .blockTouchPassThrough()
                .navigationBarsPadding(),
        ) {
            Spacer(Modifier.height(6.dp))
            Row(
                Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    formatDuration(sliderPos.toLong()),
                    color = Color.White.copy(alpha = 0.75f),
                    fontSize = 10.5.sp,
                )
                ThinProgressBar(
                    pos = sliderPos,
                    dur = sliderDur,
                    onSeek = onSeekPreview,
                    onSeekFinished = onSeekFinished,
                    modifier = Modifier
                        .weight(1f)
                        .padding(horizontal = 10.dp),
                )
                Text(
                    formatDuration(sliderDur.toLong()),
                    color = Color.White.copy(alpha = 0.75f),
                    fontSize = 10.5.sp,
                )
            }
            Spacer(Modifier.height(10.dp))

            // 设置面板：贴在按钮行正上方、右对齐（就在「设置」钮头上，视线不用找）
            AnimatedVisibility(visible = showSettings, enter = fadeIn(), exit = fadeOut()) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 14.dp),
                    horizontalArrangement = Arrangement.End,
                ) {
                    PlaySettingsPanel(
                        cover = cover,
                        onToggleCover = { onToggleCover() },
                        rotation = rotation,
                        onRotate = { onRotate() },
                        mode = playMode,
                        onCycleMode = { onCycleMode() },
                        modifier = Modifier.padding(bottom = 8.dp),
                        compact = true,
                        cacheable = cacheable,
                        onCache = onCache,
                    )
                }
            }

            // [上一个 暂停 下一个] [倍速]  ……  [合集 收藏 看过 设置 全屏]
            Row(
                Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                val idx = pagerState.currentPage
                LandControlButton(
                    Icons.Default.SkipPrevious,
                    "上一个",
                    enabled = idx > 0,
                ) { scope.launch { pagerState.animateScrollToPage(idx - 1) } }
                Spacer(Modifier.width(8.dp))
                LandControlButton(
                    if (playing) Icons.Default.Pause else Icons.Default.PlayArrow,
                    if (playing) "暂停" else "播放",
                ) { player.playWhenReady = !player.playWhenReady }
                Spacer(Modifier.width(8.dp))
                LandControlButton(
                    Icons.Default.SkipNext,
                    "下一个",
                    enabled = idx < items.lastIndex,
                ) { scope.launch { pagerState.animateScrollToPage(idx + 1) } }

                Spacer(Modifier.width(18.dp))

                Box {
                    Text(
                        "${playbackSpeed}x",
                        color = Color.White,
                        fontSize = 12.5.sp,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier
                            .clickable { speedMenu = true }
                            .padding(horizontal = 8.dp, vertical = 8.dp),
                    )
                    DropdownMenu(
                        expanded = speedMenu,
                        onDismissRequest = { speedMenu = false },
                    ) {
                        listOf(0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f).forEach { s ->
                            DropdownMenuItem(
                                text = {
                                    Text(
                                        if (s == 1.0f) "1.0x 常速" else "${s}x",
                                        color = if (s == playbackSpeed) Accent else Color.Unspecified,
                                    )
                                },
                                onClick = {
                                    onSpeedChange(s)
                                    speedMenu = false
                                },
                            )
                        }
                    }
                }

                Spacer(Modifier.weight(1f))

                LandIconButton(Icons.Default.CollectionsBookmark, "合集") { onOpenCollection() }
                Spacer(Modifier.width(10.dp))
                LandIconButton(
                    if (liked) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                    "收藏",
                    active = liked,
                ) { onToggleLiked() }
                Spacer(Modifier.width(10.dp))
                LandIconButton(
                    Icons.Default.CheckCircle,
                    if (watched) "已看过" else "看过",
                    active = watched,
                ) { onToggleWatched() }
                Spacer(Modifier.width(10.dp))
                // 适应 / 旋转 / 播放模式都收进这个面板（原来是三个平铺的按钮）
                LandIconButton(
                    Icons.Default.Settings,
                    "设置",
                    active = showSettings,
                ) { showSettings = !showSettings }
                Spacer(Modifier.width(10.dp))
                LandIconButton(
                    if (fullscreen) Icons.Default.FullscreenExit else Icons.Default.Fullscreen,
                    if (fullscreen) "切换竖屏" else "全屏",
                    active = fullscreen,
                ) { onToggleFullscreen() }
            }
            Spacer(Modifier.height(10.dp))
        }
    }
}

/**
 * 细进度条：自绘 3dp 轨道 + 9dp 圆点（Material3 Slider 默认轨道 16dp，横屏下显得又厚又笨）。
 *
 * 触摸区仍有 16dp 高；按下即独占手势（consume down），下面垫着的全屏横滑/点按手势不会再响应，
 * 避免"拖进度条时同时触发全屏横滑"的双重 seek。
 * 注意 onSeek 只改预览值，松手才 onSeekFinished 真跳。
 *
 * 横竖屏共用（竖屏底部卡片也用它，替换掉厚重的 Material Slider）。
 */
@Composable
fun ThinProgressBar(
    pos: Float,
    dur: Float,
    onSeek: (Float) -> Unit,
    onSeekFinished: (Float) -> Unit,
    modifier: Modifier = Modifier,
) {
    val fraction = if (dur > 0f) (pos / dur).coerceIn(0f, 1f) else 0f
    val thumbSize = 9.dp
    BoxWithConstraints(
        modifier
            .height(16.dp)
            .pointerInput(dur) {
                if (dur <= 0f) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = true)
                    down.consume()
                    val w = size.width.toFloat().coerceAtLeast(1f)
                    fun at(x: Float) = (x / w * dur).coerceIn(0f, dur)
                    var target = at(down.position.x)
                    onSeek(target)
                    while (true) {
                        val event = awaitPointerEvent()
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) break
                        change.consume()
                        target = at(change.position.x)
                        onSeek(target)
                    }
                    onSeekFinished(target)
                }
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        // 轨道
        Box(
            Modifier
                .fillMaxWidth()
                .height(3.dp)
                .background(Color.White.copy(alpha = 0.22f), RoundedCornerShape(2.dp))
        )
        // 已播放段
        Box(
            Modifier
                .fillMaxWidth(fraction)
                .height(3.dp)
                .background(Accent, RoundedCornerShape(2.dp))
        )
        // 圆点（贴边时不让它跑出左边界）
        Box(
            Modifier
                .offset(x = (maxWidth * fraction - thumbSize / 2).coerceAtLeast(0.dp))
                .size(thumbSize)
                .background(Accent, CircleShape)
        )
    }
}

/** 控制条上的圆形小图标按钮。 */
@Composable
private fun LandIconButton(icon: ImageVector, label: String, active: Boolean = false, onClick: () -> Unit) {
    Icon(
        icon,
        contentDescription = label,
        tint = if (active) Accent else Color.White,
        modifier = Modifier
            .size(34.dp)
            // 同 RailButton：先 clip 再 clickable，水波纹才被裁进圆形
            .clip(CircleShape)
            .background(Color.White.copy(alpha = 0.12f), CircleShape)
            .clickable { onClick() }
            .padding(7.dp),
    )
}

/** 播放控制按钮（无底圈，靠间距分组）。 */
@Composable
private fun LandControlButton(icon: ImageVector, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Icon(
        icon,
        contentDescription = label,
        tint = if (enabled) Color.White else Color.White.copy(alpha = 0.3f),
        modifier = Modifier
            .size(38.dp)
            .clickable(enabled = enabled) { onClick() }
            .padding(6.dp),
    )
}
