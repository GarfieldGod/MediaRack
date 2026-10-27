namespace MediaRack.Models;

/// <summary>
/// 源名下的一个内容文件夹。布局方式<b>跟文件夹走</b>而不是跟源走：
/// 同一个源可以同时挂「B 站缓存目录（dual）+ 普通电影目录（single）」。
/// <para>
/// 文件夹不是独立实体——"一个文件夹可以出现在多个源里"是路径级共享
/// （两个源各登记同一路径），没有跨源外键。增删重排都是本源内部的行操作。
/// </para>
/// </summary>
/// <param name="Id">存储层自增 id，管理端点用它在源内寻址。</param>
/// <param name="Position">展示与扫描顺序（小的在前）。</param>
/// <param name="Root">绝对路径，仅本机有效，不参与内容身份。</param>
/// <param name="Layout">null = auto：扫描时现场识别，并把结果写回存储。</param>
public sealed record SourceFolder(long Id, int Position, string Root, SourceLayout? Layout);
