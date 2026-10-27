package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.RotateRight
import androidx.compose.material.icons.filled.Crop
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.FitScreen
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * 播放设置小面板：把「适应 / 旋转 / 模式」三件小事收进一个入口。
 *
 * 为什么收：右侧竖排原来是 6 个圆钮（合集/收藏/看过/适应/旋转/全屏），占了半屏高度，
 * 竖屏下快要压到底部信息卡；而这三项都是**偶尔调一次**的画面设置，
 * 频率远低于合集/收藏/看过，不该和它们平铺在一起。
 *
 * 两行分组，按作用域区分：
 * - 「视频」：跟当前条目走（旋转写回条目本身，换视频就是另一回事）；
 * - 「应用」：全局设置（适应/铺满、播放模式都落盘，跟 App 不跟视频）。
 *
 * chip 都带文字：只给图标的话，"铺满/适应"和"旋转"在不同人的理解里是两种意思，
 * 而模式更是一个只有文字才说得清的东西（循环 vs 自动切集）。
 */
@Composable
fun PlaySettingsPanel(
    cover: Boolean,
    onToggleCover: () -> Unit,
    rotation: Int,
    onRotate: () -> Unit,
    mode: PlayMode,
    onCycleMode: () -> Unit,
    modifier: Modifier = Modifier,
    /// 横屏的按钮行比竖屏的竖排更矮，间距收紧一点；其余完全一致。
    compact: Boolean = false,
    /** 只有远程条目给「缓存」入口（本地条目本来就在手机上）。 */
    cacheable: Boolean = false,
    onCache: () -> Unit = {},
) {
    Column(
        modifier
            .background(Color.Black.copy(alpha = 0.78f), RoundedCornerShape(14.dp))
            // 点面板里的空位不该穿透成"隐藏 UI"——否则调完设置顺手一松手，界面就没了
            .blockTouchPassThrough()
            .padding(horizontal = 6.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        GroupRow("视频", compact) {
            SettingsChip(
                Icons.AutoMirrored.Filled.RotateRight,
                // 角度写出来：点三次之后必须能一眼看出"现在转了多少"，
                // 否则只能靠画面方向猜（而画面方向也可能本来就是竖的）
                if (rotation == 0) "旋转" else "旋转 $rotation°",
                rotation != 0,
                compact,
            ) { onRotate() }
            if (cacheable) {
                SettingsChip(Icons.Default.Download, "缓存", false, compact) { onCache() }
            }
        }
        GroupRow("应用", compact) {
            SettingsChip(
                if (cover) Icons.Default.Crop else Icons.Default.FitScreen,
                if (cover) "铺满" else "适应",
                cover,
                compact,
            ) { onToggleCover() }
            SettingsChip(mode.icon, mode.label, mode != PlayMode.PauseAtEnd, compact) { onCycleMode() }
        }
    }
}

/** 面板里的一行：行首灰字组名（视频 / 应用）+ 该组的 chip。 */
@Composable
private fun GroupRow(label: String, compact: Boolean, content: @Composable RowScope.() -> Unit) {
    Row(
        horizontalArrangement = Arrangement.spacedBy(2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            label,
            color = Color(0xFF8B919C),
            fontSize = 9.5.sp,
            modifier = Modifier.padding(start = if (compact) 5.dp else 6.dp, end = 2.dp),
        )
        content()
    }
}

/** 面板里的一项：图标 + 文字，点一下切换。 */
@Composable
private fun SettingsChip(
    icon: ImageVector,
    label: String,
    active: Boolean,
    compact: Boolean,
    onClick: () -> Unit,
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier
            .background(
                if (active) AccentSoft else Color.White.copy(alpha = 0.08f),
                RoundedCornerShape(10.dp),
            )
            .clickable { onClick() }
            .padding(horizontal = if (compact) 9.dp else 10.dp, vertical = 7.dp),
    ) {
        Icon(
            icon,
            contentDescription = label,
            tint = if (active) AccentColor else Color.White,
            modifier = Modifier.size(20.dp),
        )
        Spacer(Modifier.height(3.dp))
        Text(
            label,
            color = if (active) AccentColor else Color.White.copy(alpha = 0.85f),
            fontSize = 10.sp,
            fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1,
        )
    }
}

/** 播放模式对应的图标（模式是纯数据，图标映射留在 UI 层）。 */
val PlayMode.icon: ImageVector
    get() = when (this) {
        PlayMode.PauseAtEnd -> Icons.Default.Pause
        PlayMode.LoopOne -> Icons.Default.Repeat
        PlayMode.AutoNext -> Icons.Default.SkipNext
    }

private val AccentColor = Color(0xFFFB7299)
private val AccentSoft = Color(0xFFFB7299).copy(alpha = 0.18f)
