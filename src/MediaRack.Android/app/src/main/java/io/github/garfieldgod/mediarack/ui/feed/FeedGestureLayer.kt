package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.media3.exoplayer.ExoPlayer
import io.github.garfieldgod.mediarack.ui.formatDuration
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.roundToLong

/** 横滑拖进度时的预览：目标时间 / 总时长 / 按下瞬间的位置（相对偏移以起点计，播放中不漂移）。 */
private data class ScrubInfo(val target: Long, val dur: Long, val base: Long)

/**
 * 吞掉区域内所有触摸。
 *
 * 控制层的标题栏/控制栏只是"画面上的一块背景"，本身不处理点击，触摸会穿透到底下垫着的手势层，
 * 于是点在栏内空位就会把 UI 藏掉。挂上这个修饰符后，这块区域对下层不再透明。
 * 子级按钮（更深的节点）在 Main pass 里先于父级处理，所以按钮点击不受影响。
 */
fun Modifier.blockTouchPassThrough(): Modifier = pointerInput(Unit) {
    awaitEachGesture {
        val down = awaitFirstDown(requireUnconsumed = false)
        down.consume()
        while (true) {
            val event = awaitPointerEvent()
            event.changes.forEach { it.consume() }
            if (event.changes.none { it.pressed }) break
        }
    }
}

/**
 * 信息流手势层：垫在控制层之下、Pager 之上。横竖屏共用。
 *
 * - 点按 → 切换 UI 显隐（**延后一个双击窗口**再执行，见下）
 * - 双击 → 暂停 / 继续播放
 * - 横滑 → 拖进度：显示目标时间预览，松手 seek。映射是**非线性**的：
 *   frac = (|dx| / 有效宽度) ^ 1.9，小位移细粒度微调（几秒），位移越大增量越陡，
 *   0.82 屏宽即可走完整段时长。
 * - 竖滑 → 仅 [verticalControls] 时接管：左半屏亮度 / 右半屏音量。
 *   竖屏必须传 false——竖滑要留给 Pager 翻页，这里不消费竖直位移，Pager 照常滚。
 * - 双指 → 缩放画面（以双指中点为锚），放大后单指拖动改为平移画面。
 *
 * 关键：全程用「相对按下点的绝对位移」而不是逐帧增量——增量在 consume() 之后
 * 会被清零（positionChange 返回 0），那样一次手势只有头一帧生效，表现为「滑一下调一格」。
 *
 * 为什么单击要等一个双击窗口（[DOUBLE_TAP_TIMEOUT_MS]）：单击是「隐藏 UI」，
 * 双击是「暂停」。两者都在同一个手势层里，若单击立即执行，双击的第一次点按
 * 会先把 UI 藏掉——用户双击暂停后看到的是一片黑，反而不知道暂停成功没有。
 * 所以单击推迟到双击窗口结束再执行；窗口内来了第二下就取消单击、改判双击。
 */
@Composable
fun FeedGestureLayer(
    player: ExoPlayer,
    modifier: Modifier = Modifier,
    verticalControls: Boolean,
    /**
     * 缩放状态。传 State 而不是值：捏合期间它每帧都在变，
     * 若在 FeedContent 组合里读它，整层（含 Pager 内容）每帧都要重组。
     * 这里只在**手势协程内**读 `.value`，与组合无关。
     */
    zoom: State<VideoZoom>,
    onZoomChange: (VideoZoom) -> Unit,
    onTap: () -> Unit,
    onDoubleTap: () -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val activity = context as? android.app.Activity
    val audio = remember {
        context.getSystemService(android.content.Context.AUDIO_SERVICE) as? android.media.AudioManager
    }
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current
    val doubleTapSlopPx = with(density) { 48.dp.toPx() }

    var hint by remember { mutableStateOf<String?>(null) }
    var scrub by remember { mutableStateOf<ScrubInfo?>(null) }

    // 手势协程只跑一次（pointerInput key 不变），所以回调都用 rememberUpdatedState 兜住最新值
    val onZoomChangeState = androidx.compose.runtime.rememberUpdatedState(onZoomChange)
    val onTapState = androidx.compose.runtime.rememberUpdatedState(onTap)
    val onDoubleTapState = androidx.compose.runtime.rememberUpdatedState(onDoubleTap)

    // 双击判定状态
    var lastTapAt by remember { mutableLongStateOf(0L) }
    var lastTapPos by remember { mutableStateOf(Offset.Zero) }
    var pendingTapJob by remember { mutableStateOf<Job?>(null) }

    Box(modifier) {
        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(player, verticalControls) {
                    awaitEachGesture {
                        val down = awaitFirstDown(requireUnconsumed = false)
                        val w = size.width.toFloat()
                        val h = size.height.toFloat()
                        val slop = viewConfiguration.touchSlop
                        val center = Offset(w / 2f, h / 2f)
                        val sizePx = Size(w, h)

                        // 手势基线全部在按下瞬间采样，拖动过程中只按位移换算
                        val pos0 = player.currentPosition
                        val dur0 = player.duration
                        val volMax = audio?.getStreamMaxVolume(android.media.AudioManager.STREAM_MUSIC) ?: 15
                        val vol0 = audio?.getStreamVolume(android.media.AudioManager.STREAM_MUSIC) ?: 0
                        val win = activity?.window
                        val bri0 = win?.attributes?.screenBrightness?.takeIf { it in 0.01f..1f }
                            ?: (android.provider.Settings.System.getInt(
                                context.contentResolver,
                                android.provider.Settings.System.SCREEN_BRIGHTNESS,
                                128,
                            ) / 255f)

                        var mode = 0 // 0=未定 1=横滑进度 2=亮度 3=音量 4=双指缩放 5=放大后平移 -1=放弃
                        var seekTarget = pos0
                        var maxDist = 0f
                        var lastPos = down.position
                        // 当前被跟踪的那根手指。双指缩放结束后可能只剩另一根，
                        // 跟踪对象要跟着换，否则会误判成"手指抬起"直接结束手势。
                        var trackedId = down.id
                        // 双指缩放基线
                        var pinchBaseDist = 0f
                        var pinchBaseScale = 1f
                        var pinchBaseOffset = Offset.Zero
                        var pinchBaseCentroid = Offset.Zero

                        while (true) {
                            val event = awaitPointerEvent()
                            val pressed = event.changes.filter { it.pressed }
                            var change = event.changes.firstOrNull { it.id == trackedId }

                            // ── 双指：缩放 ───────────────────────────────
                            if (pressed.size >= 2) {
                                if (mode != 4) {
                                    mode = 4
                                    pinchBaseDist = (pressed[0].position - pressed[1].position)
                                        .getDistance().coerceAtLeast(1f)
                                    pinchBaseScale = zoom.value.scale
                                    pinchBaseOffset = zoom.value.offset
                                    pinchBaseCentroid =
                                        (pressed[0].position + pressed[1].position) / 2f
                                }
                                val p1 = pressed[0].position
                                val p2 = pressed[1].position
                                val d = (p1 - p2).getDistance().coerceAtLeast(1f)
                                val s1 = clampZoomScale(pinchBaseScale * (d / pinchBaseDist))
                                val centroid = (p1 + p2) / 2f
                                // 以双指中点为锚缩放：中点下的画面内容保持不动
                                val t1 = zoomAroundCentroid(
                                    scale0 = pinchBaseScale,
                                    offset0 = pinchBaseOffset,
                                    centroid = centroid,
                                    center = center,
                                    scale1 = s1,
                                )
                                onZoomChangeState.value(
                                    VideoZoom(s1, clampPan(t1, s1, sizePx)),
                                )
                                event.changes.forEach { it.consume() }
                                // 基准位置跟着走：抬指后接着单指平移才不会跳一大截
                                lastPos = pressed[0].position
                                continue
                            }

                            // 双指抬起一根 → 剩下的那根接管（已放大则改为平移画面）
                            if (mode == 4) {
                                mode = if (!zoom.value.isDefault) 5 else -1
                                if (pressed.size == 1) {
                                    trackedId = pressed[0].id
                                    change = pressed[0]
                                }
                                lastPos = change?.position ?: lastPos
                            }

                            if (change == null) break
                            // 绝对位移：不受 consume 影响，每一帧都能拿到真实位移
                            val dx = change.position.x - down.position.x
                            val dy = change.position.y - down.position.y
                            val dist = maxOf(abs(dx), abs(dy))
                            if (dist > maxDist) maxDist = dist
                            val delta = change.position - lastPos

                            if (mode == 0) {
                                if (change.isConsumed) {
                                    // 已被上层控件（如进度条）接管，本层让路
                                    mode = -1
                                } else if (maxDist > slop) {
                                    mode = when {
                                        // 已放大：单指拖动用来平移画面，不再抢进度/亮度
                                        !zoom.value.isDefault -> 5
                                        abs(dx) >= abs(dy) -> if (dur0 > 0) 1 else -1
                                        !verticalControls -> -1
                                        down.position.x < w / 2 -> 2
                                        else -> 3
                                    }
                                }
                            }

                            when (mode) {
                                1 -> {
                                    change.consume()
                                    // 非线性：小位移=细粒度，大位移=加速；0.82 屏宽覆盖整段
                                    val nx = (abs(dx) / (w * 0.82f)).coerceAtMost(1f)
                                    val frac = nx.pow(1.9f)
                                    val dir = if (dx >= 0f) 1f else -1f
                                    seekTarget = (pos0 + dir * frac * dur0)
                                        .roundToLong().coerceIn(0L, dur0)
                                    scrub = ScrubInfo(seekTarget, dur0, pos0)
                                }
                                2 -> {
                                    change.consume()
                                    val b = (bri0 - dy / h * 1.5f).coerceIn(0.01f, 1f)
                                    win?.let {
                                        it.attributes = it.attributes.apply { screenBrightness = b }
                                    }
                                    hint = "亮度 ${(b * 100).roundToInt()}%"
                                }
                                3 -> {
                                    change.consume()
                                    val v = (vol0 - dy / h * volMax * 1.5f).roundToInt()
                                        .coerceIn(0, volMax)
                                    audio?.setStreamVolume(android.media.AudioManager.STREAM_MUSIC, v, 0)
                                    hint = "音量 ${v * 100 / volMax}%"
                                }
                                5 -> {
                                    change.consume()
                                    val cur = zoom.value
                                    val next = clampPan(cur.offset + delta, cur.scale, sizePx)
                                    onZoomChangeState.value(cur.copy(offset = next))
                                }
                            }

                            lastPos = change.position
                            if (!change.pressed) break
                        }

                        if (mode == 1) player.seekTo(seekTarget)
                        // 全程位移没超过 slop = 点按。
                        // 不能用独立的 detectTapGestures：Pager 到边界时不会消费拖动，
                        // 一次翻页滑动会被它当成 tap，表现为"滑一下就隐藏了 UI"。
                        if (mode == 0 && maxDist <= slop) {
                            val now = android.os.SystemClock.uptimeMillis()
                            val pos = down.position
                            val isDouble = lastTapAt > 0L &&
                                now - lastTapAt <= DOUBLE_TAP_TIMEOUT_MS &&
                                (pos - lastTapPos).getDistance() <= doubleTapSlopPx
                            if (isDouble) {
                                pendingTapJob?.cancel()
                                pendingTapJob = null
                                lastTapAt = 0L
                                onDoubleTapState.value()
                            } else {
                                lastTapAt = now
                                lastTapPos = pos
                                pendingTapJob?.cancel()
                                pendingTapJob = scope.launch {
                                    delay(DOUBLE_TAP_TIMEOUT_MS)
                                    onTapState.value()
                                }
                            }
                        }
                        scrub = null
                        hint = null
                    }
                },
        )

        // 竖滑指示器
        hint?.let { text ->
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(
                    text,
                    color = Color.White,
                    fontSize = 15.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier
                        .background(Color.Black.copy(alpha = 0.65f), RoundedCornerShape(999.dp))
                        .padding(horizontal = 18.dp, vertical = 10.dp),
                )
            }
        }

        // 横滑进度预览：画面正中（横竖屏一致），不压标题栏也不压底部控制条
        scrub?.let { info ->
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                val delta = info.target - info.base
                val sign = if (delta >= 0) "+" else "-"
                Text(
                    "${sign}${formatDuration(abs(delta))}  ${formatDuration(info.target)} / ${formatDuration(info.dur)}",
                    color = Color.White,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier
                        .background(Color.Black.copy(alpha = 0.7f), RoundedCornerShape(999.dp))
                        .padding(horizontal = 18.dp, vertical = 10.dp),
                )
            }
        }
    }
}

/** 双击判定窗口（毫秒）。与系统 ViewConfiguration.getDoubleTapTimeout() 同量级。 */
private const val DOUBLE_TAP_TIMEOUT_MS = 280L
