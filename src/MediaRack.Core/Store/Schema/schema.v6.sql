-- MediaRack — schema v6
-- 主题：single 条目的文件修改时间（mtime），扫描期跳过 ffprobe 的判变依据。
--
-- 背景：single 源重扫时每个文件都要跑一次 ffprobe 子进程（100-300ms/条），
-- 不管文件变没变。库巨大时"只改了一个文件夹也要等几十分钟"。
--
-- 方案：扫描时把文件的 LastWriteTimeUtc 一并存库。下次扫描先 stat——
-- mtime + size 都没变 → 认定媒体内容未变，直接沿用库里的时长/分辨率/编码，
-- 跳过 ffprobe；只有新文件和改动过的文件才真正探测。
--
-- dual 条目不参与（key 是内容身份，没有 ffprobe 成本），该列恒为 NULL。
-- Upsert 冲突分支对 probe 字段用 COALESCE 保旧值，缓存命中时写入的值
-- 本来就与库一致，语义天然安全。

ALTER TABLE media ADD COLUMN file_mtime INTEGER;
