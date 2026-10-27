package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.ui.feed.PlayMode
import io.github.garfieldgod.mediarack.ui.feed.pageAfterPlaybackEnded
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * 播放模式的循环切换。
 *
 * 这个函数只有三行，但错一次的表现是"点循环切到自动切集"或者"卡在某个模式出不来"，
 * 而且用户只能靠反复点点点去发现——所以钉死顺序。
 */
class PlayModeTest {

    @Test
    fun `cycles in fixed order`() {
        assertEquals(PlayMode.LoopOne, PlayMode.PauseAtEnd.next())
        assertEquals(PlayMode.AutoNext, PlayMode.LoopOne.next())
        assertEquals(PlayMode.PauseAtEnd, PlayMode.AutoNext.next())
    }

    @Test
    fun `cycling wraps back to start after three taps`() {
        var mode = PlayMode.PauseAtEnd
        repeat(3) { mode = mode.next() }
        assertEquals(PlayMode.PauseAtEnd, mode)
    }

    @Test
    fun `every mode is reachable within one cycle`() {
        val seen = mutableSetOf(PlayMode.PauseAtEnd)
        var mode = PlayMode.PauseAtEnd
        repeat(PlayMode.entries.size - 1) {
            mode = mode.next()
            seen += mode
        }
        assertEquals(PlayMode.entries.toSet(), seen)
    }

    @Test
    fun `persisted ordinal restores the same mode`() {
        PlayMode.entries.forEach { assertEquals(it, PlayMode.of(it.ordinal)) }
    }

    @Test
    fun `unknown persisted ordinal falls back to pause at end`() {
        // 改过枚举顺序 / 降级安装都会读到越界值：绝不能崩，也不能静默变成"循环"
        assertEquals(PlayMode.PauseAtEnd, PlayMode.of(-1))
        assertEquals(PlayMode.PauseAtEnd, PlayMode.of(99))
    }

    @Test
    fun `only auto next advances after playback ends`() {
        assertEquals(null, pageAfterPlaybackEnded(PlayMode.PauseAtEnd, 0, 2))
        assertEquals(null, pageAfterPlaybackEnded(PlayMode.LoopOne, 0, 2))
        assertEquals(1, pageAfterPlaybackEnded(PlayMode.AutoNext, 0, 2))
    }

    @Test
    fun `auto next stops on last item instead of wrapping`() {
        assertEquals(null, pageAfterPlaybackEnded(PlayMode.AutoNext, 2, 2))
        assertEquals(null, pageAfterPlaybackEnded(PlayMode.AutoNext, 0, 0))
    }
}
