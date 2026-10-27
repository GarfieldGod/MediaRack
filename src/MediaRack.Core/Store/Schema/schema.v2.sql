-- MediaRack — schema v2
-- 主题：把"源"提升为对外可寻址的入口（/s/{slug}），并给它一把自己的令牌。
--
-- 两个设计决定：
--   1. 加列而不是改 id。media / media_track / media_meta / collection_item 全都以
--      (source_id, key) 为复合键，改 id 要级联改四张表；而 slug 只解决"URL 里能写什么"
--      这一个问题，犯不上动内容身份。
--   2. slug 允许 NULL 且唯一索引是部分索引。迁移发生在代码里（规则是生成规则，SQL 表达不了），
--      迁移窗口期内老行全是 NULL，普通唯一索引会让它们互相冲突。

ALTER TABLE source ADD COLUMN slug  TEXT;
ALTER TABLE source ADD COLUMN token TEXT;

-- 部分索引：NULL 不参与唯一，未补齐的行不会互撞。
CREATE UNIQUE INDEX IF NOT EXISTS idx_source_slug ON source(slug) WHERE slug IS NOT NULL;
