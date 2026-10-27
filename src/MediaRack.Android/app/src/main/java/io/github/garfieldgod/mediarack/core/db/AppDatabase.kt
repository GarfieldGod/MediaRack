package io.github.garfieldgod.mediarack.core.db

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

@Database(
    entities = [SourceEntity::class, ItemEntity::class, ServerEntity::class, DownloadEntity::class, MarkEntity::class],
    version = 7,
    exportSchema = false,
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun sourceDao(): SourceDao
    abstract fun itemDao(): ItemDao
    abstract fun serverDao(): ServerDao
    abstract fun downloadDao(): DownloadDao
    abstract fun markDao(): MarkDao

    companion object {
        /** v2：新增 servers 表（网络源）。v1 数据不动。 */
        private val MIGRATION_1_2 = object : androidx.room.migration.Migration(1, 2) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS servers (
                        id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                        name TEXT NOT NULL,
                        base_url TEXT NOT NULL,
                        token TEXT,
                        created_at INTEGER NOT NULL
                    )
                    """.trimIndent()
                )
            }
        }

        /** v3：servers 加 summary 缓存两列（断网降级）。 */
        private val MIGRATION_2_3 = object : androidx.room.migration.Migration(2, 3) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE servers ADD COLUMN last_summary_json TEXT")
                db.execSQL("ALTER TABLE servers ADD COLUMN fetched_at INTEGER")
            }
        }

        /** v4：downloads 表（下载到本地）。 */
        private val MIGRATION_3_4 = object : androidx.room.migration.Migration(3, 4) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS downloads (
                        server_id INTEGER NOT NULL,
                        key TEXT NOT NULL,
                        source_id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        layout TEXT NOT NULL,
                        rel_path TEXT,
                        status TEXT NOT NULL,
                        bytes_done INTEGER NOT NULL,
                        bytes_total INTEGER NOT NULL,
                        error TEXT,
                        updated_at INTEGER NOT NULL,
                        PRIMARY KEY(server_id, key)
                    )
                    """.trimIndent()
                )
                db.execSQL("CREATE INDEX IF NOT EXISTS index_downloads_status ON downloads (status)")
            }
        }

        /** v5：marks 表（信息流 UI：收藏、看过、播放进度）。 */
        private val MIGRATION_4_5 = object : androidx.room.migration.Migration(4, 5) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS marks (
                        scope TEXT NOT NULL,
                        key TEXT NOT NULL,
                        title TEXT NOT NULL,
                        liked_at INTEGER,
                        watched_at INTEGER,
                        position_ms INTEGER NOT NULL DEFAULT 0,
                        duration_ms INTEGER NOT NULL DEFAULT 0,
                        updated_at INTEGER NOT NULL,
                        PRIMARY KEY(scope, key)
                    )
                    """.trimIndent()
                )
            }
        }

        /** v6：items 加 rotation（本地条目的用户覆盖旋转角）。远程条目的角存在服务端 media_meta。 */
        private val MIGRATION_5_6 = object : androidx.room.migration.Migration(5, 6) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE items ADD COLUMN rotation INTEGER NOT NULL DEFAULT 0")
            }
        }

        /**
         * v7：marks 加 `played_at`（"最后播放时间"），供「继续观看」排序。
         *
         * 老数据没有这个字段，用 updated_at 回填——那时候两者还是同一回事
         * （除了被收藏/看过刷新过的行，那种行只会错一次，下次播放就被正确覆盖）。
         */
        private val MIGRATION_6_7 = object : androidx.room.migration.Migration(6, 7) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE marks ADD COLUMN played_at INTEGER NOT NULL DEFAULT 0")
                db.execSQL(
                    """
                    UPDATE marks SET played_at = updated_at
                    WHERE position_ms > 0 AND duration_ms > 0
                    """.trimIndent()
                )
            }
        }

        @Volatile
        private var instance: AppDatabase? = null

        fun get(context: Context): AppDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "library.db",
                ).addMigrations(
                    MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4, MIGRATION_4_5, MIGRATION_5_6, MIGRATION_6_7,
                )
                    .build().also { instance = it }
            }
    }
}
