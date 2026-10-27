package io.github.garfieldgod.mediarack.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * 视觉规格（design/ui_mockup_v3 定稿）：深色影院 + B站粉。
 * App 是全屏视频场景，不做浅色变体；管理页沿用同一色板保持观感统一。
 */
private val FeedColors = darkColorScheme(
    primary = Color(0xFFFB7299),
    onPrimary = Color.White,
    primaryContainer = Color(0xFF3A2430),
    onPrimaryContainer = Color(0xFFFFB2C8),
    secondary = Color(0xFF6FA5FF),
    background = Color(0xFF121419),
    onBackground = Color(0xFFECEEF2),
    surface = Color(0xFF1B1E26),
    onSurface = Color(0xFFECEEF2),
    surfaceVariant = Color(0xFF232733),
    onSurfaceVariant = Color(0xFF8B919C),
    error = Color(0xFFFF5D5D),
)

@Composable
fun MediaRackTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = FeedColors,
        content = content,
    )
}
