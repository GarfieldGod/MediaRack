package io.github.garfieldgod.mediarack.core.db

import androidx.room.Dao
import androidx.room.Delete
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Dao
interface SourceDao {
    @Query("SELECT * FROM sources WHERE id = :id")
    suspend fun getById(id: Long): SourceEntity?

    @Query("SELECT * FROM sources WHERE root_path = :rootPath LIMIT 1")
    suspend fun getByRootPath(rootPath: String): SourceEntity?

    @Query("SELECT * FROM sources ORDER BY id")
    suspend fun getAll(): List<SourceEntity>

    @Insert
    suspend fun insert(source: SourceEntity): Long

    @Delete
    suspend fun delete(source: SourceEntity)
}

@Dao
interface ServerDao {
    @Query("SELECT * FROM servers ORDER BY id")
    fun observeAll(): Flow<List<ServerEntity>>

    @Query("SELECT * FROM servers WHERE id = :id LIMIT 1")
    suspend fun getById(id: Long): ServerEntity?

    @Query("SELECT * FROM servers ORDER BY id")
    suspend fun getAll(): List<ServerEntity>

    @Insert
    suspend fun insert(server: ServerEntity): Long

    @Query("DELETE FROM servers WHERE id = :id")
    suspend fun deleteById(id: Long)

    /** 编辑源信息（抽屉里的「编辑」）：只改名字 / 地址 / 令牌，不动缓存与创建时间。 */
    @Query("UPDATE servers SET name = :name, base_url = :baseUrl, token = :token WHERE id = :id")
    suspend fun update(id: Long, name: String, baseUrl: String, token: String?)

    /** 记录最近一次成功拉取的聚合列表（断网降级数据源）。json=null 清除缓存。 */
    @Query("UPDATE servers SET last_summary_json = :json, fetched_at = :at WHERE id = :id")
    suspend fun updateSummary(id: Long, json: String?, at: Long?)
}

@Dao
interface DownloadDao {
    @Query("SELECT * FROM downloads WHERE server_id = :serverId ORDER BY updated_at DESC")
    fun observeByServer(serverId: Long): Flow<List<DownloadEntity>>

    /** 进行中的任务（横幅数据源），按入队先后排。 */
    @Query(
        """
        SELECT * FROM downloads
        WHERE status IN ('queued', 'downloading', 'paused')
        ORDER BY updated_at
        """
    )
    fun observePending(): Flow<List<DownloadEntity>>

    /** 缓存管理页用：所有未完成任务（含失败，方便就地重试/移除）。 */
    @Query(
        """
        SELECT * FROM downloads
        WHERE status != 'done'
        ORDER BY updated_at
        """
    )
    fun observeActive(): Flow<List<DownloadEntity>>

    @Query("SELECT * FROM downloads WHERE status = 'queued' ORDER BY updated_at LIMIT 1")
    suspend fun nextQueued(): DownloadEntity?

    @Query("SELECT * FROM downloads WHERE server_id = :serverId AND key = :key LIMIT 1")
    suspend fun get(serverId: Long, key: String): DownloadEntity?

    @Upsert
    suspend fun upsert(download: DownloadEntity)

    @Query(
        """
        UPDATE downloads SET status = :status, error = :error, updated_at = :at
        WHERE server_id = :serverId AND key = :key
        """
    )
    suspend fun setStatus(serverId: Long, key: String, status: String, error: String?, at: Long)

    /** 进度回写（节流 ~500ms 一次）；total > 0 时回填总大小。 */
    @Query(
        """
        UPDATE downloads SET
            bytes_done = :done,
            bytes_total = CASE WHEN :total > 0 THEN :total ELSE bytes_total END,
            updated_at = :at
        WHERE server_id = :serverId AND key = :key
        """
    )
    suspend fun setProgress(serverId: Long, key: String, done: Long, total: Long, at: Long)

    /** 进程重启自愈：卡在 downloading 的任务放回队列。 */
    @Query("UPDATE downloads SET status = 'queued' WHERE status = 'downloading'")
    suspend fun recoverDownloading()

    @Query("DELETE FROM downloads WHERE server_id = :serverId AND key = :key")
    suspend fun delete(serverId: Long, key: String)

    // 「我的」页统计
    @Query("SELECT COUNT(*) FROM downloads WHERE status = 'done'")
    fun observeDoneCount(): Flow<Int>

    @Query("SELECT COALESCE(SUM(bytes_done), 0) FROM downloads WHERE status = 'done'")
    fun observeDoneBytes(): Flow<Long>
}

@Dao
interface ItemDao {
    @Query("SELECT * FROM items WHERE source_id = :sourceId AND key = :key LIMIT 1")
    suspend fun get(sourceId: Long, key: String): ItemEntity?

    @Upsert
    suspend fun upsertAll(items: List<ItemEntity>)

    /**
     * 该源现有的全部条目。扫描前读一次，用来把用户设过的 rotation 带进新的 ItemEntity——
     * `@Upsert` 是全字段替换，不带的话每次重扫都会把旋转角抹成 0。
     */
    @Query("SELECT * FROM items WHERE source_id = :sourceId")
    suspend fun listAll(sourceId: Long): List<ItemEntity>

    /** 重扫自愈：删除磁盘上已不存在的条目（对齐 PC"是否已有以磁盘为准"）。 */
    @Query("DELETE FROM items WHERE source_id = :sourceId AND key NOT IN (:keys)")
    suspend fun deleteMissing(sourceId: Long, keys: List<String>)

    @Query("DELETE FROM items WHERE source_id = :sourceId")
    suspend fun deleteBySource(sourceId: Long)

    @Query("SELECT * FROM items WHERE source_id = :sourceId AND status = 'ok'")
    suspend fun listOkBySource(sourceId: Long): List<ItemEntity>

    @Query("SELECT * FROM items WHERE status = 'ok'")
    suspend fun listAllOk(): List<ItemEntity>

    /** 「已下载」统计口径：本地缓存条目（含手机 B 站缓存与下载落盘），不是下载任务表。 */
    @Query("SELECT COUNT(*) FROM items WHERE status = 'ok'")
    fun observeOkCount(): Flow<Int>

    @Query("SELECT COALESCE(SUM(size), 0) FROM items WHERE status = 'ok'")
    fun observeOkBytes(): Flow<Long>

    /** 主动删除一条本地缓存（磁盘文件由调用方删，这里只清索引，重扫会自愈）。 */
    @Query("DELETE FROM items WHERE source_id = :sourceId AND key = :key")
    suspend fun deleteItem(sourceId: Long, key: String)

    /** 本地条目的用户覆盖旋转角（0/90/180/270）。重扫不会覆盖它——扫描只 UPSERT 视频元信息。 */
    @Query("UPDATE items SET rotation = :rotation WHERE source_id = :sourceId AND key = :key")
    suspend fun setRotation(sourceId: Long, key: String, rotation: Int)

    @Query(
        """
        SELECT * FROM items
        WHERE source_id = :sourceId AND collection_dir = :dir AND status = 'ok'
        ORDER BY title
        """
    )
    suspend fun listByCollection(sourceId: Long, dir: String): List<ItemEntity>
}

@Dao
interface MarkDao {
    @Query("SELECT * FROM marks WHERE scope = :scope AND `key` = :key LIMIT 1")
    suspend fun get(scope: String, key: String): MarkEntity?

    @Upsert
    suspend fun upsert(mark: MarkEntity)

    @Query("SELECT * FROM marks WHERE liked_at IS NOT NULL ORDER BY updated_at DESC")
    fun observeLiked(): Flow<List<MarkEntity>>

    @Query("SELECT * FROM marks WHERE liked_at IS NOT NULL")
    suspend fun likedSnapshot(): List<MarkEntity>

    @Query("SELECT * FROM marks WHERE watched_at IS NOT NULL ORDER BY watched_at DESC")
    fun observeWatched(): Flow<List<MarkEntity>>

    /** 全量快照：信息流洗牌要按 scope+key 查"多久前播过"，一次读全表比逐条查快。 */
    @Query("SELECT * FROM marks")
    suspend fun allSnapshot(): List<MarkEntity>

    /**
     * 「继续观看」候选 = **上一次播放的那几条**（`played_at` 最大），按时间倒序取前 2 条。
     *
     * 以前的条件是"没看完的才留"（`position_ms < duration_ms - 5000`），看着很合理，
     * 实际结果是：这几条短视频全是十几二十秒，一播就播完 → 全部掉出候选，
     * 卡片永远显示那条很久以前**没播完**的老视频（用户看到的"总是同一个 Apex"）。
     * 有没有播完是展示层的事（卡片写「已看完 · 重播」），不该由 SQL 决定谁能上卡片。
     *
     * 取 2 条而不是 1 条：调用方要能排除"正在播的那条"（一边看一边进「我的」，
     * 卡片写着正在看的视频、点「续播」原地跳一下，没有任何意义），
     * 排掉第一条后还剩一条可显示。真正的取一条在 [FeedViewModel.continueList] 里做。
     */
    @Query(
        """
        SELECT * FROM marks
        WHERE played_at > 0 AND position_ms > 3000 AND duration_ms > 0
        ORDER BY played_at DESC
        LIMIT 2
        """
    )
    fun observeContinue(): Flow<List<MarkEntity>>

    @Query("SELECT COUNT(*) FROM marks WHERE liked_at IS NOT NULL")
    fun observeLikedCount(): Flow<Int>

    @Query("SELECT COUNT(*) FROM marks WHERE watched_at IS NOT NULL")
    fun observeWatchedCount(): Flow<Int>
}
