using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>
/// 脚本登记服务契约（PRD §8.1）。业务校验失败抛 <see cref="InvalidOperationException"/>，
/// Message 为本地化键（如 Error_PathDuplicate），由调用方翻译展示。
/// </summary>
public interface IScriptService
{
    IReadOnlyList<Script> GetAll();
    Script? GetById(int id);

    /// <summary>新增（校验：名称必填、路径存在、路径唯一）。返回新 Id。</summary>
    int Add(Script script);

    /// <summary>
    /// 更新可编辑字段（唯一性校验排除自身）；收藏状态不在此方法中修改，
    /// 必须通过 <see cref="ToggleFavorite"/> 单独更新。
    /// </summary>
    void Update(Script script);

    /// <summary>删除（仅元数据，绝不删除磁盘文件；级联清理 RunRecord 由 FK 完成）。</summary>
    void Delete(int id);

    /// <summary>内存搜索：名称/说明/标签 OrdinalIgnoreCase 包含匹配。</summary>
    IReadOnlyList<Script> Search(string keyword);

    /// <summary>切换收藏标志（Phase E）：仅更新 IsFavorite 与 UpdatedAt，其余字段不动。</summary>
    void ToggleFavorite(int id, bool isFavorite);

    /// <summary>
    /// 将脚本库与已配置目录同步：登记有效用户 .py，并移除目录外或被排除的旧记录。
    /// 环境、依赖、缓存、构建目录和目录链接不会被扫描；仅移除数据库元数据，不删除文件。
    /// 名称=文件名（去 .py 后缀）、分类=直接父目录名、路径唯一去重（已登记跳过）。
    /// 返回新增与移除的记录变更总数。
    /// </summary>
    int ImportFromPaths(IReadOnlyList<string> directoryPaths);
}
